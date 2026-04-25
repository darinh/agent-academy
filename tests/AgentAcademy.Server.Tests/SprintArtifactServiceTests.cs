using System.Text.Json;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Unit tests for <see cref="SprintArtifactService"/> — artifact storage,
/// retrieval, and content validation.
/// </summary>
public sealed class SprintArtifactServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly SprintArtifactService _sut;

    public SprintArtifactServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();
        _sut = new SprintArtifactService(
            _db, new ActivityBroadcaster(), NullLogger<SprintArtifactService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private int _sprintCounter;

    private SprintEntity AddActiveSprint(string? id = null)
    {
        var sprint = new SprintEntity
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            Number = 1,
            WorkspacePath = $"/test/{++_sprintCounter}",
            Status = "Active",
            CurrentStage = "Intake",
            CreatedAt = DateTime.UtcNow,
        };
        _db.Sprints.Add(sprint);
        _db.SaveChanges();
        return sprint;
    }

    private SprintEntity AddCompletedSprint(string? id = null)
    {
        var sprint = new SprintEntity
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            Number = 1,
            WorkspacePath = $"/test/{++_sprintCounter}",
            Status = "Completed",
            CurrentStage = "FinalSynthesis",
            CreatedAt = DateTime.UtcNow.AddHours(-2),
            CompletedAt = DateTime.UtcNow,
        };
        _db.Sprints.Add(sprint);
        _db.SaveChanges();
        return sprint;
    }

    private static string ValidRequirements() => JsonSerializer.Serialize(
        new { Title = "T", Description = "D", InScope = new[] { "a" }, OutOfScope = new[] { "b" } });

    private static string ValidSprintPlan() => JsonSerializer.Serialize(
        new { Summary = "S", Phases = new[] { new { Name = "P1", Description = "D1", Deliverables = new[] { "d" } } } });

    private static string ValidValidationReport() => JsonSerializer.Serialize(
        new { Verdict = "Pass", Findings = new[] { "OK" } });

    private static string ValidSprintReport() => JsonSerializer.Serialize(
        new { Summary = "Done", Delivered = new[] { "widget" }, Learnings = new[] { "lesson" } });

    // ═══════════════════════════════════════════════════════════════
    // StoreArtifactAsync — Parameter Validation
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StoreArtifact_NullOrEmptySprintId_Throws(string? sprintId)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _sut.StoreArtifactAsync(sprintId!, "Intake", "RequirementsDocument", ValidRequirements()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StoreArtifact_NullOrEmptyStage_Throws(string? stage)
    {
        var sprint = AddActiveSprint();
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _sut.StoreArtifactAsync(sprint.Id, stage!, "RequirementsDocument", ValidRequirements()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StoreArtifact_NullOrEmptyType_Throws(string? type)
    {
        var sprint = AddActiveSprint();
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _sut.StoreArtifactAsync(sprint.Id, "Intake", type!, ValidRequirements()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StoreArtifact_NullOrEmptyContent_Throws(string? content)
    {
        var sprint = AddActiveSprint();
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _sut.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", content!));
    }

    // ═══════════════════════════════════════════════════════════════
    // StoreArtifactAsync — Sprint Preconditions
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task StoreArtifact_SprintNotFound_ThrowsInvalidOperation()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.StoreArtifactAsync("no-such-sprint", "Intake", "RequirementsDocument", ValidRequirements()));

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task StoreArtifact_CompletedSprint_ThrowsInvalidOperation()
    {
        var sprint = AddCompletedSprint();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", ValidRequirements()));

        Assert.Contains("Completed", ex.Message);
    }

    [Fact]
    public async Task StoreArtifact_CancelledSprint_ThrowsInvalidOperation()
    {
        var sprint = new SprintEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Number = 1,
            WorkspacePath = "/test",
            Status = "Cancelled",
            CurrentStage = "Intake",
            CreatedAt = DateTime.UtcNow,
        };
        _db.Sprints.Add(sprint);
        _db.SaveChanges();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", ValidRequirements()));

        Assert.Contains("Cancelled", ex.Message);
    }

    // ═══════════════════════════════════════════════════════════════
    // StoreArtifactAsync — Stage Validation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task StoreArtifact_InvalidStage_ThrowsArgumentException()
    {
        var sprint = AddActiveSprint();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.StoreArtifactAsync(sprint.Id, "InvalidStage", "RequirementsDocument", ValidRequirements()));

        Assert.Contains("Invalid stage", ex.Message);
    }

    [Theory]
    [InlineData("Intake")]
    [InlineData("Planning")]
    [InlineData("Discussion")]
    [InlineData("Validation")]
    [InlineData("Implementation")]
    [InlineData("FinalSynthesis")]
    public async Task StoreArtifact_AllValidStages_Accepted(string stage)
    {
        var sprint = AddActiveSprint();

        // OverflowRequirements is free-form, works for any stage
        var artifact = await _sut.StoreArtifactAsync(
            sprint.Id, stage, "OverflowRequirements", "anything");

        Assert.Equal(stage, artifact.Stage);
    }

    // ═══════════════════════════════════════════════════════════════
    // StoreArtifactAsync — Type Validation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task StoreArtifact_InvalidType_ThrowsArgumentException()
    {
        var sprint = AddActiveSprint();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.StoreArtifactAsync(sprint.Id, "Intake", "BogusType", "{}"));

        Assert.Contains("Unknown artifact type", ex.Message);
    }

    // ═══════════════════════════════════════════════════════════════
    // StoreArtifactAsync — Create New Artifact
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task StoreArtifact_CreatesEntity_WithCorrectFields()
    {
        var sprint = AddActiveSprint();
        var content = ValidRequirements();

        var artifact = await _sut.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", content, "agent-1");

        Assert.Equal(sprint.Id, artifact.SprintId);
        Assert.Equal("Intake", artifact.Stage);
        Assert.Equal("RequirementsDocument", artifact.Type);
        Assert.Equal(content, artifact.Content);
        Assert.Equal("agent-1", artifact.CreatedByAgentId);
        Assert.True(artifact.Id > 0);
    }

    [Fact]
    public async Task StoreArtifact_NullAgentId_Accepted()
    {
        var sprint = AddActiveSprint();

        var artifact = await _sut.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", ValidRequirements());

        Assert.Null(artifact.CreatedByAgentId);
    }

    [Fact]
    public async Task StoreArtifact_PersistsToDatabase()
    {
        var sprint = AddActiveSprint();

        await _sut.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", ValidRequirements());

        _db.ChangeTracker.Clear();
        var count = await _db.SprintArtifacts.CountAsync(a => a.SprintId == sprint.Id);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task StoreArtifact_EmitsActivityEvent()
    {
        var sprint = AddActiveSprint();

        await _sut.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", ValidRequirements(), "agent-1");

        _db.ChangeTracker.Clear();
        var events = await _db.ActivityEvents
            .Where(e => e.Type == nameof(ActivityEventType.SprintArtifactStored))
            .ToListAsync();

        Assert.Single(events);
        Assert.Contains("RequirementsDocument", events[0].Message);

        // Verify metadata
        var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(events[0].MetadataJson!);
        Assert.NotNull(meta);
        Assert.Equal(sprint.Id, meta["sprintId"].GetString());
        Assert.Equal("Intake", meta["stage"].GetString());
        Assert.Equal("RequirementsDocument", meta["artifactType"].GetString());
        Assert.False(meta["isUpdate"].GetBoolean());
    }

    // ═══════════════════════════════════════════════════════════════
    // StoreArtifactAsync — Update Existing Artifact
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task StoreArtifact_ExistingArtifact_UpdatesContent()
    {
        var sprint = AddActiveSprint();
        var content1 = ValidRequirements();

        var original = await _sut.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", content1);

        var content2 = JsonSerializer.Serialize(
            new { Title = "Updated", Description = "New", InScope = new[] { "x" }, OutOfScope = new[] { "y" } });

        var updated = await _sut.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", content2);

        Assert.Equal(original.Id, updated.Id);
        Assert.Equal(content2, updated.Content);
        Assert.NotNull(updated.UpdatedAt);
    }

    [Fact]
    public async Task StoreArtifact_Update_DoesNotCreateDuplicate()
    {
        var sprint = AddActiveSprint();

        await _sut.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", ValidRequirements());
        await _sut.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", ValidRequirements());

        _db.ChangeTracker.Clear();
        var count = await _db.SprintArtifacts.CountAsync(a => a.SprintId == sprint.Id);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task StoreArtifact_DifferentTypesSameStage_BothStored()
    {
        var sprint = AddActiveSprint();

        await _sut.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", ValidRequirements());
        await _sut.StoreArtifactAsync(
            sprint.Id, "Intake", "OverflowRequirements", "overflow data");

        _db.ChangeTracker.Clear();
        var count = await _db.SprintArtifacts.CountAsync(a => a.SprintId == sprint.Id);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task StoreArtifact_SameTypeDifferentStages_BothStored()
    {
        var sprint = AddActiveSprint();

        await _sut.StoreArtifactAsync(
            sprint.Id, "Intake", "OverflowRequirements", "data1");
        await _sut.StoreArtifactAsync(
            sprint.Id, "Planning", "OverflowRequirements", "data2");

        _db.ChangeTracker.Clear();
        var count = await _db.SprintArtifacts.CountAsync(a => a.SprintId == sprint.Id);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task StoreArtifact_Update_EmitsEventWithIsUpdateTrue()
    {
        var sprint = AddActiveSprint();

        await _sut.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", ValidRequirements());
        await _sut.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", ValidRequirements());

        _db.ChangeTracker.Clear();
        var events = await _db.ActivityEvents
            .Where(e => e.Type == nameof(ActivityEventType.SprintArtifactStored))
            .OrderBy(e => e.OccurredAt)
            .ToListAsync();

        Assert.Equal(2, events.Count);
        var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(events[1].MetadataJson!);
        Assert.True(meta!["isUpdate"].GetBoolean());
    }

    // ═══════════════════════════════════════════════════════════════
    // ValidateArtifactContent — Unknown Type
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ValidateContent_UnknownType_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SprintArtifactService.ValidateArtifactContent("NoSuchType", "{}"));

        Assert.Contains("Unknown artifact type", ex.Message);
    }

    [Fact]
    public void ValidateContent_CaseSensitive_WrongCase_Throws()
    {
        // The parser uses ignoreCase: false
        var ex = Assert.Throws<ArgumentException>(
            () => SprintArtifactService.ValidateArtifactContent("requirementsdocument", ValidRequirements()));

        Assert.Contains("Unknown artifact type", ex.Message);
    }

    // ═══════════════════════════════════════════════════════════════
    // ValidateArtifactContent — Invalid JSON
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("RequirementsDocument")]
    [InlineData("SprintPlan")]
    [InlineData("ValidationReport")]
    [InlineData("SprintReport")]
    public void ValidateContent_MalformedJson_Throws(string type)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SprintArtifactService.ValidateArtifactContent(type, "not json at all"));

        Assert.Contains("Invalid JSON", ex.Message);
    }

    // ═══════════════════════════════════════════════════════════════
    // ValidateArtifactContent — RequirementsDocument
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ValidateContent_RequirementsDocument_ValidContent_Passes()
    {
        var exception = Record.Exception(
            () => SprintArtifactService.ValidateArtifactContent("RequirementsDocument", ValidRequirements()));

        Assert.Null(exception);
    }

    [Fact]
    public void ValidateContent_RequirementsDocument_MissingTitle_Throws()
    {
        var content = JsonSerializer.Serialize(
            new { Title = "", Description = "D", InScope = new[] { "a" }, OutOfScope = new[] { "b" } });

        var ex = Assert.Throws<ArgumentException>(
            () => SprintArtifactService.ValidateArtifactContent("RequirementsDocument", content));

        Assert.Contains("Title", ex.Message);
    }

    [Fact]
    public void ValidateContent_RequirementsDocument_MissingDescription_Throws()
    {
        var content = JsonSerializer.Serialize(
            new { Title = "T", Description = (string?)null, InScope = new[] { "a" }, OutOfScope = new[] { "b" } });

        var ex = Assert.Throws<ArgumentException>(
            () => SprintArtifactService.ValidateArtifactContent("RequirementsDocument", content));

        Assert.Contains("Description", ex.Message);
    }

    [Fact]
    public void ValidateContent_RequirementsDocument_MissingInScope_Throws()
    {
        var content = """{"Title":"T","Description":"D","OutOfScope":["b"]}""";

        var ex = Assert.Throws<ArgumentException>(
            () => SprintArtifactService.ValidateArtifactContent("RequirementsDocument", content));

        Assert.Contains("InScope", ex.Message);
    }

    [Fact]
    public void ValidateContent_RequirementsDocument_MissingOutOfScope_Throws()
    {
        var content = """{"Title":"T","Description":"D","InScope":["a"]}""";

        var ex = Assert.Throws<ArgumentException>(
            () => SprintArtifactService.ValidateArtifactContent("RequirementsDocument", content));

        Assert.Contains("OutOfScope", ex.Message);
    }

    // ═══════════════════════════════════════════════════════════════
    // ValidateArtifactContent — SprintPlan
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ValidateContent_SprintPlan_ValidContent_Passes()
    {
        var exception = Record.Exception(
            () => SprintArtifactService.ValidateArtifactContent("SprintPlan", ValidSprintPlan()));

        Assert.Null(exception);
    }

    [Fact]
    public void ValidateContent_SprintPlan_MissingSummary_Throws()
    {
        var content = JsonSerializer.Serialize(
            new { Summary = "", Phases = new[] { new { Name = "P", Description = "D", Deliverables = new[] { "d" } } } });

        var ex = Assert.Throws<ArgumentException>(
            () => SprintArtifactService.ValidateArtifactContent("SprintPlan", content));

        Assert.Contains("Summary", ex.Message);
    }

    [Fact]
    public void ValidateContent_SprintPlan_MissingPhases_Throws()
    {
        var content = """{"Summary":"S"}""";

        var ex = Assert.Throws<ArgumentException>(
            () => SprintArtifactService.ValidateArtifactContent("SprintPlan", content));

        Assert.Contains("Phases", ex.Message);
    }

    [Fact]
    public void ValidateContent_SprintPlan_PhaseMissingName_Throws()
    {
        var content = JsonSerializer.Serialize(
            new { Summary = "S", Phases = new[] { new { Name = "", Description = "D", Deliverables = new[] { "d" } } } });

        var ex = Assert.Throws<ArgumentException>(
            () => SprintArtifactService.ValidateArtifactContent("SprintPlan", content));

        Assert.Contains("Phases[0].Name", ex.Message);
    }

    [Fact]
    public void ValidateContent_SprintPlan_PhaseMissingDescription_Throws()
    {
        var content = JsonSerializer.Serialize(
            new { Summary = "S", Phases = new[] { new { Name = "P", Description = (string?)null, Deliverables = new[] { "d" } } } });

        var ex = Assert.Throws<ArgumentException>(
            () => SprintArtifactService.ValidateArtifactContent("SprintPlan", content));

        Assert.Contains("Phases[0].Description", ex.Message);
    }

    [Fact]
    public void ValidateContent_SprintPlan_PhaseMissingDeliverables_Throws()
    {
        var content = """{"Summary":"S","Phases":[{"Name":"P","Description":"D"}]}""";

        var ex = Assert.Throws<ArgumentException>(
            () => SprintArtifactService.ValidateArtifactContent("SprintPlan", content));

        Assert.Contains("Phases[0].Deliverables", ex.Message);
    }

    [Fact]
    public void ValidateContent_SprintPlan_EmptyPhases_Passes()
    {
        // Empty phases list is allowed — the list just can't be null
        var content = """{"Summary":"S","Phases":[]}""";

        var exception = Record.Exception(
            () => SprintArtifactService.ValidateArtifactContent("SprintPlan", content));

        Assert.Null(exception);
    }

    // ═══════════════════════════════════════════════════════════════
    // ValidateArtifactContent — ValidationReport
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ValidateContent_ValidationReport_ValidContent_Passes()
    {
        var exception = Record.Exception(
            () => SprintArtifactService.ValidateArtifactContent("ValidationReport", ValidValidationReport()));

        Assert.Null(exception);
    }

    [Fact]
    public void ValidateContent_ValidationReport_MissingVerdict_Throws()
    {
        var content = JsonSerializer.Serialize(
            new { Verdict = "", Findings = new[] { "f" } });

        var ex = Assert.Throws<ArgumentException>(
            () => SprintArtifactService.ValidateArtifactContent("ValidationReport", content));

        Assert.Contains("Verdict", ex.Message);
    }

    [Fact]
    public void ValidateContent_ValidationReport_MissingFindings_Throws()
    {
        var content = """{"Verdict":"Pass"}""";

        var ex = Assert.Throws<ArgumentException>(
            () => SprintArtifactService.ValidateArtifactContent("ValidationReport", content));

        Assert.Contains("Findings", ex.Message);
    }

    // ═══════════════════════════════════════════════════════════════
    // ValidateArtifactContent — SprintReport
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ValidateContent_SprintReport_ValidContent_Passes()
    {
        var exception = Record.Exception(
            () => SprintArtifactService.ValidateArtifactContent("SprintReport", ValidSprintReport()));

        Assert.Null(exception);
    }

    [Fact]
    public void ValidateContent_SprintReport_MissingSummary_Throws()
    {
        var content = JsonSerializer.Serialize(
            new { Summary = "", Delivered = new[] { "d" }, Learnings = new[] { "l" } });

        var ex = Assert.Throws<ArgumentException>(
            () => SprintArtifactService.ValidateArtifactContent("SprintReport", content));

        Assert.Contains("Summary", ex.Message);
    }

    [Fact]
    public void ValidateContent_SprintReport_MissingDelivered_Throws()
    {
        var content = """{"Summary":"S","Learnings":["l"]}""";

        var ex = Assert.Throws<ArgumentException>(
            () => SprintArtifactService.ValidateArtifactContent("SprintReport", content));

        Assert.Contains("Delivered", ex.Message);
    }

    [Fact]
    public void ValidateContent_SprintReport_MissingLearnings_Throws()
    {
        var content = """{"Summary":"S","Delivered":["d"]}""";

        var ex = Assert.Throws<ArgumentException>(
            () => SprintArtifactService.ValidateArtifactContent("SprintReport", content));

        Assert.Contains("Learnings", ex.Message);
    }

    // ═══════════════════════════════════════════════════════════════
    // ValidateArtifactContent — OverflowRequirements
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ValidateContent_OverflowRequirements_AnyContent_Passes()
    {
        var exception = Record.Exception(
            () => SprintArtifactService.ValidateArtifactContent("OverflowRequirements", "literally anything"));

        Assert.Null(exception);
    }

    [Fact]
    public void ValidateContent_OverflowRequirements_PlainText_Passes()
    {
        var exception = Record.Exception(
            () => SprintArtifactService.ValidateArtifactContent("OverflowRequirements", "not even json"));

        Assert.Null(exception);
    }

    // ═══════════════════════════════════════════════════════════════
    // GetSprintArtifactsAsync — Retrieval
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetArtifacts_NoArtifacts_ReturnsEmptyList()
    {
        var sprint = AddActiveSprint();

        var results = await _sut.GetSprintArtifactsAsync(sprint.Id);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetArtifacts_ReturnsAllArtifactsForSprint()
    {
        var sprint = AddActiveSprint();

        await _sut.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", ValidRequirements());
        await _sut.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan", ValidSprintPlan());
        await _sut.StoreArtifactAsync(sprint.Id, "Intake", "OverflowRequirements", "overflow");

        var results = await _sut.GetSprintArtifactsAsync(sprint.Id);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task GetArtifacts_FilteredByStage_ReturnsOnlyMatchingStage()
    {
        var sprint = AddActiveSprint();

        await _sut.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", ValidRequirements());
        await _sut.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan", ValidSprintPlan());

        var results = await _sut.GetSprintArtifactsAsync(sprint.Id, "Intake");

        Assert.Single(results);
        Assert.Equal("Intake", results[0].Stage);
    }

    [Fact]
    public async Task GetArtifacts_InvalidStageFilter_ThrowsArgumentException()
    {
        var sprint = AddActiveSprint();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.GetSprintArtifactsAsync(sprint.Id, "BadStage"));
    }

    [Fact]
    public async Task GetArtifacts_NullStageFilter_ReturnsAllArtifacts()
    {
        var sprint = AddActiveSprint();

        await _sut.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", ValidRequirements());
        await _sut.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan", ValidSprintPlan());

        var results = await _sut.GetSprintArtifactsAsync(sprint.Id, null);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetArtifacts_EmptyStageFilter_ReturnsAllArtifacts()
    {
        var sprint = AddActiveSprint();

        await _sut.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", ValidRequirements());
        await _sut.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan", ValidSprintPlan());

        // Empty string bypasses the stage filter
        var results = await _sut.GetSprintArtifactsAsync(sprint.Id, "");

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetArtifacts_OrderedByCreatedAt()
    {
        var sprint = AddActiveSprint();

        // Store in reverse chronological order
        await _sut.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan", ValidSprintPlan());
        await _sut.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", ValidRequirements());

        var results = await _sut.GetSprintArtifactsAsync(sprint.Id);

        // Both created at DateTime.UtcNow so order may vary, but no exception expected
        Assert.Equal(2, results.Count);
        Assert.True(results[0].CreatedAt <= results[1].CreatedAt);
    }

    [Fact]
    public async Task GetArtifacts_ArtifactsFromOtherSprint_NotIncluded()
    {
        var sprint1 = AddActiveSprint();
        var sprint2 = AddActiveSprint();

        await _sut.StoreArtifactAsync(sprint1.Id, "Intake", "RequirementsDocument", ValidRequirements());
        await _sut.StoreArtifactAsync(sprint2.Id, "Intake", "RequirementsDocument", ValidRequirements());

        var results = await _sut.GetSprintArtifactsAsync(sprint1.Id);

        Assert.Single(results);
        Assert.Equal(sprint1.Id, results[0].SprintId);
    }

    [Fact]
    public async Task GetArtifacts_NonexistentSprint_ReturnsEmpty()
    {
        var results = await _sut.GetSprintArtifactsAsync("does-not-exist");

        Assert.Empty(results);
    }

    // ═══════════════════════════════════════════════════════════════
    // StoreArtifactAsync — All Artifact Types End-to-End
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task StoreArtifact_RequirementsDocument_RoundTrips()
    {
        var sprint = AddActiveSprint();
        var content = ValidRequirements();

        var artifact = await _sut.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", content);

        _db.ChangeTracker.Clear();
        var loaded = await _db.SprintArtifacts.FindAsync(artifact.Id);
        Assert.NotNull(loaded);
        Assert.Equal(content, loaded.Content);
    }

    [Fact]
    public async Task StoreArtifact_SprintPlan_RoundTrips()
    {
        var sprint = AddActiveSprint();
        var content = ValidSprintPlan();

        var artifact = await _sut.StoreArtifactAsync(
            sprint.Id, "Planning", "SprintPlan", content);

        _db.ChangeTracker.Clear();
        var loaded = await _db.SprintArtifacts.FindAsync(artifact.Id);
        Assert.NotNull(loaded);
        Assert.Equal(content, loaded.Content);
    }

    [Fact]
    public async Task StoreArtifact_ValidationReport_RoundTrips()
    {
        var sprint = AddActiveSprint();
        var content = ValidValidationReport();

        var artifact = await _sut.StoreArtifactAsync(
            sprint.Id, "Validation", "ValidationReport", content);

        _db.ChangeTracker.Clear();
        var loaded = await _db.SprintArtifacts.FindAsync(artifact.Id);
        Assert.NotNull(loaded);
        Assert.Equal(content, loaded.Content);
    }

    [Fact]
    public async Task StoreArtifact_SprintReport_RoundTrips()
    {
        var sprint = AddActiveSprint();
        var content = ValidSprintReport();

        var artifact = await _sut.StoreArtifactAsync(
            sprint.Id, "FinalSynthesis", "SprintReport", content);

        _db.ChangeTracker.Clear();
        var loaded = await _db.SprintArtifacts.FindAsync(artifact.Id);
        Assert.NotNull(loaded);
        Assert.Equal(content, loaded.Content);
    }

    [Fact]
    public async Task StoreArtifact_OverflowRequirements_RoundTrips()
    {
        var sprint = AddActiveSprint();
        var content = "free form overflow content";

        var artifact = await _sut.StoreArtifactAsync(
            sprint.Id, "Intake", "OverflowRequirements", content);

        _db.ChangeTracker.Clear();
        var loaded = await _db.SprintArtifacts.FindAsync(artifact.Id);
        Assert.NotNull(loaded);
        Assert.Equal(content, loaded.Content);
    }

    // ═══════════════════════════════════════════════════════════════
    // SelfEvaluationReport — Static (parse-level) validation
    // (P1.4 foundation — DB-aware checks live in the verdict path PR)
    // ═══════════════════════════════════════════════════════════════

    private static string SelfEvalReportJson(
        int attempt = 1,
        SelfEvaluationOverallVerdict overall = SelfEvaluationOverallVerdict.AllPass,
        params SelfEvaluationItem[] items)
    {
        return JsonSerializer.Serialize(new SelfEvaluationReport(
            attempt, items.ToList(), overall, Notes: null));
    }

    private static SelfEvaluationItem PassItem(string taskId = "t1", string crit = "criterion") =>
        new(taskId, crit, SelfEvaluationVerdict.PASS, "evidence-line", FixPlan: null);

    private static SelfEvaluationItem FailItem(string taskId = "t1", string crit = "criterion") =>
        new(taskId, crit, SelfEvaluationVerdict.FAIL, "evidence-line", "fix the thing");

    private static SelfEvaluationItem UnverifiedItem(string taskId = "t1", string crit = "criterion") =>
        new(taskId, crit, SelfEvaluationVerdict.UNVERIFIED, "partial evidence", "verify by running X");

    [Fact]
    public async Task StoreArtifact_SelfEvalReport_AllPass_RoundTrips()
    {
        var sprint = AddActiveSprint();
        var content = SelfEvalReportJson(
            overall: SelfEvaluationOverallVerdict.AllPass,
            items: new[] { PassItem("t1"), PassItem("t2", "another criterion") });

        var artifact = await _sut.StoreArtifactAsync(
            sprint.Id, "Implementation", "SelfEvaluationReport", content);

        _db.ChangeTracker.Clear();
        var loaded = await _db.SprintArtifacts.FindAsync(artifact.Id);
        Assert.NotNull(loaded);
        Assert.Equal(content, loaded.Content);
        Assert.Equal("SelfEvaluationReport", loaded.Type);
        Assert.Equal("Implementation", loaded.Stage);
    }

    [Fact]
    public async Task StoreArtifact_SelfEvalReport_AnyFail_RoundTrips()
    {
        var sprint = AddActiveSprint();
        var content = SelfEvalReportJson(
            overall: SelfEvaluationOverallVerdict.AnyFail,
            items: new[] { PassItem("t1"), FailItem("t2", "fail criterion") });

        var artifact = await _sut.StoreArtifactAsync(
            sprint.Id, "Implementation", "SelfEvaluationReport", content);

        Assert.NotNull(artifact);
    }

    [Fact]
    public async Task StoreArtifact_SelfEvalReport_Unverified_RoundTrips()
    {
        var sprint = AddActiveSprint();
        var content = SelfEvalReportJson(
            overall: SelfEvaluationOverallVerdict.Unverified,
            items: new[] { PassItem("t1"), UnverifiedItem("t2", "unverified criterion") });

        var artifact = await _sut.StoreArtifactAsync(
            sprint.Id, "Implementation", "SelfEvaluationReport", content);

        Assert.NotNull(artifact);
    }

    [Fact]
    public void ValidateSelfEvalReport_InvalidJson_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", "{not json"));
        Assert.Contains("Invalid JSON", ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_AttemptZero_Throws()
    {
        var content = SelfEvalReportJson(attempt: 0, items: PassItem());
        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        Assert.Contains("Attempt", ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_NegativeAttempt_Throws()
    {
        var content = SelfEvalReportJson(attempt: -1, items: PassItem());
        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        Assert.Contains("Attempt", ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_NullItems_Throws()
    {
        var content = JsonSerializer.Serialize(new
        {
            Attempt = 1,
            Items = (object?)null,
            OverallVerdict = "AllPass",
            Notes = (string?)null,
        });
        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        Assert.Contains("Items", ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_EmptyItems_Throws()
    {
        var content = SelfEvalReportJson(items: Array.Empty<SelfEvaluationItem>());
        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        Assert.Contains("at least one entry", ex.Message);
    }

    [Theory]
    [InlineData("TaskId")]
    [InlineData("SuccessCriteria")]
    [InlineData("Evidence")]
    public void ValidateSelfEvalReport_MissingItemField_Throws(string field)
    {
        var item = new SelfEvaluationItem(
            TaskId: field == "TaskId" ? "" : "t1",
            SuccessCriteria: field == "SuccessCriteria" ? "" : "criterion",
            Verdict: SelfEvaluationVerdict.PASS,
            Evidence: field == "Evidence" ? "" : "evidence",
            FixPlan: null);
        var content = SelfEvalReportJson(items: item);

        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_DuplicateTaskId_Throws()
    {
        var content = SelfEvalReportJson(
            overall: SelfEvaluationOverallVerdict.AllPass,
            items: new[] { PassItem("t1"), PassItem("t1", "different criterion") });
        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        Assert.Contains("Duplicate TaskId", ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_FailWithoutFixPlan_Throws()
    {
        var item = new SelfEvaluationItem(
            "t1", "criterion", SelfEvaluationVerdict.FAIL, "evidence", FixPlan: null);
        var content = SelfEvalReportJson(
            overall: SelfEvaluationOverallVerdict.AnyFail, items: item);

        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        Assert.Contains("FixPlan", ex.Message);
        Assert.Contains("FAIL", ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_FailWithEmptyFixPlan_Throws()
    {
        var item = new SelfEvaluationItem(
            "t1", "criterion", SelfEvaluationVerdict.FAIL, "evidence", FixPlan: "   ");
        var content = SelfEvalReportJson(
            overall: SelfEvaluationOverallVerdict.AnyFail, items: item);

        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        Assert.Contains("FixPlan", ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_UnverifiedWithoutFixPlan_Throws()
    {
        var item = new SelfEvaluationItem(
            "t1", "criterion", SelfEvaluationVerdict.UNVERIFIED, "evidence", FixPlan: null);
        var content = SelfEvalReportJson(
            overall: SelfEvaluationOverallVerdict.Unverified, items: item);

        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        Assert.Contains("FixPlan", ex.Message);
        Assert.Contains("UNVERIFIED", ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_RollupAllPass_DeclaredAnyFail_Throws()
    {
        // Items are all PASS but report claims AnyFail — agent cannot lie about rollup.
        var content = SelfEvalReportJson(
            overall: SelfEvaluationOverallVerdict.AnyFail,
            items: new[] { PassItem("t1"), PassItem("t2", "another") });

        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        Assert.Contains("OverallVerdict mismatch", ex.Message);
        Assert.Contains("AllPass", ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_RollupAnyFail_DeclaredAllPass_Throws()
    {
        // One FAIL but report claims AllPass.
        var content = SelfEvalReportJson(
            overall: SelfEvaluationOverallVerdict.AllPass,
            items: new[] { PassItem("t1"), FailItem("t2", "criterion") });

        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        Assert.Contains("OverallVerdict mismatch", ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_RollupUnverified_DeclaredAllPass_Throws()
    {
        // No FAILs but at least one UNVERIFIED → must be Unverified, not AllPass.
        var content = SelfEvalReportJson(
            overall: SelfEvaluationOverallVerdict.AllPass,
            items: new[] { PassItem("t1"), UnverifiedItem("t2", "criterion") });

        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        Assert.Contains("OverallVerdict mismatch", ex.Message);
        Assert.Contains("Unverified", ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_RollupUnverified_DeclaredAnyFail_Throws()
    {
        // No FAILs but at least one UNVERIFIED → must be Unverified, not AnyFail.
        var content = SelfEvalReportJson(
            overall: SelfEvaluationOverallVerdict.AnyFail,
            items: new[] { PassItem("t1"), UnverifiedItem("t2", "criterion") });

        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        Assert.Contains("OverallVerdict mismatch", ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_RollupAnyFailDominatesUnverified_AcceptsAnyFail()
    {
        // Mixed FAIL + UNVERIFIED → AnyFail (FAIL dominates).
        var content = SelfEvalReportJson(
            overall: SelfEvaluationOverallVerdict.AnyFail,
            items: new[]
            {
                PassItem("t1"),
                FailItem("t2", "fail criterion"),
                UnverifiedItem("t3", "unverified criterion"),
            });

        // Should NOT throw — AnyFail is correct when FAIL is present.
        SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content);
    }

    [Fact]
    public void ValidateSelfEvalReport_MissingOverallVerdictField_Throws()
    {
        // Adversarial review (codex 2026-04-25): omitting OverallVerdict from the
        // JSON would silently default to enum 0 (= AllPass) and pass the rollup
        // check if all items are PASS. Defeats the no-lying guarantee.
        var content = JsonSerializer.Serialize(new
        {
            Attempt = 1,
            Items = new[] { new { TaskId = "t1", SuccessCriteria = "c", Verdict = "PASS", Evidence = "e", FixPlan = (string?)null } },
            // OverallVerdict intentionally omitted
        });

        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        Assert.Contains("OverallVerdict", ex.Message);
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_NullOverallVerdictField_Throws()
    {
        var content = JsonSerializer.Serialize(new
        {
            Attempt = 1,
            Items = new[] { new { TaskId = "t1", SuccessCriteria = "c", Verdict = "PASS", Evidence = "e", FixPlan = (string?)null } },
            OverallVerdict = (string?)null,
        });

        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        Assert.Contains("OverallVerdict", ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_MissingItemVerdictField_Throws()
    {
        // Same loophole at item level: omitted Verdict defaults to PASS silently.
        var content = JsonSerializer.Serialize(new
        {
            Attempt = 1,
            Items = new[] { new { TaskId = "t1", SuccessCriteria = "c", Evidence = "e" } },
            OverallVerdict = "AllPass",
        });

        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        Assert.Contains("Verdict", ex.Message);
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_MissingAttemptField_Throws()
    {
        var content = JsonSerializer.Serialize(new
        {
            Items = new[] { new { TaskId = "t1", SuccessCriteria = "c", Verdict = "PASS", Evidence = "e", FixPlan = (string?)null } },
            OverallVerdict = "AllPass",
        });

        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        Assert.Contains("Attempt", ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_NullItemElement_Throws()
    {
        // Adversarial review (codex 2026-04-25): Items: [null] would crash with
        // NullReferenceException instead of returning a controlled validation error.
        var content = "{ \"Attempt\": 1, \"Items\": [null], \"OverallVerdict\": \"AllPass\" }";

        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        Assert.Contains("Items[0]", ex.Message);
        Assert.Contains("null", ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_NonObjectRoot_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", "[]"));
        Assert.Contains("JSON object", ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_NonObjectItem_Throws()
    {
        var content = "{ \"Attempt\": 1, \"Items\": [\"not-an-object\"], \"OverallVerdict\": \"AllPass\" }";

        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        Assert.Contains("Items[0]", ex.Message);
    }

    [Fact]
    public void ValidateSelfEvalReport_ItemsAsObject_Throws()
    {
        // Round-2 review (codex): if Items is an object instead of array, the
        // pre-pass should still produce a controlled error, not crash.
        var content = "{ \"Attempt\": 1, \"Items\": {\"foo\":1}, \"OverallVerdict\": \"AllPass\" }";

        var ex = Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
        // Either rejected at JsonDocument item-check (skipped because not array)
        // and fails downstream at deserialize→Items list check, or fails directly.
        Assert.IsType<ArgumentException>(ex);
    }

    [Fact]
    public void ValidateSelfEvalReport_ItemsAsScalar_Throws()
    {
        var content = "{ \"Attempt\": 1, \"Items\": 42, \"OverallVerdict\": \"AllPass\" }";

        Assert.Throws<ArgumentException>(() =>
            SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content));
    }

    [Fact]
    public void ValidateSelfEvalReport_CamelCasePayload_RoundTrips()
    {
        // Round-2 review (codex): the existing JsonSerializerOptions in
        // ValidateArtifactContent uses PropertyNameCaseInsensitive=true, so
        // camelCase payloads (the JS/TS convention) must continue to round-trip.
        // Regression-guard: the JsonDocument pre-pass is also case-insensitive.
        var content = JsonSerializer.Serialize(new
        {
            attempt = 1,
            items = new[]
            {
                new { taskId = "t1", successCriteria = "c", verdict = "PASS", evidence = "e", fixPlan = (string?)null },
            },
            overallVerdict = "AllPass",
            notes = (string?)null,
        });

        // Should NOT throw — case-insensitive match on every required field.
        SprintArtifactService.ValidateArtifactContent("SelfEvaluationReport", content);
    }

    [Fact]
    public async Task StoreArtifact_SelfEvalReport_BadJson_RejectedAtStore()
    {
        // Defence-in-depth: malformed JSON reaches StoreArtifactAsync from a hostile
        // agent and must be rejected before persistence.
        var sprint = AddActiveSprint();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.StoreArtifactAsync(sprint.Id, "Implementation", "SelfEvaluationReport", "{ \"Attempt\": 1, items: ["));

        Assert.Empty(_db.SprintArtifacts.ToList());
    }

    [Fact]
    public async Task StoreArtifact_SelfEvalReport_RollupMismatch_RejectedAtStore()
    {
        var sprint = AddActiveSprint();
        var content = SelfEvalReportJson(
            overall: SelfEvaluationOverallVerdict.AllPass,
            items: FailItem("t1"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.StoreArtifactAsync(sprint.Id, "Implementation", "SelfEvaluationReport", content));

        Assert.Empty(_db.SprintArtifacts.ToList());
    }
}
