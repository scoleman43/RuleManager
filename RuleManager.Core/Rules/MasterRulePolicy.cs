using RuleManager.Core.Domain;

namespace RuleManager.Core.Rules;

public static class MasterRulePolicy
{
    private static readonly RuleTransactionType[] MoneyOutTypes =
    [
        RuleTransactionType.Expense,
        RuleTransactionType.Transfer,
        RuleTransactionType.Check
    ];

    private static readonly RuleTransactionType[] MoneyInTypes =
    [
        RuleTransactionType.Deposit,
        RuleTransactionType.Transfer
    ];

    public static IReadOnlyList<RuleTransactionType> GetAllowedTransactionTypes(RuleDirection direction) =>
        direction == RuleDirection.MoneyOut ? MoneyOutTypes : MoneyInTypes;

    public static IReadOnlyList<RuleMatchOperator> GetAllowedOperators(RuleMatchField field) =>
        field == RuleMatchField.Amount
            ? [RuleMatchOperator.Equals, RuleMatchOperator.DoesNotEqual, RuleMatchOperator.GreaterThan, RuleMatchOperator.LessThan]
            : [RuleMatchOperator.Contains, RuleMatchOperator.DoesNotContain, RuleMatchOperator.Equals, RuleMatchOperator.DoesNotEqual, RuleMatchOperator.StartsWith, RuleMatchOperator.EndsWith];

    public static bool CanCreate(RuleDirection direction, RuleTransactionType transactionType) =>
        GetAllowedTransactionTypes(direction).Contains(transactionType)
        && transactionType != RuleTransactionType.CreditCardPayment;
}
