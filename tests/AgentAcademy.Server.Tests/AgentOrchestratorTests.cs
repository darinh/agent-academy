using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Tests;

public class AgentOrchestratorTests
{
    [Fact]
    public void BuildAssignmentPlanContent_IncludesObjectiveAndCriteria()
    {
        var assignment = new ParsedTaskAssignment(
            Agent: "Hephaestus",
            Title: "Add plan seeding",
            Description: "Persist plan content for breakout rooms",
            Criteria: ["Plan tab shows content", "No API regressions"],
            Type: TaskType.Feature);

        var content = PromptBuilder.BuildAssignmentPlanContent(assignment);

        Assert.Contains("# Add plan seeding", content);
        Assert.Contains("## Objective", content);
        Assert.Contains("Persist plan content for breakout rooms", content);
        Assert.Contains("## Acceptance Criteria", content);
        Assert.Contains("- Plan tab shows content", content);
    }
}
