using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NPOI.HSSF.UserModel;
using RuleManager.Core.Domain;

namespace RuleManager.Data.Services;

public sealed record QuickBooksExportResult(
    byte[]? Content,
    string? FileName,
    int RuleCount,
    IReadOnlyList<string> Errors)
{
    public bool Success => Content is not null && Errors.Count == 0;
}

public sealed class QuickBooksRuleExportService(IDbContextFactory<RuleManagerDbContext> dbFactory)
{
    public async Task<QuickBooksExportResult> GenerateClientExportAsync(
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var client = await db.Clients
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == clientId, cancellationToken);

        if (client is null)
            return Failed("Client was not found.");

        var assignments = await db.ClientRuleAssignments
            .Where(x => x.ClientId == clientId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var assignedRuleIds = assignments
            .Select(x => x.MasterRuleId)
            .Distinct()
            .ToArray();

        var rules = await db.MasterRules
            .Where(x => assignedRuleIds.Contains(x.Id) && x.IsActive)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var clientSpecificRules = await db.ClientRules
            .Where(x => x.ClientId == clientId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (rules.Count == 0 && clientSpecificRules.Count == 0)
            return Failed("This client does not have any active or client-specific rules to export.");

        var exportRows = new List<ExportRow>();
        var errors = new List<string>();

        var clientSpecificNames = clientSpecificRules
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var assignmentPriority = assignments
            .ToDictionary(x => x.MasterRuleId, x => x.ExportPriority);

        var orderedRules = new List<(RuleBase Rule, int Priority)>();

        orderedRules.AddRange(clientSpecificRules.Select(rule =>
            ((RuleBase)rule, rule.ExportPriority)));

        orderedRules.AddRange(rules
            .Where(rule => !clientSpecificNames.Contains(rule.Name))
            .Select(rule =>
                ((RuleBase)rule,
                 assignmentPriority.TryGetValue(rule.Id, out var priority)
                    ? priority
                    : int.MaxValue)));

        foreach (var item in orderedRules
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Rule.Name))
        {
            var row = BuildRow(item.Rule, out var error);

            if (row is null)
            {
                errors.Add($"{item.Rule.Name}: {error}");
                continue;
            }

            exportRows.Add(row);
        }

        if (errors.Count > 0)
            return new QuickBooksExportResult(null, null, exportRows.Count, errors);

        using var workbook = new HSSFWorkbook();
        var sheet = workbook.CreateSheet("Rules");

        var header = sheet.CreateRow(0);
        header.CreateCell(0).SetCellValue("Rule Name");
        header.CreateCell(1).SetCellValue("Rule Conditions");
        header.CreateCell(2).SetCellValue("Rule Outputs");

        for (var i = 0; i < exportRows.Count; i++)
        {
            var row = sheet.CreateRow(i + 1);
            row.CreateCell(0).SetCellValue(exportRows[i].Name);
            row.CreateCell(1).SetCellValue(exportRows[i].ConditionsJson);
            row.CreateCell(2).SetCellValue(exportRows[i].OutputsJson);
        }

        sheet.SetColumnWidth(0, 32 * 256);
        sheet.SetColumnWidth(1, 90 * 256);
        sheet.SetColumnWidth(2, 70 * 256);

        using var stream = new MemoryStream();
        workbook.Write(stream);

        var safeClientName = SanitizeFileName(client.Name);
        var fileName = $"{safeClientName}_Bank_Feed_Rules.xls";

        return new QuickBooksExportResult(
            stream.ToArray(),
            fileName,
            exportRows.Count,
            Array.Empty<string>());
    }

    private static ExportRow? BuildRow(RuleBase rule, out string? error)
    {
        error = null;

        if (rule.IsReadOnlyImport
            || rule.IsAccountSpecific
            || rule.TransactionType == RuleTransactionType.CreditCardPayment)
        {
            if (!string.IsNullOrWhiteSpace(rule.OriginalConditionsJson)
                && !string.IsNullOrWhiteSpace(rule.OriginalOutputsJson))
            {
                return new ExportRow(
                    rule.Name,
                    rule.OriginalConditionsJson,
                    rule.OriginalOutputsJson);
            }

            error = "This imported account-specific/read-only rule does not have its original QuickBooks JSON.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(rule.CategoryName))
        {
            error = "A QuickBooks category is required.";
            return null;
        }

        if (rule.Conditions.Count == 0)
        {
            error = "At least one condition is required.";
            return null;
        }

        var conditionObjects = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["ruleType"] = 10,
                ["value"] = rule.Direction == RuleDirection.MoneyOut ? "-1" : "1"
            }
        };

        foreach (var condition in rule.Conditions)
        {
            if (!TryGetConditionRuleType(condition, out var ruleType))
            {
                error = $"The combination '{condition.Field} / {FormatOperator(condition.Operator)}' does not yet have a verified QuickBooks export mapping.";
                return null;
            }

            var value = condition.Value;
            if (condition.Field == RuleMatchField.Amount)
            {
                if (rule.Direction != RuleDirection.MoneyOut)
                {
                    error = "Money in Amount export has not yet been verified against a real QuickBooks export.";
                    return null;
                }

                if (!decimal.TryParse(
                    condition.Value,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var amount))
                {
                    error = $"Amount condition '{condition.Value}' is not a valid number.";
                    return null;
                }

                value = (-Math.Abs(amount)).ToString("0.00", CultureInfo.InvariantCulture);
            }

            conditionObjects.Add(new Dictionary<string, object?>
            {
                ["ruleType"] = ruleType,
                ["value"] = value
            });
        }

        var conditions = new Dictionary<string, object?>
        {
            ["ruleConditions"] = conditionObjects,
            ["isAndRule"] = rule.MatchAllConditions
        };

        var actionObjects = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["actionType"] = 0,
                ["value"] = rule.CategoryName
            }
        };

        var transactionCode = GetTransactionCode(rule);

        if (transactionCode is null
            && rule.TransactionType is not RuleTransactionType.Expense
            && rule.TransactionType is not RuleTransactionType.Deposit)
        {
            error = $"The transaction type '{FormatTransactionType(rule.TransactionType)}' does not have a verified QuickBooks export mapping.";
            return null;
        }

        if (transactionCode is not null)
        {
            actionObjects.Add(new Dictionary<string, object?>
            {
                ["actionType"] = 7,
                ["value"] = transactionCode
            });
        }

        if (!string.IsNullOrWhiteSpace(rule.Payee))
        {
            actionObjects.Add(new Dictionary<string, object?>
            {
                ["actionType"] = 5,
                ["value"] = rule.Payee
            });
        }

        if (rule.AutoAdd)
        {
            actionObjects.Add(new Dictionary<string, object?>
            {
                ["actionType"] = 8,
                ["value"] = true
            });
        }

        var outputs = new Dictionary<string, object?>
        {
            ["ruleActions"] = actionObjects
        };

        return new ExportRow(
            rule.Name,
            JsonSerializer.Serialize(conditions),
            JsonSerializer.Serialize(outputs));
    }

    private static bool TryGetConditionRuleType(RuleCondition condition, out int ruleType)
    {
        ruleType = condition.Field switch
        {
            RuleMatchField.Description when condition.Operator == RuleMatchOperator.Contains => 1,
            RuleMatchField.BankText when condition.Operator == RuleMatchOperator.Contains => 6,
            RuleMatchField.BankText when condition.Operator == RuleMatchOperator.DoesNotContain => 8,
            RuleMatchField.Amount when condition.Operator == RuleMatchOperator.Equals => 2,
            RuleMatchField.Amount when condition.Operator == RuleMatchOperator.DoesNotEqual => 7,
            RuleMatchField.Amount when condition.Operator == RuleMatchOperator.GreaterThan => 3,
            RuleMatchField.Amount when condition.Operator == RuleMatchOperator.LessThan => 4,
            _ => -1
        };

        return ruleType >= 0;
    }

    private static string? GetTransactionCode(RuleBase rule) => rule.TransactionType switch
    {
        RuleTransactionType.Expense when rule.Direction == RuleDirection.MoneyOut => null,
        RuleTransactionType.Deposit when rule.Direction == RuleDirection.MoneyIn => null,
        RuleTransactionType.Check when rule.Direction == RuleDirection.MoneyOut => "3",
        RuleTransactionType.Transfer => "26",
        _ => null
    };

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(name) ? "QuickBooks" : name.Trim();
    }

    private static string FormatOperator(RuleMatchOperator op) => op switch
    {
        RuleMatchOperator.DoesNotContain => "Doesn't contain",
        RuleMatchOperator.DoesNotEqual => "Doesn't equal",
        RuleMatchOperator.StartsWith => "Starts with",
        RuleMatchOperator.EndsWith => "Ends with",
        RuleMatchOperator.GreaterThan => "Is greater than",
        RuleMatchOperator.LessThan => "Is less than",
        _ => op.ToString()
    };

    private static string FormatTransactionType(RuleTransactionType type) => type switch
    {
        RuleTransactionType.CreditCardPayment => "Credit card payment",
        _ => string.Concat(type.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString()))
    };

    private static QuickBooksExportResult Failed(string error) =>
        new(null, null, 0, new[] { error });

    private sealed record ExportRow(
        string Name,
        string ConditionsJson,
        string OutputsJson);
}
