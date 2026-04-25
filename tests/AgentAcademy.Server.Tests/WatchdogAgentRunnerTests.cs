using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.AgentWatchdog;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Unit tests for <see cref="WatchdogAgentRunner"/>: the helper that wraps
/// <see cref="IAgentExecutor.RunAsync"/> with watchdog liveness registration
/// for direct callers outside the conversation round loop.
/// </summary>
public class WatchdogAgentRunnerTests
{
    private static AgentDefinition Agent(string id = "agent-x", string name = "AgentX") =>
        new(Id: id, Name: name, Role: "Test", Summary: "t", StartupPrompt: "go",
            Model: null, CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: false);

    [Fact]
    public async Task RunAsync_RegistersTurn_AndForwardsToExecutor()
    {
        var executor = Substitute.For<IAgentExecutor>();
        var tracker = new TestTracker();
        var sut = new WatchdogAgentRunner(executor, tracker, NullLogger<WatchdogAgentRunner>.Instance);
        executor
            .RunAsync(Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("hi");

        var result = await sut.RunAsync(Agent(), "prompt", "room-1", sprintId: "sprint-9");

        Assert.Equal("hi", result);
        Assert.Single(tracker.Registered);
        var reg = tracker.Registered[0];
        Assert.Equal("agent-x", reg.AgentId);
        Assert.Equal("AgentX", reg.AgentName);
        Assert.Equal("room-1", reg.RoomId);
        Assert.Equal("sprint-9", reg.SprintId);
        Assert.True(reg.Disposed, "tracker registration must be disposed when runner returns");
        Assert.False(string.IsNullOrEmpty(reg.TurnId));
        await executor.Received(1).RunAsync(
            Arg.Any<AgentDefinition>(), "prompt", "room-1",
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), reg.TurnId);
    }

    [Fact]
    public async Task RunAsync_NullRoomId_UsesSyntheticTrackedRoomId()
    {
        var executor = Substitute.For<IAgentExecutor>();
        var tracker = new TestTracker();
        var sut = new WatchdogAgentRunner(executor, tracker, NullLogger<WatchdogAgentRunner>.Instance);
        executor.RunAsync(default!, default!, default, default, default, default).ReturnsForAnyArgs("ok");

        await sut.RunAsync(Agent(id: "summarizer"), "prompt", roomId: null);

        var reg = Assert.Single(tracker.Registered);
        Assert.Equal("out-of-band:summarizer", reg.RoomId);
        // Executor still receives the original null roomId (so SDK doesn't see the synthetic)
        await executor.Received(1).RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), (string?)null,
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task RunAsync_OuterCancellation_PropagatesOperationCanceledException()
    {
        var executor = Substitute.For<IAgentExecutor>();
        var tracker = new TestTracker();
        var sut = new WatchdogAgentRunner(executor, tracker, NullLogger<WatchdogAgentRunner>.Instance);
        using var outer = new CancellationTokenSource();
        outer.Cancel();
        executor.RunAsync(default!, default!, default, default, default, default).ReturnsForAnyArgs<string>(
            _ => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.RunAsync(Agent(), "p", "r", cancellationToken: outer.Token));
    }

    [Fact]
    public async Task RunAsync_WatchdogCancellation_ReturnsEmptyString()
    {
        var executor = Substitute.For<IAgentExecutor>();
        // Tracker that immediately cancels the registered CTS to simulate the
        // watchdog killing the turn mid-flight.
        var tracker = new CancellingTracker();
        var sut = new WatchdogAgentRunner(executor, tracker, NullLogger<WatchdogAgentRunner>.Instance);
        executor.RunAsync(default!, default!, default, default, default, default).ReturnsForAnyArgs<string>(
            ci =>
            {
                var ct = ci.Arg<CancellationToken>();
                ct.ThrowIfCancellationRequested();
                return "should-not-return-this";
            });

        var result = await sut.RunAsync(Agent(), "p", "r");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task RunAsync_QuotaException_PropagatesToCaller()
    {
        // Quota handling is caller-specific; the runner must not swallow it.
        var executor = Substitute.For<IAgentExecutor>();
        var tracker = new TestTracker();
        var sut = new WatchdogAgentRunner(executor, tracker, NullLogger<WatchdogAgentRunner>.Instance);
        executor.RunAsync(default!, default!, default, default, default, default)
            .ThrowsForAnyArgs(new AgentQuotaExceededException("agent-x", "rpm", "rate", 30));

        await Assert.ThrowsAsync<AgentQuotaExceededException>(() =>
            sut.RunAsync(Agent(), "p", "r"));
    }

    private sealed record Registration(
        string TurnId, string AgentId, string AgentName, string RoomId,
        string? SprintId, CancellationTokenSource Cts)
    {
        public bool Disposed { get; set; }
    }

    private sealed class TestTracker : IAgentLivenessTracker
    {
        public List<Registration> Registered { get; } = new();

        public IDisposable RegisterTurn(string turnId, string agentId, string agentName,
            string roomId, string? sprintId, CancellationTokenSource cts)
        {
            var reg = new Registration(turnId, agentId, agentName, roomId, sprintId, cts);
            Registered.Add(reg);
            return new Token(() => reg.Disposed = true);
        }

        public void NoteProgress(string turnId, string kind) { }
        public void NoteEvent(string turnId, string kind) { }
        public int IncrementDenial(string turnId, string kind) => 0;
        public void NoteProgressBySessionId(string? sessionId, string kind) { }
        public void NoteEventBySessionId(string? sessionId, string kind) { }
        public int IncrementDenialBySessionId(string? sessionId, string kind) => -1;
        public void LinkSession(string sessionId, string turnId) { }
        public void UnlinkSession(string sessionId) { }
        public IReadOnlyList<TurnDiagnostic> Snapshot() => Array.Empty<TurnDiagnostic>();
        public bool TryMarkStalledAndCancel(string turnId, string reason) => false;

        private sealed class Token : IDisposable
        {
            private readonly Action _onDispose;
            public Token(Action onDispose) { _onDispose = onDispose; }
            public void Dispose() => _onDispose();
        }
    }

    /// <summary>Tracker variant that cancels the supplied CTS at registration time.</summary>
    private sealed class CancellingTracker : IAgentLivenessTracker
    {
        public IDisposable RegisterTurn(string turnId, string agentId, string agentName,
            string roomId, string? sprintId, CancellationTokenSource cts)
        {
            cts.Cancel();
            return new NoOpDisposable();
        }
        public void NoteProgress(string turnId, string kind) { }
        public void NoteEvent(string turnId, string kind) { }
        public int IncrementDenial(string turnId, string kind) => 0;
        public void NoteProgressBySessionId(string? sessionId, string kind) { }
        public void NoteEventBySessionId(string? sessionId, string kind) { }
        public int IncrementDenialBySessionId(string? sessionId, string kind) => -1;
        public void LinkSession(string sessionId, string turnId) { }
        public void UnlinkSession(string sessionId) { }
        public IReadOnlyList<TurnDiagnostic> Snapshot() => Array.Empty<TurnDiagnostic>();
        public bool TryMarkStalledAndCancel(string turnId, string reason) => false;

        private sealed class NoOpDisposable : IDisposable { public void Dispose() { } }
    }
}
