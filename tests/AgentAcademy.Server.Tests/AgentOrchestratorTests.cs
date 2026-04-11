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

    // ── Stuck Detection Constants ────────────────────────────────

    [Fact]
    public void MaxConsecutiveIdleRounds_IsReasonableDefault()
    {
        Assert.True(AgentOrchestrator.MaxConsecutiveIdleRounds >= 3,
            "MaxConsecutiveIdleRounds should be at least 3 to allow for planning rounds");
        Assert.True(AgentOrchestrator.MaxConsecutiveIdleRounds <= 20,
            "MaxConsecutiveIdleRounds should not be so high that stuck agents waste resources");
    }

    [Fact]
    public void MaxBreakoutRounds_IsReasonableDefault()
    {
        Assert.True(AgentOrchestrator.MaxBreakoutRounds >= 50,
            "MaxBreakoutRounds should allow complex multi-step tasks");
        Assert.True(AgentOrchestrator.MaxBreakoutRounds <= 500,
            "MaxBreakoutRounds should prevent truly unbounded loops");
    }

    [Fact]
    public void MaxBreakoutRounds_ExceedsMaxConsecutiveIdleRounds()
    {
        Assert.True(AgentOrchestrator.MaxBreakoutRounds > AgentOrchestrator.MaxConsecutiveIdleRounds,
            "MaxBreakoutRounds must exceed MaxConsecutiveIdleRounds");
    }
}
