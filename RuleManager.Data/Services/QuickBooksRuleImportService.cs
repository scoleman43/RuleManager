using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using RuleManager.Core.Domain;

namespace RuleManager.Data.Services;

public enum RuleImportStatus
{
    New,
    Changed,
    Unchanged,
    Duplicate,
    Unsupported
}

public sealed class RuleImportPreviewItem
{
    public int RowNumber { get; init; }
    public string Name { get; init; } = string.Empty;
    public RuleDirection Direction { get; init; }
    public RuleTransactionType TransactionType { get; init; }
    public List<RuleCondition> Conditions { get; init; } = new();
    public string? CategoryName { get; init; }
    public bool AutoAdd { get; init; }
    public string? OriginalConditionsJson { get; init; }
    public string? OriginalOutputsJson { get; init; }
    public bool IsReadOnlyImport { get; init; }
    public string? UnsupportedReason { get; init; }
    public RuleImportStatus Status { get; set; }
    public Guid? ExistingRuleId { get; set; }
    public bool Selected { get; set; }
    public string? ClientComparison { get; set; }
}

public sealed class QuickBooksRuleImportService(IDbContextFactory<RuleManagerDbContext> dbFactory)
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    public async Task<List<RuleImportPreviewItem>> AnalyzeAsync(
        Guid organizationId,
        Stream workbookStream,
        CancellationToken cancellationToken = default)
    {
        var parsed = ParseWorkbook(workbookStream);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existingRules = await db.MasterRules
            .Where(x => x.OrganizationId == organizationId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var existingByName = existingRules
            .GroupBy(x => x.Name, NameComparer)
            .ToDictionary(x => x.Key, x => x.First(), NameComparer);

        var duplicateNames = parsed
            .GroupBy(x => x.Name, NameComparer)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .ToHashSet(NameComparer);

        foreach (var item in parsed)
        {
            if (!string.IsNullOrWhiteSpace(item.UnsupportedReason))
            {
                item.Status = RuleImportStatus.Unsupported;
                item.Selected = false;
                continue;
            }

            if (duplicateNames.Contains(item.Name))
            {
                item.Status = RuleImportStatus.Duplicate;
                item.Selected = false;
                continue;
            }

            if (!existingByName.TryGetValue(item.Name, out var existing))
            {
                item.Status = RuleImportStatus.New;
                item.Selected = true;
                continue;
            }

            item.ExistingRuleId = existing.Id;

            if (Equivalent(existing, item))
            {
                item.Status = RuleImportStatus.Unchanged;
                item.Selected = false;
            }
            else
            {
                item.Status = RuleImportStatus.Changed;
                item.Selected = true;
            }
        }

        return parsed;
    }

    public async Task<(int Added, int Updated)> ApplyAsync(
        Guid organizationId,
        IEnumerable<RuleImportPreviewItem> items,
        CancellationToken cancellationToken = default)
    {
        var selected = items
            .Where(x => x.Selected && x.Status is RuleImportStatus.New or RuleImportStatus.Changed)
            .ToList();

        if (selected.Count == 0)
            return (0, 0);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var added = 0;
        var updated = 0;

        foreach (var item in selected)
        {
            MasterRule entity;

            if (item.ExistingRuleId.HasValue)
            {
                entity = await db.MasterRules.SingleAsync(
                    x => x.Id == item.ExistingRuleId.Value && x.OrganizationId == organizationId,
                    cancellationToken);
                updated++;
            }
            else
            {
                entity = new MasterRule
                {
                    OrganizationId = organizationId
                };
                db.MasterRules.Add(entity);
                added++;
            }

            entity.Name = item.Name;
            entity.Direction = item.Direction;
            entity.TransactionType = item.TransactionType;
            entity.CategoryName = item.CategoryName;
            entity.CategoryId = null;
            entity.Payee = null;
            entity.MatchAllConditions = true;
            entity.AutoAdd = item.AutoAdd;
            entity.Conditions = item.Conditions.Select(x => new RuleCondition
            {
                Field = x.Field,
                Operator = x.Operator,
                Value = x.Value
            }).ToList();
            entity.OriginalConditionsJson = item.OriginalConditionsJson;
            entity.OriginalOutputsJson = item.OriginalOutputsJson;
            entity.IsAccountSpecific = false;
            entity.IsReadOnlyImport = item.IsReadOnlyImport;
            entity.IsActive = true;
            entity.ModifiedUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return (added, updated);
    }

    private static List<RuleImportPreviewItem> ParseWorkbook(Stream stream)
    {
        using var workbook = new HSSFWorkbook(stream);
        if (workbook.NumberOfSheets == 0)
            throw new InvalidDataException("The workbook does not contain a worksheet.");

        var sheet = workbook.GetSheetAt(0);
        var formatter = new DataFormatter();

        var headerRow = sheet.GetRow(sheet.FirstRowNum)
            ?? throw new InvalidDataException("The workbook does not contain a header row.");

        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = headerRow.FirstCellNum; i < headerRow.LastCellNum; i++)
        {
            var value = formatter.FormatCellValue(headerRow.GetCell(i)).Trim();
            if (!string.IsNullOrWhiteSpace(value))
                headers[value] = i;
        }

        foreach (var required in new[] { "Rule Name", "Rule Conditions", "Rule Outputs" })
        {
            if (!headers.ContainsKey(required))
                throw new InvalidDataException($"The workbook is missing the required '{required}' column.");
        }

        var result = new List<RuleImportPreviewItem>();

        for (var rowIndex = sheet.FirstRowNum + 1; rowIndex <= sheet.LastRowNum; rowIndex++)
        {
            var row = sheet.GetRow(rowIndex);
            if (row is null)
                continue;

            var name = formatter.FormatCellValue(row.GetCell(headers["Rule Name"])).Trim();
            var conditionsJson = formatter.FormatCellValue(row.GetCell(headers["Rule Conditions"])).Trim();
            var outputsJson = formatter.FormatCellValue(row.GetCell(headers["Rule Outputs"])).Trim();

            if (string.IsNullOrWhiteSpace(name)
                && string.IsNullOrWhiteSpace(conditionsJson)
                && string.IsNullOrWhiteSpace(outputsJson))
                continue;

            result.Add(ParseRule(rowIndex + 1, name, conditionsJson, outputsJson));
        }

        return result;
    }

    private static RuleImportPreviewItem ParseRule(
        int rowNumber,
        string name,
        string conditionsJson,
        string outputsJson)
    {
        var reasons = new List<string>();
        var direction = RuleDirection.MoneyOut;
        var hasDirection = false;
        var conditions = new List<RuleCondition>();

        if (string.IsNullOrWhiteSpace(name))
            reasons.Add("Rule name is blank.");

        if (!TryParseJson(conditionsJson, out var conditionsDoc, out var conditionsError))
        {
            reasons.Add($"Rule Conditions JSON is invalid: {conditionsError}");
        }
        else
        {
            using (conditionsDoc)
            {
                foreach (var obj in EnumerateObjects(conditionsDoc!.RootElement))
                {
                    if (!TryGetInt(obj, "ruleType", out var ruleType))
                        continue;

                    var value = GetValueAsString(obj, "value");

                    switch (ruleType)
                    {
                        case 10:
                            if (value == "-1")
                            {
                                direction = RuleDirection.MoneyOut;
                                hasDirection = true;
                            }
                            else if (value == "1")
                            {
                                direction = RuleDirection.MoneyIn;
                                hasDirection = true;
                            }
                            else
                            {
                                reasons.Add($"Unsupported direction value '{value}'.");
                            }
                            break;

                        case 1:
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                conditions.Add(new RuleCondition
                                {
                                    Field = RuleMatchField.Description,
                                    Operator = RuleMatchOperator.Contains,
                                    Value = value
                                });
                            }
                            break;

                        case 6:
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                conditions.Add(new RuleCondition
                                {
                                    Field = RuleMatchField.BankText,
                                    Operator = RuleMatchOperator.Contains,
                                    Value = value
                                });
                            }
                            break;

                        default:
                            reasons.Add($"Unsupported condition ruleType {ruleType}.");
                            break;
                    }
                }
            }
        }

        if (!hasDirection)
            reasons.Add("No supported Money in / Money out condition was found.");

        if (conditions.Count == 0)
            reasons.Add("No supported Description or Bank text condition was found.");

        string? categoryName = null;
        var autoAdd = false;
        int? transactionCode = null;

        if (!TryParseJson(outputsJson, out var outputsDoc, out var outputsError))
        {
            reasons.Add($"Rule Outputs JSON is invalid: {outputsError}");
        }
        else
        {
            using (outputsDoc)
            {
                foreach (var obj in EnumerateObjects(outputsDoc!.RootElement))
                {
                    if (!TryGetInt(obj, "actionType", out var actionType))
                        continue;

                    switch (actionType)
                    {
                        case 0:
                            categoryName = GetValueAsString(obj, "value");
                            break;

                        case 7:
                            var transactionValue = GetValueAsString(obj, "value");
                            if (int.TryParse(transactionValue, out var code))
                                transactionCode = code;
                            else
                                reasons.Add($"Unsupported transaction type value '{transactionValue}'.");
                            break;

                        case 8:
                            autoAdd = GetValueAsBoolean(obj, "value");
                            break;

                        default:
                            reasons.Add($"Unsupported output actionType {actionType}.");
                            break;
                    }
                }
            }
        }

        var transactionType = ResolveTransactionType(direction, transactionCode, reasons);

        return new RuleImportPreviewItem
        {
            RowNumber = rowNumber,
            Name = name,
            Direction = direction,
            TransactionType = transactionType,
            Conditions = conditions,
            CategoryName = NullIfWhiteSpace(categoryName),
            AutoAdd = autoAdd,
            OriginalConditionsJson = NullIfWhiteSpace(conditionsJson),
            OriginalOutputsJson = NullIfWhiteSpace(outputsJson),
            IsReadOnlyImport = reasons.Count > 0,
            UnsupportedReason = reasons.Count == 0 ? null : string.Join(" ", reasons.Distinct()),
            Status = reasons.Count == 0 ? RuleImportStatus.New : RuleImportStatus.Unsupported
        };
    }

    private static RuleTransactionType ResolveTransactionType(
        RuleDirection direction,
        int? transactionCode,
        ICollection<string> reasons)
    {
        if (!transactionCode.HasValue)
            return direction == RuleDirection.MoneyOut
                ? RuleTransactionType.Expense
                : RuleTransactionType.Deposit;

        return transactionCode.Value switch
        {
            3 => RuleTransactionType.Check,
            26 => RuleTransactionType.Transfer,
            64 => RuleTransactionType.CreditCardPayment,
            _ => UnsupportedTransaction(transactionCode.Value, direction, reasons)
        };
    }

    private static RuleTransactionType UnsupportedTransaction(
        int code,
        RuleDirection direction,
        ICollection<string> reasons)
    {
        reasons.Add($"Unsupported transaction action value '{code}'.");
        return direction == RuleDirection.MoneyOut
            ? RuleTransactionType.Expense
            : RuleTransactionType.Deposit;
    }

    private static bool Equivalent(MasterRule existing, RuleImportPreviewItem imported)
    {
        if (existing.Direction != imported.Direction
            || existing.TransactionType != imported.TransactionType
            || existing.AutoAdd != imported.AutoAdd
            || !string.Equals(existing.CategoryName ?? string.Empty, imported.CategoryName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            return false;

        var left = existing.Conditions
            .Select(NormalizeCondition)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var right = imported.Conditions
            .Select(NormalizeCondition)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        return left.SequenceEqual(right, StringComparer.Ordinal);
    }

    private static string NormalizeCondition(RuleCondition condition) =>
        $"{condition.Field}|{condition.Operator}|{condition.Value.Trim().ToUpperInvariant()}";

    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var property in element.EnumerateObject())
            {
                foreach (var child in EnumerateObjects(property.Value))
                    yield return child;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var childElement in element.EnumerateArray())
            {
                foreach (var child in EnumerateObjects(childElement))
                    yield return child;
            }
        }
    }

    private static bool TryParseJson(string json, out JsonDocument? document, out string? error)
    {
        try
        {
            document = JsonDocument.Parse(json);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            document = null;
            error = ex.Message;
            return false;
        }
    }

    private static bool TryGetInt(JsonElement obj, string propertyName, out int value)
    {
        value = default;
        if (!TryGetProperty(obj, propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number)
            return property.TryGetInt32(out value);

        return int.TryParse(property.ToString(), out value);
    }

    private static string? GetValueAsString(JsonElement obj, string propertyName)
    {
        if (!TryGetProperty(obj, propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => property.ToString()
        };
    }

    private static bool GetValueAsBoolean(JsonElement obj, string propertyName)
    {
        if (!TryGetProperty(obj, propertyName, out var property))
            return false;

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return property.GetBoolean();

        return bool.TryParse(property.ToString(), out var value) && value;
    }

    private static bool TryGetProperty(JsonElement obj, string propertyName, out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
