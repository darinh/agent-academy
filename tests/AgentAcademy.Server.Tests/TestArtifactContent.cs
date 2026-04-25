namespace AgentAcademy.Server.Tests;

/// <summary>
/// Valid JSON content for each artifact type, for use in tests that need to
/// pass content validation but don't care about the specific values.
/// </summary>
internal static class TestArtifactContent
{
    public const string RequirementsDocument = """
        {"Title":"Test Requirements","Description":"Build a widget","InScope":["core"],"OutOfScope":["edge cases"]}
        """;

    public const string SprintPlan = """
        {"Summary":"Sprint plan","Phases":[{"Name":"Phase 1","Description":"Build it","Deliverables":["widget.cs"]}]}
        """;

    public const string ValidationReport = """
        {"Verdict":"Approved","Findings":["Looks good"]}
        """;

    public const string SprintReport = """
        {"Summary":"Sprint complete","Delivered":["widget"],"Learnings":["TDD works"]}
        """;

    public const string OverflowRequirements = """
        {"items":["leftover task"]}
        """;
}
