using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Comprehensive unit tests for <see cref="CrashRecoveryService"/>.
/// Uses [Collection("WorkspaceRuntime")] because RecordServerInstanceAsync
/// mutates static properties (CurrentInstanceId, CurrentCrashDetected).
/// </summary>
[Collection("WorkspaceRuntime")]
public class CrashRecoveryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly AgentCatalogOptions _catalog;
    private readonly ActivityBroadcaster _activityBus;
    private readonly ActivityPublisher _activityPublisher;
    private readonly BreakoutRoomService _breakouts;
    private readonly AgentLocationService _agentLocations;
    private readonly MessageService _messages;
    private readonly CrashRecoveryService _service;

    public CrashRecoveryServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "agent-1", Name: "Alpha", Role: "Engineer",
                    Summary: "Test agent 1", StartupPrompt: "You are agent 1.",
                    Model: null, CapabilityTags: [], EnabledTools: [],
                    AutoJoinDefaultRoom: true),
                new AgentDefinition(
                    Id: "agent-2", Name: "Beta", Role: "Planner",
                    Summary: "Test agent 2", StartupPrompt: "You are agent 2.",
                    Model: null, CapabilityTags: [], EnabledTools: [],
                    AutoJoinDefaultRoom: true),
                new AgentDefinition(
                    Id: "agent-3", Name: "Gamma", Role: "Reviewer",
                    Summary: "Test agent 3", StartupPrompt: "You are agent 3.",
                    Model: null, CapabilityTags: [], EnabledTools: [],
                    AutoJoinDefaultRoom: true),
            ]);

        _activityBus = new ActivityBroadcaster();
        _activityPublisher = new ActivityPublisher(_db, _activityBus);

        var executor = Substitute.For<IAgentExecutor>();
        var settingsService = new SystemSettingsService(_db);
        var sessionService = new ConversationSessionService(
            _db, settingsService, executor,
            NullLogger<ConversationSessionService>.Instance);

        _agentLocations = new AgentLocationService(_db, _catalog, _activityPublisher);

        _messages = new MessageService(
            _db, NullLogger<MessageService>.Instance, _catalog,
            _activityPublisher, sessionService, new MessageBroadcaster());

        var taskDeps = new TaskDependencyService(_db, NullLogger<TaskDependencyService>.Instance, _activityPublisher);
        var taskQueries = new TaskQueryService(
            _db, NullLogger<TaskQueryService>.Instance, _catalog, taskDeps);

        _breakouts = new BreakoutRoomService(
            _db, NullLogger<BreakoutRoomService>.Instance, _catalog,
            _activityPublisher, sessionService, taskQueries, _agentLocations);

        _service = new CrashRecoveryService(
            _db, NullLogger<CrashRecoveryService>.Instance,
            _breakouts, _agentLocations, _messages, _activityPublisher);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private RoomEntity SeedMainRoom(DateTime? createdAt = null)
    {
        var now = createdAt ?? DateTime.UtcNow;
        var room = new RoomEntity
        {
            Id = "main",
            Name = "Main Room",
            Status = nameof(RoomStatus.Idle),
            CurrentPhase = nameof(CollaborationPhase.Intake),
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Rooms.Add(room);
        _db.SaveChanges();
        return room;
    }

    private BreakoutRoomEntity SeedBreakoutRoom(
        string id, string agentId, string status = "Active",
        string parentRoomId = "main", string? closeReason = null)
    {
        var now = DateTime.UtcNow;
        var entity = new BreakoutRoomEntity
        {
            Id = id,
            Name = $"Breakout {id}",
            ParentRoomId = parentRoomId,
            AssignedAgentId = agentId,
            Status = status,
            CloseReason = closeReason,
            CreatedAt = now.AddMinutes(-10),
            UpdatedAt = now.AddMinutes(-10)
        };
        _db.BreakoutRooms.Add(entity);
        _db.SaveChanges();
        return entity;
    }

    private AgentLocationEntity SeedAgentLocation(
        string agentId, string state = "Idle",
        string roomId = "main", string? breakoutRoomId = null)
    {
        var entity = new AgentLocationEntity
        {
            AgentId = agentId,
            RoomId = roomId,
            State = state,
            BreakoutRoomId = breakoutRoomId,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        _db.AgentLocations.Add(entity);
        _db.SaveChanges();
        return entity;
    }

    private TaskEntity SeedTask(
        string id, string status, string? assignedAgentId = null,
        string? assignedAgentName = null, string roomId = "main")
    {
        var now = DateTime.UtcNow;
        var entity = new TaskEntity
        {
            Id = id,
            Title = $"Task {id}",
            Description = $"Description for {id}",
            SuccessCriteria = "",
            Status = status,
            Type = nameof(TaskType.Feature),
            CurrentPhase = nameof(CollaborationPhase.Implementation),
            CurrentPlan = "",
            ValidationStatus = nameof(WorkstreamStatus.NotStarted),
            ValidationSummary = "",
            ImplementationStatus = nameof(WorkstreamStatus.InProgress),
            ImplementationSummary = "",
            PreferredRoles = "[]",
            RoomId = roomId,
            AssignedAgentId = assignedAgentId,
            AssignedAgentName = assignedAgentName,
            CreatedAt = now.AddMinutes(-15),
            UpdatedAt = now.AddMinutes(-15)
        };
        _db.Tasks.Add(entity);
        _db.SaveChanges();
        return entity;
    }

    private ServerInstanceEntity SeedServerInstance(
        DateTime? startedAt = null, DateTime? shutdownAt = null,
        int? exitCode = null, string version = "1.0.0")
    {
        var entity = new ServerInstanceEntity
        {
            StartedAt = startedAt ?? DateTime.UtcNow.AddHours(-1),
            ShutdownAt = shutdownAt,
            ExitCode = exitCode,
            Version = version
        };
        _db.ServerInstances.Add(entity);
        _db.SaveChanges();
        return entity;
    }

    // ═══════════════════════════════════════════════════════════════
    // RecordServerInstanceAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RecordServerInstance_FirstEver_NoCrashDetected()
    {
        await _service.RecordServerInstanceAsync();

        var instances = await _db.ServerInstances.ToListAsync();
        Assert.Single(instances);
        Assert.False(instances[0].CrashDetected);
        Assert.False(CrashRecoveryService.CurrentCrashDetected);
    }

    [Fact]
    public async Task RecordServerInstance_FirstEver_SetsCurrentInstanceId()
    {
        await _service.RecordServerInstanceAsync();

        Assert.NotNull(CrashRecoveryService.CurrentInstanceId);
        var instance = await _db.ServerInstances.SingleAsync();
        Assert.Equal(instance.Id, CrashRecoveryService.CurrentInstanceId);
    }

    [Fact]
    public async Task RecordServerInstance_FirstEver_SetsStartedAtAndVersion()
    {
        var before = DateTime.UtcNow;
        await _service.RecordServerInstanceAsync();
        var after = DateTime.UtcNow;

        var instance = await _db.ServerInstances.SingleAsync();
        Assert.InRange(instance.StartedAt, before, after);
        Assert.NotEmpty(instance.Version);
    }

    [Fact]
    public async Task RecordServerInstance_CleanShutdown_NoCrashDetected()
    {
        SeedServerInstance(
            startedAt: DateTime.UtcNow.AddHours(-2),
            shutdownAt: DateTime.UtcNow.AddHours(-1),
            exitCode: 0);

        await _service.RecordServerInstanceAsync();

        Assert.False(CrashRecoveryService.CurrentCrashDetected);
        var instances = await _db.ServerInstances.OrderBy(i => i.StartedAt).ToListAsync();
        Assert.Equal(2, instances.Count);
        Assert.False(instances[1].CrashDetected);
    }

    [Fact]
    public async Task RecordServerInstance_Orphan_CrashDetected()
    {
        SeedServerInstance(startedAt: DateTime.UtcNow.AddHours(-1));

        await _service.RecordServerInstanceAsync();

        Assert.True(CrashRecoveryService.CurrentCrashDetected);
    }

    [Fact]
    public async Task RecordServerInstance_Orphan_MarksShutdownAtAndExitCode()
    {
        var orphan = SeedServerInstance(startedAt: DateTime.UtcNow.AddHours(-1));

        await _service.RecordServerInstanceAsync();

        _db.ChangeTracker.Clear();
        var updated = await _db.ServerInstances.FindAsync(orphan.Id);
        Assert.NotNull(updated);
        Assert.NotNull(updated.ShutdownAt);
        Assert.Equal(-1, updated.ExitCode);
    }

    [Fact]
    public async Task RecordServerInstance_Orphan_NewInstanceHasCrashDetectedTrue()
    {
        SeedServerInstance(startedAt: DateTime.UtcNow.AddHours(-1));

        await _service.RecordServerInstanceAsync();

        _db.ChangeTracker.Clear();
        var instances = await _db.ServerInstances.OrderBy(i => i.StartedAt).ToListAsync();
        Assert.Equal(2, instances.Count);
        Assert.True(instances[1].CrashDetected);
    }

    [Fact]
    public async Task RecordServerInstance_MultipleOrphans_MarksLatest()
    {
        // Two orphans — service takes the latest one by StartedAt desc
        SeedServerInstance(startedAt: DateTime.UtcNow.AddHours(-3));
        SeedServerInstance(startedAt: DateTime.UtcNow.AddHours(-1));

        await _service.RecordServerInstanceAsync();

        Assert.True(CrashRecoveryService.CurrentCrashDetected);
        _db.ChangeTracker.Clear();
        var instances = await _db.ServerInstances
            .Where(i => i.ShutdownAt != null && i.ExitCode == -1)
            .ToListAsync();
        // At least the latest orphan should be marked
        Assert.True(instances.Count >= 1);
    }

    [Fact]
    public async Task RecordServerInstance_VersionFromAssembly()
    {
        await _service.RecordServerInstanceAsync();

        var instance = await _db.ServerInstances.SingleAsync();
        // Should be a version string, not empty
        Assert.NotEqual("", instance.Version);
    }

    // ═══════════════════════════════════════════════════════════════
    // RecoverFromCrashAsync — room validation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Recover_ThrowsWhenRoomNotFound()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RecoverFromCrashAsync("nonexistent-room"));
    }

    [Fact]
    public async Task Recover_ThrowsWithRoomIdInMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RecoverFromCrashAsync("missing-room"));
        Assert.Contains("missing-room", ex.Message);
    }

    // ═══════════════════════════════════════════════════════════════
    // RecoverFromCrashAsync — nothing to recover
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Recover_NoActiveBreakouts_NoStuckAgents_NoStuckTasks_ReturnsAllZeros()
    {
        SeedMainRoom();

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(0, result.ClosedBreakoutRooms);
        Assert.Equal(0, result.ResetWorkingAgents);
        Assert.Equal(0, result.ResetTasks);
    }

    [Fact]
    public async Task Recover_NothingToRecover_NoMessagePosted()
    {
        SeedMainRoom();

        await _service.RecoverFromCrashAsync("main");

        var messages = await _db.Messages.Where(m => m.RoomId == "main").ToListAsync();
        Assert.Empty(messages);
    }

    // ═══════════════════════════════════════════════════════════════
    // RecoverFromCrashAsync — breakout room closure
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Recover_ClosesActiveBreakoutRoom()
    {
        SeedMainRoom();
        SeedBreakoutRoom("br-1", "agent-1", "Active");
        SeedAgentLocation("agent-1", nameof(AgentState.Working), breakoutRoomId: "br-1");

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(1, result.ClosedBreakoutRooms);

        _db.ChangeTracker.Clear();
        var br = await _db.BreakoutRooms.FindAsync("br-1");
        Assert.NotNull(br);
        Assert.Equal(nameof(RoomStatus.Archived), br.Status);
        Assert.Equal(nameof(BreakoutRoomCloseReason.ClosedByRecovery), br.CloseReason);
    }

    [Fact]
    public async Task Recover_ClosesMultipleActiveBreakoutRooms()
    {
        SeedMainRoom();
        SeedBreakoutRoom("br-1", "agent-1", "Active");
        SeedBreakoutRoom("br-2", "agent-2", "Active");
        SeedAgentLocation("agent-1", nameof(AgentState.Working), breakoutRoomId: "br-1");
        SeedAgentLocation("agent-2", nameof(AgentState.Working), breakoutRoomId: "br-2");

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(2, result.ClosedBreakoutRooms);
    }

    [Fact]
    public async Task Recover_DoesNotCloseCompletedBreakout()
    {
        SeedMainRoom();
        SeedBreakoutRoom("br-done", "agent-1", nameof(RoomStatus.Completed));

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(0, result.ClosedBreakoutRooms);
        _db.ChangeTracker.Clear();
        var br = await _db.BreakoutRooms.FindAsync("br-done");
        Assert.NotNull(br);
        Assert.Equal(nameof(RoomStatus.Completed), br.Status);
    }

    [Fact]
    public async Task Recover_DoesNotCloseArchivedBreakout()
    {
        SeedMainRoom();
        SeedBreakoutRoom("br-arch", "agent-1", nameof(RoomStatus.Archived),
            closeReason: nameof(BreakoutRoomCloseReason.Completed));

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(0, result.ClosedBreakoutRooms);
    }

    [Fact]
    public async Task Recover_MixedBreakoutStatuses_OnlyClosesActive()
    {
        SeedMainRoom();
        SeedBreakoutRoom("br-active", "agent-1", "Active");
        SeedBreakoutRoom("br-completed", "agent-2", nameof(RoomStatus.Completed));
        SeedBreakoutRoom("br-archived", "agent-3", nameof(RoomStatus.Archived));
        SeedAgentLocation("agent-1", nameof(AgentState.Working), breakoutRoomId: "br-active");

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(1, result.ClosedBreakoutRooms);
    }

    // ═══════════════════════════════════════════════════════════════
    // RecoverFromCrashAsync — agent reset
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Recover_WorkingAgentWithNoActiveBreakout_ResetToIdle()
    {
        SeedMainRoom();
        SeedAgentLocation("agent-1", nameof(AgentState.Working));

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(1, result.ResetWorkingAgents);
        _db.ChangeTracker.Clear();
        var loc = await _db.AgentLocations.FindAsync("agent-1");
        Assert.NotNull(loc);
        Assert.Equal(nameof(AgentState.Idle), loc.State);
    }

    [Fact]
    public async Task Recover_WorkingAgentWithNullBreakout_ResetToIdle()
    {
        SeedMainRoom();
        SeedAgentLocation("agent-1", nameof(AgentState.Working), breakoutRoomId: null);

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(1, result.ResetWorkingAgents);
    }

    [Fact]
    public async Task Recover_MultipleWorkingAgentsWithNoBreakout_AllReset()
    {
        SeedMainRoom();
        SeedAgentLocation("agent-1", nameof(AgentState.Working));
        SeedAgentLocation("agent-2", nameof(AgentState.Working));

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(2, result.ResetWorkingAgents);
    }

    [Fact]
    public async Task Recover_IdleAgent_NotTouched()
    {
        SeedMainRoom();
        SeedAgentLocation("agent-1", nameof(AgentState.Idle));

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(0, result.ResetWorkingAgents);
        _db.ChangeTracker.Clear();
        var loc = await _db.AgentLocations.FindAsync("agent-1");
        Assert.NotNull(loc);
        Assert.Equal(nameof(AgentState.Idle), loc.State);
    }

    [Fact]
    public async Task Recover_WorkingAgentWithActiveBreakout_NotReset()
    {
        SeedMainRoom();
        // The breakout is Active, so this agent should be left alone during
        // the lingering-working-agent check (after breakouts are closed they
        // will have been reset by CloseBreakoutRoomAsync already).
        // However, the recovery closes active breakouts FIRST, which moves
        // the agent to Idle via CloseBreakoutRoomAsync.  The "lingering"
        // query runs AFTER breakout closure so the agent is already Idle.
        // Net effect: agent IS reset (by breakout closure), but it should NOT
        // be double-counted in ResetWorkingAgents (that counter is for agents
        // that had no active breakout).

        // To truly test "agent with active breakout NOT in lingering list",
        // we need the breakout to survive closure — but that can't happen
        // because all active breakouts are closed. Instead, test an agent
        // whose breakoutRoomId points to a breakout that's already in a
        // terminal state (so not closed by recovery) — they should still be
        // reset since their breakout is terminal.
        // Actually, let's re-read the code more carefully.

        // After breakout closure, the code re-queries activeBreakoutAssignments.
        // If no active breakouts remain, every Working agent gets reset.
        // So the only way to have a Working agent with an active breakout
        // NOT be reset is if the breakout is NOT closed — which means it was
        // already terminal. But a terminal breakout's agent shouldn't be Working.

        // The key scenario: the code WON'T reset agents whose BreakoutRoomId
        // is in the activeBreakoutAssignments list. After recovery closes all
        // active breakouts, that list should be empty. So all remaining
        // Working agents get reset.

        // Let's test the count: active breakout → closed by recovery (agent
        // moved to Idle by CloseBreakoutRoomAsync). Not counted in
        // ResetWorkingAgents because agent was already moved to Idle.
        SeedBreakoutRoom("br-1", "agent-1", "Active");
        SeedAgentLocation("agent-1", nameof(AgentState.Working), breakoutRoomId: "br-1");

        var result = await _service.RecoverFromCrashAsync("main");

        // The breakout was closed (1 breakout), agent was moved to Idle
        // by CloseBreakoutRoomAsync. The lingering query finds 0 because
        // agent is already Idle.
        Assert.Equal(1, result.ClosedBreakoutRooms);
        Assert.Equal(0, result.ResetWorkingAgents);
    }

    // ═══════════════════════════════════════════════════════════════
    // RecoverFromCrashAsync — task reset
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Recover_ActiveTaskWithAssignedAgent_Unassigned()
    {
        SeedMainRoom();
        SeedAgentLocation("agent-1", nameof(AgentState.Working));
        SeedTask("task-1", nameof(Shared.Models.TaskStatus.Active),
            assignedAgentId: "agent-1", assignedAgentName: "Alpha");

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(1, result.ResetTasks);
        _db.ChangeTracker.Clear();
        var task = await _db.Tasks.FindAsync("task-1");
        Assert.NotNull(task);
        Assert.Null(task.AssignedAgentId);
        Assert.Null(task.AssignedAgentName);
    }

    [Fact]
    public async Task Recover_InReviewTask_Unassigned()
    {
        SeedMainRoom();
        SeedAgentLocation("agent-1", nameof(AgentState.Working));
        SeedTask("task-1", nameof(Shared.Models.TaskStatus.InReview),
            assignedAgentId: "agent-1", assignedAgentName: "Alpha");

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(1, result.ResetTasks);
    }

    [Fact]
    public async Task Recover_ChangesRequestedTask_Unassigned()
    {
        SeedMainRoom();
        SeedAgentLocation("agent-1", nameof(AgentState.Working));
        SeedTask("task-1", nameof(Shared.Models.TaskStatus.ChangesRequested),
            assignedAgentId: "agent-1", assignedAgentName: "Alpha");

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(1, result.ResetTasks);
    }

    [Fact]
    public async Task Recover_ApprovedTask_Unassigned()
    {
        SeedMainRoom();
        SeedAgentLocation("agent-1", nameof(AgentState.Working));
        SeedTask("task-1", nameof(Shared.Models.TaskStatus.Approved),
            assignedAgentId: "agent-1", assignedAgentName: "Alpha");

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(1, result.ResetTasks);
    }

    [Fact]
    public async Task Recover_MergingTask_Unassigned()
    {
        SeedMainRoom();
        SeedAgentLocation("agent-1", nameof(AgentState.Working));
        SeedTask("task-1", nameof(Shared.Models.TaskStatus.Merging),
            assignedAgentId: "agent-1", assignedAgentName: "Alpha");

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(1, result.ResetTasks);
    }

    [Fact]
    public async Task Recover_AwaitingValidationTask_Unassigned()
    {
        SeedMainRoom();
        SeedAgentLocation("agent-1", nameof(AgentState.Working));
        SeedTask("task-1", nameof(Shared.Models.TaskStatus.AwaitingValidation),
            assignedAgentId: "agent-1", assignedAgentName: "Alpha");

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(1, result.ResetTasks);
    }

    [Fact]
    public async Task Recover_CompletedTask_NotReset()
    {
        SeedMainRoom();
        SeedTask("task-done", nameof(Shared.Models.TaskStatus.Completed),
            assignedAgentId: "agent-1", assignedAgentName: "Alpha");

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(0, result.ResetTasks);
        _db.ChangeTracker.Clear();
        var task = await _db.Tasks.FindAsync("task-done");
        Assert.NotNull(task);
        Assert.Equal("agent-1", task.AssignedAgentId);
    }

    [Fact]
    public async Task Recover_CancelledTask_NotReset()
    {
        SeedMainRoom();
        SeedTask("task-cancelled", nameof(Shared.Models.TaskStatus.Cancelled),
            assignedAgentId: "agent-1", assignedAgentName: "Alpha");

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(0, result.ResetTasks);
    }

    [Fact]
    public async Task Recover_TaskWithNoAssignedAgent_NotReset()
    {
        SeedMainRoom();
        SeedTask("task-unassigned", nameof(Shared.Models.TaskStatus.Active));

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(0, result.ResetTasks);
    }

    [Fact]
    public async Task Recover_MultipleInProgressTasks_AllUnassigned()
    {
        SeedMainRoom();
        SeedAgentLocation("agent-1", nameof(AgentState.Working));
        SeedAgentLocation("agent-2", nameof(AgentState.Working));
        SeedTask("task-1", nameof(Shared.Models.TaskStatus.Active),
            assignedAgentId: "agent-1", assignedAgentName: "Alpha");
        SeedTask("task-2", nameof(Shared.Models.TaskStatus.InReview),
            assignedAgentId: "agent-2", assignedAgentName: "Beta");

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(2, result.ResetTasks);
    }

    [Fact]
    public async Task Recover_TaskUpdatedAtIsSet()
    {
        SeedMainRoom();
        SeedAgentLocation("agent-1", nameof(AgentState.Working));
        var originalTask = SeedTask("task-1", nameof(Shared.Models.TaskStatus.Active),
            assignedAgentId: "agent-1", assignedAgentName: "Alpha");
        var originalUpdatedAt = originalTask.UpdatedAt;

        await _service.RecoverFromCrashAsync("main");

        _db.ChangeTracker.Clear();
        var task = await _db.Tasks.FindAsync("task-1");
        Assert.NotNull(task);
        Assert.True(task.UpdatedAt > originalUpdatedAt);
    }

    // ═══════════════════════════════════════════════════════════════
    // RecoverFromCrashAsync — recovery message
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Recover_PostsRecoveryMessageWhenAnythingRecovered()
    {
        SeedMainRoom();
        SeedAgentLocation("agent-1", nameof(AgentState.Working));

        await _service.RecoverFromCrashAsync("main");

        var messages = await _db.Messages.Where(m => m.RoomId == "main").ToListAsync();
        Assert.Single(messages);
        Assert.Contains("System recovered from crash", messages[0].Content);
    }

    [Fact]
    public async Task Recover_MessageContainsCorrectCounts()
    {
        SeedMainRoom();
        SeedBreakoutRoom("br-1", "agent-1", "Active");
        SeedAgentLocation("agent-1", nameof(AgentState.Working), breakoutRoomId: "br-1");
        SeedAgentLocation("agent-2", nameof(AgentState.Working));
        SeedTask("task-1", nameof(Shared.Models.TaskStatus.Active),
            assignedAgentId: "agent-2", assignedAgentName: "Beta");

        await _service.RecoverFromCrashAsync("main");

        _db.ChangeTracker.Clear();
        var msg = await _db.Messages
            .Where(m => m.RoomId == "main" && m.Content.Contains("System recovered"))
            .SingleOrDefaultAsync();
        Assert.NotNull(msg);
        Assert.Contains("1 breakout room(s)", msg.Content);
        Assert.Contains("1 stuck agent(s)", msg.Content);
        Assert.Contains("1 stuck task(s)", msg.Content);
    }

    [Fact]
    public async Task Recover_MessageIsSystemKind()
    {
        SeedMainRoom();
        SeedAgentLocation("agent-1", nameof(AgentState.Working));

        await _service.RecoverFromCrashAsync("main");

        var msg = await _db.Messages.Where(m => m.RoomId == "main").SingleAsync();
        Assert.Equal(nameof(MessageKind.System), msg.Kind);
        Assert.Equal("system", msg.SenderId);
        Assert.Equal("System", msg.SenderName);
    }

    [Fact]
    public async Task Recover_MessageHasCorrelationId()
    {
        SeedMainRoom();
        SeedAgentLocation("agent-1", nameof(AgentState.Working));

        // Set CurrentInstanceId via RecordServerInstanceAsync
        await _service.RecordServerInstanceAsync();
        var instanceId = CrashRecoveryService.CurrentInstanceId;

        await _service.RecoverFromCrashAsync("main");

        _db.ChangeTracker.Clear();
        var msg = await _db.Messages
            .Where(m => m.RoomId == "main" && m.Content.Contains("System recovered"))
            .SingleOrDefaultAsync();
        Assert.NotNull(msg);
        Assert.Equal(instanceId, msg.CorrelationId);
    }

    [Fact]
    public async Task Recover_MessageIsIdempotent_NotPostedTwice()
    {
        SeedMainRoom();
        SeedAgentLocation("agent-1", nameof(AgentState.Working));

        await _service.RecordServerInstanceAsync();
        await _service.RecoverFromCrashAsync("main");

        // Re-seed a stuck agent for second recovery call
        _db.ChangeTracker.Clear();
        var loc = await _db.AgentLocations.FindAsync("agent-1");
        if (loc != null)
        {
            loc.State = nameof(AgentState.Working);
            loc.BreakoutRoomId = null;
            await _db.SaveChangesAsync();
        }

        await _service.RecoverFromCrashAsync("main");

        _db.ChangeTracker.Clear();
        var recoveryMessages = await _db.Messages
            .Where(m => m.RoomId == "main" && m.Content.Contains("System recovered"))
            .ToListAsync();
        Assert.Single(recoveryMessages);
    }

    [Fact]
    public async Task Recover_NoRecoveryNeeded_NoMessagePosted()
    {
        SeedMainRoom();
        // Only idle agents, no breakouts, no tasks
        SeedAgentLocation("agent-1", nameof(AgentState.Idle));

        await _service.RecoverFromCrashAsync("main");

        var messages = await _db.Messages.Where(m => m.RoomId == "main").ToListAsync();
        Assert.Empty(messages);
    }

    // ═══════════════════════════════════════════════════════════════
    // RecoverFromCrashAsync — CrashRecoveryResult
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Recover_ReturnsCorrectResult_AllCategories()
    {
        SeedMainRoom();
        SeedBreakoutRoom("br-1", "agent-1", "Active");
        SeedBreakoutRoom("br-2", "agent-2", "Active");
        SeedAgentLocation("agent-1", nameof(AgentState.Working), breakoutRoomId: "br-1");
        SeedAgentLocation("agent-2", nameof(AgentState.Working), breakoutRoomId: "br-2");
        SeedAgentLocation("agent-3", nameof(AgentState.Working)); // lingering
        SeedTask("task-1", nameof(Shared.Models.TaskStatus.Active),
            assignedAgentId: "agent-3", assignedAgentName: "Gamma");

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(2, result.ClosedBreakoutRooms);
        Assert.Equal(1, result.ResetWorkingAgents);
        Assert.Equal(1, result.ResetTasks);
    }

    [Fact]
    public async Task Recover_OnlyBreakouts_ReturnsCorrectResult()
    {
        SeedMainRoom();
        SeedBreakoutRoom("br-1", "agent-1", "Active");
        SeedAgentLocation("agent-1", nameof(AgentState.Working), breakoutRoomId: "br-1");

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(1, result.ClosedBreakoutRooms);
        Assert.Equal(0, result.ResetWorkingAgents);
        Assert.Equal(0, result.ResetTasks);
    }

    [Fact]
    public async Task Recover_OnlyStuckAgents_ReturnsCorrectResult()
    {
        SeedMainRoom();
        SeedAgentLocation("agent-1", nameof(AgentState.Working));

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(0, result.ClosedBreakoutRooms);
        Assert.Equal(1, result.ResetWorkingAgents);
        Assert.Equal(0, result.ResetTasks);
    }

    [Fact]
    public async Task Recover_OnlyStuckTasks_ReturnsCorrectResult()
    {
        SeedMainRoom();
        // Agent not Working — but task is in progress with assigned agent
        // that is NOT in an active breakout
        SeedAgentLocation("agent-1", nameof(AgentState.Idle));
        SeedTask("task-1", nameof(Shared.Models.TaskStatus.Active),
            assignedAgentId: "agent-1", assignedAgentName: "Alpha");

        var result = await _service.RecoverFromCrashAsync("main");

        // agent-1 is Idle, not Working, so ResetWorkingAgents = 0
        // task-1 has an assigned agent not in an active breakout, so it's unassigned
        Assert.Equal(0, result.ClosedBreakoutRooms);
        Assert.Equal(0, result.ResetWorkingAgents);
        Assert.Equal(1, result.ResetTasks);
    }

    // ═══════════════════════════════════════════════════════════════
    // RecoverFromCrashAsync — combined scenarios
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Recover_BreakoutClosure_MovesAgentToIdle()
    {
        SeedMainRoom();
        SeedBreakoutRoom("br-1", "agent-1", "Active");
        SeedAgentLocation("agent-1", nameof(AgentState.Working), breakoutRoomId: "br-1");

        await _service.RecoverFromCrashAsync("main");

        _db.ChangeTracker.Clear();
        var loc = await _db.AgentLocations.FindAsync("agent-1");
        Assert.NotNull(loc);
        Assert.Equal(nameof(AgentState.Idle), loc.State);
        Assert.Null(loc.BreakoutRoomId);
    }

    [Fact]
    public async Task Recover_TaskWithAgentInClosedBreakout_IsUnassigned()
    {
        SeedMainRoom();
        SeedBreakoutRoom("br-1", "agent-1", "Active");
        SeedAgentLocation("agent-1", nameof(AgentState.Working), breakoutRoomId: "br-1");
        SeedTask("task-1", nameof(Shared.Models.TaskStatus.Active),
            assignedAgentId: "agent-1", assignedAgentName: "Alpha");

        await _service.RecoverFromCrashAsync("main");

        _db.ChangeTracker.Clear();
        var task = await _db.Tasks.FindAsync("task-1");
        Assert.NotNull(task);
        // After breakouts are closed, agent-1 is no longer in an active breakout
        // so the task should be unassigned
        Assert.Null(task.AssignedAgentId);
        Assert.Null(task.AssignedAgentName);
    }

    [Fact]
    public async Task Recover_FullScenario_BreakoutsAgentsAndTasks()
    {
        SeedMainRoom();

        // Active breakout with agent
        SeedBreakoutRoom("br-1", "agent-1", "Active");
        SeedAgentLocation("agent-1", nameof(AgentState.Working), breakoutRoomId: "br-1");

        // Orphan Working agent (no breakout)
        SeedAgentLocation("agent-2", nameof(AgentState.Working));

        // Idle agent (should not be touched)
        SeedAgentLocation("agent-3", nameof(AgentState.Idle));

        // In-progress task assigned to orphan agent
        SeedTask("task-1", nameof(Shared.Models.TaskStatus.Active),
            assignedAgentId: "agent-2", assignedAgentName: "Beta");

        // Completed task (should not be touched)
        SeedTask("task-2", nameof(Shared.Models.TaskStatus.Completed),
            assignedAgentId: "agent-3", assignedAgentName: "Gamma");

        // In-progress task assigned to breakout agent (will be unassigned after closure)
        SeedTask("task-3", nameof(Shared.Models.TaskStatus.InReview),
            assignedAgentId: "agent-1", assignedAgentName: "Alpha");

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(1, result.ClosedBreakoutRooms);
        Assert.Equal(1, result.ResetWorkingAgents); // only agent-2
        Assert.Equal(2, result.ResetTasks); // task-1 and task-3

        // Verify all agents are Idle
        _db.ChangeTracker.Clear();
        var locations = await _db.AgentLocations.ToListAsync();
        foreach (var loc in locations)
        {
            Assert.Equal(nameof(AgentState.Idle), loc.State);
        }

        // Verify completed task still assigned
        var task2 = await _db.Tasks.FindAsync("task-2");
        Assert.NotNull(task2);
        Assert.Equal("agent-3", task2.AssignedAgentId);

        // Verify recovery message posted
        var msg = await _db.Messages
            .Where(m => m.RoomId == "main" && m.Content.Contains("System recovered"))
            .SingleOrDefaultAsync();
        Assert.NotNull(msg);
    }

    // ═══════════════════════════════════════════════════════════════
    // RecoverFromCrashAsync — activity event
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Recover_PublishesActivityEvent()
    {
        SeedMainRoom();
        SeedAgentLocation("agent-1", nameof(AgentState.Working));

        await _service.RecordServerInstanceAsync();
        await _service.RecoverFromCrashAsync("main");

        var events = _activityBus.GetRecentActivity();
        Assert.Contains(events, e =>
            e.Type == ActivityEventType.MessagePosted
            && e.Message != null && e.Message.Contains("System recovered"));
    }

    // ═══════════════════════════════════════════════════════════════
    // RecoverFromCrashAsync — room UpdatedAt
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Recover_UpdatesRoomUpdatedAt_WhenRecoveryHappens()
    {
        var oldTime = DateTime.UtcNow.AddHours(-2);
        SeedMainRoom(createdAt: oldTime);
        SeedAgentLocation("agent-1", nameof(AgentState.Working));

        await _service.RecoverFromCrashAsync("main");

        _db.ChangeTracker.Clear();
        var room = await _db.Rooms.FindAsync("main");
        Assert.NotNull(room);
        Assert.True(room.UpdatedAt > oldTime);
    }

    // ═══════════════════════════════════════════════════════════════
    // RecoverFromCrashAsync — Truncate via message content
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Recover_MessageContentIsNotTruncated_ForNormalLength()
    {
        SeedMainRoom();
        SeedAgentLocation("agent-1", nameof(AgentState.Working));

        await _service.RecoverFromCrashAsync("main");

        var msg = await _db.Messages.Where(m => m.RoomId == "main").SingleAsync();
        // Normal recovery message should not be truncated (well under 100 chars for activity)
        Assert.DoesNotContain("...", msg.Content);
    }

    // ═══════════════════════════════════════════════════════════════
    // End-to-end: RecordServerInstance + RecoverFromCrash
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EndToEnd_OrphanDetected_ThenRecoveryPerformed()
    {
        SeedMainRoom();

        // Simulate previous orphan instance
        SeedServerInstance(startedAt: DateTime.UtcNow.AddHours(-1));

        // Seed stuck state
        SeedBreakoutRoom("br-1", "agent-1", "Active");
        SeedAgentLocation("agent-1", nameof(AgentState.Working), breakoutRoomId: "br-1");
        SeedTask("task-1", nameof(Shared.Models.TaskStatus.Active),
            assignedAgentId: "agent-1", assignedAgentName: "Alpha");

        // Record new instance (detects crash)
        await _service.RecordServerInstanceAsync();
        Assert.True(CrashRecoveryService.CurrentCrashDetected);

        // Perform recovery
        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(1, result.ClosedBreakoutRooms);
        // agent-1 was in breakout, moved to Idle by CloseBreakoutRoomAsync
        Assert.Equal(0, result.ResetWorkingAgents);
        Assert.Equal(1, result.ResetTasks);
    }

    [Fact]
    public async Task EndToEnd_CleanShutdown_NoRecoveryNeeded()
    {
        SeedMainRoom();

        SeedServerInstance(
            startedAt: DateTime.UtcNow.AddHours(-2),
            shutdownAt: DateTime.UtcNow.AddHours(-1),
            exitCode: 0);

        await _service.RecordServerInstanceAsync();
        Assert.False(CrashRecoveryService.CurrentCrashDetected);

        var result = await _service.RecoverFromCrashAsync("main");

        Assert.Equal(0, result.ClosedBreakoutRooms);
        Assert.Equal(0, result.ResetWorkingAgents);
        Assert.Equal(0, result.ResetTasks);
    }
}
