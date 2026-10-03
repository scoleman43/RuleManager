using RuleManager.Core.Domain;

namespace RuleManager.Core.Rules;

public sealed record ConsistencySummary(
    int Total,
    int Inherited,
    int Overridden,
    int Missing,
    int Conflict)
{
    public int Issues => Missing + Conflict;
}

public static class ConsistencyCalculator
{
    public static ConsistencySummary Calculate(
        IEnumerable<Guid> activeMasterRuleIds,
        IEnumerable<ClientRuleAssignment> assignments)
    {
        var masterIds = activeMasterRuleIds.Distinct().ToArray();
        var byMaster = assignments
            .GroupBy(x => x.MasterRuleId)
            .ToDictionary(x => x.Key, x => x.First());

        var inherited = 0;
        var overridden = 0;
        var missing = 0;
        var conflict = 0;

        foreach (var masterId in masterIds)
        {
            if (!byMaster.TryGetValue(masterId, out var assignment))
            {
                missing++;
                continue;
            }

            switch (assignment.Status)
            {
                case RuleAssignmentStatus.Overridden:
                    overridden++;
                    break;
                case RuleAssignmentStatus.Conflict:
                    conflict++;
                    break;
                default:
                    inherited++;
                    break;
            }
        }

        return new ConsistencySummary(masterIds.Length, inherited, overridden, missing, conflict);
    }
}
