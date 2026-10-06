using System.Text.Json;
using RuleManager.Core.Domain;

namespace RuleManager.Core.Rules;

public static class RuleImportSnapshot
{
    public static string Serialize(RuleBase rule) =>
        JsonSerializer.Serialize(new Snapshot(
            rule.Name,
            rule.Direction,
            rule.MatchAllConditions,
            rule.Conditions.Select(x => new ConditionSnapshot(x.Field, x.Operator, x.Value)).ToList(),
            rule.TransactionType,
            rule.CategoryName,
            rule.Payee,
            rule.AutoAdd));

    public static bool MatchesBaseline(RuleBase rule)
    {
        if (string.IsNullOrWhiteSpace(rule.ImportedBaselineJson))
            return !rule.IsModifiedSinceImport;

        return string.Equals(
            Serialize(rule),
            rule.ImportedBaselineJson,
            StringComparison.Ordinal);
    }

    private sealed record Snapshot(
        string Name,
        RuleDirection Direction,
        bool MatchAllConditions,
        IReadOnlyList<ConditionSnapshot> Conditions,
        RuleTransactionType TransactionType,
        string? CategoryName,
        string? Payee,
        bool AutoAdd);

    private sealed record ConditionSnapshot(
        RuleMatchField Field,
        RuleMatchOperator Operator,
        string Value);
}
