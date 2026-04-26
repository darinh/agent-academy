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

    [Fact]
    public void Parse_MergePr_Recognized()
    {
        var text = "MERGE_PR: taskId=task-abc123";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("MERGE_PR", result.Commands[0].Command);
        Assert.Equal("task-abc123", result.Commands[0].Args["taskId"]);
    }

    [Fact]
    public void Parse_MergePr_WithDeleteBranch_Recognized()
    {
        var text = """
            MERGE_PR:
              taskId: task-abc123
              deleteBranch: true
            """;
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("MERGE_PR", result.Commands[0].Command);
        Assert.Equal("task-abc123", result.Commands[0].Args["taskId"]);
        Assert.Equal("true", result.Commands[0].Args["deleteBranch"]);
    }

    [Theory]
    [InlineData("START_SPRINT:")]
    [InlineData("ADVANCE_STAGE:")]
    [InlineData("STORE_ARTIFACT:\n  type: RequirementsDocument\n  content: doc")]
    [InlineData("COMPLETE_SPRINT:")]
    public void Parse_SprintCommands_Recognized(string text)
    {
        var result = _parser.Parse(text);
        Assert.Single(result.Commands);
    }

    // ── Mutation-Killing: Behavioral Mutants ───────────────────

    [Fact]
    public void Parse_LegacyBlock_DoesNotSkipFollowingLine()
    {
        // Kills L95 (continue removal): without continue, the line after
        // TASK ASSIGNMENT gets double-incremented and skipped.
        var text = "TASK ASSIGNMENT:\nThis line must survive\nLIST_ROOMS:";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("LIST_ROOMS", result.Commands[0].Command);
        Assert.Contains("This line must survive", result.RemainingText);
        // Legacy block line appears exactly once
        Assert.Single(result.RemainingText.Split('\n'), l => l.Contains("TASK ASSIGNMENT:"));
    }

    [Fact]
    public void Parse_ContinuationLineRequiresCurrentArgKey()
    {
        // Kills L135 (&& → ||): without currentArgKey, an indented non-key
        // line should break arg parsing, not be treated as continuation.
        var text = "READ_FILE:\n  not a key value pair\n  path: foo.txt";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        // With correct &&: the first indented line doesn't match arg pattern,
        // no currentArgKey exists, so parser breaks. "path: foo.txt" not parsed as arg.
        Assert.DoesNotContain("path", result.Commands[0].Args.Keys);
    }

    [Fact]
    public void Parse_MultiLineContinuation_PreservesNewlines()
    {
        // Kills L138 (AppendLine removal): continuation lines need
        // newlines between them.
        var text = "SET_PLAN:\n  Content: Line One\n    Line Two\n    Line Three";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        var content = result.Commands[0].Args["Content"];
        Assert.Contains("\n", content);
        Assert.Contains("Line Two", content);
        Assert.Contains("Line Three", content);
    }

    [Fact]
    public void Parse_NonContinuationLine_BreaksArgParsing()
    {
        // Kills L143 (break removal): a line that is not an arg pattern,
        // not a continuation, and not blank should stop arg parsing.
        var text = "READ_FILE:\n  path: foo.txt\nNot an arg line\n  other: bar";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("foo.txt", result.Commands[0].Args["path"]);
        // "other" should NOT be parsed as an arg for READ_FILE
        Assert.DoesNotContain("other", result.Commands[0].Args.Keys);
        // The non-continuation line should be in remaining text
        Assert.Contains("Not an arg line", result.RemainingText);
    }

    [Fact]
    public void Parse_WhitespaceOnlyLine_TerminatesArgParsing()
    {
        // Kills L107 (|| → &&) and L109 (break removal): a line containing
        // only whitespace (but not the empty string) is IsNullOrWhiteSpace-true
        // yet StartsWith("  ")-true. Without the explicit break on whitespace,
        // such a line would be swallowed as a continuation of the current
        // arg, and any subsequent indented text would be appended to it.
        var text = "REMEMBER:\n  Key: my-key\n  Content: first-line\n   \n  stray-continuation";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("my-key", result.Commands[0].Args["Key"]);
        Assert.Equal("first-line", result.Commands[0].Args["Content"]);
        Assert.DoesNotContain("stray-continuation", result.Commands[0].Args["Content"]);
        Assert.Contains("stray-continuation", result.RemainingText);
    }

    [Fact]
    public void Parse_NoArgCommand_HasEmptyArgs()
    {
        // Kills L155 (&& → ||): when firstLineValue is empty AND args
        // is empty, the || mutation enters the inline-args block and
        // creates a spurious "value" = "" entry.
        var text = "LIST_ROOMS:";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Empty(result.Commands[0].Args);
    }

    [Fact]
    public void Parse_MultiLineArgs_IgnoreFirstLineValue()
    {
        // Kills L155 variant: when multi-line args exist, the first-line
        // value should NOT also be processed as inline args.
        var text = "REMEMBER: ignore=this\n  Category: pattern\n  Key: test-key";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("pattern", result.Commands[0].Args["Category"]);
        Assert.Equal("test-key", result.Commands[0].Args["Key"]);
        // The first-line "ignore=this" should not create additional args
        Assert.DoesNotContain("ignore", result.Commands[0].Args.Keys);
    }

    [Fact]
    public void Parse_CommitChanges_TreatsValueAsRawText()
    {
        // Verifies RawValueCommands prevents splitting on key=value.
        var text = "COMMIT_CHANGES: fix: set timeout=30s for retries";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("COMMIT_CHANGES", result.Commands[0].Command);
        Assert.Equal("fix: set timeout=30s for retries", result.Commands[0].Args["value"]);
        // "timeout" should NOT be split as a key
        Assert.DoesNotContain("timeout", result.Commands[0].Args.Keys);
    }

    [Fact]
    public void Parse_CommandFollowedByBlankLine_TerminatesArgs()
    {
        // Robustness: blank line must terminate arg parsing so subsequent
        // text goes to remaining. (L120/L122 mutants are equivalent —
        // the fallback break at L143 produces identical behavior.)
        var text = "READ_FILE:\n  path: foo.txt\n\nThis is remaining text";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("foo.txt", result.Commands[0].Args["path"]);
        Assert.Contains("This is remaining text", result.RemainingText);
    }

    [Fact]
    public void Parse_CommandFollowedByAnotherCommand_TerminatesArgs()
    {
        // Robustness: next command header terminates arg parsing for
        // the current command and starts a new one.
        var text = "READ_FILE:\n  path: foo.txt\nLIST_ROOMS:";
        var result = _parser.Parse(text);

        Assert.Equal(2, result.Commands.Count);
        Assert.Equal("READ_FILE", result.Commands[0].Command);
        Assert.Equal("foo.txt", result.Commands[0].Args["path"]);
        Assert.Equal("LIST_ROOMS", result.Commands[1].Command);
    }

    // ── Mutation-Killing: KnownCommands Coverage ──────────────────

    public static IEnumerable<object[]> AllKnownCommands =>
        CommandParser.KnownCommands.Select(cmd => new object[] { cmd });

    [Theory]
    [MemberData(nameof(AllKnownCommands))]
    public void Parse_EveryKnownCommand_IsRecognized(string commandName)
    {
        // Kills string mutations on KnownCommands set entries (L32-L50):
        // mutating any command name to "" makes it unrecognizable.
        var text = $"{commandName}: test-value";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal(commandName, result.Commands[0].Command);
    }

    [Theory]
    [MemberData(nameof(AllKnownCommands))]
    public void Parse_EveryKnownCommand_StrippedFromRemaining(string commandName)
    {
        // Ensures recognized commands don't leak into remaining text.
        var text = $"Preamble text\n{commandName}: test-value\nTrailing text";
        var result = _parser.Parse(text);

        Assert.DoesNotContain($"{commandName}:", result.RemainingText);
    }

    [Fact]
    public void KnownCommands_ContainsExpectedCount()
    {
        // Guard: if someone adds/removes a command, this test catches it.
        Assert.Equal(105, CommandParser.KnownCommands.Count);
    }

    // ── Mutation-Killing: LegacyBlocks ────────────────────────────

    [Theory]
    [InlineData("TASK ASSIGNMENT")]
    [InlineData("WORK REPORT")]
    [InlineData("REVIEW")]
    public void Parse_LegacyBlock_GoesToRemainingText(string blockName)
    {
        // Kills string mutations on LegacyBlocks entries: each block name
        // must be individually recognized and excluded from command parsing.
        var text = $"{blockName}:\nSome content here";
        var result = _parser.Parse(text);

        Assert.Empty(result.Commands);
        Assert.Contains($"{blockName}:", result.RemainingText);
    }

    // ── Mutation-Killing: RawValueCommands ────────────────────────

    [Fact]
    public void Parse_CommitChanges_RawValue_NotSplitOnEquals()
    {
        // Kills "COMMIT_CHANGES" string mutation in RawValueCommands:
        // if the entry is mutated to "", COMMIT_CHANGES falls through to
        // ParseInlineArgs which splits on "=" signs.
        var text = "COMMIT_CHANGES: refactor: rename timeout=30s default";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        // With RawValueCommands working, the entire value is preserved
        Assert.Equal("refactor: rename timeout=30s default", result.Commands[0].Args["value"]);
        // Without it, "timeout" would appear as a separate key
        Assert.DoesNotContain("timeout", result.Commands[0].Args.Keys);
    }

    // ── Mutation-Killing: Replace mutations ───────────────────────

    [Fact]
    public void Parse_SpaceInKnownCommandName_IsRecognized()
    {
        // Kills L74 Replace(" ", "_") mutations: the parser's regex allows
        // uppercase headers with spaces (e.g. "CREATE PR"), and the Replace
        // normalizes them to the underscore form in KnownCommands. Mutating
        // the replacement to "" produces "CREATEPR" which is not a known
        // command, so the header falls through to RemainingText instead of
        // being parsed as a command.
        var text = "CREATE PR: title=Fix";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("CREATE_PR", result.Commands[0].Command);
        Assert.Equal("Fix", result.Commands[0].Args["title"]);
    }

    [Fact]
    public void Parse_InlineArgs_ParsesMultipleKeyValuePairs()
    {
        // Verifies ParseInlineArgs handles multiple key=value pairs.
        var text = "RECALL: category=gotcha key=some-key";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("gotcha", result.Commands[0].Args["category"]);
        Assert.Equal("some-key", result.Commands[0].Args["key"]);
    }

    [Fact]
    public void Parse_InlineArgs_NoEqualsSign_TreatsAsRawValue()
    {
        // When inline text has no key=value pairs, treated as raw "value".
        var text = "SEARCH_CODE: some search query here";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("some search query here", result.Commands[0].Args["value"]);
    }

    // ── Markdown emphasis tolerance ─────────────────────────────
    // Agents frequently emit commands wrapped in markdown (`**STORE_ARTIFACT:**`,
    // `` `LIST_ROOMS:` ``) instead of the bare form taught in prompts. The parser
    // must strip leading/trailing emphasis so commands aren't silently dropped.

    [Theory]
    [InlineData("**LIST_ROOMS:**")]
    [InlineData("*LIST_ROOMS:*")]
    [InlineData("`LIST_ROOMS:`")]
    [InlineData("__LIST_ROOMS:__")]
    [InlineData("_LIST_ROOMS:_")]
    [InlineData("~~LIST_ROOMS:~~")]
    [InlineData("**LIST_ROOMS**:")]
    [InlineData("`LIST_ROOMS`:")]
    public void Parse_CommandWithSurroundingMarkdownEmphasis_Recognized(string text)
    {
        var result = _parser.Parse(text);
        Assert.Single(result.Commands);
        Assert.Equal("LIST_ROOMS", result.Commands[0].Command);
    }

    [Fact]
    public void Parse_BoldCommandWithInlineArgs_ExtractsArgs()
    {
        var text = "**STORE_ARTIFACT:** Type=RequirementsDocument Content=hello";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("STORE_ARTIFACT", result.Commands[0].Command);
        Assert.Equal("RequirementsDocument", result.Commands[0].Args["Type"]);
        Assert.Equal("hello", result.Commands[0].Args["Content"]);
    }

    [Fact]
    public void Parse_BoldCommandWithIndentedMultiLineArgs_ExtractsArgs()
    {
        var text = "**STORE_ARTIFACT:**\n  Type: RequirementsDocument\n  Content: full doc body";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("STORE_ARTIFACT", result.Commands[0].Command);
        Assert.Equal("RequirementsDocument", result.Commands[0].Args["Type"]);
        Assert.Equal("full doc body", result.Commands[0].Args["Content"]);
    }

    [Fact]
    public void Parse_BoldNextCommand_TerminatesPriorCommandArgs()
    {
        // Lookahead must also tolerate emphasis so a `**ADVANCE_STAGE:**`
        // properly terminates the prior command's arg block instead of being
        // swallowed as a continuation line.
        var text = "STORE_ARTIFACT:\n  Type: x\n  Content: y\n**ADVANCE_STAGE:**";
        var result = _parser.Parse(text);

        Assert.Equal(2, result.Commands.Count);
        Assert.Equal("STORE_ARTIFACT", result.Commands[0].Command);
        Assert.Equal("ADVANCE_STAGE", result.Commands[1].Command);
        Assert.Equal("y", result.Commands[0].Args["Content"]);
    }

    [Fact]
    public void Parse_UnknownCommandWithEmphasis_PassesThroughToRemaining()
    {
        // `**TASK ASSIGNMENT:**` is not a known command — original line must
        // survive in remaining text (preserves legacy block behavior).
        var text = "**TASK ASSIGNMENT:**\nDo the thing\nLIST_ROOMS:";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("LIST_ROOMS", result.Commands[0].Command);
        Assert.Contains("**TASK ASSIGNMENT:**", result.RemainingText);
        Assert.Contains("Do the thing", result.RemainingText);
    }

    [Fact]
    public void Parse_LowercaseInsideEmphasis_NotRecognizedAsCommand()
    {
        // Don't promote arbitrary bold text to commands — must still match
        // the uppercase command-name shape.
        var text = "**hello there:** some content";
        var result = _parser.Parse(text);

        Assert.Empty(result.Commands);
        Assert.Contains("hello there", result.RemainingText);
    }

    [Theory]
    [InlineData("STORE_ARTIFACT: Content=hello**", "hello**")]
    [InlineData("STORE_ARTIFACT: Content=hello__", "hello__")]
    [InlineData("STORE_ARTIFACT: Content=hello~~", "hello~~")]
    [InlineData("STORE_ARTIFACT: Content=git", "git")]
    public void Parse_UnpairedTrailingEmphasis_PreservedInValue(string text, string expectedContent)
    {
        // Regression guard for the v1 fix that over-eagerly stripped
        // trailing emphasis from value text. Only paired whole-line wrappers
        // get peeled — mid-line or unmatched trailing emphasis must survive.
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("STORE_ARTIFACT", result.Commands[0].Command);
        Assert.Equal(expectedContent, result.Commands[0].Args["Content"]);
    }

    [Fact]
    public void Parse_TrailingEmphasisInRawValue_Preserved()
    {
        // For a positional raw value (no key=value), trailing emphasis must
        // also survive when it isn't paired with a leading wrapper.
        var text = "SEARCH_CODE: query with **bold** word**";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("query with **bold** word**", result.Commands[0].Args["value"]);
    }

    [Theory]
    [InlineData("**STORE_ARTIFACT: Type=X Content=Y**", "X", "Y")]
    [InlineData("`STORE_ARTIFACT: Type=X Content=Y`", "X", "Y")]
    [InlineData("__STORE_ARTIFACT: Type=X Content=Y__", "X", "Y")]
    public void Parse_WholeLineWrappedCommandWithValue_StripsWrapper(string text, string expectedType, string expectedContent)
    {
        // When the entire line is wrapped in matching emphasis, peel one
        // layer so the inner command parses cleanly.
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("STORE_ARTIFACT", result.Commands[0].Command);
        Assert.Equal(expectedType, result.Commands[0].Args["Type"]);
        Assert.Equal(expectedContent, result.Commands[0].Args["Content"]);
    }

    [Theory]
    [InlineData("**STORE_ARTIFACT:** Type=X Content=Y**")]  // hybrid: keyword wrapped + trailing **
    [InlineData("**hello**world**")]                          // unbalanced trailing pair inside
    public void Parse_HybridUnbalancedEmphasis_DoesNotMisUnwrap(string text)
    {
        // Round-2 reviewer regression: TryUnwrapPairedEmphasis must refuse
        // to peel when the inner content already contains the wrapper —
        // otherwise it eats trailing characters that belong to the value.
        var result = _parser.Parse(text);

        if (result.Commands.Count == 1)
        {
            // STORE_ARTIFACT case: trailing ** must survive in the value
            Assert.Contains("**", result.Commands[0].Args.Values.LastOrDefault() ?? "");
        }
        else
        {
            // Non-command (`**hello**world**`) just passes through to remaining
            Assert.Empty(result.Commands);
            Assert.Contains("**hello**world**", result.RemainingText);
        }
    }

    [Fact]
    public void Parse_IndentedUppercaseArgKey_NotMisDetectedAsNextCommand()
    {
        // Round-2 reviewer regression: NextCommandLookahead anchored at
        // column 0. An indented uppercase key like `  TYPE:` inside an
        // arg block must NOT terminate the prior command.
        var text = "STORE_ARTIFACT:\n  TYPE: RequirementsDocument\n  CONTENT: full body";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("STORE_ARTIFACT", result.Commands[0].Command);
        Assert.Equal("RequirementsDocument", result.Commands[0].Args["TYPE"]);
        Assert.Equal("full body", result.Commands[0].Args["CONTENT"]);
    }

    [Fact]
    public void Parse_IndentedBoldNextCommand_NotTreatedAsCommand()
    {
        // An indented `  **ADVANCE_STAGE:**` is inside a previous arg
        // block, not a column-0 command. Lookahead doesn't fire (it's
        // anchored at column 0), so the line is absorbed as continuation
        // text of the prior arg — but no second command is emitted.
        var text = "STORE_ARTIFACT:\n  Type: x\n  **ADVANCE_STAGE:**";
        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("STORE_ARTIFACT", result.Commands[0].Command);
        Assert.Contains("x", result.Commands[0].Args["Type"]);
    }
}
