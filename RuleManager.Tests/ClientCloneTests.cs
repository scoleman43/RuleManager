using System.Text;
using Microsoft.EntityFrameworkCore;
using RuleManager.Core.Domain;
using RuleManager.Data;
using RuleManager.Data.Services;

namespace RuleManager.Tests;

public class ClientCloneTests
{
    [Fact]
    public async Task Clone_CopiesRulesMappingsAndReferences_AndResetsImportProvenance()
    {
        var factory = new TestDbContextFactory(nameof(Clone_CopiesRulesMappingsAndReferences_AndResetsImportProvenance));
        var organizationId = Guid.NewGuid();
        var sourceClientId = Guid.NewGuid();
        var masterRuleId = Guid.NewGuid();

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Organizations.Add(new Organization { Id = organizationId, Name = "Firm" });
            db.Clients.Add(new Client
            {
                Id = sourceClientId,
                OrganizationId = organizationId,
                Name = "Source Client"
            });

            db.MasterRules.Add(new MasterRule
            {
                Id = masterRuleId,
                OrganizationId = organizationId,
                Name = "Office Supplies",
                Direction = RuleDirection.MoneyOut,
                TransactionType = RuleTransactionType.Expense,
                CategoryName = "Office Supplies",
                Conditions = new()
                {
                    new RuleCondition
                    {
                        Field = RuleMatchField.BankText,
                        Operator = RuleMatchOperator.Contains,
                        Value = "STAPLES"
                    }
                }
            });

            var assignment = new ClientRuleAssignment
            {
                ClientId = sourceClientId,
                MasterRuleId = masterRuleId,
                IsExplicit = true,
                ExportPriority = 2
            };
            db.ClientRuleAssignments.Add(assignment);
            db.RuleOverrides.Add(new RuleOverride
            {
                ClientRuleAssignmentId = assignment.Id,
                Reason = "Client QuickBooks reference mapping",
                OverrideJson = ClientRuleReferenceService.WriteMapping("Office Expense", "Staples Inc.")
            });

            db.ClientRules.Add(new ClientRule
            {
                ClientId = sourceClientId,
                Name = "Card Payment",
                Direction = RuleDirection.MoneyOut,
                TransactionType = RuleTransactionType.CreditCardPayment,
                CategoryName = "Visa",
                ExportPriority = 1,
                IsAccountSpecific = true,
                ImportedQuickBooksRuleName = "Card Payment",
                ImportedBaselineJson = "{}",
                IsModifiedSinceImport = true,
                Conditions = new()
                {
                    new RuleCondition
                    {
                        Field = RuleMatchField.BankText,
                        Operator = RuleMatchOperator.Contains,
                        Value = "PAYMENT"
                    }
                }
            });

            db.ClientReferences.AddRange(
                new ClientReference
                {
                    ClientId = sourceClientId,
                    Type = ClientReferenceType.Account,
                    Name = "Checking",
                    QuickBooksAccountNumber = "112720",
                    QuickBooksAccountType = "Bank",
                    QuickBooksDetailType = "Checking",
                    Source = ClientReferenceSource.ChartOfAccountsImport
                },
                new ClientReference
                {
                    ClientId = sourceClientId,
                    Type = ClientReferenceType.Category,
                    Name = "Office Expense",
                    QuickBooksAccountNumber = "6000",
                    QuickBooksAccountType = "Expenses",
                    QuickBooksDetailType = "Office/General Administrative Expenses",
                    Source = ClientReferenceSource.ChartOfAccountsImport
                });

            await db.SaveChangesAsync();
        }

        var service = new ClientCloneService(factory);
        var result = await service.CloneAsync(
            sourceClientId,
            new CloneClientRequest("New Client", "New QBO", null, true));

        await using var verify = await factory.CreateDbContextAsync();

        var clone = await verify.Clients.SingleAsync(x => x.Id == result.ClientId);
        Assert.Equal("New Client", clone.Name);

        var clonedAssignment = await verify.ClientRuleAssignments
            .SingleAsync(x => x.ClientId == clone.Id && x.MasterRuleId == masterRuleId);
        Assert.True(clonedAssignment.IsExplicit);
        Assert.Equal(2, clonedAssignment.ExportPriority);

        var clonedOverride = await verify.RuleOverrides
            .SingleAsync(x => x.ClientRuleAssignmentId == clonedAssignment.Id);
        Assert.Contains("Office Expense", clonedOverride.OverrideJson);

        var clonedClientRule = await verify.ClientRules.SingleAsync(x => x.ClientId == clone.Id);
        Assert.Null(clonedClientRule.ImportedQuickBooksRuleName);
        Assert.Null(clonedClientRule.ImportedBaselineJson);
        Assert.False(clonedClientRule.IsModifiedSinceImport);

        var references = await verify.ClientReferences
            .Where(x => x.ClientId == clone.Id)
            .ToListAsync();
        Assert.Equal(2, references.Count);
        Assert.Contains(references, x => x.QuickBooksAccountNumber == "112720");
    }

    [Fact]
    public async Task ChartExport_CreatesQboImportColumnsAndPreservesAccountNumber()
    {
        var factory = new TestDbContextFactory(nameof(ChartExport_CreatesQboImportColumnsAndPreservesAccountNumber));
        var organizationId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Organizations.Add(new Organization { Id = organizationId, Name = "Firm" });
            db.Clients.Add(new Client
            {
                Id = clientId,
                OrganizationId = organizationId,
                Name = "Source Client"
            });
            db.ClientReferences.Add(new ClientReference
            {
                ClientId = clientId,
                Type = ClientReferenceType.Account,
                Name = "Checking Account - Bank of America",
                QuickBooksAccountNumber = "112720",
                QuickBooksAccountType = "Bank",
                QuickBooksDetailType = "Checking",
                Source = ClientReferenceSource.ChartOfAccountsImport
            });
            await db.SaveChangesAsync();
        }

        var service = new QuickBooksChartOfAccountsExportService(factory);
        var result = await service.GenerateAsync(clientId, "New Client");

        var csv = Encoding.UTF8.GetString(result.Content);
        Assert.Contains("Account Number,Account Name,Type,Detail Type", csv);
        Assert.Contains("112720,Checking Account - Bank of America,Bank,Checking", csv);
        Assert.Equal("New Client_Chart_of_Accounts_QBO_Import.csv", result.FileName);
    }

    private sealed class TestDbContextFactory(string databaseName) : IDbContextFactory<RuleManagerDbContext>
    {
        private readonly DbContextOptions<RuleManagerDbContext> options =
            new DbContextOptionsBuilder<RuleManagerDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options;

        public RuleManagerDbContext CreateDbContext() => new(options);

        public Task<RuleManagerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
