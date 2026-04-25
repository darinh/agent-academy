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

    // ── Denial log escalation (per-closure) ─────────────────────

    [Fact]
    public async Task Create_DenialLogging_FirstThreeAtWarning()
    {
        var mockLogger = Substitute.For<ILogger>();
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, mockLogger, new TestDoubles.NoOpAgentLivenessTracker());

        // Fire 3 denials
        for (var i = 0; i < 3; i++)
            await handler(MakeRequest("shell"), MakeInvocation());

        mockLogger.Received(3).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task Create_DenialLogging_FourthAtWarningWithSuppression()
    {
        var mockLogger = Substitute.For<ILogger>();
        mockLogger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, mockLogger, new TestDoubles.NoOpAgentLivenessTracker());

        for (var i = 0; i < 4; i++)
            await handler(MakeRequest("shell"), MakeInvocation());

        // 4 Warning calls total (3 regular + 1 suppression notice).
        mockLogger.Received(4).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task Create_DenialLogging_FifthAndBeyondAtDebug()
    {
        var mockLogger = Substitute.For<ILogger>();
        mockLogger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, mockLogger, new TestDoubles.NoOpAgentLivenessTracker());

        for (var i = 0; i < 6; i++)
            await handler(MakeRequest("shell"), MakeInvocation());

        // 4 Warning (first 3 + suppression at 4th), 2 Debug (5th + 6th).
        mockLogger.Received(4).Log(
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
        // Each Create() call returns a handler with its own denial counter,
        // scoped to that session and GC'd with it. Two handlers do not share
        // state, even when invoked with the same sessionId.
        var loggerA = Substitute.For<ILogger>();
        loggerA.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var loggerB = Substitute.For<ILogger>();
        loggerB.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

        var handlerA = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, loggerA, new TestDoubles.NoOpAgentLivenessTracker());
        var handlerB = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, loggerB, new TestDoubles.NoOpAgentLivenessTracker());

        // Fire 4 denials on handler A — should see 4 Warning logs
        // (3 escalating + 1 suppression notice).
        for (var i = 0; i < 4; i++)
            await handlerA(MakeRequest("shell"), MakeInvocation("shared-session"));

        loggerA.Received(4).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Handler B has a fresh counter — first denial logs at Warning #1.
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
