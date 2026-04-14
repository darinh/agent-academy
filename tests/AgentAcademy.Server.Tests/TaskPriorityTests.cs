using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public class TaskPriorityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly AgentCatalogOptions _catalog;
    private readonly TaskQueryService _queryService;

    public TaskPriorityTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection).Options;
        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "eng-1", Name: "Engineer", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true)
            ]
        );

        var activityBus = new ActivityBroadcaster();
        var activityPublisher = new ActivityPublisher(_db, activityBus);
        var taskDeps = new TaskDependencyService(_db, NullLogger<TaskDependencyService>.Instance, activityPublisher);
        _queryService = new TaskQueryService(_db, NullLogger<TaskQueryService>.Instance, _catalog, taskDeps);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private TaskEntity CreateTask(string id, string title, int priority = 2, string status = "Active")
    {
        return new TaskEntity
        {
            Id = id,
            Title = title,
            Description = "desc",
            SuccessCriteria = "criteria",
            Status = status,
            Type = "Feature",
            CurrentPhase = "Planning",
            CurrentPlan = "",
            ValidationStatus = "NotStarted",
            ValidationSummary = "",
            ImplementationStatus = "NotStarted",
            ImplementationSummary = "",
            PreferredRoles = "[]",
            FleetModels = "[]",
            TestsCreated = "[]",
            Priority = priority,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    // ── Enum Mapping ────────────────────────────────────────────

    [Theory]
    [InlineData(0, TaskPriority.Critical)]
    [InlineData(1, TaskPriority.High)]
    [InlineData(2, TaskPriority.Medium)]
    [InlineData(3, TaskPriority.Low)]
    public async Task BuildTaskSnapshot_MapsPriorityCorrectly(int dbValue, TaskPriority expected)
    {
        var entity = CreateTask($"t-{dbValue}", $"Task {dbValue}", priority: dbValue);
        _db.Tasks.Add(entity);
        await _db.SaveChangesAsync();

        var snapshot = TaskQueryService.BuildTaskSnapshot(entity);

        Assert.Equal(expected, snapshot.Priority);
    }

    [Fact]
    public void BuildTaskSnapshot_InvalidPriorityValue_DefaultsToMedium()
    {
        var entity = CreateTask("t-invalid", "Invalid", priority: 99);

        var snapshot = TaskQueryService.BuildTaskSnapshot(entity);

        Assert.Equal(TaskPriority.Medium, snapshot.Priority);
    }

    // ── Default Value ───────────────────────────────────────────

    [Fact]
    public async Task NewTaskEntity_DefaultsPriorityToMedium()
    {
        var entity = new TaskEntity
        {
            Id = "t-default",
            Title = "Default priority",
            Description = "desc",
            SuccessCriteria = "",
            PreferredRoles = "[]",
            FleetModels = "[]",
            TestsCreated = "[]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Tasks.Add(entity);
        await _db.SaveChangesAsync();

        var loaded = await _db.Tasks.FindAsync("t-default");
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Priority);
    }

    // ── Sorting ─────────────────────────────────────────────────

    [Fact]
    public async Task GetTasksAsync_SortsByPriorityThenCreatedAt()
    {
        var now = DateTime.UtcNow;

        var tLow = CreateTask("t-low", "Low task", priority: 3);
        tLow.CreatedAt = now.AddMinutes(-3);
        var tCrit = CreateTask("t-crit", "Critical task", priority: 0);
        tCrit.CreatedAt = now.AddMinutes(-2);
        var tMed1 = CreateTask("t-med1", "Medium old", priority: 2);
        tMed1.CreatedAt = now.AddMinutes(-5);
        var tMed2 = CreateTask("t-med2", "Medium new", priority: 2);
        tMed2.CreatedAt = now.AddMinutes(-1);

        _db.Tasks.AddRange(tLow, tCrit, tMed1, tMed2);
        await _db.SaveChangesAsync();

        var tasks = await _queryService.GetTasksAsync();

        Assert.Equal("t-crit", tasks[0].Id);
        // Medium tasks: most recent first
        Assert.Equal("t-med2", tasks[1].Id);
        Assert.Equal("t-med1", tasks[2].Id);
        Assert.Equal("t-low", tasks[3].Id);
    }

    // ── Update Priority ─────────────────────────────────────────

    [Fact]
    public async Task UpdateTaskPriorityAsync_ChangesPriority()
    {
        var entity = CreateTask("t-update", "To update", priority: 2);
        _db.Tasks.Add(entity);
        await _db.SaveChangesAsync();

        var result = await _queryService.UpdateTaskPriorityAsync("t-update", TaskPriority.Critical);

        Assert.Equal(TaskPriority.Critical, result.Priority);

        var reloaded = await _db.Tasks.FindAsync("t-update");
        Assert.NotNull(reloaded);
        Assert.Equal(0, reloaded.Priority);
    }

    [Fact]
    public async Task UpdateTaskPriorityAsync_ThrowsForMissingTask()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _queryService.UpdateTaskPriorityAsync("nonexistent", TaskPriority.High));
    }

    [Fact]
    public async Task UpdateTaskPriorityAsync_UpdatesTimestamp()
    {
        var entity = CreateTask("t-ts", "Timestamp check", priority: 2);
        entity.UpdatedAt = DateTime.UtcNow.AddHours(-1);
        _db.Tasks.Add(entity);
        await _db.SaveChangesAsync();

        var before = entity.UpdatedAt;
        var result = await _queryService.UpdateTaskPriorityAsync("t-ts", TaskPriority.Low);

        Assert.True(result.UpdatedAt > before);
    }

    // ── Task Creation with Priority ─────────────────────────────

    [Theory]
    [InlineData(TaskPriority.Critical)]
    [InlineData(TaskPriority.High)]
    [InlineData(TaskPriority.Low)]
    public void StageNewTask_SetsPriorityFromRequest(TaskPriority priority)
    {
        var db2Conn = new SqliteConnection("Data Source=:memory:");
        db2Conn.Open();
        var db2 = new AgentAcademyDbContext(
            new DbContextOptionsBuilder<AgentAcademyDbContext>()
                .UseSqlite(db2Conn).Options);
        db2.Database.EnsureCreated();

        var activityBus = new ActivityBroadcaster();
        var activityPublisher = new ActivityPublisher(db2, activityBus);
        var taskDeps = new TaskDependencyService(db2, NullLogger<TaskDependencyService>.Instance, activityPublisher);
        var lifecycle = new TaskLifecycleService(db2, NullLogger<TaskLifecycleService>.Instance, _catalog,
            activityPublisher, taskDeps);

        var request = new TaskAssignmentRequest(
            Title: "Priority test",
            Description: "desc",
            SuccessCriteria: "criteria",
            RoomId: null,
            PreferredRoles: [],
            Priority: priority
        );

        var (task, _) = lifecycle.StageNewTask(request, "room-1", null, true, "corr-1");

        Assert.Equal(priority, task.Priority);

        db2.Dispose();
        db2Conn.Dispose();
    }

    // ── Enum Serialization ──────────────────────────────────────

    [Theory]
    [InlineData("Critical", TaskPriority.Critical)]
    [InlineData("High", TaskPriority.High)]
    [InlineData("Medium", TaskPriority.Medium)]
    [InlineData("Low", TaskPriority.Low)]
    [InlineData("critical", TaskPriority.Critical)]
    [InlineData("HIGH", TaskPriority.High)]
    public void TaskPriority_ParsesCaseInsensitively(string input, TaskPriority expected)
    {
        Assert.True(Enum.TryParse<TaskPriority>(input, ignoreCase: true, out var result));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TaskPriority_NumericValues_AreCorrectForSorting()
    {
        Assert.Equal(0, (int)TaskPriority.Critical);
        Assert.Equal(1, (int)TaskPriority.High);
        Assert.Equal(2, (int)TaskPriority.Medium);
        Assert.Equal(3, (int)TaskPriority.Low);
        Assert.True((int)TaskPriority.Critical < (int)TaskPriority.High);
        Assert.True((int)TaskPriority.High < (int)TaskPriority.Medium);
        Assert.True((int)TaskPriority.Medium < (int)TaskPriority.Low);
    }
}
