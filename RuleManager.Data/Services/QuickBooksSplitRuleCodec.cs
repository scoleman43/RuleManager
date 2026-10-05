using System.Globalization;
using System.Text.Json;

namespace RuleManager.Data.Services;

public enum QuickBooksSplitType
{
    Percentage,
    Amount
}

public sealed class QuickBooksSplitLine
{
    public string CategoryName { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsRemainder { get; set; }
}

public sealed class QuickBooksSplitDefinition
{
    public QuickBooksSplitType Type { get; set; }
    public List<QuickBooksSplitLine> Lines { get; set; } = new();
}

public static class QuickBooksSplitRuleCodec
{
    public static bool IsSplitRule(string? outputsJson) =>
        TryParse(outputsJson, out _);

    public static bool TryParse(string? outputsJson, out QuickBooksSplitDefinition? definition)
    {
        definition = null;

        if (string.IsNullOrWhiteSpace(outputsJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(outputsJson);
            if (!doc.RootElement.TryGetProperty("ruleActions", out var actions)
                || actions.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var action in actions.EnumerateArray())
            {
                if (!action.TryGetProperty("actionType", out var actionType)
                    || !TryGetInt(actionType, out var actionTypeValue)
                    || actionTypeValue != 6
                    || !action.TryGetProperty("value", out var value)
                    || value.ValueKind != JsonValueKind.Object
                    || !value.TryGetProperty("actionInfoList", out var list)
                    || list.ValueKind != JsonValueKind.Array)
                    continue;

                var parsed = new QuickBooksSplitDefinition();
                string? detectedType = null;

                foreach (var line in list.EnumerateArray())
                {
                    if (line.ValueKind != JsonValueKind.Object)
                        return false;

                    var category = GetString(line, "categoryId");
                    var splitValue = GetString(line, "splitValue");
                    var splitType = GetString(line, "splitType");

                    if (string.IsNullOrWhiteSpace(category)
                        || string.IsNullOrWhiteSpace(splitValue)
                        || string.IsNullOrWhiteSpace(splitType))
                        return false;

                    detectedType ??= splitType;
                    if (!string.Equals(detectedType, splitType, StringComparison.OrdinalIgnoreCase))
                        return false;

                    parsed.Lines.Add(new QuickBooksSplitLine
                    {
                        CategoryName = category,
                        Value = string.Equals(splitValue, "R", StringComparison.OrdinalIgnoreCase)
                            ? string.Empty
                            : splitValue,
                        IsRemainder = string.Equals(splitValue, "R", StringComparison.OrdinalIgnoreCase)
                    });
                }

                if (parsed.Lines.Count < 2 || detectedType is null)
                    return false;

                parsed.Type = detectedType.Equals("percentage", StringComparison.OrdinalIgnoreCase)
                    ? QuickBooksSplitType.Percentage
                    : detectedType.Equals("amount", StringComparison.OrdinalIgnoreCase)
                        ? QuickBooksSplitType.Amount
                        : throw new InvalidDataException($"Unsupported QuickBooks split type '{detectedType}'.");

                definition = parsed;
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }

        return false;
    }

    public static string Serialize(QuickBooksSplitDefinition definition)
    {
        var splitType = definition.Type == QuickBooksSplitType.Percentage
            ? "percentage"
            : "amount";

        var actionInfoList = definition.Lines.Select(line => new Dictionary<string, object?>
        {
            ["categoryId"] = line.CategoryName,
            ["splitValue"] = line.IsRemainder
                ? "R"
                : NormalizeNumericValue(line.Value),
            ["splitType"] = splitType
        }).ToArray();

        var outputs = new Dictionary<string, object?>
        {
            ["ruleActions"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["actionType"] = 6,
                    ["value"] = new Dictionary<string, object?>
                    {
                        ["actionInfoList"] = actionInfoList
                    }
                }
            }
        };

        return JsonSerializer.Serialize(outputs);
    }

    public static string? Validate(QuickBooksSplitDefinition definition)
    {
        if (definition.Lines.Count < 2)
            return "Split rules require at least two split lines.";

        if (definition.Lines.Any(x => string.IsNullOrWhiteSpace(x.CategoryName)))
            return "Every split line requires a category.";

        if (definition.Type == QuickBooksSplitType.Percentage)
        {
            if (definition.Lines.Any(x => x.IsRemainder))
                return "Percentage splits cannot use a remainder line.";

            decimal total = 0;
            foreach (var line in definition.Lines)
            {
                if (!TryParsePositiveDecimal(line.Value, out var value))
                    return "Every percentage split line requires a positive numeric value.";

                total += value;
            }

            if (total != 100m)
                return $"Percentage split lines must total 100%. Current total: {total:0.##}%.";
        }
        else
        {
            if (definition.Lines.Count(x => x.IsRemainder) != 1)
                return "Amount splits require exactly one remainder line.";

            foreach (var line in definition.Lines.Where(x => !x.IsRemainder))
            {
                if (!TryParsePositiveDecimal(line.Value, out _))
                    return "Every fixed-amount split line requires a positive numeric value.";
            }
        }

        return null;
    }

    private static bool TryParsePositiveDecimal(string? text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value)
        && value > 0;

    private static string NormalizeNumericValue(string value)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            return value.Trim();

        return parsed.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static bool TryGetInt(JsonElement element, out int value)
    {
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetInt32(out value);

        return int.TryParse(element.ToString(), out value);
    }
}
