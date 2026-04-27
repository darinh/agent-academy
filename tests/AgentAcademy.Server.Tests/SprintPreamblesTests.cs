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

    // ── Goal card workflow in Implementation ─────────────────────

    [Fact]
    public void BuildPreamble_Implementation_IncludesGoalCardWorkflow()
    {
        var preamble = SprintPreambles.BuildPreamble(1, "Implementation");

        Assert.Contains("CREATE_GOAL_CARD", preamble);
        Assert.Contains("goal card", preamble.ToLowerInvariant());
    }

    [Fact]
    public void BuildPreamble_Implementation_GoalCardBeforePR()
    {
        var preamble = SprintPreambles.BuildPreamble(1, "Implementation");

        var goalCardIndex = preamble.IndexOf("CREATE_GOAL_CARD");
        var createPrIndex = preamble.IndexOf("CREATE_PR");

        Assert.True(goalCardIndex > -1, "Implementation preamble should mention CREATE_GOAL_CARD");
        Assert.True(createPrIndex > goalCardIndex, "Goal card step should come before CREATE_PR step");
    }

    [Fact]
    public void BuildPreamble_Implementation_GoalCardAutoIncludedInPR()
    {
        var preamble = SprintPreambles.BuildPreamble(1, "Implementation");

        Assert.Contains("Goal card content is automatically included in the PR description", preamble);
    }

    // ── STORE_ARTIFACT JSON schema visibility ────────────────────
    // Regression: Sprint #14 stalled in Intake because the planner kept
    // submitting markdown for RequirementsDocument / SprintPlan /
    // ValidationReport / SprintReport, which the validator rejects with
    // VALIDATION errors. The Intake/Planning/Validation/FinalSynthesis
    // preambles must surface the actual JSON schema (not free-form
    // `Content=<the document>`) so agents can produce a valid payload
    // on the first attempt.
    //
    // The expected JSON strings below are the EXACT schema fragments
    // returned by SprintArtifactService.GetSchemaHint. If GetSchemaHint
    // changes, update both — the test exists to keep the preamble
    // contract and the validator contract synchronised.

    [Theory]
    [InlineData(
        "Intake",
        "RequirementsDocument",
        """{"Title":"...","Description":"...","InScope":["...","..."],"OutOfScope":["...","..."]}""")]
    [InlineData(
        "Planning",
        "SprintPlan",
        """{"Summary":"...","Phases":[{"Name":"...","Description":"...","Deliverables":["...","..."]}],"OverflowRequirements":["..."]}""")]
    [InlineData(
        "Validation",
        "ValidationReport",
        """{"Verdict":"...","Findings":["...","..."],"RequiredChanges":["..."]}""")]
    [InlineData(
        "FinalSynthesis",
        "SprintReport",
        """{"Summary":"...","Delivered":["...","..."],"Learnings":["...","..."],"OverflowRequirements":["..."]}""")]
    public void BuildPreamble_StoreArtifactStages_ShowExactJsonSchema(
        string stage, string artifactType, string expectedSchema)
    {
        var preamble = SprintPreambles.BuildPreamble(1, stage);

        Assert.Contains($"Type: {artifactType}", preamble);
        Assert.Contains(expectedSchema, preamble);
        Assert.Contains("valid JSON", preamble);
        // Guard against the regression that started this fix: free-form
        // "Content=<the document>" / "Content=<plan ...>" instructions
        // must NOT appear, otherwise agents fall back to markdown.
        Assert.DoesNotContain("Content=<", preamble);
    }

    [Fact]
    public void BuildPreamble_FinalSynthesis_OverflowRequirementsRemainsFreeForm()
    {
        // OverflowRequirements is the one artifact the validator does NOT
        // schema-check (SprintArtifactService.ValidateArtifactContent
        // returns early). The preamble must say so, otherwise the planner
        // wastes rounds trying to JSON-encode plain prose.
        var preamble = SprintPreambles.BuildPreamble(1, "FinalSynthesis");

        Assert.Contains("Type: OverflowRequirements", preamble);
        Assert.Contains("free-form", preamble);
    }
}
