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
        new() { SessionId = sessionId };

    // ── Safe kinds ──────────────────────────────────────────────

    [Theory]
    [InlineData("custom-tool")]
    [InlineData("read")]
    [InlineData("tool")]
    public async Task Create_SafeKind_Approved(string kind)
    {
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, _logger);

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
            new HashSet<string> { "some-tool" }, _logger);

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
            new HashSet<string> { "some-tool" }, _logger);

        var result = await handler(MakeRequest(kind), MakeInvocation(sessionId));

        Assert.Equal(PermissionRequestResultKind.DeniedByRules, result.Kind);
        AgentPermissionHandler.ClearSession(sessionId);
    }

    // ── No tools registered → approve all ───────────────────────

    [Theory]
    [InlineData("shell")]
    [InlineData("write")]
    [InlineData("custom-tool")]
    public async Task Create_NoToolsRegistered_ApprovesEverything(string kind)
    {
        var handler = AgentPermissionHandler.Create(
            new HashSet<string>(), _logger);

        var result = await handler(MakeRequest(kind), MakeInvocation());

        Assert.Equal(PermissionRequestResultKind.Approved, result.Kind);
    }

    // ── Denial log escalation ───────────────────────────────────

    [Fact]
    public async Task Create_DenialLogging_FirstThreeAtWarning()
    {
        var mockLogger = Substitute.For<ILogger>();
        var sessionId = $"log-test-{Guid.NewGuid():N}";
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, mockLogger);

        // Fire 3 denials
        for (var i = 0; i < 3; i++)
            await handler(MakeRequest("shell"), MakeInvocation(sessionId));

        // Verify 3 Warning-level log calls
        mockLogger.Received(3).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        AgentPermissionHandler.ClearSession(sessionId);
    }

    [Fact]
    public async Task Create_DenialLogging_FourthAtWarningWithSuppression()
    {
        var mockLogger = Substitute.For<ILogger>();
        // Enable all log levels
        mockLogger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var sessionId = $"log-test-4th-{Guid.NewGuid():N}";
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, mockLogger);

        // Fire 4 denials
        for (var i = 0; i < 4; i++)
            await handler(MakeRequest("shell"), MakeInvocation(sessionId));

        // 4 Warning calls total (3 regular + 1 suppression)
        mockLogger.Received(4).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        AgentPermissionHandler.ClearSession(sessionId);
    }

    [Fact]
    public async Task Create_DenialLogging_FifthAndBeyondAtDebug()
    {
        var mockLogger = Substitute.For<ILogger>();
        mockLogger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var sessionId = $"log-test-5th-{Guid.NewGuid():N}";
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, mockLogger);

        // Fire 6 denials
        for (var i = 0; i < 6; i++)
            await handler(MakeRequest("shell"), MakeInvocation(sessionId));

        // 4 Warning (first 3 + suppression at 4th), 2 Debug (5th + 6th)
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

        AgentPermissionHandler.ClearSession(sessionId);
    }

    // ── ClearSession ────────────────────────────────────────────

    [Fact]
    public async Task ClearSession_ResetsDenialCount()
    {
        var mockLogger = Substitute.For<ILogger>();
        mockLogger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var sessionId = $"clear-test-{Guid.NewGuid():N}";
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, mockLogger);

        // Fire 5 denials (past suppression threshold)
        for (var i = 0; i < 5; i++)
            await handler(MakeRequest("shell"), MakeInvocation(sessionId));

        // Clear and fire again — should log at Warning (count reset)
        AgentPermissionHandler.ClearSession(sessionId);
        mockLogger.ClearReceivedCalls();

        await handler(MakeRequest("shell"), MakeInvocation(sessionId));

        mockLogger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        AgentPermissionHandler.ClearSession(sessionId);
    }

    [Fact]
    public void ClearSession_NonexistentSession_DoesNotThrow()
    {
        // Should be a no-op for unknown sessions
        AgentPermissionHandler.ClearSession("nonexistent-" + Guid.NewGuid());
    }

    // ── Null session ID ─────────────────────────────────────────

    [Fact]
    public async Task Create_NullSessionId_UsesFallback()
    {
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "some-tool" }, _logger);

        // Should not throw even with null session ID
        var result = await handler(MakeRequest("shell"), MakeInvocation(null));

        Assert.Equal(PermissionRequestResultKind.DeniedByRules, result.Kind);
        // Clean up the "unknown" fallback session bucket
        AgentPermissionHandler.ClearSession("unknown");
    }
}
