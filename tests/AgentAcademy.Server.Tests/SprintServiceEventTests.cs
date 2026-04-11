using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for SprintService event broadcasting: metadata enrichment,
/// queue-then-flush ordering, and event persistence.
/// </summary>
public class SprintServiceEventTests : IDisposable
{
    private const string TestWorkspace = "/tmp/test-workspace";
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly ActivityBroadcaster _broadcaster;
    private readonly SprintService _service;
    private readonly List<ActivityEvent> _capturedEvents = [];

    public SprintServiceEventTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _broadcaster = new ActivityBroadcaster();
        _broadcaster.Subscribe(evt => _capturedEvents.Add(evt));

        _service = new SprintService(_db, _broadcaster, NullLogger<SprintService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── CreateSprintAsync event metadata ──────────────────────────

    [Fact]
    public async Task CreateSprint_BroadcastsSprintStartedWithMetadata()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        var evt = Assert.Single(_capturedEvents);
        Assert.Equal(ActivityEventType.SprintStarted, evt.Type);
        Assert.NotNull(evt.Metadata);
        Assert.Equal(sprint.Id, evt.Metadata!["sprintId"]?.ToString());
        Assert.Equal("Active", evt.Metadata["status"]?.ToString());
        Assert.Equal("Intake", evt.Metadata["currentStage"]?.ToString());
    }

    [Fact]
    public async Task CreateSprint_MetadataIncludesSprintNumber()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        var evt = Assert.Single(_capturedEvents);
        Assert.NotNull(evt.Metadata);
        // JsonElement comparison — the value comes through as a JsonElement from the dictionary
        var numberValue = evt.Metadata!["sprintNumber"];
        Assert.Equal(1, Convert.ToInt32(numberValue));
    }

    [Fact]
    public async Task CreateSprint_PersistsMetadataJsonToDatabase()
    {
        await _service.CreateSprintAsync(TestWorkspace);

        var entity = await _db.ActivityEvents.FirstAsync();
        Assert.NotNull(entity.MetadataJson);
        Assert.Contains("sprintId", entity.MetadataJson);
        Assert.Contains("Intake", entity.MetadataJson);
    }

    // ── AdvanceStageAsync event metadata ─────────────────────────

    [Fact]
    public async Task AdvanceStage_SignOffRequired_BroadcastsSignoffRequestedMetadata()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _service.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", "doc");
        _capturedEvents.Clear();

        // Advance from Intake → Planning (requires sign-off by default)
        await _service.AdvanceStageAsync(sprint.Id);

        var evt = Assert.Single(_capturedEvents);
        Assert.Equal(ActivityEventType.SprintStageAdvanced, evt.Type);
        Assert.NotNull(evt.Metadata);
        Assert.Equal("signoff_requested", evt.Metadata!["action"]?.ToString());
        Assert.Equal("Intake", evt.Metadata["currentStage"]?.ToString());
        Assert.Equal("Planning", evt.Metadata["pendingStage"]?.ToString());
        Assert.True(Convert.ToBoolean(evt.Metadata["awaitingSignOff"]));
    }

    [Fact]
    public async Task ApproveAdvance_BroadcastsApprovedMetadata()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _service.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", "doc");
        await _service.AdvanceStageAsync(sprint.Id); // triggers sign-off
        _capturedEvents.Clear();

        await _service.ApproveAdvanceAsync(sprint.Id);

        var evt = Assert.Single(_capturedEvents);
        Assert.NotNull(evt.Metadata);
        Assert.Equal("approved", evt.Metadata!["action"]?.ToString());
        Assert.Equal("Planning", evt.Metadata["currentStage"]?.ToString());
        Assert.False(Convert.ToBoolean(evt.Metadata["awaitingSignOff"]));
    }

    [Fact]
    public async Task RejectAdvance_BroadcastsRejectedMetadata()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _service.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", "doc");
        await _service.AdvanceStageAsync(sprint.Id); // triggers sign-off
        _capturedEvents.Clear();

        await _service.RejectAdvanceAsync(sprint.Id);

        var evt = Assert.Single(_capturedEvents);
        Assert.NotNull(evt.Metadata);
        Assert.Equal("rejected", evt.Metadata!["action"]?.ToString());
        Assert.Equal("Intake", evt.Metadata["currentStage"]?.ToString());
        Assert.False(Convert.ToBoolean(evt.Metadata["awaitingSignOff"]));
    }

    // ── StoreArtifactAsync event metadata ────────────────────────

    [Fact]
    public async Task StoreArtifact_NewArtifact_BroadcastsWithIsUpdateFalse()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        _capturedEvents.Clear();

        await _service.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", "content", "agent-1");

        var evt = Assert.Single(_capturedEvents);
        Assert.Equal(ActivityEventType.SprintArtifactStored, evt.Type);
        Assert.NotNull(evt.Metadata);
        Assert.Equal("Intake", evt.Metadata!["stage"]?.ToString());
        Assert.Equal("RequirementsDocument", evt.Metadata["artifactType"]?.ToString());
        Assert.Equal("agent-1", evt.Metadata["createdByAgentId"]?.ToString());
        Assert.False(Convert.ToBoolean(evt.Metadata["isUpdate"]));
    }

    [Fact]
    public async Task StoreArtifact_UpdateExisting_BroadcastsWithIsUpdateTrue()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _service.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", "v1", "agent-1");
        _capturedEvents.Clear();

        await _service.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", "v2", "agent-1");

        var evt = Assert.Single(_capturedEvents);
        Assert.NotNull(evt.Metadata);
        Assert.True(Convert.ToBoolean(evt.Metadata!["isUpdate"]));
    }

    [Fact]
    public async Task StoreArtifact_MetadataIncludesSprintId()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        _capturedEvents.Clear();

        await _service.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", "content", "agent-1");

        var evt = Assert.Single(_capturedEvents);
        Assert.NotNull(evt.Metadata);
        Assert.Equal(sprint.Id, evt.Metadata!["sprintId"]?.ToString());
    }

    [Fact]
    public async Task StoreArtifact_UpdateExisting_MetadataIncludesArtifactId()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _service.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", "v1", "agent-1");
        _capturedEvents.Clear();

        await _service.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", "v2", "agent-1");

        var evt = Assert.Single(_capturedEvents);
        Assert.NotNull(evt.Metadata);
        Assert.NotNull(evt.Metadata!["artifactId"]);
    }

    // ── CompleteSprint / CancelSprint event metadata ─────────────

    [Fact]
    public async Task CompleteSprint_BroadcastsCompletedStatus()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        _capturedEvents.Clear();

        await _service.CompleteSprintAsync(sprint.Id, force: true);

        var evt = Assert.Single(_capturedEvents);
        Assert.Equal(ActivityEventType.SprintCompleted, evt.Type);
        Assert.NotNull(evt.Metadata);
        Assert.Equal(sprint.Id, evt.Metadata!["sprintId"]?.ToString());
        Assert.Equal("Completed", evt.Metadata["status"]?.ToString());
    }

    [Fact]
    public async Task CancelSprint_BroadcastsCancelledStatus()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        _capturedEvents.Clear();

        await _service.CancelSprintAsync(sprint.Id);

        var evt = Assert.Single(_capturedEvents);
        Assert.Equal(ActivityEventType.SprintCancelled, evt.Type);
        Assert.NotNull(evt.Metadata);
        Assert.Equal("Cancelled", evt.Metadata!["status"]?.ToString());
    }

    // ── Event ordering: flush after save ─────────────────────────

    [Fact]
    public async Task Events_AreBroadcastAfterDatabasePersist()
    {
        // Subscribe to capture the DB state at the moment of broadcast
        bool entityExistedAtBroadcast = false;
        var unsubscribe = _broadcaster.Subscribe(evt =>
        {
            if (evt.Type is ActivityEventType.SprintStarted)
            {
                // At broadcast time, the sprint should already be in the DB
                entityExistedAtBroadcast = _db.Sprints.Any(s => s.WorkspacePath == TestWorkspace);
            }
        });

        await _service.CreateSprintAsync(TestWorkspace);

        Assert.True(entityExistedAtBroadcast,
            "Sprint entity should exist in DB when event is broadcast (flush-after-save)");

        unsubscribe();
    }

    [Fact]
    public async Task Events_ActivityEventPersistedBeforeBroadcast()
    {
        bool activityPersistedAtBroadcast = false;
        var unsubscribe = _broadcaster.Subscribe(evt =>
        {
            if (evt.Type is ActivityEventType.SprintStarted)
            {
                activityPersistedAtBroadcast = _db.ActivityEvents
                    .Any(e => e.Type == nameof(ActivityEventType.SprintStarted));
            }
        });

        await _service.CreateSprintAsync(TestWorkspace);

        Assert.True(activityPersistedAtBroadcast,
            "Activity event entity should be persisted before broadcast");

        unsubscribe();
    }

    [Fact]
    public async Task MultipleOperations_EachFlushesOwnEvents()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        Assert.Single(_capturedEvents);

        await _service.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", "content", "a1");
        Assert.Equal(2, _capturedEvents.Count);

        await _service.AdvanceStageAsync(sprint.Id);
        Assert.Equal(3, _capturedEvents.Count);

        // Verify each event has distinct type/metadata
        Assert.Equal(ActivityEventType.SprintStarted, _capturedEvents[0].Type);
        Assert.Equal(ActivityEventType.SprintArtifactStored, _capturedEvents[1].Type);
        Assert.Equal(ActivityEventType.SprintStageAdvanced, _capturedEvents[2].Type);
    }

    // ── Metadata on all event types includes sprintId ────────────

    [Fact]
    public async Task AllEvents_IncludeSprintIdInMetadata()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _service.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", "c", "a1");
        await _service.AdvanceStageAsync(sprint.Id);
        await _service.ApproveAdvanceAsync(sprint.Id);
        await _service.CompleteSprintAsync(sprint.Id, force: true);

        Assert.Equal(5, _capturedEvents.Count);
        foreach (var evt in _capturedEvents)
        {
            Assert.NotNull(evt.Metadata);
            Assert.Equal(sprint.Id, evt.Metadata!["sprintId"]?.ToString());
        }
    }

    // ── MetadataJson roundtrip in DB ─────────────────────────────

    [Fact]
    public async Task MetadataJson_RoundtripsCorrectly()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        var entities = await _db.ActivityEvents.ToListAsync();
        foreach (var entity in entities)
        {
            Assert.NotNull(entity.MetadataJson);
            Assert.Contains("sprintId", entity.MetadataJson);
            Assert.Contains(sprint.Id, entity.MetadataJson);
        }
    }
}
