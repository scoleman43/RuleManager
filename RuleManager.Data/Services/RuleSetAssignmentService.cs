using Microsoft.EntityFrameworkCore;
using RuleManager.Core.Domain;

namespace RuleManager.Data.Services;

public sealed class RuleSetAssignmentService(IDbContextFactory<RuleManagerDbContext> dbFactory)
{
    public async Task AssignRuleSetToClientAsync(Guid clientId, Guid ruleSetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var exists = await db.ClientRuleSetAssignments.AnyAsync(
            x => x.ClientId == clientId && x.RuleSetId == ruleSetId,
            cancellationToken);

        if (!exists)
        {
            db.ClientRuleSetAssignments.Add(new ClientRuleSetAssignment
            {
                ClientId = clientId,
                RuleSetId = ruleSetId
            });
        }

        var masterRuleIds = await db.RuleSetRules
            .Where(x => x.RuleSetId == ruleSetId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.CreatedUtc)
            .Select(x => x.MasterRuleId)
            .ToListAsync(cancellationToken);

        var existing = await db.ClientRuleAssignments
            .Where(x => x.ClientId == clientId && masterRuleIds.Contains(x.MasterRuleId))
            .Select(x => x.MasterRuleId)
            .ToListAsync(cancellationToken);

        var existingSet = existing.ToHashSet();
        var nextPriority = await GetNextExportPriorityAsync(db, clientId, cancellationToken);

        foreach (var masterRuleId in masterRuleIds.Where(x => !existingSet.Contains(x)))
        {
            db.ClientRuleAssignments.Add(new ClientRuleAssignment
            {
                ClientId = clientId,
                MasterRuleId = masterRuleId,
                Status = RuleAssignmentStatus.Inherited,
                IsExplicit = false,
                ExportPriority = nextPriority++
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveRuleSetFromClientAsync(Guid clientId, Guid ruleSetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var assignment = await db.ClientRuleSetAssignments
            .SingleOrDefaultAsync(x => x.ClientId == clientId && x.RuleSetId == ruleSetId, cancellationToken);

        if (assignment is null)
            return;

        db.ClientRuleSetAssignments.Remove(assignment);

        var removedRuleIds = await db.RuleSetRules
            .Where(x => x.RuleSetId == ruleSetId)
            .Select(x => x.MasterRuleId)
            .ToListAsync(cancellationToken);

        var remainingSetIds = await db.ClientRuleSetAssignments
            .Where(x => x.ClientId == clientId && x.RuleSetId != ruleSetId)
            .Select(x => x.RuleSetId)
            .ToListAsync(cancellationToken);

        var stillRequiredRuleIds = await db.RuleSetRules
            .Where(x => remainingSetIds.Contains(x.RuleSetId))
            .Select(x => x.MasterRuleId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var stillRequired = stillRequiredRuleIds.ToHashSet();

        var clientRules = await db.ClientRuleAssignments
            .Where(x => x.ClientId == clientId && removedRuleIds.Contains(x.MasterRuleId))
            .ToListAsync(cancellationToken);

        foreach (var rule in clientRules)
        {
            if (!rule.IsExplicit &&
                rule.Status == RuleAssignmentStatus.Inherited &&
                !stillRequired.Contains(rule.MasterRuleId))
            {
                db.ClientRuleAssignments.Remove(rule);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRuleToSetAsync(Guid ruleSetId, Guid masterRuleId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var exists = await db.RuleSetRules.AnyAsync(
            x => x.RuleSetId == ruleSetId && x.MasterRuleId == masterRuleId,
            cancellationToken);

        if (!exists)
        {
            var nextSort = await db.RuleSetRules
                .Where(x => x.RuleSetId == ruleSetId)
                .Select(x => (int?)x.SortOrder)
                .MaxAsync(cancellationToken) ?? 0;

            db.RuleSetRules.Add(new RuleSetRule
            {
                RuleSetId = ruleSetId,
                MasterRuleId = masterRuleId,
                SortOrder = nextSort + 1
            });
        }

        var clientIds = await db.ClientRuleSetAssignments
            .Where(x => x.RuleSetId == ruleSetId)
            .Select(x => x.ClientId)
            .ToListAsync(cancellationToken);

        var alreadyAssigned = await db.ClientRuleAssignments
            .Where(x => clientIds.Contains(x.ClientId) && x.MasterRuleId == masterRuleId)
            .Select(x => x.ClientId)
            .ToListAsync(cancellationToken);

        var alreadyAssignedSet = alreadyAssigned.ToHashSet();

        foreach (var clientId in clientIds.Where(x => !alreadyAssignedSet.Contains(x)))
        {
            var priority = await GetNextExportPriorityAsync(db, clientId, cancellationToken);

            db.ClientRuleAssignments.Add(new ClientRuleAssignment
            {
                ClientId = clientId,
                MasterRuleId = masterRuleId,
                Status = RuleAssignmentStatus.Inherited,
                IsExplicit = false,
                ExportPriority = priority
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<int> GetNextExportPriorityAsync(
        RuleManagerDbContext db,
        Guid clientId,
        CancellationToken cancellationToken)
    {
        var assignmentMax = await db.ClientRuleAssignments
            .Where(x => x.ClientId == clientId)
            .Select(x => (int?)x.ExportPriority)
            .MaxAsync(cancellationToken) ?? 0;

        var clientRuleMax = await db.ClientRules
            .Where(x => x.ClientId == clientId)
            .Select(x => (int?)x.ExportPriority)
            .MaxAsync(cancellationToken) ?? 0;

        return Math.Max(assignmentMax, clientRuleMax) + 1;
    }

    public async Task RemoveRuleFromSetAsync(Guid ruleSetId, Guid masterRuleId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var setRule = await db.RuleSetRules
            .SingleOrDefaultAsync(x => x.RuleSetId == ruleSetId && x.MasterRuleId == masterRuleId, cancellationToken);

        if (setRule is null)
            return;

        db.RuleSetRules.Remove(setRule);

        var clientIds = await db.ClientRuleSetAssignments
            .Where(x => x.RuleSetId == ruleSetId)
            .Select(x => x.ClientId)
            .ToListAsync(cancellationToken);

        foreach (var clientId in clientIds)
        {
            var otherSetIds = await db.ClientRuleSetAssignments
                .Where(x => x.ClientId == clientId && x.RuleSetId != ruleSetId)
                .Select(x => x.RuleSetId)
                .ToListAsync(cancellationToken);

            var requiredElsewhere = await db.RuleSetRules.AnyAsync(
                x => otherSetIds.Contains(x.RuleSetId) && x.MasterRuleId == masterRuleId,
                cancellationToken);

            if (requiredElsewhere)
                continue;

            var clientRule = await db.ClientRuleAssignments
                .SingleOrDefaultAsync(
                    x => x.ClientId == clientId && x.MasterRuleId == masterRuleId,
                    cancellationToken);

            if (clientRule is not null &&
                !clientRule.IsExplicit &&
                clientRule.Status == RuleAssignmentStatus.Inherited)
            {
                db.ClientRuleAssignments.Remove(clientRule);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
