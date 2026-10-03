using RuleManager.Core.Domain;
using RuleManager.Core.Rules;

namespace RuleManager.Tests;

public class ConsistencyCalculatorTests
{
    [Fact]
    public void Calculate_CountsMissingAndAssignedStatuses()
    {
        var inheritedId = Guid.NewGuid();
        var overriddenId = Guid.NewGuid();
        var conflictId = Guid.NewGuid();
        var missingId = Guid.NewGuid();

        var assignments = new[]
        {
            new ClientRuleAssignment { MasterRuleId = inheritedId, Status = RuleAssignmentStatus.Inherited },
            new ClientRuleAssignment { MasterRuleId = overriddenId, Status = RuleAssignmentStatus.Overridden },
            new ClientRuleAssignment { MasterRuleId = conflictId, Status = RuleAssignmentStatus.Conflict }
        };

        var result = ConsistencyCalculator.Calculate(
            [inheritedId, overriddenId, conflictId, missingId],
            assignments);

        Assert.Equal(4, result.Total);
        Assert.Equal(1, result.Inherited);
        Assert.Equal(1, result.Overridden);
        Assert.Equal(1, result.Conflict);
        Assert.Equal(1, result.Missing);
        Assert.Equal(2, result.Issues);
    }
}
