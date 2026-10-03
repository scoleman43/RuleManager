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

        var assignedRuleIds = await db.ClientRuleAssignments
            .Where(x => x.ClientId == clientId)
            .Select(x => x.MasterRuleId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var rules = await db.MasterRules
            .Where(x => assignedRuleIds.Contains(x.Id) && x.IsActive)
            .OrderBy(x => x.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (rules.Count == 0)
            return Failed("This client does not have any active rules assigned.");

        var exportRows = new List<ExportRow>();
        var errors = new List<string>();

        foreach (var rule in rules)
        {
            var row = BuildRow(rule, out var error);

            if (row is null)
            {
                errors.Add($"{rule.Name}: {error}");
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

    private static ExportRow? BuildRow(MasterRule rule, out string? error)
    {
        error = null;

        if (rule.IsReadOnlyImport)
        {
            if (!string.IsNullOrWhiteSpace(rule.OriginalConditionsJson)
                && !string.IsNullOrWhiteSpace(rule.OriginalOutputsJson))
            {
                return new ExportRow(
                    rule.Name,
                    rule.OriginalConditionsJson,
                    rule.OriginalOutputsJson);
            }

            error = "This imported read-only rule does not have its original QuickBooks JSON.";
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
            if (condition.Operator != RuleMatchOperator.Contains)
            {
                error = $"The operator '{FormatOperator(condition.Operator)}' does not yet have a verified QuickBooks export mapping.";
                return null;
            }

            var ruleType = condition.Field switch
            {
                RuleMatchField.Description => 1,
                RuleMatchField.BankText => 6,
                RuleMatchField.Amount => (int?)null,
                _ => null
            };

            if (!ruleType.HasValue)
            {
                error = $"The condition field '{condition.Field}' does not yet have a verified QuickBooks export mapping.";
                return null;
            }

            conditionObjects.Add(new Dictionary<string, object?>
            {
                ["ruleType"] = ruleType.Value,
                ["value"] = condition.Value
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

    private static string? GetTransactionCode(MasterRule rule) => rule.TransactionType switch
    {
        RuleTransactionType.Expense when rule.Direction == RuleDirection.MoneyOut => null,
        RuleTransactionType.Deposit when rule.Direction == RuleDirection.MoneyIn => null,
        RuleTransactionType.Check when rule.Direction == RuleDirection.MoneyOut => "3",
        RuleTransactionType.Transfer => "26",
        RuleTransactionType.CreditCardPayment when rule.Direction == RuleDirection.MoneyOut => "64",
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
