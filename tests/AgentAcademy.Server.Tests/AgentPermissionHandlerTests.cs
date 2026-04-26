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
}
