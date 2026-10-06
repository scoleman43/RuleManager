using RuleManager.Core.Domain;
using RuleManager.Core.Rules;

namespace RuleManager.Tests;

public class RuleImportSnapshotTests
{
    [Fact]
    public void Snapshot_DoesNotMarkUnchangedRuleAsModified()
    {
        var rule = CreateRule();
        rule.ImportedBaselineJson = RuleImportSnapshot.Serialize(rule);

        Assert.True(RuleImportSnapshot.MatchesBaseline(rule));
    }

    [Fact]
    public void Snapshot_DetectsChanges_AndRecognizesRevert()
    {
        var rule = CreateRule();
        rule.ImportedBaselineJson = RuleImportSnapshot.Serialize(rule);

        rule.Payee = "Different Vendor";
        Assert.False(RuleImportSnapshot.MatchesBaseline(rule));

        rule.Payee = "Staples";
        Assert.True(RuleImportSnapshot.MatchesBaseline(rule));
    }

    private static MasterRule CreateRule() => new()
    {
        Name = "Imported rule",
        Direction = RuleDirection.MoneyOut,
        TransactionType = RuleTransactionType.Expense,
        CategoryName = "Office Supplies",
        Payee = "Staples",
        MatchAllConditions = true,
        AutoAdd = true,
        Conditions = new()
        {
            new RuleCondition
            {
                Field = RuleMatchField.BankText,
                Operator = RuleMatchOperator.Contains,
                Value = "STAPLES"
            }
        }
    };
}
