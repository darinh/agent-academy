using AgentAcademy.Server.Services;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for SprintPreambles — stage preamble generation and roster filtering.
/// </summary>
public class SprintPreamblesTests
{
    // ── BuildPreamble ────────────────────────────────────────────

    [Theory]
    [InlineData("Intake")]
    [InlineData("Planning")]
    [InlineData("Discussion")]
    [InlineData("Validation")]
    [InlineData("Implementation")]
    [InlineData("FinalSynthesis")]
    public void BuildPreamble_ContainsStageHeader(string stage)
    {
        var preamble = SprintPreambles.BuildPreamble(1, stage);

        Assert.Contains("SPRINT #1", preamble);
        Assert.Contains(stage.ToUpperInvariant().Replace("FINALSYNTHESIS", "FINAL SYNTHESIS"), preamble);
    }

    [Fact]
    public void BuildPreamble_IncludesPriorContext()
    {
        var priorContext = new List<(string Stage, string Summary)>
        {
            ("Intake", "Requirements gathered"),
            ("Planning", "Tasks planned"),
        };

        var preamble = SprintPreambles.BuildPreamble(2, "Discussion", priorContext);

        Assert.Contains("PRIOR STAGE CONTEXT", preamble);
        Assert.Contains("--- Intake ---", preamble);
        Assert.Contains("Requirements gathered", preamble);
        Assert.Contains("--- Planning ---", preamble);
        Assert.Contains("Tasks planned", preamble);
    }

    [Fact]
    public void BuildPreamble_NoPriorContextSection_WhenEmpty()
    {
        var preamble = SprintPreambles.BuildPreamble(1, "Intake");

        Assert.DoesNotContain("PRIOR STAGE CONTEXT", preamble);
    }

    [Fact]
    public void BuildPreamble_NoPriorContextSection_WhenNull()
    {
        var preamble = SprintPreambles.BuildPreamble(1, "Intake", null);

        Assert.DoesNotContain("PRIOR STAGE CONTEXT", preamble);
    }

    [Fact]
    public void BuildPreamble_UnknownStage_StillIncludesSprintHeader()
    {
        var preamble = SprintPreambles.BuildPreamble(5, "UnknownStage");

        Assert.Contains("SPRINT #5", preamble);
    }

    // ── IsRoleAllowedInStage ─────────────────────────────────────

    [Theory]
    [InlineData("Planner", "Intake", true)]
    [InlineData("Architect", "Intake", false)]
    [InlineData("SoftwareEngineer", "Intake", false)]
    [InlineData("Reviewer", "Intake", false)]
    [InlineData("TechnicalWriter", "Intake", false)]
    public void IsRoleAllowedInStage_Intake(string role, string stage, bool expected)
    {
        Assert.Equal(expected, SprintPreambles.IsRoleAllowedInStage(role, stage));
    }

    [Theory]
    [InlineData("Planner", "Planning", true)]
    [InlineData("Architect", "Planning", true)]
    [InlineData("SoftwareEngineer", "Planning", false)]
    [InlineData("Reviewer", "Planning", false)]
    public void IsRoleAllowedInStage_Planning(string role, string stage, bool expected)
    {
        Assert.Equal(expected, SprintPreambles.IsRoleAllowedInStage(role, stage));
    }

    [Theory]
    [InlineData("Planner", "Discussion", true)]
    [InlineData("Architect", "Discussion", true)]
    [InlineData("SoftwareEngineer", "Discussion", true)]
    [InlineData("TechnicalWriter", "Discussion", true)]
    [InlineData("Reviewer", "Discussion", false)]
    public void IsRoleAllowedInStage_Discussion(string role, string stage, bool expected)
    {
        Assert.Equal(expected, SprintPreambles.IsRoleAllowedInStage(role, stage));
    }

    [Theory]
    [InlineData("Planner", "Validation", true)]
    [InlineData("Reviewer", "Validation", true)]
    [InlineData("SoftwareEngineer", "Validation", true)]
    public void IsRoleAllowedInStage_Validation_AllAllowed(string role, string stage, bool expected)
    {
        Assert.Equal(expected, SprintPreambles.IsRoleAllowedInStage(role, stage));
    }

    [Theory]
    [InlineData("Planner", "Implementation", true)]
    [InlineData("Reviewer", "Implementation", true)]
    [InlineData("SoftwareEngineer", "Implementation", true)]
    public void IsRoleAllowedInStage_Implementation_AllAllowed(string role, string stage, bool expected)
    {
        Assert.Equal(expected, SprintPreambles.IsRoleAllowedInStage(role, stage));
    }

    [Theory]
    [InlineData("Planner", "FinalSynthesis", true)]
    [InlineData("Reviewer", "FinalSynthesis", true)]
    public void IsRoleAllowedInStage_FinalSynthesis_AllAllowed(string role, string stage, bool expected)
    {
        Assert.Equal(expected, SprintPreambles.IsRoleAllowedInStage(role, stage));
    }

    [Fact]
    public void IsRoleAllowedInStage_UnknownStage_AllowsAll()
    {
        Assert.True(SprintPreambles.IsRoleAllowedInStage("Reviewer", "UnknownStage"));
        Assert.True(SprintPreambles.IsRoleAllowedInStage("Planner", "UnknownStage"));
    }

    // ── FilterByStageRoster ──────────────────────────────────────

    private record TestAgent(string Id, string Role);

    [Fact]
    public void FilterByStageRoster_IntakeOnlyAllowsPlanner()
    {
        var agents = new List<TestAgent>
        {
            new("planner-1", "Planner"),
            new("architect-1", "Architect"),
            new("swe-1", "SoftwareEngineer"),
            new("reviewer-1", "Reviewer"),
        };

        var filtered = SprintPreambles.FilterByStageRoster(agents, "Intake", a => a.Role);

        Assert.Single(filtered);
        Assert.Equal("planner-1", filtered[0].Id);
    }

    [Fact]
    public void FilterByStageRoster_DiscussionExcludesReviewer()
    {
        var agents = new List<TestAgent>
        {
            new("planner-1", "Planner"),
            new("architect-1", "Architect"),
            new("swe-1", "SoftwareEngineer"),
            new("reviewer-1", "Reviewer"),
            new("writer-1", "TechnicalWriter"),
        };

        var filtered = SprintPreambles.FilterByStageRoster(agents, "Discussion", a => a.Role);

        Assert.Equal(4, filtered.Count);
        Assert.DoesNotContain(filtered, a => a.Role == "Reviewer");
    }

    [Fact]
    public void FilterByStageRoster_ValidationAllowsAll()
    {
        var agents = new List<TestAgent>
        {
            new("planner-1", "Planner"),
            new("reviewer-1", "Reviewer"),
            new("swe-1", "SoftwareEngineer"),
        };

        var filtered = SprintPreambles.FilterByStageRoster(agents, "Validation", a => a.Role);

        Assert.Equal(3, filtered.Count);
    }

    [Fact]
    public void FilterByStageRoster_EmptyInput_ReturnsEmpty()
    {
        var filtered = SprintPreambles.FilterByStageRoster(
            Array.Empty<TestAgent>(), "Intake", a => a.Role);

        Assert.Empty(filtered);
    }

    // ── Overflow preamble ────────────────────────────────────────

    [Fact]
    public void BuildPreamble_IncludesOverflowContent_AtIntake()
    {
        var overflow = """{"items": ["unfinished feature"]}""";

        var preamble = SprintPreambles.BuildPreamble(2, "Intake", null, overflow);

        Assert.Contains("OVERFLOW FROM PREVIOUS SPRINT", preamble);
        Assert.Contains("unfinished feature", preamble);
    }

    [Fact]
    public void BuildPreamble_IgnoresOverflow_AtOtherStages()
    {
        var overflow = """{"items": ["unfinished feature"]}""";

        var preamble = SprintPreambles.BuildPreamble(2, "Planning", null, overflow);

        Assert.DoesNotContain("OVERFLOW FROM PREVIOUS SPRINT", preamble);
    }

    [Fact]
    public void BuildPreamble_NoOverflowSection_WhenNull()
    {
        var preamble = SprintPreambles.BuildPreamble(1, "Intake", null, null);

        Assert.DoesNotContain("OVERFLOW FROM PREVIOUS SPRINT", preamble);
    }
}
