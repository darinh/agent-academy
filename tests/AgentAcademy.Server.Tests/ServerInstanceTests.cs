using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public class ServerInstanceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly WorkspaceRuntime _runtime;

    public ServerInstanceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        var catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Collaboration Room",
            Agents: []);

        var activityBus = new ActivityBroadcaster();

        _runtime = new WorkspaceRuntime(
            _db,
            NullLogger<WorkspaceRuntime>.Instance,
            catalog,
            activityBus);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task InitializeAsync_CreatesServerInstance()
    {
        await _runtime.InitializeAsync();

        var instances = await _db.ServerInstances.ToListAsync();
        Assert.Single(instances);

        var instance = instances[0];
        Assert.NotNull(instance.Id);
        Assert.NotNull(instance.Version);
        Assert.True(instance.StartedAt <= DateTime.UtcNow);
        Assert.Null(instance.ShutdownAt);
        Assert.Null(instance.ExitCode);
        Assert.False(instance.CrashDetected);
    }

    [Fact]
    public async Task InitializeAsync_SetsCurrentInstanceId()
    {
        await _runtime.InitializeAsync();

        Assert.NotNull(WorkspaceRuntime.CurrentInstanceId);
    }

    [Fact]
    public async Task InitializeAsync_DetectsCrash_WhenPreviousInstanceNotShutDown()
    {
        // Simulate a previous instance that didn't shut down
        var orphan = new ServerInstanceEntity
        {
            StartedAt = DateTime.UtcNow.AddHours(-1),
            Version = "1.0.0"
            // ShutdownAt is null — simulates a crash
        };
        _db.ServerInstances.Add(orphan);
        await _db.SaveChangesAsync();

        await _runtime.InitializeAsync();

        var instances = await _db.ServerInstances
            .OrderBy(i => i.StartedAt)
            .ToListAsync();

        Assert.Equal(2, instances.Count);

        // Orphan should be marked as crashed
        var orphanUpdated = instances[0];
        Assert.NotNull(orphanUpdated.ShutdownAt);
        Assert.Equal(-1, orphanUpdated.ExitCode);

        // New instance should have CrashDetected = true
        var current = instances[1];
        Assert.True(current.CrashDetected);
        Assert.Null(current.ShutdownAt);
    }

    [Fact]
    public async Task InitializeAsync_NoCrash_WhenPreviousInstanceShutDownCleanly()
    {
        // Simulate a previous instance that shut down cleanly
        var previous = new ServerInstanceEntity
        {
            StartedAt = DateTime.UtcNow.AddHours(-1),
            ShutdownAt = DateTime.UtcNow.AddMinutes(-30),
            ExitCode = 0,
            Version = "1.0.0"
        };
        _db.ServerInstances.Add(previous);
        await _db.SaveChangesAsync();

        await _runtime.InitializeAsync();

        var instances = await _db.ServerInstances
            .OrderBy(i => i.StartedAt)
            .ToListAsync();

        Assert.Equal(2, instances.Count);

        // New instance should NOT have CrashDetected
        var current = instances[1];
        Assert.False(current.CrashDetected);
    }

    [Fact]
    public void DefaultRoomId_ReturnsConfiguredValue()
    {
        Assert.Equal("main", _runtime.DefaultRoomId);
    }
}

public class ServerInstanceEntityTests
{
    [Fact]
    public void NewEntity_HasDefaults()
    {
        var entity = new ServerInstanceEntity();

        Assert.NotNull(entity.Id);
        Assert.NotEmpty(entity.Id);
        Assert.True(entity.StartedAt <= DateTime.UtcNow);
        Assert.Null(entity.ShutdownAt);
        Assert.Null(entity.ExitCode);
        Assert.False(entity.CrashDetected);
        Assert.Equal("", entity.Version);
    }

    [Fact]
    public void NewEntity_GeneratesUniqueIds()
    {
        var a = new ServerInstanceEntity();
        var b = new ServerInstanceEntity();
        Assert.NotEqual(a.Id, b.Id);
    }
}

public class InstanceHealthResultTests
{
    [Fact]
    public void InstanceHealthResult_HasCorrectShape()
    {
        var result = new InstanceHealthResult(
            InstanceId: "test-123",
            StartedAt: DateTime.UtcNow,
            Version: "1.0.0",
            CrashDetected: false,
            ExecutorOperational: true,
            AuthFailed: false);

        Assert.Equal("test-123", result.InstanceId);
        Assert.Equal("1.0.0", result.Version);
        Assert.False(result.CrashDetected);
        Assert.True(result.ExecutorOperational);
        Assert.False(result.AuthFailed);
    }
}
