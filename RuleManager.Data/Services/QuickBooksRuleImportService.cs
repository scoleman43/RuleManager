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
    ClientSpecific,
    Unsupported
}

public enum ChangedRuleImportAction
{
    UpdateExisting,
    CreateNew
}

public sealed class RuleImportPreviewItem
{
    public int RowNumber { get; init; }
    public string Name { get; init; } = string.Empty;
    public RuleDirection Direction { get; init; }
    public RuleTransactionType TransactionType { get; init; }
    public List<RuleCondition> Conditions { get; init; } = new();
    public string? CategoryName { get; init; }
    public string? Payee { get; init; }
    public bool AutoAdd { get; init; }
    public bool MatchAllConditions { get; init; } = true;
    public string? OriginalConditionsJson { get; init; }
    public string? OriginalOutputsJson { get; init; }
    public bool IsAccountSpecific { get; init; }
    public bool IsReadOnlyImport { get; init; }
    public bool IsSplitRule { get; init; }
    public string? UnsupportedReason { get; init; }
    public RuleImportStatus Status { get; set; }
    public Guid? ExistingRuleId { get; set; }
    public ChangedRuleImportAction ChangedAction { get; set; } = ChangedRuleImportAction.UpdateExisting;
    public string NewRuleName { get; set; } = string.Empty;
    public bool Selected { get; set; }
    public string? ClientComparison { get; set; }

    public string EffectiveName =>
        Status == RuleImportStatus.Changed
        && ChangedAction == ChangedRuleImportAction.CreateNew
        && !string.IsNullOrWhiteSpace(NewRuleName)
            ? NewRuleName.Trim()
            : Name;
}

public sealed record RuleImportApplyResult(
    int Added,
    int Updated,
    int ClientSpecificAdded,
    int ClientSpecificUpdated);

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

            if (item.IsAccountSpecific)
            {
                item.Status = RuleImportStatus.ClientSpecific;
                item.Selected = true;
                continue;
            }

            if (!existingByName.TryGetValue(item.Name, out var existing))
            {
                item.Status = RuleImportStatus.New;
                item.Selected = true;
                continue;
            }

            item.ExistingRuleId = existing.Id;

            if (existing.IsActive && Equivalent(existing, item))
            {
                item.Status = RuleImportStatus.Unchanged;
                item.Selected = false;
            }
            else
            {
                item.Status = RuleImportStatus.Changed;
                item.ChangedAction = ChangedRuleImportAction.UpdateExisting;
                item.NewRuleName = string.Empty;
                item.Selected = true;
            }
        }

        return parsed;
    }

    public async Task<RuleImportApplyResult> ApplyAsync(
        Guid organizationId,
        IEnumerable<RuleImportPreviewItem> items,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        var allItems = items.ToList();

        var selectedLibraryRules = allItems
            .Where(x => x.Selected && x.Status is RuleImportStatus.New or RuleImportStatus.Changed)
            .ToList();

        var selectedClientSpecificRules = allItems
            .Where(x => x.Selected && x.Status == RuleImportStatus.ClientSpecific)
            .ToList();

        if (selectedClientSpecificRules.Count > 0 && !clientId.HasValue)
            throw new InvalidOperationException("Select a client before importing account-specific rules.");

        if (selectedLibraryRules.Count == 0 && selectedClientSpecificRules.Count == 0)
            return new RuleImportApplyResult(0, 0, 0, 0);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var createAsNewItems = selectedLibraryRules
            .Where(x => x.Status == RuleImportStatus.Changed
                && x.ChangedAction == ChangedRuleImportAction.CreateNew)
            .ToList();

        var duplicateNewNames = createAsNewItems
            .Where(x => !string.IsNullOrWhiteSpace(x.NewRuleName))
            .GroupBy(x => x.NewRuleName.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicateNewNames is not null)
            throw new InvalidOperationException($"More than one changed rule is being created as '{duplicateNewNames.Key}'. Use a unique name for each new reusable rule.");

        foreach (var item in createAsNewItems)
        {
            if (string.IsNullOrWhiteSpace(item.NewRuleName))
                throw new InvalidOperationException($"Enter a new rule name for changed rule '{item.Name}'.");

            var newName = item.NewRuleName.Trim();
            var normalizedNewName = newName.ToUpperInvariant();

            var nameExists = await db.MasterRules.AnyAsync(
                x => x.OrganizationId == organizationId
                    && x.Name.ToUpper() == normalizedNewName,
                cancellationToken);

            if (nameExists)
                throw new InvalidOperationException($"A reusable rule named '{newName}' already exists. Choose a different name.");
        }

        var added = 0;
        var updated = 0;
        var clientSpecificAdded = 0;
        var clientSpecificUpdated = 0;

        var importedCategoryNames = selectedLibraryRules
            .Select(x => x.CategoryName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var categories = await db.Categories
            .Where(x => x.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);

        foreach (var categoryName in importedCategoryNames)
        {
            if (categories.Any(x => string.Equals(x.Name, categoryName, StringComparison.OrdinalIgnoreCase)))
                continue;

            var category = new Category
            {
                OrganizationId = organizationId,
                Name = categoryName
            };
            db.Categories.Add(category);
            categories.Add(category);
        }

        foreach (var item in selectedLibraryRules)
        {
            MasterRule entity;

            var createAsNew = item.Status == RuleImportStatus.Changed
                && item.ChangedAction == ChangedRuleImportAction.CreateNew;

            if (item.ExistingRuleId.HasValue && !createAsNew)
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

            entity.Name = createAsNew ? item.NewRuleName.Trim() : item.Name;
            entity.Direction = item.Direction;
            entity.TransactionType = item.TransactionType;
            entity.CategoryName = item.CategoryName;
            entity.CategoryId = categories
                .FirstOrDefault(x => string.Equals(x.Name, item.CategoryName, StringComparison.OrdinalIgnoreCase))
                ?.Id;
            entity.Payee = item.Payee;
            entity.MatchAllConditions = item.MatchAllConditions;
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

        if (clientId.HasValue)
        {
            var assignmentMax = await db.ClientRuleAssignments
                .Where(x => x.ClientId == clientId.Value)
                .Select(x => (int?)x.ExportPriority)
                .MaxAsync(cancellationToken) ?? 0;

            var clientRuleMax = await db.ClientRules
                .Where(x => x.ClientId == clientId.Value)
                .Select(x => (int?)x.ExportPriority)
                .MaxAsync(cancellationToken) ?? 0;

            var nextPriority = Math.Max(assignmentMax, clientRuleMax) + 1;

            foreach (var item in selectedClientSpecificRules)
            {
                var entity = await db.ClientRules
                    .SingleOrDefaultAsync(
                        x => x.ClientId == clientId.Value && x.Name == item.Name,
                        cancellationToken);

                if (entity is null)
                {
                    entity = new ClientRule
                    {
                        ClientId = clientId.Value,
                        SourceImportKey = item.Name,
                        ExportPriority = nextPriority++
                    };
                    db.ClientRules.Add(entity);
                    clientSpecificAdded++;
                }
                else
                {
                    clientSpecificUpdated++;
                }

                entity.Name = item.Name;
                entity.Direction = item.Direction;
                entity.TransactionType = item.TransactionType;
                entity.CategoryName = item.CategoryName;
                entity.CategoryId = null;
                entity.Payee = item.Payee;
                entity.MatchAllConditions = item.MatchAllConditions;
                entity.AutoAdd = item.AutoAdd;
                entity.Conditions = item.Conditions.Select(x => new RuleCondition
                {
                    Field = x.Field,
                    Operator = x.Operator,
                    Value = x.Value
                }).ToList();
                entity.OriginalConditionsJson = item.OriginalConditionsJson;
                entity.OriginalOutputsJson = item.OriginalOutputsJson;
                entity.IsAccountSpecific = true;
                entity.IsReadOnlyImport = true;
                entity.ModifiedUtc = DateTime.UtcNow;
            }
        }

        if (clientId.HasValue)
        {
            await DiscoverClientReferencesAsync(
                db,
                clientId.Value,
                allItems.Where(x => x.Status is not RuleImportStatus.Unsupported and not RuleImportStatus.Duplicate),
                cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new RuleImportApplyResult(
            added,
            updated,
            clientSpecificAdded,
            clientSpecificUpdated);
    }

    private static async Task DiscoverClientReferencesAsync(
        RuleManagerDbContext db,
        Guid clientId,
        IEnumerable<RuleImportPreviewItem> items,
        CancellationToken cancellationToken)
    {
        var discovered = new HashSet<(ClientReferenceType Type, string Name)>();

        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.CategoryName))
            {
                var type = item.TransactionType is RuleTransactionType.Transfer or RuleTransactionType.CreditCardPayment
                    ? ClientReferenceType.Account
                    : ClientReferenceType.Category;

                discovered.Add((type, item.CategoryName.Trim()));
            }

            if (!string.IsNullOrWhiteSpace(item.Payee))
                discovered.Add((ClientReferenceType.Payee, item.Payee.Trim()));

            if (item.IsSplitRule
                && QuickBooksSplitRuleCodec.TryParse(item.OriginalOutputsJson, out var split)
                && split is not null)
            {
                foreach (var line in split.Lines)
                {
                    if (!string.IsNullOrWhiteSpace(line.CategoryName))
                        discovered.Add((ClientReferenceType.Category, line.CategoryName.Trim()));
                }
            }
        }

        if (discovered.Count == 0)
            return;

        var existing = await db.ClientReferences
            .Where(x => x.ClientId == clientId)
            .ToListAsync(cancellationToken);

        foreach (var reference in discovered)
        {
            var match = existing.FirstOrDefault(x =>
                x.Type == reference.Type
                && string.Equals(x.Name, reference.Name, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                match = new ClientReference
                {
                    ClientId = clientId,
                    Type = reference.Type,
                    Name = reference.Name,
                    Source = ClientReferenceSource.Imported,
                    IsActive = true
                };
                db.ClientReferences.Add(match);
                existing.Add(match);
            }
            else
            {
                match.IsActive = true;
                if (match.Source is ClientReferenceSource.Imported or ClientReferenceSource.ManualOverride)
                    match.Source = ClientReferenceSource.Imported;
                match.ModifiedUtc = DateTime.UtcNow;
            }
        }
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
        var matchAllConditions = true;
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
                    if (TryGetProperty(obj, "isAndRule", out var isAndRuleElement)
                        && isAndRuleElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        matchAllConditions = isAndRuleElement.GetBoolean();
                    }
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
                            AddCondition(conditions, RuleMatchField.Description, RuleMatchOperator.Contains, value);
                            break;

                        case 6:
                            AddCondition(conditions, RuleMatchField.BankText, RuleMatchOperator.Contains, value);
                            break;

                        case 8:
                            AddCondition(conditions, RuleMatchField.BankText, RuleMatchOperator.DoesNotContain, value);
                            break;

                        case 13:
                            AddCondition(conditions, RuleMatchField.BankText, RuleMatchOperator.Equals, value);
                            break;

                        case 2:
                            AddCondition(conditions, RuleMatchField.Amount, RuleMatchOperator.Equals, NormalizeAmount(value));
                            break;

                        case 7:
                            AddCondition(conditions, RuleMatchField.Amount, RuleMatchOperator.DoesNotEqual, NormalizeAmount(value));
                            break;

                        case 3:
                            AddCondition(conditions, RuleMatchField.Amount, RuleMatchOperator.GreaterThan, NormalizeAmount(value));
                            break;

                        case 4:
                            AddCondition(conditions, RuleMatchField.Amount, RuleMatchOperator.LessThan, NormalizeAmount(value));
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
        string? payee = null;
        var autoAdd = false;
        var hasSplitOutput = false;
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

                        case 5:
                            payee = GetValueAsString(obj, "value");
                            break;

                        case 6:
                            // Verified QBO split output. Preserve the original output JSON exactly
                            // until split editing is modeled in the RuleManager UI.
                            hasSplitOutput = true;
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
        var isAccountSpecific = transactionCode == 64;

        return new RuleImportPreviewItem
        {
            RowNumber = rowNumber,
            Name = name,
            Direction = direction,
            TransactionType = transactionType,
            Conditions = conditions,
            CategoryName = NullIfWhiteSpace(categoryName),
            Payee = NullIfWhiteSpace(payee),
            AutoAdd = autoAdd,
            MatchAllConditions = matchAllConditions,
            OriginalConditionsJson = NullIfWhiteSpace(conditionsJson),
            OriginalOutputsJson = NullIfWhiteSpace(outputsJson),
            IsAccountSpecific = isAccountSpecific,
            IsReadOnlyImport = isAccountSpecific || hasSplitOutput || reasons.Count > 0,
            IsSplitRule = hasSplitOutput,
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
            || existing.MatchAllConditions != imported.MatchAllConditions
            || !string.Equals(existing.CategoryName ?? string.Empty, imported.CategoryName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(existing.Payee ?? string.Empty, imported.Payee ?? string.Empty, StringComparison.OrdinalIgnoreCase))
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

    private static void AddCondition(
        ICollection<RuleCondition> conditions,
        RuleMatchField field,
        RuleMatchOperator op,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        conditions.Add(new RuleCondition
        {
            Field = field,
            Operator = op,
            Value = value
        });
    }

    private static string? NormalizeAmount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var normalized = value.Trim();
        return normalized.StartsWith("-", StringComparison.Ordinal)
            ? normalized[1..]
            : normalized;
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
