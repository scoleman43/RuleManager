namespace RuleManager.Core.Domain;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class Organization : Entity
{
    public string Name { get; set; } = string.Empty;
    public ICollection<Client> Clients { get; set; } = new List<Client>();
    public ICollection<Category> Categories { get; set; } = new List<Category>();
    public ICollection<MasterRule> MasterRules { get; set; } = new List<MasterRule>();
    public ICollection<RuleSet> RuleSets { get; set; } = new List<RuleSet>();
}

public sealed class Client : Entity
{
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? QuickBooksCompanyName { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public ICollection<ClientRule> Rules { get; set; } = new List<ClientRule>();
}

public sealed class Category : Entity
{
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    public string MainCategory => Name.Split(':', 2)[0].Trim();
    public string? SubCategory
    {
        get
        {
            var parts = Name.Split(':', 2);
            return parts.Length == 2 ? parts[1].Trim() : null;
        }
    }
}

public sealed class ClientReference : Entity
{
    public Guid ClientId { get; set; }
    public ClientReferenceType Type { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? QuickBooksAccountNumber { get; set; }
    public string? QuickBooksAccountType { get; set; }
    public string? QuickBooksDetailType { get; set; }
    public ClientReferenceSource Source { get; set; } = ClientReferenceSource.Imported;
    public bool IsActive { get; set; } = true;
}

public sealed class RuleCondition
{
    public RuleMatchField Field { get; set; }
    public RuleMatchOperator Operator { get; set; }
    public string Value { get; set; } = string.Empty;
}

public abstract class RuleBase : Entity
{
    public string Name { get; set; } = string.Empty;
    public RuleDirection Direction { get; set; }
    public bool MatchAllConditions { get; set; } = true;
    public List<RuleCondition> Conditions { get; set; } = new();
    public RuleTransactionType TransactionType { get; set; }
    public Guid? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? Payee { get; set; }
    public bool AutoAdd { get; set; }
    public string? OriginalConditionsJson { get; set; }
    public string? OriginalOutputsJson { get; set; }
    public string? ImportedQuickBooksRuleName { get; set; }
    public string? ImportedBaselineJson { get; set; }
    public bool IsModifiedSinceImport { get; set; }
    public bool IsAccountSpecific { get; set; }
    public bool IsReadOnlyImport { get; set; }
}

public sealed class MasterRule : RuleBase
{
    public Guid OrganizationId { get; set; }
    public bool IsActive { get; set; } = true;
    public ICollection<ClientRuleAssignment> ClientAssignments { get; set; } = new List<ClientRuleAssignment>();
}

public sealed class ClientRule : RuleBase
{
    public Guid ClientId { get; set; }
    public Guid? SourceMasterRuleId { get; set; }
    public string? SourceImportKey { get; set; }
    public int ExportPriority { get; set; }
}

public sealed class RuleSet : Entity
{
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public ICollection<RuleSetRule> Rules { get; set; } = new List<RuleSetRule>();
}

public sealed class RuleSetRule : Entity
{
    public Guid RuleSetId { get; set; }
    public Guid MasterRuleId { get; set; }
    public int SortOrder { get; set; }
}

public sealed class ClientRuleAssignment : Entity
{
    public Guid ClientId { get; set; }
    public Guid MasterRuleId { get; set; }
    public Guid? ClientRuleId { get; set; }
    public RuleAssignmentStatus Status { get; set; } = RuleAssignmentStatus.Inherited;
    public bool IsExplicit { get; set; }
    public int ExportPriority { get; set; }
    public RuleOverride? Override { get; set; }
}

public sealed class ClientRuleSetAssignment : Entity
{
    public Guid ClientId { get; set; }
    public Guid RuleSetId { get; set; }
}

public sealed class RuleOverride : Entity
{
    public Guid ClientRuleAssignmentId { get; set; }
    public string? Reason { get; set; }
    public string? OverrideJson { get; set; }
}

public sealed class ImportBatch : Entity
{
    public Guid OrganizationId { get; set; }
    public Guid ClientId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public BatchStatus Status { get; set; } = BatchStatus.Pending;
    public int RuleCount { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class ExportBatch : Entity
{
    public Guid OrganizationId { get; set; }
    public Guid ClientId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public BatchStatus Status { get; set; } = BatchStatus.Pending;
    public int RuleCount { get; set; }
}

public sealed class AuditEntry : Entity
{
    public Guid OrganizationId { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string? DetailsJson { get; set; }
}
