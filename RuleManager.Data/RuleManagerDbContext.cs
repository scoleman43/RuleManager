using Microsoft.EntityFrameworkCore;
using RuleManager.Core.Domain;

namespace RuleManager.Data;

public sealed class RuleManagerDbContext(DbContextOptions<RuleManagerDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<ClientReference> ClientReferences => Set<ClientReference>();
    public DbSet<MasterRule> MasterRules => Set<MasterRule>();
    public DbSet<ClientRule> ClientRules => Set<ClientRule>();
    public DbSet<RuleSet> RuleSets => Set<RuleSet>();
    public DbSet<RuleSetRule> RuleSetRules => Set<RuleSetRule>();
    public DbSet<ClientRuleAssignment> ClientRuleAssignments => Set<ClientRuleAssignment>();
    public DbSet<ClientRuleSetAssignment> ClientRuleSetAssignments => Set<ClientRuleSetAssignment>();
    public DbSet<RuleOverride> RuleOverrides => Set<RuleOverride>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ExportBatch> ExportBatches => Set<ExportBatch>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Organization>().Property(x => x.Name).HasMaxLength(200).IsRequired();
        modelBuilder.Entity<Client>().Property(x => x.Name).HasMaxLength(200).IsRequired();
        modelBuilder.Entity<Category>().Property(x => x.Name).HasMaxLength(255).IsRequired();
        modelBuilder.Entity<ClientReference>().Property(x => x.Name).HasMaxLength(255).IsRequired();
        modelBuilder.Entity<ClientReference>().Property(x => x.QuickBooksAccountType).HasMaxLength(100);
        modelBuilder.Entity<ClientReference>().Property(x => x.QuickBooksDetailType).HasMaxLength(150);
        modelBuilder.Entity<MasterRule>().Property(x => x.Name).HasMaxLength(255).IsRequired();
        modelBuilder.Entity<ClientRule>().Property(x => x.Name).HasMaxLength(255).IsRequired();
        modelBuilder.Entity<RuleSet>().Property(x => x.Name).HasMaxLength(200).IsRequired();

        modelBuilder.Entity<Category>()
            .HasIndex(x => new { x.OrganizationId, x.Name })
            .IsUnique();

        modelBuilder.Entity<ClientReference>()
            .HasIndex(x => new { x.ClientId, x.Type, x.Name })
            .IsUnique();

        modelBuilder.Entity<RuleSetRule>()
            .HasIndex(x => new { x.RuleSetId, x.MasterRuleId })
            .IsUnique();

        modelBuilder.Entity<ClientRuleAssignment>()
            .HasIndex(x => new { x.ClientId, x.MasterRuleId })
            .IsUnique();

        modelBuilder.Entity<ClientRuleSetAssignment>()
            .HasIndex(x => new { x.ClientId, x.RuleSetId })
            .IsUnique();

        modelBuilder.Entity<MasterRule>().OwnsMany(x => x.Conditions, owned => owned.ToJson());
        modelBuilder.Entity<ClientRule>().OwnsMany(x => x.Conditions, owned => owned.ToJson());

        base.OnModelCreating(modelBuilder);
    }
}
