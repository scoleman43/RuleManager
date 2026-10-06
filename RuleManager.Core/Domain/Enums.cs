namespace RuleManager.Core.Domain;

public enum RuleDirection
{
    MoneyOut = -1,
    MoneyIn = 1
}

public enum RuleMatchField
{
    Description,
    BankText,
    Amount
}

public enum RuleMatchOperator
{
    Contains,
    DoesNotContain,
    Equals,
    DoesNotEqual,
    StartsWith,
    EndsWith,
    GreaterThan,
    LessThan
}

public enum RuleTransactionType
{
    Expense,
    Deposit,
    Transfer,
    Check,
    CreditCardPayment
}

public enum RuleAssignmentStatus
{
    Inherited,
    Overridden,
    Missing,
    Conflict,
    Unmanaged
}

public enum BatchStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}


public enum ClientReferenceType
{
    Category,
    Payee,
    Account
}

public enum ClientReferenceSource
{
    Imported,
    ChartOfAccountsImport,
    VendorImport,
    ManualOverride,
    ApiVerified
}
