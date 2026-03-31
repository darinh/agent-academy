using AgentAcademy.Server.Commands;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Tests;

public class CommandParserTests
{
    private readonly CommandParser _parser = new();

    // ── Basic Parsing ──────────────────────────────────────────

    [Fact]
    public void Parse_EmptyText_ReturnsNoCommands()
    {
        var result = _parser.Parse("");
        Assert.Empty(result.Commands);
    }

    [Fact]
    public void Parse_NoCommands_ReturnsOriginalText()
    {
        var text = "Just a regular message about architecture decisions.";
        var result = _parser.Parse(text);
        Assert.Empty(result.Commands);
        Assert.Equal(text, result.RemainingText);
    }

    [Fact]
    public void Parse_SingleInlineCommand_Extracts()
    {
        var text = "LIST_ROOMS:";
        var result = _parser.Parse(text);
        Assert.Single(result.Commands);
        Assert.Equal("LIST_ROOMS", result.Commands[0].Command);
    }

    [Fact]
    public void Parse_CommandWithInlineArgs_Extracts()
    {
        var text = "RECALL: category=gotcha";
        var result = _parser.Parse(text);
        Assert.Single(result.Commands);
        Assert.Equal("RECALL", result.Commands[0].Command);
        Assert.Equal("gotcha", result.Commands[0].Args["category"]);
    }

    [Fact]
    public void Parse_CommandWithMultiLineArgs_Extracts()
    {
        var text = """
            REMEMBER:
              Category: pattern
              Key: ef-core-include
              Value: EF Core requires explicit Include() for navigation properties.
            """;
        var result = _parser.Parse(text);
        Assert.Single(result.Commands);
        Assert.Equal("REMEMBER", result.Commands[0].Command);
        Assert.Equal("pattern", result.Commands[0].Args["Category"]);
        Assert.Equal("ef-core-include", result.Commands[0].Args["Key"]);
        Assert.Contains("Include()", result.Commands[0].Args["Value"]);
    }

    [Fact]
    public void Parse_MultipleCommands_ExtractsAll()
    {
        var text = """
            LIST_ROOMS:

            LIST_AGENTS:
            """;
        var result = _parser.Parse(text);
        Assert.Equal(2, result.Commands.Count);
        Assert.Equal("LIST_ROOMS", result.Commands[0].Command);
        Assert.Equal("LIST_AGENTS", result.Commands[1].Command);
    }

    // ── Legacy Block Preservation ──────────────────────────────

    [Fact]
    public void Parse_TaskAssignment_NotParsedAsCommand()
    {
        var text = """
            TASK ASSIGNMENT:
            Agent: @Archimedes
            Title: Design the schema
            """;
        var result = _parser.Parse(text);
        Assert.Empty(result.Commands);
        Assert.Contains("TASK ASSIGNMENT:", result.RemainingText);
    }

    [Fact]
    public void Parse_WorkReport_NotParsedAsCommand()
    {
        var text = """
            WORK REPORT:
            Status: COMPLETE
            Files: src/Program.cs
            """;
        var result = _parser.Parse(text);
        Assert.Empty(result.Commands);
        Assert.Contains("WORK REPORT:", result.RemainingText);
    }

    [Fact]
    public void Parse_Review_NotParsedAsCommand()
    {
        var text = """
            REVIEW:
            Verdict: APPROVED
            Findings:
            - All tests pass
            """;
        var result = _parser.Parse(text);
        Assert.Empty(result.Commands);
        Assert.Contains("REVIEW:", result.RemainingText);
    }

    // ── Mixed Content ──────────────────────────────────────────

    [Fact]
    public void Parse_MixedTextAndCommands_SeparatesCorrectly()
    {
        var text = """
            I need to check the current state of the rooms.

            LIST_ROOMS:

            Based on the rooms, I'll also check the agents.

            LIST_AGENTS:
            """;
        var result = _parser.Parse(text);
        Assert.Equal(2, result.Commands.Count);
        Assert.Contains("check the current state", result.RemainingText);
        Assert.Contains("Based on the rooms", result.RemainingText);
        Assert.DoesNotContain("LIST_ROOMS:", result.RemainingText);
        Assert.DoesNotContain("LIST_AGENTS:", result.RemainingText);
    }

    [Fact]
    public void Parse_CommandsWithLegacyBlocks_CoexistCorrectly()
    {
        var text = """
            Let me check the tasks first.

            LIST_TASKS:

            TASK ASSIGNMENT:
            Agent: @Hephaestus
            Title: Implement the feature
            Description: Build the thing
            Acceptance Criteria:
            - It works
            """;
        var result = _parser.Parse(text);
        Assert.Single(result.Commands);
        Assert.Equal("LIST_TASKS", result.Commands[0].Command);
        Assert.Contains("TASK ASSIGNMENT:", result.RemainingText);
    }

    // ── Unknown Commands ───────────────────────────────────────

    [Fact]
    public void Parse_UnknownCommand_NotParsed()
    {
        var text = "RANDOM_THING: some value";
        var result = _parser.Parse(text);
        Assert.Empty(result.Commands);
        Assert.Contains("RANDOM_THING:", result.RemainingText);
    }

    // ── FORGET shorthand ───────────────────────────────────────

    [Fact]
    public void Parse_ForgetWithInlineKey_Extracts()
    {
        var text = "FORGET: key=old-memory";
        var result = _parser.Parse(text);
        Assert.Single(result.Commands);
        Assert.Equal("FORGET", result.Commands[0].Command);
        Assert.Equal("old-memory", result.Commands[0].Args["key"]);
    }

    // ── READ_FILE with args ────────────────────────────────────

    [Fact]
    public void Parse_ReadFileWithArgs_Extracts()
    {
        var text = """
            READ_FILE:
              path: src/Program.cs
              startLine: 10
              endLine: 20
            """;
        var result = _parser.Parse(text);
        Assert.Single(result.Commands);
        Assert.Equal("READ_FILE", result.Commands[0].Command);
        Assert.Equal("src/Program.cs", result.Commands[0].Args["path"]);
        Assert.Equal("10", result.Commands[0].Args["startLine"]);
        Assert.Equal("20", result.Commands[0].Args["endLine"]);
    }

    [Fact]
    public void Parse_SetPlanWithMultilineContent_Extracts()
    {
        var text = """
            SET_PLAN:
              Content: # Execution Plan
                - Review code
                - Implement fix
            """;
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("SET_PLAN", result.Commands[0].Command);
        Assert.Contains("# Execution Plan", result.Commands[0].Args["Content"]);
        Assert.Contains("- Implement fix", result.Commands[0].Args["Content"]);
    }
}
