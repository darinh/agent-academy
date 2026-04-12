using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Tests;

public class AgentResponseParserTests
{
    // ── PASS Detection ──────────────────────────────────────────

    [Theory]
    [InlineData("PASS", true)]
    [InlineData("pass", true)]
    [InlineData("Pass", true)]
    [InlineData("  PASS  ", true)]
    [InlineData("N/A", true)]
    [InlineData("n/a", true)]
    [InlineData("No comment.", true)]
    [InlineData("Nothing to add.", true)]
    [InlineData("PASS — I have nothing to add.", true)] // <30 chars and contains PASS
    [InlineData("This is a substantial response about the architecture.", false)]
    [InlineData("", false)] // empty is not PASS — it's filtered earlier
    [InlineData("I'll pass on this topic and instead discuss...", false)]
    public void IsPassResponse_DetectsCorrectly(string input, bool expected)
    {
        Assert.Equal(expected, AgentResponseParser.IsPassResponse(input));
    }

    // ── Stub Offline Detection ─────────────────────────────────

    [Theory]
    [InlineData("⚠️ Agent **Socrates** (Reviewer) is offline — the Copilot SDK is not connected. Log in via GitHub OAuth or check server logs to activate.", true)]
    [InlineData("⚠️ Agent **Hephaestus** (SoftwareEngineer) is offline — the Copilot SDK is not connected. Log in via GitHub OAuth or check server logs to activate.", true)]
    [InlineData("The agent is offline and cannot respond.", false)]
    [InlineData("I'll review the code now.", false)]
    [InlineData("PASS", false)]
    [InlineData("", false)]
    public void IsStubOfflineResponse_DetectsCorrectly(string input, bool expected)
    {
        Assert.Equal(expected, AgentResponseParser.IsStubOfflineResponse(input));
    }

    // ── Task Assignment Parsing ─────────────────────────────────

    [Fact]
    public void ParseTaskAssignments_ParsesSingleAssignment()
    {
        var content = """
            Let me assign this work.

            TASK ASSIGNMENT:
            Agent: @Archimedes
            Title: Design the API schema
            Description: Create OpenAPI spec for the /agents endpoint
            Acceptance Criteria:
            - Includes GET and POST methods
            - Returns proper error codes
            """;

        var result = AgentResponseParser.ParseTaskAssignments(content);

        Assert.Single(result);
        Assert.Equal("Archimedes", result[0].Agent);
        Assert.Equal("Design the API schema", result[0].Title);
        Assert.Contains("OpenAPI spec", result[0].Description);
        Assert.Equal(2, result[0].Criteria.Count);
        Assert.Contains("Includes GET and POST methods", result[0].Criteria);
        Assert.Contains("Returns proper error codes", result[0].Criteria);
    }

    [Fact]
    public void ParseTaskAssignments_ParsesMultipleAssignments()
    {
        var content = """
            We need two pieces of work:

            TASK ASSIGNMENT:
            Agent: @Builder
            Title: Implement backend
            Description: Build the REST API

            TASK ASSIGNMENT:
            Agent: @Designer
            Title: Create UI mockups
            Description: Design the dashboard layout
            Acceptance Criteria:
            - Mobile responsive
            """;

        var result = AgentResponseParser.ParseTaskAssignments(content);

        Assert.Equal(2, result.Count);
        Assert.Equal("Builder", result[0].Agent);
        Assert.Equal("Designer", result[1].Agent);
        Assert.Single(result[1].Criteria);
    }

    [Fact]
    public void ParseTaskAssignments_ReturnsEmpty_WhenNoBlocks()
    {
        var content = "Just a normal response with no task assignments.";
        var result = AgentResponseParser.ParseTaskAssignments(content);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseTaskAssignments_RequiresAgentAndTitle()
    {
        var content = """
            TASK ASSIGNMENT:
            Description: Missing agent and title
            """;

        var result = AgentResponseParser.ParseTaskAssignments(content);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseTaskAssignments_HandlesAgentWithoutAtSign()
    {
        var content = """
            TASK ASSIGNMENT:
            Agent: Builder
            Title: Do the thing
            Description: A task
            """;

        var result = AgentResponseParser.ParseTaskAssignments(content);
        Assert.Single(result);
        Assert.Equal("Builder", result[0].Agent);
    }

    [Fact]
    public void ParseTaskAssignments_CaseInsensitiveHeader()
    {
        var content = """
            task assignment:
            Agent: @Bot
            Title: Test task
            Description: Case test
            """;

        var result = AgentResponseParser.ParseTaskAssignments(content);
        Assert.Single(result);
    }

    // ── Work Report Parsing ─────────────────────────────────────

    [Fact]
    public void ParseWorkReport_ParsesCompleteReport()
    {
        var content = """
            I've finished the implementation.

            WORK REPORT:
            Status: COMPLETE
            Files:
            - src/api.cs
            - src/models.cs
            Evidence: All endpoints implemented and tested with 100% coverage.
            """;

        var result = AgentResponseParser.ParseWorkReport(content);

        Assert.NotNull(result);
        Assert.Equal("COMPLETE", result.Status);
        Assert.Equal(2, result.Files.Count);
        Assert.Contains("src/api.cs", result.Files);
        Assert.Contains("src/models.cs", result.Files);
        Assert.Contains("100% coverage", result.Evidence);
    }

    [Fact]
    public void ParseWorkReport_ReturnsNull_WhenNoBlock()
    {
        var content = "Just working on things, no report yet.";
        Assert.Null(AgentResponseParser.ParseWorkReport(content));
    }

    [Fact]
    public void ParseWorkReport_HandlesMinimalReport()
    {
        var content = """
            WORK REPORT:
            Status: IN PROGRESS
            """;

        var result = AgentResponseParser.ParseWorkReport(content);

        Assert.NotNull(result);
        Assert.Equal("IN PROGRESS", result.Status);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void ParseWorkReport_CaseInsensitive()
    {
        var content = """
            work report:
            Status: complete
            Files: main.py
            Evidence: Done
            """;

        var result = AgentResponseParser.ParseWorkReport(content);
        Assert.NotNull(result);
        Assert.Equal("complete", result.Status);
    }

    // ── Review Verdict Parsing ──────────────────────────────────

    [Fact]
    public void ParseReviewVerdict_ParsesApproval()
    {
        var content = """
            The work looks good overall.

            REVIEW:
            Verdict: APPROVED
            Findings:
            - Clean code structure
            - Good test coverage
            """;

        var result = AgentResponseParser.ParseReviewVerdict(content);

        Assert.NotNull(result);
        Assert.Equal("APPROVED", result.Verdict);
        Assert.Equal(2, result.Findings.Count);
    }

    [Fact]
    public void ParseReviewVerdict_ParsesNeedsFix()
    {
        var content = """
            REVIEW:
            Verdict: NEEDS FIX
            Findings:
            - Missing error handling in API controller
            - No input validation
            """;

        var result = AgentResponseParser.ParseReviewVerdict(content);

        Assert.NotNull(result);
        Assert.Equal("NEEDS FIX", result.Verdict);
        Assert.Equal(2, result.Findings.Count);
    }

    [Fact]
    public void ParseReviewVerdict_ReturnsNull_WhenNoBlock()
    {
        Assert.Null(AgentResponseParser.ParseReviewVerdict("Just some text, no review block."));
    }

    [Fact]
    public void ParseReviewVerdict_HandlesAlternateLabels()
    {
        var content = """
            REVIEW:
            Decision: APPROVED
            Issues:
            - Minor naming inconsistency
            """;

        var result = AgentResponseParser.ParseReviewVerdict(content);

        Assert.NotNull(result);
        Assert.Equal("APPROVED", result.Verdict);
        Assert.Single(result.Findings);
    }

    [Fact]
    public void ParseReviewVerdict_CaseInsensitive()
    {
        var content = """
            review:
            verdict: approved
            findings:
            - All good
            """;

        var result = AgentResponseParser.ParseReviewVerdict(content);
        Assert.NotNull(result);
        Assert.Equal("approved", result.Verdict);
    }

    // ── Message Kind Inference ───────────────────────────────────

    [Theory]
    [InlineData("Planner", MessageKind.Coordination)]
    [InlineData("Architect", MessageKind.Decision)]
    [InlineData("SoftwareEngineer", MessageKind.Response)]
    [InlineData("Reviewer", MessageKind.Review)]
    [InlineData("Validator", MessageKind.Validation)]
    [InlineData("TechnicalWriter", MessageKind.SpecChangeProposal)]
    [InlineData("UnknownRole", MessageKind.Response)]
    [InlineData("", MessageKind.Response)]
    public void InferMessageKind_MapsCorrectly(string role, MessageKind expected)
    {
        Assert.Equal(expected, AgentResponseParser.InferMessageKind(role));
    }

    // ── Agent Tag Parsing ───────────────────────────────────────

    private static List<AgentDefinition> MakeAgents(params (string id, string name)[] agents) =>
        agents.Select(a => new AgentDefinition(
            a.id, a.name, "SoftwareEngineer", "Test agent", "prompt",
            null, [], [], false)).ToList();

    [Fact]
    public void ParseTaggedAgents_FindsByName()
    {
        var agents = MakeAgents(("arch", "Archimedes"), ("heph", "Hephaestus"));
        var response = "I think @Archimedes should handle the design.";

        var result = AgentResponseParser.ParseTaggedAgents(agents, response);

        Assert.Single(result);
        Assert.Equal("arch", result[0].Id);
    }

    [Fact]
    public void ParseTaggedAgents_FindsById()
    {
        var agents = MakeAgents(("arch", "Archimedes"), ("heph", "Hephaestus"));
        var response = "Assigning to @arch for implementation.";

        var result = AgentResponseParser.ParseTaggedAgents(agents, response);

        Assert.Single(result);
        Assert.Equal("arch", result[0].Id);
    }

    [Fact]
    public void ParseTaggedAgents_FindsMultipleAgents()
    {
        var agents = MakeAgents(("arch", "Archimedes"), ("heph", "Hephaestus"), ("soc", "Socrates"));
        var response = "Archimedes and Hephaestus should collaborate on this.";

        var result = AgentResponseParser.ParseTaggedAgents(agents, response);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, a => a.Id == "arch");
        Assert.Contains(result, a => a.Id == "heph");
    }

    [Fact]
    public void ParseTaggedAgents_CaseInsensitive()
    {
        var agents = MakeAgents(("arch", "Archimedes"));
        var response = "Let archimedes handle this.";

        var result = AgentResponseParser.ParseTaggedAgents(agents, response);

        Assert.Single(result);
        Assert.Equal("arch", result[0].Id);
    }

    [Fact]
    public void ParseTaggedAgents_ReturnsEmpty_WhenNoMatches()
    {
        var agents = MakeAgents(("arch", "Archimedes"), ("heph", "Hephaestus"));
        var response = "I'll handle this task myself.";

        var result = AgentResponseParser.ParseTaggedAgents(agents, response);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseTaggedAgents_ReturnsEmpty_WhenNullOrWhitespace()
    {
        var agents = MakeAgents(("arch", "Archimedes"));

        Assert.Empty(AgentResponseParser.ParseTaggedAgents(agents, ""));
        Assert.Empty(AgentResponseParser.ParseTaggedAgents(agents, "   "));
    }

    [Fact]
    public void ParseTaggedAgents_DoesNotDuplicateAgent()
    {
        var agents = MakeAgents(("arch", "Archimedes"));
        var response = "Archimedes (@arch) is the right choice — Archimedes should do it.";

        var result = AgentResponseParser.ParseTaggedAgents(agents, response);

        Assert.Single(result);
    }

    [Fact]
    public void ParseTaggedAgents_RespectsMaxLimit()
    {
        var agents = Enumerable.Range(1, 10)
            .Select(i => new AgentDefinition(
                $"a{i}", $"Agent{i}", "SoftwareEngineer", "Test agent", "prompt",
                null, [], [], false))
            .ToList();
        var response = string.Join(" and ", agents.Select(a => a.Name));

        var result = AgentResponseParser.ParseTaggedAgents(agents, response);

        Assert.Equal(AgentResponseParser.MaxTaggedAgents, result.Count);
    }

    [Fact]
    public void ParseTaggedAgents_MatchesWithoutAtSign()
    {
        var agents = MakeAgents(("arch", "Archimedes"));
        var response = "Archimedes can review this.";

        var result = AgentResponseParser.ParseTaggedAgents(agents, response);

        Assert.Single(result);
    }
}
