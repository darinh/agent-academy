using AgentAcademy.Server.Services;
using GitHub.Copilot.SDK;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Pins the structured one-line format produced by
/// <see cref="ToolEventDescriber"/>. The format is part of the operational
/// contract — log-scraping (grep/jq, /tmp/aa-dev.log triage workflows) and
/// the AgentWatchdog correlation depend on stable field names and ordering.
/// Tests cover all four event types, redaction of secrets in argument blobs,
/// truncation of oversized free-text fields, MCP / sub-agent context, and
/// the success/error branches of complete events.
/// </summary>
public sealed class ToolEventDescriberTests
{
    // ── tool_start ──────────────────────────────────────────────

    [Fact]
    public void DescribeStart_BasicTool_FormatsAllFields()
    {
        var data = new ToolExecutionStartData
        {
            ToolCallId = "call-1",
            ToolName = "write_file",
            Arguments = "{\"path\":\"src/Program.cs\",\"content\":\"// hello\"}",
        };

        var line = ToolEventDescriber.DescribeStart(data);

        Assert.Contains("tool_start", line);
        Assert.Contains("tool=\"write_file\"", line);
        Assert.Contains("tool_call_id=call-1", line);
        Assert.Contains("Program.cs", line);
        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain("\r", line);
    }

    [Fact]
    public void DescribeStart_NullToolName_RendersPlaceholder()
    {
        var data = new ToolExecutionStartData
        {
            ToolCallId = "call-1",
            ToolName = null!,
            Arguments = null!,
        };

        var line = ToolEventDescriber.DescribeStart(data);

        Assert.Contains("tool=\"(null)\"", line);
        Assert.Contains("args=\"(empty)\"", line);
    }

    [Fact]
    public void DescribeStart_McpTool_IncludesMcpServerAndOriginalName()
    {
        var data = new ToolExecutionStartData
        {
            ToolCallId = "call-2",
            ToolName = "playwright_navigate",
            Arguments = "{\"url\":\"https://example.com\"}",
            McpServerName = "playwright",
            McpToolName = "navigate",
        };

        var line = ToolEventDescriber.DescribeStart(data);

        Assert.Contains("mcp_server=\"playwright\"", line);
        Assert.Contains("mcp_tool=\"navigate\"", line);
    }

    [Fact]
    public void DescribeStart_NoMcp_OmitsMcpClause()
    {
        var data = new ToolExecutionStartData
        {
            ToolCallId = "call-3",
            ToolName = "read_file",
            Arguments = "{}",
        };

        var line = ToolEventDescriber.DescribeStart(data);

        Assert.DoesNotContain("mcp_server", line);
        Assert.DoesNotContain("mcp_tool", line);
    }

    [Fact]
    public void DescribeStart_SubAgentCall_IncludesParentToolCallId()
    {
        var data = new ToolExecutionStartData
        {
            ToolCallId = "call-child",
            ToolName = "read_file",
            Arguments = "{}",
            ParentToolCallId = "call-parent",
        };

        var line = ToolEventDescriber.DescribeStart(data);

        Assert.Contains("parent_tool_call_id=call-parent", line);
    }

    [Fact]
    public void DescribeStart_TopLevelCall_OmitsParentClause()
    {
        var data = new ToolExecutionStartData
        {
            ToolCallId = "call-1",
            ToolName = "read_file",
            Arguments = "{}",
        };

        var line = ToolEventDescriber.DescribeStart(data);

        Assert.DoesNotContain("parent_tool_call_id", line);
    }

    [Fact]
    public void DescribeStart_RedactsGitHubTokenInArguments()
    {
        var data = new ToolExecutionStartData
        {
            ToolCallId = "call-1",
            ToolName = "shell",
            Arguments = "{\"cmd\":\"curl -H 'Authorization: Bearer ghp_abcdefghijklmnopqrstuvwxyz0123456789AB' https://api.github.com\"}",
        };

        var line = ToolEventDescriber.DescribeStart(data);

        Assert.DoesNotContain("ghp_abcdefghijklmnopqrstuvwxyz0123456789AB", line);
        Assert.Contains("[REDACTED]", line);
    }

    [Fact]
    public void DescribeStart_RedactsPasswordAssignment()
    {
        var data = new ToolExecutionStartData
        {
            ToolCallId = "call-1",
            ToolName = "shell",
            Arguments = "PGPASSWORD=hunter2-very-secret psql",
        };

        var line = ToolEventDescriber.DescribeStart(data);

        Assert.DoesNotContain("hunter2-very-secret", line);
        Assert.Contains("[REDACTED]", line);
    }

    [Theory]
    [InlineData("{\"token\":\"supersecret123\"}", "supersecret123")]
    [InlineData("{\"password\":\"hunter2hunter2\"}", "hunter2hunter2")]
    [InlineData("{\"api_key\":\"sk-live-abcd1234\"}", "sk-live-abcd1234")]
    [InlineData("{\"client_secret\":\"abcdefghijklmn\"}", "abcdefghijklmn")]
    public void DescribeStart_RedactsJsonEncodedSecretFields(string args, string secret)
    {
        var data = new ToolExecutionStartData
        {
            ToolCallId = "call-1",
            ToolName = "shell",
            Arguments = args,
        };

        var line = ToolEventDescriber.DescribeStart(data);

        Assert.DoesNotContain(secret, line);
        Assert.Contains("[REDACTED]", line);
    }

    [Fact]
    public void DescribeStart_EscapesEmbeddedQuotesSoFormatStaysParseable()
    {
        // JSON arguments are common — they contain embedded quotes that
        // would otherwise close the surrounding `args="..."` clause and
        // produce ambiguous log lines.
        var data = new ToolExecutionStartData
        {
            ToolCallId = "call-1",
            ToolName = "write_file",
            Arguments = "{\"path\":\"src/Foo.cs\",\"content\":\"// hi\"}",
        };

        var line = ToolEventDescriber.DescribeStart(data);

        // The args clause must terminate with a single unescaped " followed
        // by either end-of-line or whitespace + next key. The presence of
        // backslash-escaped quotes inside is the canonical proof that the
        // value was escaped before interpolation.
        Assert.Contains(@"\""path\""", line);
        // And the surrounding clause structure is still recognizable.
        Assert.Matches(@"args=""[^""\\]*(\\.[^""\\]*)*""", line);
    }

    [Fact]
    public void DescribeStart_EscapesEmbeddedBackslashes()
    {
        var data = new ToolExecutionStartData
        {
            ToolCallId = "call-1",
            ToolName = "shell",
            Arguments = @"C:\Windows\System32",
        };

        var line = ToolEventDescriber.DescribeStart(data);

        // Embedded \ becomes \\ so the value can't smuggle a stray escape
        // into a downstream consumer that does its own unescape pass.
        Assert.Contains(@"C:\\Windows\\System32", line);
    }

    [Fact]
    public void DescribeStart_TruncatesOversizedArguments()
    {
        var bigArg = new string('x', 1000);
        var data = new ToolExecutionStartData
        {
            ToolCallId = "call-1",
            ToolName = "write_file",
            Arguments = bigArg,
        };

        var line = ToolEventDescriber.DescribeStart(data);

        // Truncation budget is 240 chars + the ellipsis marker
        Assert.Contains("…", line);
        // The whole line still has to be reasonable — no 1000 chars of "x"
        Assert.True(line.Length < 600, $"Expected line < 600 chars, got {line.Length}");
    }

    [Fact]
    public void DescribeStart_StripsNewlinesFromArguments()
    {
        var data = new ToolExecutionStartData
        {
            ToolCallId = "call-1",
            ToolName = "write_file",
            Arguments = "line1\nline2\rline3\r\nline4",
        };

        var line = ToolEventDescriber.DescribeStart(data);

        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain("\r", line);
        Assert.Contains("line1 line2 line3", line);
    }

    // ── tool_progress ───────────────────────────────────────────

    [Fact]
    public void DescribeProgress_FormatsMessage()
    {
        var data = new ToolExecutionProgressData
        {
            ToolCallId = "call-1",
            ProgressMessage = "Loaded 12 of 47 files",
        };

        var line = ToolEventDescriber.DescribeProgress(data);

        Assert.Contains("tool_progress", line);
        Assert.Contains("tool_call_id=call-1", line);
        Assert.Contains("Loaded 12 of 47 files", line);
    }

    [Fact]
    public void DescribeProgress_EmptyMessage_RendersPlaceholder()
    {
        var data = new ToolExecutionProgressData
        {
            ToolCallId = "call-1",
            ProgressMessage = null!,
        };

        var line = ToolEventDescriber.DescribeProgress(data);

        Assert.Contains("msg=\"(empty)\"", line);
    }

    // ── tool_complete ───────────────────────────────────────────

    [Fact]
    public void DescribeComplete_Success_FormatsResultAndElapsed()
    {
        var data = new ToolExecutionCompleteData
        {
            ToolCallId = "call-1",
            Success = true,
            Model = "claude-opus-4.7",
            InteractionId = "int-42",
            Result = new ToolExecutionCompleteDataResult
            {
                Content = "{\"status\":\"ok\",\"path\":\"src/Program.cs\"}",
            },
        };

        var line = ToolEventDescriber.DescribeComplete(data, elapsedMs: 1234);

        Assert.Contains("tool_complete", line);
        Assert.Contains("tool_call_id=call-1", line);
        Assert.Contains("success=True", line);
        Assert.Contains("model=\"claude-opus-4.7\"", line);
        Assert.Contains("interaction_id=int-42", line);
        Assert.Contains("elapsed_ms=1234", line);
        Assert.Contains("status", line);
    }

    [Fact]
    public void DescribeComplete_NoElapsed_OmitsElapsedClause()
    {
        var data = new ToolExecutionCompleteData
        {
            ToolCallId = "call-1",
            Success = true,
            Model = "gpt-5",
            Result = new ToolExecutionCompleteDataResult { Content = "ok" },
        };

        var line = ToolEventDescriber.DescribeComplete(data);

        Assert.DoesNotContain("elapsed_ms", line);
    }

    [Fact]
    public void DescribeComplete_Failure_FormatsErrorPreview()
    {
        var data = new ToolExecutionCompleteData
        {
            ToolCallId = "call-1",
            Success = false,
            Model = "gpt-5",
            Error = new ToolExecutionCompleteDataError
            {
                Message = "tool host crashed: ENOENT /usr/bin/bash",
                Code = "EHOSTERR",
            },
        };

        var line = ToolEventDescriber.DescribeComplete(data, elapsedMs: 50);

        Assert.Contains("success=False", line);
        Assert.Contains("ERROR:", line);
        Assert.Contains("[EHOSTERR]", line);
        Assert.Contains("ENOENT", line);
    }

    [Fact]
    public void DescribeComplete_FailureWithoutErrorDetail_RendersPlaceholder()
    {
        var data = new ToolExecutionCompleteData
        {
            ToolCallId = "call-1",
            Success = false,
            Model = "gpt-5",
            Error = null,
        };

        var line = ToolEventDescriber.DescribeComplete(data);

        Assert.Contains("(no error detail)", line);
    }

    [Fact]
    public void DescribeComplete_FailureWithoutErrorCode_OmitsCodeBracket()
    {
        var data = new ToolExecutionCompleteData
        {
            ToolCallId = "call-1",
            Success = false,
            Model = "gpt-5",
            Error = new ToolExecutionCompleteDataError
            {
                Message = "permission denied",
                Code = null,
            },
        };

        var line = ToolEventDescriber.DescribeComplete(data);

        Assert.Contains("ERROR: permission denied", line);
        Assert.DoesNotContain("[]", line);
    }

    [Fact]
    public void DescribeComplete_NullResultOnSuccess_RendersEmpty()
    {
        var data = new ToolExecutionCompleteData
        {
            ToolCallId = "call-1",
            Success = true,
            Model = "gpt-5",
            Result = null,
        };

        var line = ToolEventDescriber.DescribeComplete(data);

        Assert.Contains("result=\"(empty)\"", line);
    }

    [Fact]
    public void DescribeComplete_DetailedContentFallback_UsedWhenContentMissing()
    {
        var data = new ToolExecutionCompleteData
        {
            ToolCallId = "call-1",
            Success = true,
            Model = "gpt-5",
            Result = new ToolExecutionCompleteDataResult
            {
                Content = null!,
                DetailedContent = "diff --git a/foo.cs b/foo.cs",
            },
        };

        var line = ToolEventDescriber.DescribeComplete(data);

        Assert.Contains("foo.cs", line);
    }

    [Fact]
    public void DescribeComplete_RedactsSecretsInResult()
    {
        var data = new ToolExecutionCompleteData
        {
            ToolCallId = "call-1",
            Success = true,
            Model = "gpt-5",
            Result = new ToolExecutionCompleteDataResult
            {
                Content = "Created GitHub token: ghp_abcdefghijklmnopqrstuvwxyz0123456789AB",
            },
        };

        var line = ToolEventDescriber.DescribeComplete(data);

        Assert.DoesNotContain("ghp_abcdefghijklmnopqrstuvwxyz0123456789AB", line);
        Assert.Contains("[REDACTED]", line);
    }

    // ── session_warning ─────────────────────────────────────────

    [Fact]
    public void DescribeWarning_FormatsCategoryAndMessage()
    {
        var data = new SessionWarningData
        {
            WarningType = "subscription",
            Message = "Premium quota at 95% — model auto-fallback in 30s",
        };

        var line = ToolEventDescriber.DescribeWarning(data);

        Assert.Contains("session_warning", line);
        Assert.Contains("category=\"subscription\"", line);
        Assert.Contains("Premium quota", line);
    }

    [Fact]
    public void DescribeWarning_WithUrl_IncludesUrlClause()
    {
        var data = new SessionWarningData
        {
            WarningType = "policy",
            Message = "Workspace exceeds policy",
            Url = "https://docs.github.com/policies",
        };

        var line = ToolEventDescriber.DescribeWarning(data);

        Assert.Contains("docs.github.com", line);
    }

    [Fact]
    public void DescribeWarning_StripsUrlPathAndQueryToProtectAgainstTokenLeak()
    {
        // SDK-supplied warning URLs can carry signed query parameters or
        // session-recovery tokens. RedactUrl keeps scheme+host (the
        // diagnostic value) and drops everything after.
        var data = new SessionWarningData
        {
            WarningType = "subscription",
            Message = "Auth token refresh failed",
            Url = "https://login.example.com/refresh?token=ghs_realsecrettokenhere1234567890ABCDEFGH&user=darin",
        };

        var line = ToolEventDescriber.DescribeWarning(data);

        Assert.DoesNotContain("ghs_realsecrettokenhere1234567890ABCDEFGH", line);
        Assert.DoesNotContain("user=darin", line);
        Assert.Contains("login.example.com", line);
    }

    [Fact]
    public void DescribeWarning_NoUrl_OmitsUrlClause()
    {
        var data = new SessionWarningData
        {
            WarningType = "mcp",
            Message = "MCP server reconnected",
        };

        var line = ToolEventDescriber.DescribeWarning(data);

        Assert.DoesNotContain("url=", line);
    }

    [Fact]
    public void DescribeWarning_NullCategory_RendersPlaceholder()
    {
        var data = new SessionWarningData
        {
            WarningType = null!,
            Message = "x",
        };

        var line = ToolEventDescriber.DescribeWarning(data);

        Assert.Contains("category=\"(unknown)\"", line);
    }

    // ── Trunc ───────────────────────────────────────────────────

    [Fact]
    public void Trunc_NullOrEmpty_RendersPlaceholder()
    {
        Assert.Equal("(empty)", ToolEventDescriber.Trunc(null));
        Assert.Equal("(empty)", ToolEventDescriber.Trunc(""));
    }

    [Fact]
    public void Trunc_ShortString_PassesThroughUnchanged()
    {
        Assert.Equal("hello world", ToolEventDescriber.Trunc("hello world"));
    }

    [Fact]
    public void Trunc_OversizedString_AppendsEllipsis()
    {
        var s = new string('a', 500);

        var result = ToolEventDescriber.Trunc(s);

        Assert.EndsWith("…", result);
        Assert.Equal(241, result.Length); // 240 chars + ellipsis
    }
}
