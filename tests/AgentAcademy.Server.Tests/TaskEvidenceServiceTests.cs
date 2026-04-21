using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Tests;

public class TaskEvidenceServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly ActivityBroadcaster _bus;
    private readonly ActivityPublisher _activity;
    private readonly TaskEvidenceService _sut;

    private const string TaskId = "task-001";
    private const string AgentId = "engineer-1";
    private const string AgentName = "Hephaestus";
    private const string RoomId = "room-1";

    public TaskEvidenceServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _bus = new ActivityBroadcaster();
        _activity = new ActivityPublisher(_db, _bus);
        _sut = new TaskEvidenceService(_db, _activity);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void EnsureRoom(string roomId = RoomId)
    {
        if (_db.Rooms.Find(roomId) is not null) return;
        _db.Rooms.Add(new RoomEntity
        {
            Id = roomId,
            Name = "Test Room",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
    }

    private TaskEntity SeedTask(string id = TaskId, string status = "Active")
    {
        EnsureRoom();
        var task = new TaskEntity
        {
            Id = id,
            Title = "Test task",
            Description = "Desc",
            SuccessCriteria = "Criteria",
            Status = status,
            RoomId = RoomId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Tasks.Add(task);
        _db.SaveChanges();
        return task;
    }

    private TaskEvidenceEntity SeedEvidence(
        string taskId = TaskId,
        string phase = "After",
        string checkName = "build",
        bool passed = true)
    {
        var entity = new TaskEvidenceEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            TaskId = taskId,
            Phase = phase,
            CheckName = checkName,
            Tool = "bash",
            Passed = passed,
            AgentId = AgentId,
            AgentName = AgentName,
            CreatedAt = DateTime.UtcNow
        };
        _db.TaskEvidence.Add(entity);
        _db.SaveChanges();
        return entity;
    }

    // ── RecordEvidenceAsync ─────────────────────────────────

    [Fact]
    public async Task RecordEvidence_ReturnsTaskEvidenceModel()
    {
        SeedTask();

        var result = await _sut.RecordEvidenceAsync(
            TaskId, AgentId, AgentName, EvidencePhase.After,
            "build", "bash", "dotnet build", 0, "Build succeeded", true);

        Assert.Equal(TaskId, result.TaskId);
        Assert.Equal(EvidencePhase.After, result.Phase);
        Assert.Equal("build", result.CheckName);
        Assert.Equal("bash", result.Tool);
        Assert.Equal("dotnet build", result.Command);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Build succeeded", result.OutputSnippet);
        Assert.True(result.Passed);
        Assert.Equal(AgentId, result.AgentId);
        Assert.Equal(AgentName, result.AgentName);
    }

    [Fact]
    public async Task RecordEvidence_PersistsEntityToDatabase()
    {
        SeedTask();

        await _sut.RecordEvidenceAsync(
            TaskId, AgentId, AgentName, EvidencePhase.Baseline,
            "tests", "bash", "dotnet test", 0, "All passed", true);

        var persisted = await _db.TaskEvidence.FirstOrDefaultAsync(e => e.TaskId == TaskId);
        Assert.NotNull(persisted);
        Assert.Equal("Baseline", persisted.Phase);
        Assert.Equal("tests", persisted.CheckName);
        Assert.True(persisted.Passed);
    }

    [Fact]
    public async Task RecordEvidence_TruncatesOutputSnippetAt500Chars()
    {
        SeedTask();
        var longOutput = new string('x', 600);

        var result = await _sut.RecordEvidenceAsync(
            TaskId, AgentId, AgentName, EvidencePhase.After,
            "build", "bash", null, null, longOutput, true);

        Assert.Equal(500, result.OutputSnippet!.Length);

        var entity = await _db.TaskEvidence.FirstAsync(e => e.TaskId == TaskId);
        Assert.Equal(500, entity.OutputSnippet!.Length);
    }

    [Fact]
    public async Task RecordEvidence_NullOutputSnippetPassedThrough()
    {
        SeedTask();

        var result = await _sut.RecordEvidenceAsync(
            TaskId, AgentId, AgentName, EvidencePhase.After,
            "check", "manual", null, null, null, true);

        Assert.Null(result.OutputSnippet);
    }

    [Theory]
    [InlineData(EvidencePhase.Baseline, "Baseline")]
    [InlineData(EvidencePhase.After, "After")]
    [InlineData(EvidencePhase.Review, "Review")]
    public async Task RecordEvidence_SetsCorrectPhaseString(EvidencePhase phase, string expectedStr)
    {
        SeedTask();

        await _sut.RecordEvidenceAsync(
            TaskId, AgentId, AgentName, phase,
            "check", "bash", null, null, null, true);

        var entity = await _db.TaskEvidence.FirstAsync(e => e.TaskId == TaskId);
        Assert.Equal(expectedStr, entity.Phase);
    }

    [Fact]
    public async Task RecordEvidence_PublishesEvidenceRecordedEvent()
    {
        SeedTask();
        ActivityEvent? captured = null;
        _bus.Subscribe(e => captured = e);

        await _sut.RecordEvidenceAsync(
            TaskId, AgentId, AgentName, EvidencePhase.After,
            "build", "bash", null, null, null, true);

        Assert.NotNull(captured);
        Assert.Equal(ActivityEventType.EvidenceRecorded, captured.Type);
        Assert.Contains("build", captured.Message);
        Assert.Contains("passed", captured.Message);
    }

    [Fact]
    public async Task RecordEvidence_ThrowsWhenTaskNotFound()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.RecordEvidenceAsync(
                "nonexistent", AgentId, AgentName, EvidencePhase.After,
                "build", "bash", null, null, null, true));

        Assert.Contains("nonexistent", ex.Message);
    }

    // ── CheckGatesAsync ─────────────────────────────────────

    [Fact]
    public async Task CheckGates_ActiveTaskNoChecks_NotMet()
    {
        SeedTask(status: "Active");

        var result = await _sut.CheckGatesAsync(TaskId);

        Assert.False(result.Met);
        Assert.Equal(0, result.PassedChecks);
        Assert.Equal(1, result.RequiredChecks);
        Assert.Equal("AwaitingValidation", result.TargetPhase);
    }

    [Fact]
    public async Task CheckGates_ActiveTaskOneAfterPassed_Met()
    {
        SeedTask(status: "Active");
        SeedEvidence(phase: "After", checkName: "build", passed: true);

        var result = await _sut.CheckGatesAsync(TaskId);

        Assert.True(result.Met);
        Assert.Equal(1, result.PassedChecks);
    }

    [Fact]
    public async Task CheckGates_ActiveTaskOneAfterFailed_NotMet()
    {
        SeedTask(status: "Active");
        SeedEvidence(phase: "After", checkName: "build", passed: false);

        var result = await _sut.CheckGatesAsync(TaskId);

        Assert.False(result.Met);
        Assert.Equal(0, result.PassedChecks);
    }

    [Fact]
    public async Task CheckGates_AwaitingValidationOneCheck_NotMet()
    {
        SeedTask(status: "AwaitingValidation");
        SeedEvidence(phase: "After", checkName: "build", passed: true);

        var result = await _sut.CheckGatesAsync(TaskId);

        Assert.False(result.Met);
        Assert.Equal(1, result.PassedChecks);
        Assert.Equal(2, result.RequiredChecks);
        Assert.Equal("InReview", result.TargetPhase);
    }

    [Fact]
    public async Task CheckGates_AwaitingValidationTwoDistinctChecks_Met()
    {
        SeedTask(status: "AwaitingValidation");
        SeedEvidence(phase: "After", checkName: "build", passed: true);
        SeedEvidence(phase: "After", checkName: "tests", passed: true);

        var result = await _sut.CheckGatesAsync(TaskId);

        Assert.True(result.Met);
        Assert.Equal(2, result.PassedChecks);
    }

    [Fact]
    public async Task CheckGates_AwaitingValidationDuplicateCheckNames_CountsDistinctOnly()
    {
        SeedTask(status: "AwaitingValidation");
        SeedEvidence(phase: "After", checkName: "build", passed: true);
        SeedEvidence(phase: "After", checkName: "build", passed: true);

        var result = await _sut.CheckGatesAsync(TaskId);

        Assert.False(result.Met);
        Assert.Equal(1, result.PassedChecks);
    }

    [Fact]
    public async Task CheckGates_InReviewNoReviewChecks_NotMet()
    {
        SeedTask(status: "InReview");

        var result = await _sut.CheckGatesAsync(TaskId);

        Assert.False(result.Met);
        Assert.Equal(0, result.PassedChecks);
        Assert.Equal(1, result.RequiredChecks);
        Assert.Equal("Approved", result.TargetPhase);
    }

    [Fact]
    public async Task CheckGates_InReviewOneReviewPassed_Met()
    {
        SeedTask(status: "InReview");
        SeedEvidence(phase: "Review", checkName: "code-review", passed: true);

        var result = await _sut.CheckGatesAsync(TaskId);

        Assert.True(result.Met);
        Assert.Equal(1, result.PassedChecks);
    }

    [Fact]
    public async Task CheckGates_CompletedTask_MetWithZeroRequired()
    {
        SeedTask(status: "Completed");

        var result = await _sut.CheckGatesAsync(TaskId);

        Assert.True(result.Met);
        Assert.Equal(0, result.RequiredChecks);
        Assert.Equal("N/A", result.TargetPhase);
    }

    [Fact]
    public async Task CheckGates_ReturnsAllEvidenceInList()
    {
        SeedTask(status: "Active");
        SeedEvidence(phase: "Baseline", checkName: "baseline-build", passed: true);
        SeedEvidence(phase: "After", checkName: "after-build", passed: true);

        var result = await _sut.CheckGatesAsync(TaskId);

        Assert.Equal(2, result.Evidence.Count);
    }

    [Fact]
    public async Task CheckGates_MissingChecksListsSuggestedNotYetPassed()
    {
        SeedTask(status: "Active");

        var result = await _sut.CheckGatesAsync(TaskId);

        Assert.Contains("build", result.MissingChecks);
        Assert.Contains("tests", result.MissingChecks);
        Assert.Contains("type-check", result.MissingChecks);
    }

    [Fact]
    public async Task CheckGates_ThrowsWhenTaskNotFound()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.CheckGatesAsync("nonexistent"));
    }
}
