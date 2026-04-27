using AgentAcademy.Server.Services;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public sealed class AgentPermissionHandlerTests
{
    private readonly ILogger _logger = NullLogger.Instance;

    private static PermissionRequest MakeRequest(string kind) =>
        new() { Kind = kind };

    private static PermissionInvocation MakeInvocation(string? sessionId = "session-1") =>
        new() { SessionId = sessionId! };

    // ── Safe kinds ──────────────────────────────────────────────

    [Theory]
    [InlineData("custom-tool")]
    [InlineData("read")]
    [InlineData("tool")]
    public async Task Create_SafeKind_Approved(string kind)
    {
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, _logger, new TestDoubles.NoOpAgentLivenessTracker());

        var result = await handler(MakeRequest(kind), MakeInvocation());

        Assert.Equal(PermissionRequestResultKind.Approved, result.Kind);
    }

    [Theory]
    [InlineData("CUSTOM-TOOL")]
    [InlineData("Read")]
    [InlineData("TOOL")]
    public async Task Create_SafeKind_CaseInsensitive_Approved(string kind)
    {
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, _logger, new TestDoubles.NoOpAgentLivenessTracker());

        var result = await handler(MakeRequest(kind), MakeInvocation());

        Assert.Equal(PermissionRequestResultKind.Approved, result.Kind);
    }

    // ── Unsafe kinds ────────────────────────────────────────────

    [Theory]
    [InlineData("shell")]
    [InlineData("write")]
    [InlineData("url")]
    [InlineData("execute")]
    [InlineData("unknown-kind")]
    public async Task Create_UnsafeKind_Denied(string kind)
    {
        // Use a unique session per test to avoid cross-test denial count leakage
        var sessionId = $"unsafe-{kind}-{Guid.NewGuid():N}";
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, _logger, new TestDoubles.NoOpAgentLivenessTracker());

        var result = await handler(MakeRequest(kind), MakeInvocation(sessionId));

        Assert.Equal(PermissionRequestResultKind.DeniedByRules, result.Kind);
    }

    // ── Tool-implied kinds (regression: Sprint #2 hang 2026-04-25) ──
    //
    // When a write/shell-capable tool is registered, the SDK fires
    // permission requests with Kind="write"/"shell" for the underlying
    // operations even though the tool envelope (Kind="tool"/"custom-tool")
    // is also requested. If we deny the underlying kind, the SDK silently
    // retries until the per-turn budget is exhausted and the agent goes
    // dark, killing the conversation round and breaking sprint self-drive.

    [Theory]
    [InlineData("write")]
    [InlineData("WRITE")]
    public async Task Create_WriteFileRegistered_WriteKindApproved(string kind)
    {
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "write_file" }, _logger, new TestDoubles.NoOpAgentLivenessTracker());

        var result = await handler(MakeRequest(kind), MakeInvocation());

        Assert.Equal(PermissionRequestResultKind.Approved, result.Kind);
    }

    [Fact]
    public async Task Create_WriteFileRegistered_ShellKindStillDenied()
    {
        // write_file does NOT imply shell — only commit_changes does.
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "write_file" }, _logger, new TestDoubles.NoOpAgentLivenessTracker());

        var result = await handler(MakeRequest("shell"), MakeInvocation($"isolated-{Guid.NewGuid():N}"));

        Assert.Equal(PermissionRequestResultKind.DeniedByRules, result.Kind);
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("write")]
    [InlineData("Shell")]
    public async Task Create_CommitChangesRegistered_ShellAndWriteApproved(string kind)
    {
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "commit_changes" }, _logger, new TestDoubles.NoOpAgentLivenessTracker());

        var result = await handler(MakeRequest(kind), MakeInvocation());

        Assert.Equal(PermissionRequestResultKind.Approved, result.Kind);
    }

    [Fact]
    public async Task Create_HephaestusToolset_AllExpectedKindsApproved()
    {
        // Mirrors Hephaestus's session toolset (per agents.json EnabledTools
        // = chat, task-state, code, code-write, task-write, memory which
        // expand to read_file, search_code, list_*, write_file, commit_changes,
        // and friends). Verifies the exact failure mode from the 2026-04-25
        // incident is fixed: write + shell are approved when the corresponding
        // tools are registered.
        var hephaestusTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "list_tasks", "list_rooms", "show_agents",
            "read_file", "search_code",
            "create_task", "update_task_status", "add_task_comment",
            "remember", "recall",
            "write_file", "commit_changes",
        };
        var handler = AgentPermissionHandler.Create(hephaestusTools, _logger, new TestDoubles.NoOpAgentLivenessTracker());

        Assert.Equal(
            PermissionRequestResultKind.Approved,
            (await handler(MakeRequest("write"), MakeInvocation("h-write"))).Kind);
        Assert.Equal(
            PermissionRequestResultKind.Approved,
            (await handler(MakeRequest("shell"), MakeInvocation("h-shell"))).Kind);
        Assert.Equal(
            PermissionRequestResultKind.Approved,
            (await handler(MakeRequest("tool"), MakeInvocation("h-tool"))).Kind);
        Assert.Equal(
            PermissionRequestResultKind.Approved,
            (await handler(MakeRequest("read"), MakeInvocation("h-read"))).Kind);
        Assert.Equal(
            PermissionRequestResultKind.DeniedByRules,
            (await handler(MakeRequest("url"), MakeInvocation($"h-url-{Guid.NewGuid():N}"))).Kind);
    }

    [Fact]
    public async Task Create_ReadOnlyAgent_WriteAndShellStillDenied()
    {
        // Read-only agents (Aristotle, Socrates) only have envelope tools.
        // Their underlying operation kinds must still be denied, otherwise
        // the safety guarantee that "read-only agents cannot mutate"
        // collapses.
        var readOnlyTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "read_file", "search_code", "list_tasks", "list_rooms",
            "show_agents", "remember", "recall",
        };
        var handler = AgentPermissionHandler.Create(readOnlyTools, _logger, new TestDoubles.NoOpAgentLivenessTracker());

        Assert.Equal(
            PermissionRequestResultKind.DeniedByRules,
            (await handler(MakeRequest("write"), MakeInvocation($"ro-w-{Guid.NewGuid():N}"))).Kind);
        Assert.Equal(
            PermissionRequestResultKind.DeniedByRules,
            (await handler(MakeRequest("shell"), MakeInvocation($"ro-s-{Guid.NewGuid():N}"))).Kind);
    }

    // ── No tools registered → approve all ───────────────────────

    [Theory]
    [InlineData("shell")]
    [InlineData("write")]
    [InlineData("custom-tool")]
    public async Task Create_NoToolsRegistered_ApprovesEverything(string kind)
    {
        var handler = AgentPermissionHandler.Create(
            new HashSet<string>(), _logger, new TestDoubles.NoOpAgentLivenessTracker());

        var result = await handler(MakeRequest(kind), MakeInvocation());

        Assert.Equal(PermissionRequestResultKind.Approved, result.Kind);
    }

    // ── Hook kind (always-safe — SDK lifecycle event) ────────────
    //
    // Per inspection of GitHub.Copilot.SDK 0.2.2, "hook" is fired during
    // session priming (before any user-initiated tool runs). Denying it
    // surfaces as "unexpected user permission response" on the first turn
    // and was the root cause of P1.9-blocker-A (supervised acceptance run
    // 2026-04-26). Must be approved unconditionally — no tool registration
    // implies it; it's pure SDK lifecycle.

    [Theory]
    [InlineData("hook")]
    [InlineData("HOOK")]
    [InlineData("Hook")]
    public async Task Create_HookKind_AlwaysApproved(string kind)
    {
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, _logger, new TestDoubles.NoOpAgentLivenessTracker());

        var result = await handler(MakeRequest(kind), MakeInvocation());

        Assert.Equal(PermissionRequestResultKind.Approved, result.Kind);
    }

    [Fact]
    public async Task Create_HookKind_ApprovedEvenWithNoToolsRegistered()
    {
        // No-tools branch already approves everything, but this asserts the
        // explicit AlwaysSafeKinds path also handles it (defence in depth
        // against a future refactor that drops the no-tools shortcut).
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "any-tool" }, _logger, new TestDoubles.NoOpAgentLivenessTracker());

        var result = await handler(MakeRequest("hook"), MakeInvocation());

        Assert.Equal(PermissionRequestResultKind.Approved, result.Kind);
    }

    // ── Other unhandled SDK kinds (mcp, memory) — explicitly denied ──
    //
    // These exist in GitHub.Copilot.SDK 0.2.2 but no current tool implies
    // them. Pinning the deny behaviour here so a future SDK update or
    // accidental dictionary edit doesn't silently grant them.

    [Theory]
    [InlineData("mcp")]
    [InlineData("memory")]
    public async Task Create_UnimpliedSdkKind_Denied(string kind)
    {
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, _logger, new TestDoubles.NoOpAgentLivenessTracker());

        var result = await handler(
            MakeRequest(kind),
            MakeInvocation($"unimplied-{kind}-{Guid.NewGuid():N}"));

        Assert.Equal(PermissionRequestResultKind.DeniedByRules, result.Kind);
    }

    // ── Denial log dedup-by-Kind (replaces old count-based escalation) ──

    [Fact]
    public async Task Create_DenialLogging_FirstDenialOfKindAtWarning()
    {
        var mockLogger = Substitute.For<ILogger>();
        mockLogger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, mockLogger, new TestDoubles.NoOpAgentLivenessTracker());

        await handler(MakeRequest("shell"), MakeInvocation());

        mockLogger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task Create_DenialLogging_RepeatedKindGoesToDebug()
    {
        var mockLogger = Substitute.For<ILogger>();
        mockLogger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, mockLogger, new TestDoubles.NoOpAgentLivenessTracker());

        // 5 denials of the same kind: 1 Warning (first), 4 Debug (repeats).
        for (var i = 0; i < 5; i++)
            await handler(MakeRequest("shell"), MakeInvocation());

        mockLogger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        mockLogger.Received(4).Log(
            LogLevel.Debug,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task Create_DenialLogging_EachDistinctKindLoggedAtWarningOnce()
    {
        // Regression guard for P1.9-blocker-A: a future SDK kind appearing
        // for the first time must be visible at default log level, even if
        // many denials of an already-known kind have already occurred this
        // session. Without dedup-by-Kind, the prior count-based suppression
        // would hide the novel kind at Debug.
        var mockLogger = Substitute.For<ILogger>();
        mockLogger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, mockLogger, new TestDoubles.NoOpAgentLivenessTracker());

        // 10 denials of "shell" first (would have exhausted the old
        // count-based suppression), then one "url" and one "memory".
        for (var i = 0; i < 10; i++)
            await handler(MakeRequest("shell"), MakeInvocation());
        await handler(MakeRequest("url"), MakeInvocation());
        await handler(MakeRequest("memory"), MakeInvocation());

        // Three distinct kinds → exactly three Warning logs.
        mockLogger.Received(3).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Nine repeats of "shell" at Debug.
        mockLogger.Received(9).Log(
            LogLevel.Debug,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task Create_DenialLogging_KindDedupIsCaseInsensitive()
    {
        // PermissionRequest.Kind is matched case-insensitively elsewhere;
        // the dedup set must follow the same rule, otherwise an SDK that
        // sends "Shell" once and "shell" once would log two Warnings for
        // the same logical kind.
        var mockLogger = Substitute.For<ILogger>();
        mockLogger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, mockLogger, new TestDoubles.NoOpAgentLivenessTracker());

        await handler(MakeRequest("shell"), MakeInvocation());
        await handler(MakeRequest("SHELL"), MakeInvocation());
        await handler(MakeRequest("Shell"), MakeInvocation());

        mockLogger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        mockLogger.Received(2).Log(
            LogLevel.Debug,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // ── Per-closure state (new behavior) ─────────────────────────

    [Fact]
    public async Task Create_DenialCount_IsPerHandlerInstance()
    {
        // Each Create() call returns a handler with its own denial state
        // (count + per-Kind dedup set), scoped to that session and GC'd
        // with it. Two handlers do not share state, even when invoked with
        // the same sessionId.
        var loggerA = Substitute.For<ILogger>();
        loggerA.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var loggerB = Substitute.For<ILogger>();
        loggerB.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

        var handlerA = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, loggerA, new TestDoubles.NoOpAgentLivenessTracker());
        var handlerB = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, loggerB, new TestDoubles.NoOpAgentLivenessTracker());

        // Fire 4 denials of "shell" on handler A — under per-Kind dedup,
        // exactly 1 Warning (first denial of "shell") + 3 Debug (repeats).
        for (var i = 0; i < 4; i++)
            await handlerA(MakeRequest("shell"), MakeInvocation("shared-session"));

        loggerA.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
        loggerA.Received(3).Log(
            LogLevel.Debug,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Handler B has a fresh dedup set — first "shell" denial logs at
        // Warning #1 even though handler A has already seen it.
        await handlerB(MakeRequest("shell"), MakeInvocation("shared-session"));

        loggerB.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void ClearSession_IsObsoleteNoOp()
    {
        // ClearSession is retained for backward compatibility but no longer
        // affects any handler — denial counters are closure-local.
#pragma warning disable CS0618
        AgentPermissionHandler.ClearSession("anything");
        AgentPermissionHandler.ClearSession(string.Empty);
#pragma warning restore CS0618
    }

    // ── Null session ID ─────────────────────────────────────────

    [Fact]
    public async Task Create_NullSessionId_UsesFallback()
    {
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, _logger, new TestDoubles.NoOpAgentLivenessTracker());

        // Should not throw even with null session ID
        var result = await handler(MakeRequest("shell"), MakeInvocation(null));

        Assert.Equal(PermissionRequestResultKind.DeniedByRules, result.Kind);
    }

    // ── DescribeRequest ─────────────────────────────────────────
    //
    // These tests pin the diagnostic format the handler emits when it denies
    // a request. The format is what makes ad-hoc log scraping (grep/jq) work
    // when triaging an agent that's hitting a permission wall.

    private static PermissionRequestShell MakeShell(
        string? toolCallId = null,
        string fullCommandText = "",
        string intention = "",
        bool hasWriteFileRedirection = false,
        PermissionRequestShellCommandsItem[]? commands = null) =>
        new()
        {
            Kind = "shell",
            ToolCallId = toolCallId!,
            FullCommandText = fullCommandText,
            Intention = intention,
            Commands = commands ?? Array.Empty<PermissionRequestShellCommandsItem>(),
            PossiblePaths = Array.Empty<string>(),
            PossibleUrls = Array.Empty<PermissionRequestShellPossibleUrlsItem>(),
            HasWriteFileRedirection = hasWriteFileRedirection,
            CanOfferSessionApproval = false,
            Warning = string.Empty,
        };

    private static PermissionRequestWrite MakeWrite(
        string? toolCallId = null,
        string? fileName = null,
        string? diff = null,
        string intention = "") =>
        new()
        {
            Kind = "write",
            ToolCallId = toolCallId!,
            FileName = fileName!,
            Diff = diff!,
            NewFileContents = string.Empty,
            Intention = intention,
        };

    private static PermissionRequestUrl MakeUrl(string? url = null, string intention = "") =>
        new()
        {
            Kind = "url",
            ToolCallId = null!,
            Url = url!,
            Intention = intention,
        };

    private static PermissionRequestMcp MakeMcp(
        string? serverName = null,
        string? toolName = null,
        bool readOnly = false) =>
        new()
        {
            Kind = "mcp",
            ToolCallId = null!,
            ServerName = serverName!,
            ToolName = toolName!,
            ToolTitle = string.Empty,
            Args = new object(),
            ReadOnly = readOnly,
        };

    private static PermissionRequestRead MakeRead(string? path = null, string intention = "") =>
        new()
        {
            Kind = "read",
            ToolCallId = null!,
            Path = path!,
            Intention = intention,
        };

    private static PermissionRequestMemory MakeMemory(string subject = "", string fact = "") =>
        new()
        {
            Kind = "memory",
            ToolCallId = null!,
            Subject = subject,
            Fact = fact,
            Citations = string.Empty,
        };

    private static PermissionRequestCustomTool MakeCustomTool(string? toolName = null, string description = "") =>
        new()
        {
            Kind = "custom-tool",
            ToolCallId = null!,
            ToolName = toolName!,
            ToolDescription = description,
            Args = new object(),
        };

    private static PermissionRequestHook MakeHook(string? toolName = null, string hookMessage = "") =>
        new()
        {
            Kind = "hook",
            ToolCallId = null!,
            ToolName = toolName!,
            ToolArgs = new object(),
            HookMessage = hookMessage,
        };

    [Fact]
    public void DescribeRequest_Shell_IncludesCommandIntentionAndCallId()
    {
        var req = MakeShell(
            toolCallId: "tc-abc123",
            fullCommandText: "git status --porcelain",
            intention: "Check whether worktree is clean before committing",
            hasWriteFileRedirection: false,
            commands: new[]
            {
                new PermissionRequestShellCommandsItem { Identifier = "git", ReadOnly = true }
            });

        var detail = AgentPermissionHandler.DescribeRequest(req);

        Assert.Contains("shell", detail);
        Assert.Contains("tc-abc123", detail);
        Assert.Contains("git status --porcelain", detail);
        Assert.Contains("Check whether worktree is clean", detail);
        Assert.Contains("has_write_redirect=False", detail);
        Assert.Contains("cmd_count=1", detail);
    }

    [Fact]
    public void DescribeRequest_Shell_TruncatesLongFields()
    {
        var longCmd = new string('a', 500);
        var req = MakeShell(fullCommandText: longCmd, intention: "x");

        var detail = AgentPermissionHandler.DescribeRequest(req);

        Assert.Contains("…", detail);
        Assert.True(detail.Length < 600, $"detail unexpectedly long: {detail.Length} chars");
    }

    [Fact]
    public void DescribeRequest_Shell_StripsNewlines()
    {
        var req = MakeShell(fullCommandText: "line1\nline2\rline3", intention: "multi\nline");

        var detail = AgentPermissionHandler.DescribeRequest(req);

        Assert.DoesNotContain('\n', detail);
        Assert.DoesNotContain('\r', detail);
        Assert.Contains("line1 line2 line3", detail);
    }

    [Fact]
    public void DescribeRequest_Write_IncludesFileNameAndDiffPresence()
    {
        var req = MakeWrite(
            toolCallId: "tc-w1",
            fileName: "src/Foo.cs",
            diff: "@@ -1 +1 @@\n-old\n+new",
            intention: "Update Foo");

        var detail = AgentPermissionHandler.DescribeRequest(req);

        Assert.Contains("write", detail);
        Assert.Contains("src/Foo.cs", detail);
        Assert.Contains("has_diff=True", detail);
        Assert.Contains("tc-w1", detail);
    }

    [Fact]
    public void DescribeRequest_Url_IncludesUrl()
    {
        var req = MakeUrl(url: "https://api.github.com/user", intention: "Probe auth");

        var detail = AgentPermissionHandler.DescribeRequest(req);

        Assert.Contains("url", detail);
        // RedactUrl strips path/query (defence in depth — query strings often
        // carry tokens). Host alone is enough signal for triage.
        Assert.Contains("api.github.com", detail);
        Assert.Contains("Probe auth", detail);
    }

    [Fact]
    public void DescribeRequest_Mcp_IncludesServerAndTool()
    {
        var req = MakeMcp(serverName: "github", toolName: "create_pull_request", readOnly: false);

        var detail = AgentPermissionHandler.DescribeRequest(req);

        Assert.Contains("github", detail);
        Assert.Contains("create_pull_request", detail);
        Assert.Contains("read_only=False", detail);
    }

    [Fact]
    public void DescribeRequest_BasePermissionRequest_FallsBack()
    {
        // The handler may receive the bare base type if a future SDK adds a
        // new Kind without a subtype shipping yet. The fallback must not throw
        // and must surface the type name so we can chase it in the SDK.
        var req = new PermissionRequest { Kind = "future-kind" };

        var detail = AgentPermissionHandler.DescribeRequest(req);

        Assert.Contains("unrecognized subtype", detail);
        Assert.Contains("PermissionRequest", detail);
    }

    [Fact]
    public void DescribeRequest_NullFields_RenderAsEmptyOrNull()
    {
        // Defensive: SDK fields come in as empty/null. Description must not NRE.
        var req = MakeShell();

        var detail = AgentPermissionHandler.DescribeRequest(req);

        Assert.Contains("(empty)", detail);
        Assert.Contains("tool_call_id=(null)", detail);
        Assert.Contains("cmd_count=0", detail);
    }

    [Fact]
    public void DescribeRequest_Read_IncludesPath()
    {
        var req = MakeRead(path: "src/Foo.cs", intention: "Inspect file");

        var detail = AgentPermissionHandler.DescribeRequest(req);

        Assert.Contains("read", detail);
        Assert.Contains("src/Foo.cs", detail);
        Assert.Contains("Inspect file", detail);
    }

    [Fact]
    public void DescribeRequest_Memory_IncludesSubjectAndFact()
    {
        var req = MakeMemory(subject: "user-prefs", fact: "Prefers concise responses");

        var detail = AgentPermissionHandler.DescribeRequest(req);

        Assert.Contains("memory", detail);
        Assert.Contains("user-prefs", detail);
        Assert.Contains("Prefers concise responses", detail);
    }

    [Fact]
    public void DescribeRequest_CustomTool_IncludesToolNameAndDescription()
    {
        var req = MakeCustomTool(toolName: "create_pr", description: "Open a PR against develop");

        var detail = AgentPermissionHandler.DescribeRequest(req);

        Assert.Contains("custom-tool", detail);
        Assert.Contains("create_pr", detail);
        Assert.Contains("Open a PR against develop", detail);
    }

    [Fact]
    public void DescribeRequest_Hook_IncludesToolNameAndHookMessage()
    {
        var req = MakeHook(toolName: "pre_tool_use", hookMessage: "Validate tool args");

        var detail = AgentPermissionHandler.DescribeRequest(req);

        Assert.Contains("hook", detail);
        Assert.Contains("pre_tool_use", detail);
        Assert.Contains("Validate tool args", detail);
    }

    // ── Redaction ───────────────────────────────────────────────
    //
    // The denial path is best-effort defence in depth: secrets the agent
    // assembles into a denied command should not bleed into world-readable
    // logs. These tests pin the redaction surface so a future change to
    // SecretPatterns can't silently weaken the contract.

    [Theory]
    [InlineData("git push https://x:ghp_abcdefghijklmnopqrstuvwxyz0123456789AB@github.com/foo")]
    [InlineData("export TOKEN=ghs_abcdefghijklmnopqrstuvwxyz0123456789AB && curl ...")]
    [InlineData("ghu_abcdefghijklmnopqrstuvwxyz0123456789AB")]
    public void Redact_GitHubTokens_AreReplaced(string input)
    {
        var result = AgentPermissionHandler.Redact(input);

        Assert.Contains("[REDACTED]", result);
        Assert.DoesNotContain("ghp_abcdefghijklmnopqrstuvwxyz0123456789AB", result);
        Assert.DoesNotContain("ghs_abcdefghijklmnopqrstuvwxyz0123456789AB", result);
        Assert.DoesNotContain("ghu_abcdefghijklmnopqrstuvwxyz0123456789AB", result);
    }

    [Fact]
    public void Redact_BearerTokens_AreReplaced()
    {
        var result = AgentPermissionHandler.Redact("curl -H 'Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig' https://api");

        Assert.Contains("[REDACTED]", result);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9.payload.sig", result);
    }

    [Theory]
    [InlineData("DB_PASSWORD=hunter2-secret")]
    [InlineData("api_key=sk-proj-abcdefghijklmn")]
    [InlineData("client_secret=oauth-secret-blob")]
    public void Redact_AssignmentSecrets_AreReplaced(string input)
    {
        var result = AgentPermissionHandler.Redact(input);

        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void Redact_OrdinaryText_IsUnchanged()
    {
        // The redactor is high-precision: routine commands must pass through
        // intact, otherwise the diagnostic loses signal.
        var result = AgentPermissionHandler.Redact("git status --porcelain && dotnet build");

        Assert.Equal("git status --porcelain && dotnet build", result);
    }

    [Fact]
    public void RedactUrl_StripsPathAndQuery()
    {
        var result = AgentPermissionHandler.RedactUrl(
            "https://api.github.com/user?access_token=ghp_abcdefghijklmnopqrstuvwxyz0123456789AB");

        Assert.Equal("https://api.github.com/[…]", result);
        Assert.DoesNotContain("access_token", result);
        Assert.DoesNotContain("ghp_", result);
    }

    [Fact]
    public void RedactUrl_NullOrEmpty_ReturnsNullSentinel()
    {
        Assert.Equal("(null)", AgentPermissionHandler.RedactUrl(null));
        Assert.Equal("(null)", AgentPermissionHandler.RedactUrl(string.Empty));
    }

    [Fact]
    public void DescribeRequest_Url_RedactsTokenInQueryString()
    {
        // Integration: the URL describer must use RedactUrl so denied URL
        // requests don't leak tokens that came in via query strings.
        var req = MakeUrl(
            url: "https://api.github.com/repos?access_token=ghp_abcdefghijklmnopqrstuvwxyz0123456789AB",
            intention: "Probe repos");

        var detail = AgentPermissionHandler.DescribeRequest(req);

        Assert.DoesNotContain("ghp_", detail);
        Assert.DoesNotContain("access_token", detail);
        Assert.Contains("api.github.com", detail);
    }

    [Fact]
    public void DescribeRequest_Shell_RedactsTokenInCommand()
    {
        // Integration: shell command text passes through Trunc → Redact.
        var req = MakeShell(
            fullCommandText: "git push https://x:ghp_abcdefghijklmnopqrstuvwxyz0123456789AB@github.com/foo",
            intention: "Push branch");

        var detail = AgentPermissionHandler.DescribeRequest(req);

        Assert.DoesNotContain("ghp_abcdefghijklmnopqrstuvwxyz0123456789AB", detail);
        Assert.Contains("[REDACTED]", detail);
    }
}
