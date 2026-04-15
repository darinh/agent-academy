using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Handles server instance lifecycle tracking and crash recovery:
/// detecting unclean shutdowns, closing orphaned breakout rooms,
/// resetting stuck agents, and resetting abandoned tasks.
/// </summary>
public sealed class CrashRecoveryService : ICrashRecoveryService
{
    private static readonly HashSet<string> InProgressStatuses = new(StringComparer.Ordinal)
    {
        nameof(Shared.Models.TaskStatus.Active),
        nameof(Shared.Models.TaskStatus.InReview),
        nameof(Shared.Models.TaskStatus.ChangesRequested),
        nameof(Shared.Models.TaskStatus.Approved),
        nameof(Shared.Models.TaskStatus.Merging),
        nameof(Shared.Models.TaskStatus.AwaitingValidation),
    };

    private static readonly HashSet<string> TerminalBreakoutStatuses =
        BreakoutRoomService.TerminalBreakoutStatuses;

    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<CrashRecoveryService> _logger;
    private readonly IBreakoutRoomService _breakouts;
    private readonly IAgentLocationService _agentLocations;
    private readonly IMessageService _messages;
    private readonly IActivityPublisher _activity;

    public CrashRecoveryService(
        AgentAcademyDbContext db,
        ILogger<CrashRecoveryService> logger,
        IBreakoutRoomService breakouts,
        IAgentLocationService agentLocations,
        IMessageService messages,
        IActivityPublisher activity)
    {
        _db = db;
        _logger = logger;
        _breakouts = breakouts;
        _agentLocations = agentLocations;
        _messages = messages;
        _activity = activity;
    }

    /// <summary>
    /// The ID of the current server instance. Set during
    /// <see cref="RecordServerInstanceAsync"/>.
    /// Used by the health endpoint for client reconnect protocol.
    /// </summary>
    public static string? CurrentInstanceId { get; private set; }

    /// <summary>
    /// Whether a crash was detected on the most recent startup
    /// (previous instance had no clean shutdown).
    /// </summary>
    public static bool CurrentCrashDetected { get; private set; }

    /// <summary>
    /// Records a new server instance and detects if the previous one crashed
    /// (had no clean shutdown). The current instance ID is stored in
    /// <see cref="CurrentInstanceId"/> for the health endpoint.
    /// </summary>
    public async Task RecordServerInstanceAsync()
    {
        var version = typeof(CrashRecoveryService).Assembly
            .GetName().Version?.ToString() ?? "0.0.0";

        var orphan = await _db.ServerInstances
            .Where(si => si.ShutdownAt == null)
            .OrderByDescending(si => si.StartedAt)
            .FirstOrDefaultAsync();

        var crashDetected = false;
        if (orphan is not null)
        {
            orphan.ShutdownAt = DateTime.UtcNow;
            orphan.ExitCode = -1;
            crashDetected = true;

            _logger.LogWarning(
                "Previous server instance {InstanceId} (started {StartedAt}) did not shut down cleanly — marking as crashed",
                orphan.Id, orphan.StartedAt);
        }

        var instance = new ServerInstanceEntity
        {
            StartedAt = DateTime.UtcNow,
            CrashDetected = crashDetected,
            Version = version
        };

        _db.ServerInstances.Add(instance);
        await _db.SaveChangesAsync();

        CurrentInstanceId = instance.Id;
        CurrentCrashDetected = crashDetected;

        _logger.LogInformation(
            "Server instance {InstanceId} started (version {Version}, crash detected: {Crash})",
            instance.Id, version, crashDetected);
    }

    public async Task<CrashRecoveryResult> RecoverFromCrashAsync(string mainRoomId)
    {
        var mainRoom = await _db.Rooms.FindAsync(mainRoomId)
            ?? throw new InvalidOperationException($"Room '{mainRoomId}' not found");

        var activeBreakoutIds = await _db.BreakoutRooms
            .Where(br => !TerminalBreakoutStatuses.Contains(br.Status))
            .OrderBy(br => br.CreatedAt)
            .Select(br => br.Id)
            .ToListAsync();

        foreach (var breakoutId in activeBreakoutIds)
        {
            await _breakouts.CloseBreakoutRoomAsync(breakoutId, BreakoutRoomCloseReason.ClosedByRecovery);
        }

        var activeBreakoutAssignments = await _db.BreakoutRooms
            .Where(br => !TerminalBreakoutStatuses.Contains(br.Status))
            .Select(br => br.Id)
            .ToListAsync();

        var lingeringWorkingAgents = await _db.AgentLocations
            .Where(loc => loc.State == nameof(AgentState.Working)
                && (loc.BreakoutRoomId == null || !activeBreakoutAssignments.Contains(loc.BreakoutRoomId)))
            .OrderBy(loc => loc.AgentId)
            .ToListAsync();

        foreach (var location in lingeringWorkingAgents)
        {
            await _agentLocations.MoveAgentAsync(location.AgentId, location.RoomId, AgentState.Idle);
        }

        var activeAssigneeIds = await _db.AgentLocations
            .Where(loc => loc.State == nameof(AgentState.Working)
                && loc.BreakoutRoomId != null
                && activeBreakoutAssignments.Contains(loc.BreakoutRoomId))
            .Select(loc => loc.AgentId)
            .ToListAsync();

        var recoverableTasks = await _db.Tasks
            .Where(task => InProgressStatuses.Contains(task.Status)
                && !string.IsNullOrEmpty(task.AssignedAgentId)
                && !activeAssigneeIds.Contains(task.AssignedAgentId!))
            .OrderBy(task => task.CreatedAt)
            .ToListAsync();

        foreach (var task in recoverableTasks)
        {
            task.AssignedAgentId = null;
            task.AssignedAgentName = null;
            task.UpdatedAt = DateTime.UtcNow;
        }

        var now = DateTime.UtcNow;
        var recoveredAnything = activeBreakoutIds.Count > 0
            || lingeringWorkingAgents.Count > 0
            || recoverableTasks.Count > 0;

        if (recoveredAnything)
        {
            var message = $"System recovered from crash. Closed {activeBreakoutIds.Count} breakout room(s), reset {lingeringWorkingAgents.Count} stuck agent(s), and reset {recoverableTasks.Count} stuck task(s).";
            var recoveryCorrelationId = CurrentInstanceId;
            var alreadyNotified = !string.IsNullOrWhiteSpace(recoveryCorrelationId)
                && await _db.Messages.AnyAsync(m => m.RoomId == mainRoomId && m.CorrelationId == recoveryCorrelationId);

            if (!alreadyNotified)
            {
                var entity = _messages.CreateMessageEntity(mainRoomId, MessageKind.System, message, recoveryCorrelationId, now);
                _db.Messages.Add(entity);
                mainRoom.UpdatedAt = now;

                _activity.Publish(ActivityEventType.MessagePosted, mainRoomId, null, null,
                    $"System: {Truncate(message, 100)}", recoveryCorrelationId);
            }
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Crash recovery completed for room {RoomId}: closed {BreakoutCount} breakouts, reset {AgentCount} stuck agents, reset {TaskCount} stuck tasks (notification posted: {Posted})",
            mainRoomId, activeBreakoutIds.Count, lingeringWorkingAgents.Count, recoverableTasks.Count, recoveredAnything);

        return new CrashRecoveryResult(activeBreakoutIds.Count, lingeringWorkingAgents.Count, recoverableTasks.Count);
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
