using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentAcademy.Server.Services.AgentWatchdog;

/// <summary>
/// Background service that periodically scans <see cref="IAgentLivenessTracker"/>
/// for stalled agent turns and recovers them. A turn is "stalled" when either
/// (a) <c>now - LastProgressAt &gt; StallThresholdSeconds</c>, or (b) its
/// <c>DenialCount &gt;= MaxDenialsPerTurn</c> (and the trigger is enabled).
///
/// Recovery, in strict order, is best-effort and never throws:
/// <list type="number">
///   <item>Log a structured STALL REPORT (Warning).</item>
///   <item><see cref="IAgentLivenessTracker.TryMarkStalledAndCancel(string, string)"/> —
///   atomic state transition + CTS cancel. Idempotent across ticks.</item>
///   <item>Post a system message to the room (if enabled).</item>
///   <item>Invalidate the agent's SDK session via <see cref="IAgentExecutor.InvalidateSessionAsync"/>
///   so the next turn starts on a clean session.</item>
/// </list>
///
/// The watchdog never awaits the runner. The runner observes the per-turn CTS
/// firing as <see cref="OperationCanceledException"/>, returns an empty
/// <c>AgentTurnResult</c>, and disposes its registration — at which point the
/// tracker drops the entry.
/// </summary>
public sealed class AgentWatchdogService : BackgroundService
{
    private readonly IAgentLivenessTracker _tracker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAgentExecutor _executor;
    private readonly IOptionsMonitor<AgentWatchdogOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentWatchdogService> _logger;

    public AgentWatchdogService(
        IAgentLivenessTracker tracker,
        IServiceScopeFactory scopeFactory,
        IAgentExecutor executor,
        IOptionsMonitor<AgentWatchdogOptions> options,
        TimeProvider time,
        ILogger<AgentWatchdogService> logger)
    {
        _tracker = tracker;
        _scopeFactory = scopeFactory;
        _executor = executor;
        _options = options;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startup = _options.CurrentValue;
        _logger.LogInformation(
            "AgentWatchdog starting (Enabled={Enabled}, StallThresholdSeconds={Stall}, ScanIntervalSeconds={Scan}, MaxDenialsPerTurn={MaxDenials})",
            startup.Enabled, startup.StallThresholdSeconds, startup.ScanIntervalSeconds, startup.MaxDenialsPerTurn);

        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _options.CurrentValue;
            var delaySeconds = Math.Max(1, opts.ScanIntervalSeconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), _time, stoppingToken);
            }
            catch (OperationCanceledException) { break; }

            if (!opts.Enabled) continue;

            try
            {
                await ScanOnceAsync(opts, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AgentWatchdog scan failed");
            }
        }
    }

    /// <summary>
    /// Single scan tick. Internal so tests can drive it deterministically
    /// without spinning the BackgroundService loop.
    /// </summary>
    internal async Task ScanOnceAsync(AgentWatchdogOptions opts, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var stallAfter = TimeSpan.FromSeconds(Math.Max(1, opts.StallThresholdSeconds));
        var denialCap = opts.MaxDenialsPerTurn;

        foreach (var turn in _tracker.Snapshot())
        {
            if (ct.IsCancellationRequested) return;
            if (turn.State != TurnState.Running) continue;

            var quietFor = now - turn.LastProgressAt;
            bool timeStall = quietFor > stallAfter;
            bool denialStall = denialCap > 0 && turn.DenialCount >= denialCap;
            if (!timeStall && !denialStall) continue;

            var reason = timeStall
                ? $"no progress for {(int)quietFor.TotalSeconds}s (threshold={(int)stallAfter.TotalSeconds}s)"
                : $"denial storm: {turn.DenialCount} denials >= {denialCap}";

            _logger.LogWarning(
                "STALL REPORT turn={TurnId} agent={AgentName}({AgentId}) room={RoomId} sprint={SprintId} ageSec={Age} denials={Denials} lastEvent={LastEventKind} reason={Reason}",
                turn.TurnId, turn.AgentName, turn.AgentId, turn.RoomId, turn.SprintId,
                (int)quietFor.TotalSeconds, turn.DenialCount, turn.LastEventKind, reason);

            // Step 2: atomic mark + cancel. If false, another tick already
            // handled this turn; skip the rest so we don't re-post or
            // re-invalidate.
            if (!_tracker.TryMarkStalledAndCancel(turn.TurnId, reason)) continue;

            // Step 3: best-effort room notice.
            if (opts.PostStallNoticeToRoom)
            {
                _ = PostStallNoticeAsync(turn, reason);
            }

            // Step 4: best-effort session invalidation. Fire-and-forget so a
            // slow invalidation does not block other stalled turns in the same
            // scan tick. Errors are logged inside the helper.
            _ = InvalidateSessionAsync(turn);
        }
    }

    private async Task PostStallNoticeAsync(TurnDiagnostic turn, string reason)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
            var content =
                $"⚠️ **Watchdog**: {turn.AgentName} appeared stalled ({reason}). " +
                $"Cancelling the turn and invalidating the session so the next round can recover. " +
                $"Diagnostics: {turn.DenialCount} permission denials, last event `{turn.LastEventKind ?? "(none)"}`.";
            await messageService.PostSystemStatusAsync(turn.RoomId, content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AgentWatchdog could not post stall notice for turn {TurnId} room {RoomId}",
                turn.TurnId, turn.RoomId);
        }
    }

    private async Task InvalidateSessionAsync(TurnDiagnostic turn)
    {
        try
        {
            await _executor.InvalidateSessionAsync(turn.AgentId, turn.RoomId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AgentWatchdog could not invalidate session for agent {AgentId} room {RoomId}",
                turn.AgentId, turn.RoomId);
        }
    }
}
