using RuleManager.Core.Domain;
using RuleManager.Core.Rules;

namespace RuleManager.Tests;

public class MasterRulePolicyTests
{
    [Fact]
    public void MoneyOut_AllowsExpenseTransferCheckAndCreditCardPayment()
    {
        var allowed = MasterRulePolicy.GetAllowedTransactionTypes(RuleDirection.MoneyOut);

        Assert.Contains(RuleTransactionType.Expense, allowed);
        Assert.Contains(RuleTransactionType.Transfer, allowed);
        Assert.Contains(RuleTransactionType.Check, allowed);
        Assert.Contains(RuleTransactionType.CreditCardPayment, allowed);
        Assert.DoesNotContain(RuleTransactionType.Deposit, allowed);
        Assert.True(MasterRulePolicy.CanCreate(
            RuleDirection.MoneyOut,
            RuleTransactionType.CreditCardPayment));
    }

    [Fact]
    public void MoneyIn_AllowsDepositAndTransfer()
    {
        var allowed = MasterRulePolicy.GetAllowedTransactionTypes(RuleDirection.MoneyIn);

        Assert.Contains(RuleTransactionType.Deposit, allowed);
        Assert.Contains(RuleTransactionType.Transfer, allowed);
        Assert.DoesNotContain(RuleTransactionType.Expense, allowed);
        Assert.DoesNotContain(RuleTransactionType.Check, allowed);
        Assert.DoesNotContain(RuleTransactionType.CreditCardPayment, allowed);
    }

    [Fact]
    public void Amount_UsesNumericOperators()
    {
        var allowed = MasterRulePolicy.GetAllowedOperators(RuleMatchField.Amount);

        Assert.Contains(RuleMatchOperator.GreaterThan, allowed);
        Assert.Contains(RuleMatchOperator.LessThan, allowed);
        Assert.DoesNotContain(RuleMatchOperator.Contains, allowed);
    }
}
