using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Tests;

public class PhaseTransitionValidatorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly PhaseTransitionValidator _validator;
    private const string RoomId = "test-room";

    public PhaseTransitionValidatorTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection).Options;
        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _db.Rooms.Add(new RoomEntity
        {
            Id = RoomId,
            Name = "Test Room",
            CurrentPhase = "Intake",
            Status = "Active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        _validator = new PhaseTransitionValidator(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private TaskEntity AddTask(string status)
    {
        var task = new TaskEntity
        {
            Id = Guid.NewGuid().ToString(),
            Title = $"Task ({status})",
            RoomId = RoomId,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Tasks.Add(task);
        _db.SaveChanges();
        return task;
    }

    // ── Intake and Planning gates (always open) ──────────────────

    [Theory]
    [InlineData(CollaborationPhase.Intake)]
    [InlineData(CollaborationPhase.Planning)]
    public async Task IntakeAndPlanning_AlwaysAllowed(CollaborationPhase target)
    {
        var gate = await _validator.ValidateTransitionAsync(RoomId, CollaborationPhase.Intake, target);
        Assert.True(gate.Allowed);
        Assert.Null(gate.Reason);
    }

    // ── Discussion requires at least 1 task ──────────────────────

    [Fact]
    public async Task Discussion_BlockedWithNoTasks()
    {
        var gate = await _validator.ValidateTransitionAsync(
            RoomId, CollaborationPhase.Planning, CollaborationPhase.Discussion);
        Assert.False(gate.Allowed);
        Assert.Contains("task", gate.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Discussion_AllowedWithAnyTask()
    {
        AddTask(nameof(Shared.Models.TaskStatus.Queued));

        var gate = await _validator.ValidateTransitionAsync(
            RoomId, CollaborationPhase.Planning, CollaborationPhase.Discussion);
        Assert.True(gate.Allowed);
    }

    // ── Validation requires at least 1 task ──────────────────────

    [Fact]
    public async Task Validation_BlockedWithNoTasks()
    {
        var gate = await _validator.ValidateTransitionAsync(
            RoomId, CollaborationPhase.Discussion, CollaborationPhase.Validation);
        Assert.False(gate.Allowed);
        Assert.Contains("task", gate.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validation_AllowedWithAnyTask()
    {
        AddTask(nameof(Shared.Models.TaskStatus.Active));

        var gate = await _validator.ValidateTransitionAsync(
            RoomId, CollaborationPhase.Discussion, CollaborationPhase.Validation);
        Assert.True(gate.Allowed);
    }

    // ── Implementation requires approved/completed ───────────────

    [Fact]
    public async Task Implementation_BlockedWithOnlyActiveTasks()
    {
        AddTask(nameof(Shared.Models.TaskStatus.Active));
        AddTask(nameof(Shared.Models.TaskStatus.Queued));

        var gate = await _validator.ValidateTransitionAsync(
            RoomId, CollaborationPhase.Validation, CollaborationPhase.Implementation);
        Assert.False(gate.Allowed);
        Assert.Contains("Approved", gate.Reason!);
    }

    [Theory]
    [InlineData(nameof(Shared.Models.TaskStatus.Approved))]
    [InlineData(nameof(Shared.Models.TaskStatus.Completed))]
    [InlineData(nameof(Shared.Models.TaskStatus.Merging))]
    public async Task Implementation_AllowedWithApprovedOrCompleted(string status)
    {
        AddTask(nameof(Shared.Models.TaskStatus.Active));
        AddTask(status);

        var gate = await _validator.ValidateTransitionAsync(
            RoomId, CollaborationPhase.Validation, CollaborationPhase.Implementation);
        Assert.True(gate.Allowed);
    }

    // ── FinalSynthesis requires all tasks terminal ───────────────

    [Fact]
    public async Task FinalSynthesis_BlockedWithActiveTasks()
    {
        AddTask(nameof(Shared.Models.TaskStatus.Completed));
        AddTask(nameof(Shared.Models.TaskStatus.Active));

        var gate = await _validator.ValidateTransitionAsync(
            RoomId, CollaborationPhase.Implementation, CollaborationPhase.FinalSynthesis);
        Assert.False(gate.Allowed);
        Assert.Contains("in progress", gate.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FinalSynthesis_BlockedWithNoTasks()
    {
        var gate = await _validator.ValidateTransitionAsync(
            RoomId, CollaborationPhase.Implementation, CollaborationPhase.FinalSynthesis);
        Assert.False(gate.Allowed);
        Assert.Contains("task", gate.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FinalSynthesis_AllowedWhenAllTerminal()
    {
        AddTask(nameof(Shared.Models.TaskStatus.Completed));
        AddTask(nameof(Shared.Models.TaskStatus.Cancelled));

        var gate = await _validator.ValidateTransitionAsync(
            RoomId, CollaborationPhase.Implementation, CollaborationPhase.FinalSynthesis);
        Assert.True(gate.Allowed);
    }

    // ── Backward transitions always allowed ──────────────────────

    [Theory]
    [InlineData(CollaborationPhase.Implementation, CollaborationPhase.Intake)]
    [InlineData(CollaborationPhase.FinalSynthesis, CollaborationPhase.Planning)]
    [InlineData(CollaborationPhase.Discussion, CollaborationPhase.Intake)]
    public async Task BackwardTransition_AlwaysAllowed(
        CollaborationPhase from, CollaborationPhase to)
    {
        // No tasks — forward would be blocked, but backward is free
        var gate = await _validator.ValidateTransitionAsync(RoomId, from, to);
        Assert.True(gate.Allowed);
    }

    // ── Same-phase transition always allowed ─────────────────────

    [Theory]
    [InlineData(CollaborationPhase.Intake)]
    [InlineData(CollaborationPhase.Implementation)]
    public async Task SamePhase_AlwaysAllowed(CollaborationPhase phase)
    {
        var gate = await _validator.ValidateTransitionAsync(RoomId, phase, phase);
        Assert.True(gate.Allowed);
    }

    // ── GetGatesAsync returns all phases ─────────────────────────

    [Fact]
    public async Task GetGates_ReturnsAllPhases()
    {
        var status = await _validator.GetGatesAsync(RoomId);
        Assert.Equal(6, status.Gates.Count);
        Assert.True(status.Gates["Intake"].Allowed);
        Assert.True(status.Gates["Planning"].Allowed);
        Assert.False(status.Gates["Discussion"].Allowed); // no tasks
        Assert.False(status.Gates["Validation"].Allowed); // no tasks
        Assert.False(status.Gates["Implementation"].Allowed); // no approved
        Assert.False(status.Gates["FinalSynthesis"].Allowed); // no tasks
    }

    [Fact]
    public async Task GetGates_UpdatesAfterTaskCreation()
    {
        AddTask(nameof(Shared.Models.TaskStatus.Approved));

        var status = await _validator.GetGatesAsync(RoomId);
        Assert.True(status.Gates["Discussion"].Allowed);
        Assert.True(status.Gates["Validation"].Allowed);
        Assert.True(status.Gates["Implementation"].Allowed);
        Assert.False(status.Gates["FinalSynthesis"].Allowed); // not all terminal
    }
}
