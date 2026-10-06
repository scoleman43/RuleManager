using System.Text.Json;
using RuleManager.Core.Domain;

namespace RuleManager.Data.Services;

public sealed record ClientRuleReferenceMapping(
    string? CategoryName,
    string? Payee);

public sealed record ClientRuleReferenceValidation(
    bool IsReady,
    bool CategoryReady,
    bool PayeeReady,
    string? EffectiveCategoryName,
    string? EffectivePayee,
    string Summary);

public static class ClientRuleReferenceService
{
    public static ClientRuleReferenceMapping ReadMapping(RuleOverride? ruleOverride)
    {
        if (string.IsNullOrWhiteSpace(ruleOverride?.OverrideJson))
            return new ClientRuleReferenceMapping(null, null);

        try
        {
            return JsonSerializer.Deserialize<ClientRuleReferenceMapping>(ruleOverride.OverrideJson)
                ?? new ClientRuleReferenceMapping(null, null);
        }
        catch (JsonException)
        {
            return new ClientRuleReferenceMapping(null, null);
        }
    }

    public static string WriteMapping(string? categoryName, string? payee) =>
        JsonSerializer.Serialize(new ClientRuleReferenceMapping(
            NullIfWhiteSpace(categoryName),
            NullIfWhiteSpace(payee)));

    public static ClientRuleReferenceValidation Validate(
        MasterRule rule,
        RuleOverride? ruleOverride,
        IReadOnlyCollection<ClientReference> references)
    {
        var mapping = ReadMapping(ruleOverride);
        var effectiveCategory = mapping.CategoryName ?? rule.CategoryName;
        var effectivePayee = mapping.Payee ?? rule.Payee;

        var categoryRequired = !string.IsNullOrWhiteSpace(effectiveCategory);
        var payeeRequired = !string.IsNullOrWhiteSpace(effectivePayee);

        var hasChartOfAccounts = references.Any(x =>
            x.IsActive
            && x.Source == ClientReferenceSource.ChartOfAccountsImport);

        var hasPayeeCatalog = references.Any(x =>
            x.IsActive
            && x.Type == ClientReferenceType.Payee
            && x.Source is ClientReferenceSource.VendorImport
                or ClientReferenceSource.Imported
                or ClientReferenceSource.ApiVerified
                or ClientReferenceSource.ManualOverride);

        var categoryReady = !categoryRequired
            || (hasChartOfAccounts && references.Any(x =>
                x.IsActive
                && x.Type == ClientReferenceType.Category
                && string.Equals(x.Name, effectiveCategory, StringComparison.OrdinalIgnoreCase)));

        var payeeReady = !payeeRequired
            || (hasPayeeCatalog && references.Any(x =>
                x.IsActive
                && x.Type == ClientReferenceType.Payee
                && string.Equals(x.Name, effectivePayee, StringComparison.OrdinalIgnoreCase)));

        var isReady = categoryReady && payeeReady;

        var issues = new List<string>();
        if (!categoryReady)
            issues.Add(hasChartOfAccounts ? "category not found" : "Chart of Accounts not imported");
        if (!payeeReady)
            issues.Add(hasPayeeCatalog ? "payee not found" : "Vendor/payee list not imported");

        return new ClientRuleReferenceValidation(
            isReady,
            categoryReady,
            payeeReady,
            effectiveCategory,
            effectivePayee,
            isReady ? "Ready" : $"Needs review: {string.Join(", ", issues)}");
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
