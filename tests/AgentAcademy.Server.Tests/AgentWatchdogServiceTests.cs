using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.AgentWatchdog;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for <see cref="AgentWatchdogService"/>'s scan logic. We exercise
/// <c>ScanOnceAsync</c> directly with a <see cref="FakeTimeProvider"/> so
/// the test is fully deterministic.
/// </summary>
public sealed class AgentWatchdogServiceTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly AgentLivenessTracker _tracker;
    private readonly IAgentExecutor _executor = Substitute.For<IAgentExecutor>();
    private readonly IMessageService _messageService = Substitute.For<IMessageService>();
    private readonly IServiceScopeFactory _scopeFactory;

    public AgentWatchdogServiceTests()
    {
        _tracker = new AgentLivenessTracker(_time, NullLogger<AgentLivenessTracker>.Instance);

        // Build a real scope factory that resolves IMessageService to our substitute.
        var sc = new ServiceCollection();
        sc.AddSingleton(_messageService);
        _scopeFactory = sc.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private AgentWatchdogService CreateService() => new(
        _tracker, _scopeFactory, _executor,
        new OptionsMonitorWrapper(new AgentWatchdogOptions()),
        _time,
        NullLogger<AgentWatchdogService>.Instance);

    private static AgentWatchdogOptions Opts(
        bool enabled = true, int stallSec = 90, int scanSec = 30,
        int maxDenials = 10, bool postNotice = true) => new()
        {
            Enabled = enabled,
            StallThresholdSeconds = stallSec,
            ScanIntervalSeconds = scanSec,
            MaxDenialsPerTurn = maxDenials,
            PostStallNoticeToRoom = postNotice,
        };

    [Fact]
    public async Task ScanOnce_StaleTurn_CancelsCts_PostsMessage_InvalidatesSession()
    {
        var cts = new CancellationTokenSource();
        using var reg = _tracker.RegisterTurn("t1", "agent1", "Agent One", "room1", "sprint-x", cts);

        // Push the clock 120s ahead — well past the 90s stall threshold.
        _time.Advance(TimeSpan.FromSeconds(120));

        var svc = CreateService();
        await svc.ScanOnceAsync(Opts(), CancellationToken.None);

        // Best-effort fire-and-forget tasks need a beat to settle.
        await Task.Delay(50);

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(TurnState.StallDetected, _tracker.Snapshot()[0].State);
        await _executor.Received(1).InvalidateSessionAsync("agent1", "room1");
        await _messageService.Received(1).PostSystemStatusAsync("room1", Arg.Is<string>(s => s.Contains("Watchdog") && s.Contains("Agent One")));
    }

    [Fact]
    public async Task ScanOnce_FreshTurn_DoesNothing()
    {
        var cts = new CancellationTokenSource();
        using var reg = _tracker.RegisterTurn("t1", "a", "A", "r", null, cts);
        _time.Advance(TimeSpan.FromSeconds(10)); // well under 90s

        await CreateService().ScanOnceAsync(Opts(), CancellationToken.None);
        await Task.Delay(50);

        Assert.False(cts.IsCancellationRequested);
        Assert.Equal(TurnState.Running, _tracker.Snapshot()[0].State);
        await _executor.DidNotReceive().InvalidateSessionAsync(Arg.Any<string>(), Arg.Any<string?>());
        await _messageService.DidNotReceive().PostSystemStatusAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ScanOnce_DenialStorm_CancelsEvenWithRecentProgress()
    {
        var cts = new CancellationTokenSource();
        using var reg = _tracker.RegisterTurn("t1", "a", "A", "r", null, cts);
        for (int i = 0; i < 10; i++) _tracker.IncrementDenial("t1", "url");
        // Progress was just now (registration time); time barely advanced.
        _time.Advance(TimeSpan.FromSeconds(2));

        await CreateService().ScanOnceAsync(Opts(maxDenials: 10), CancellationToken.None);
        await Task.Delay(50);

        Assert.True(cts.IsCancellationRequested);
        await _executor.Received(1).InvalidateSessionAsync("a", "r");
    }

    [Fact]
    public async Task ScanOnce_AlreadyStalled_DoesNotDuplicateRecovery()
    {
        var cts = new CancellationTokenSource();
        using var reg = _tracker.RegisterTurn("t1", "a", "A", "r", null, cts);
        _time.Advance(TimeSpan.FromSeconds(120));

        var svc = CreateService();
        await svc.ScanOnceAsync(Opts(), CancellationToken.None);
        await svc.ScanOnceAsync(Opts(), CancellationToken.None); // 2nd tick
        await Task.Delay(50);

        // The state moves to StallDetected after the first tick; the 2nd
        // tick's `if (turn.State != TurnState.Running) continue;` filter
        // skips it — so InvalidateSessionAsync still fires only once.
        await _executor.Received(1).InvalidateSessionAsync("a", "r");
        await _messageService.Received(1).PostSystemStatusAsync("r", Arg.Any<string>());
    }

    [Fact]
    public async Task ScanOnce_PostStallNoticeDisabled_StillCancelsAndInvalidates()
    {
        var cts = new CancellationTokenSource();
        using var reg = _tracker.RegisterTurn("t1", "a", "A", "r", null, cts);
        _time.Advance(TimeSpan.FromSeconds(120));

        await CreateService().ScanOnceAsync(Opts(postNotice: false), CancellationToken.None);
        await Task.Delay(50);

        Assert.True(cts.IsCancellationRequested);
        await _executor.Received(1).InvalidateSessionAsync("a", "r");
        await _messageService.DidNotReceive().PostSystemStatusAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ScanOnce_PostMessageThrows_DoesNotPreventInvalidate()
    {
        var cts = new CancellationTokenSource();
        using var reg = _tracker.RegisterTurn("t1", "a", "A", "r", null, cts);
        _time.Advance(TimeSpan.FromSeconds(120));

        _messageService
            .When(m => m.PostSystemStatusAsync(Arg.Any<string>(), Arg.Any<string>()))
            .Do(_ => throw new InvalidOperationException("boom"));

        await CreateService().ScanOnceAsync(Opts(), CancellationToken.None);
        await Task.Delay(100);

        Assert.True(cts.IsCancellationRequested);
        await _executor.Received(1).InvalidateSessionAsync("a", "r");
    }

    [Fact]
    public async Task ScanOnce_MultipleStalledTurns_HandlesEach()
    {
        var c1 = new CancellationTokenSource();
        var c2 = new CancellationTokenSource();
        using var r1 = _tracker.RegisterTurn("t1", "a1", "A1", "r1", null, c1);
        using var r2 = _tracker.RegisterTurn("t2", "a2", "A2", "r2", null, c2);
        _time.Advance(TimeSpan.FromSeconds(120));

        await CreateService().ScanOnceAsync(Opts(), CancellationToken.None);
        await Task.Delay(50);

        Assert.True(c1.IsCancellationRequested);
        Assert.True(c2.IsCancellationRequested);
        await _executor.Received(1).InvalidateSessionAsync("a1", "r1");
        await _executor.Received(1).InvalidateSessionAsync("a2", "r2");
    }

    [Fact]
    public async Task ScanOnce_CompletedTurn_IsIgnored()
    {
        var cts = new CancellationTokenSource();
        var reg = _tracker.RegisterTurn("t1", "a", "A", "r", null, cts);
        reg.Dispose(); // marks Completed and removes
        _time.Advance(TimeSpan.FromSeconds(120));

        await CreateService().ScanOnceAsync(Opts(), CancellationToken.None);
        await Task.Delay(50);

        await _executor.DidNotReceive().InvalidateSessionAsync(Arg.Any<string>(), Arg.Any<string?>());
    }

    private sealed class OptionsMonitorWrapper : IOptionsMonitor<AgentWatchdogOptions>
    {
        private readonly AgentWatchdogOptions _value;
        public OptionsMonitorWrapper(AgentWatchdogOptions value) { _value = value; }
        public AgentWatchdogOptions CurrentValue => _value;
        public AgentWatchdogOptions Get(string? name) => _value;
        public IDisposable? OnChange(Action<AgentWatchdogOptions, string?> listener) => null;
    }
}
