using Microsoft.EntityFrameworkCore;
using RuleManager.Core.Domain;

namespace RuleManager.Data.Services;

public sealed record CloneClientRequest(
    string Name,
    string? QuickBooksCompanyName,
    string? Notes,
    bool CopyQuickBooksReferences);

public sealed record CloneClientResult(
    Guid ClientId,
    int RuleSetCount,
    int ReusableRuleCount,
    int ClientSpecificRuleCount,
    int ReferenceCount);

public sealed class ClientCloneService(
    IDbContextFactory<RuleManagerDbContext> dbFactory)
{
    public async Task<CloneClientResult> CloneAsync(
        Guid sourceClientId,
        CloneClientRequest request,
        CancellationToken cancellationToken = default)
    {
        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Client name is required.", nameof(request));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var source = await db.Clients
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == sourceClientId, cancellationToken)
            ?? throw new InvalidOperationException("The source client no longer exists.");

        var duplicateName = await db.Clients.AnyAsync(x =>
            x.OrganizationId == source.OrganizationId
            && x.Name.ToLower() == name.ToLower(),
            cancellationToken);

        if (duplicateName)
            throw new InvalidOperationException($"A client named '{name}' already exists.");

        var clone = new Client
        {
            OrganizationId = source.OrganizationId,
            Name = name,
            QuickBooksCompanyName = NullIfWhiteSpace(request.QuickBooksCompanyName),
            Notes = NullIfWhiteSpace(request.Notes),
            IsActive = true
        };
        db.Clients.Add(clone);

        var sourceRuleSets = await db.ClientRuleSetAssignments
            .Where(x => x.ClientId == sourceClientId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        foreach (var sourceRuleSet in sourceRuleSets)
        {
            db.ClientRuleSetAssignments.Add(new ClientRuleSetAssignment
            {
                ClientId = clone.Id,
                RuleSetId = sourceRuleSet.RuleSetId
            });
        }

        var sourceClientRules = await db.ClientRules
            .Where(x => x.ClientId == sourceClientId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var clientRuleIdMap = new Dictionary<Guid, Guid>();
        foreach (var sourceRule in sourceClientRules)
        {
            var clonedRule = new ClientRule
            {
                ClientId = clone.Id,
                SourceMasterRuleId = sourceRule.SourceMasterRuleId,
                SourceImportKey = null,
                ExportPriority = sourceRule.ExportPriority,
                Name = sourceRule.Name,
                Direction = sourceRule.Direction,
                MatchAllConditions = sourceRule.MatchAllConditions,
                Conditions = sourceRule.Conditions.Select(x => new RuleCondition
                {
                    Field = x.Field,
                    Operator = x.Operator,
                    Value = x.Value
                }).ToList(),
                TransactionType = sourceRule.TransactionType,
                CategoryId = sourceRule.CategoryId,
                CategoryName = sourceRule.CategoryName,
                Payee = sourceRule.Payee,
                AutoAdd = sourceRule.AutoAdd,
                OriginalConditionsJson = sourceRule.OriginalConditionsJson,
                OriginalOutputsJson = sourceRule.OriginalOutputsJson,
                ImportedQuickBooksRuleName = null,
                ImportedBaselineJson = null,
                IsModifiedSinceImport = false,
                IsAccountSpecific = sourceRule.IsAccountSpecific,
                IsReadOnlyImport = sourceRule.IsReadOnlyImport
            };

            db.ClientRules.Add(clonedRule);
            clientRuleIdMap[sourceRule.Id] = clonedRule.Id;
        }

        var sourceAssignments = await db.ClientRuleAssignments
            .Where(x => x.ClientId == sourceClientId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var sourceAssignmentIds = sourceAssignments.Select(x => x.Id).ToArray();
        var sourceOverrides = await db.RuleOverrides
            .Where(x => sourceAssignmentIds.Contains(x.ClientRuleAssignmentId))
            .AsNoTracking()
            .ToDictionaryAsync(x => x.ClientRuleAssignmentId, cancellationToken);

        foreach (var sourceAssignment in sourceAssignments)
        {
            var clonedAssignment = new ClientRuleAssignment
            {
                ClientId = clone.Id,
                MasterRuleId = sourceAssignment.MasterRuleId,
                ClientRuleId = sourceAssignment.ClientRuleId.HasValue
                    && clientRuleIdMap.TryGetValue(sourceAssignment.ClientRuleId.Value, out var newClientRuleId)
                        ? newClientRuleId
                        : null,
                Status = sourceAssignment.Status,
                IsExplicit = sourceAssignment.IsExplicit,
                ExportPriority = sourceAssignment.ExportPriority
            };

            db.ClientRuleAssignments.Add(clonedAssignment);

            if (sourceOverrides.TryGetValue(sourceAssignment.Id, out var sourceOverride))
            {
                db.RuleOverrides.Add(new RuleOverride
                {
                    ClientRuleAssignmentId = clonedAssignment.Id,
                    Reason = sourceOverride.Reason,
                    OverrideJson = sourceOverride.OverrideJson
                });
            }
        }

        var copiedReferences = 0;
        if (request.CopyQuickBooksReferences)
        {
            var sourceReferences = await db.ClientReferences
                .Where(x => x.ClientId == sourceClientId)
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            foreach (var sourceReference in sourceReferences)
            {
                db.ClientReferences.Add(new ClientReference
                {
                    ClientId = clone.Id,
                    Type = sourceReference.Type,
                    Name = sourceReference.Name,
                    QuickBooksAccountNumber = sourceReference.QuickBooksAccountNumber,
                    QuickBooksAccountType = sourceReference.QuickBooksAccountType,
                    QuickBooksDetailType = sourceReference.QuickBooksDetailType,
                    Source = sourceReference.Source,
                    IsActive = sourceReference.IsActive
                });
            }

            copiedReferences = sourceReferences.Count;
        }

        await db.SaveChangesAsync(cancellationToken);

        return new CloneClientResult(
            clone.Id,
            sourceRuleSets.Count,
            sourceAssignments.Count,
            sourceClientRules.Count,
            copiedReferences);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
