using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class RoomArtifactTrackerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly RoomArtifactTracker _tracker;

    public RoomArtifactTrackerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        // Seed rooms used by tests so activity event FK constraints pass
        _db.Rooms.AddRange(
            MakeRoom("room-1"),
            MakeRoom("room-a"),
            MakeRoom("room-b"));
        _db.SaveChanges();

        var activityBus = new ActivityBroadcaster();
        var activityPublisher = new ActivityPublisher(_db, activityBus);
        _tracker = new RoomArtifactTracker(_db, activityPublisher, NullLogger<RoomArtifactTracker>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static RoomEntity MakeRoom(string id) => new()
    {
        Id = id,
        Name = id,
        Status = "Active",
        CurrentPhase = "Intake",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ── RecordAsync ──────────────────────────────────────────────

    [Fact]
    public async Task RecordAsync_PersistsArtifact()
    {
        await _tracker.RecordAsync("room-1", "agent-1", "src/Models/User.cs", "Created");

        var artifacts = await _db.RoomArtifacts.ToListAsync();
        Assert.Single(artifacts);
        Assert.Equal("room-1", artifacts[0].RoomId);
        Assert.Equal("agent-1", artifacts[0].AgentId);
        Assert.Equal("src/Models/User.cs", artifacts[0].FilePath);
        Assert.Equal("Created", artifacts[0].Operation);
        Assert.Null(artifacts[0].CommitSha);
    }

    [Fact]
    public async Task RecordAsync_PublishesActivityEvent()
    {
        var activityBus = new ActivityBroadcaster();
        var publisher = new ActivityPublisher(_db, activityBus);
        var tracker = new RoomArtifactTracker(_db, publisher, NullLogger<RoomArtifactTracker>.Instance);

        ActivityEvent? captured = null;
        activityBus.Subscribe(e => captured = e);

        await tracker.RecordAsync("room-1", "agent-1", "src/X.cs", "Updated");

        Assert.NotNull(captured);
        Assert.Equal(ActivityEventType.ArtifactEvaluated, captured.Type);
        Assert.Equal("room-1", captured.RoomId);
        Assert.Equal("agent-1", captured.ActorId);
    }

    [Theory]
    [InlineData(null, "agent-1", "file.cs", "Created")]
    [InlineData("", "agent-1", "file.cs", "Created")]
    [InlineData("room-1", null, "file.cs", "Created")]
    [InlineData("room-1", "", "file.cs", "Created")]
    [InlineData("room-1", "agent-1", null, "Created")]
    [InlineData("room-1", "agent-1", "", "Created")]
    public async Task RecordAsync_NoOpsOnMissingFields(string? roomId, string? agentId, string? filePath, string operation)
    {
        await _tracker.RecordAsync(roomId, agentId!, filePath!, operation);

        var count = await _db.RoomArtifacts.CountAsync();
        Assert.Equal(0, count);
    }

    // ── RecordCommitAsync ────────────────────────────────────────

    [Fact]
    public async Task RecordCommitAsync_PersistsOnePerFile()
    {
        var files = new List<string> { "src/A.cs", "src/B.cs", "src/C.cs" };
        await _tracker.RecordCommitAsync("room-1", "agent-1", "abc123", files);

        var artifacts = await _db.RoomArtifacts.ToListAsync();
        Assert.Equal(3, artifacts.Count);
        Assert.All(artifacts, a =>
        {
            Assert.Equal("room-1", a.RoomId);
            Assert.Equal("agent-1", a.AgentId);
            Assert.Equal("Committed", a.Operation);
            Assert.Equal("abc123", a.CommitSha);
        });
        Assert.Equal(files, artifacts.Select(a => a.FilePath).ToList());
    }

    [Fact]
    public async Task RecordCommitAsync_NoOpsOnEmptyFileList()
    {
        await _tracker.RecordCommitAsync("room-1", "agent-1", "sha123", []);

        var count = await _db.RoomArtifacts.CountAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task RecordCommitAsync_NoOpsOnNullRoom()
    {
        await _tracker.RecordCommitAsync(null, "agent-1", "sha123", ["file.cs"]);

        var count = await _db.RoomArtifacts.CountAsync();
        Assert.Equal(0, count);
    }

    // ── GetRoomArtifactsAsync ────────────────────────────────────

    [Fact]
    public async Task GetRoomArtifactsAsync_ReturnsOrderedByTimestampDesc()
    {
        _db.RoomArtifacts.AddRange(
            new RoomArtifactEntity { RoomId = "room-1", AgentId = "a", FilePath = "old.cs", Operation = "Created", Timestamp = DateTime.UtcNow.AddMinutes(-10) },
            new RoomArtifactEntity { RoomId = "room-1", AgentId = "a", FilePath = "new.cs", Operation = "Created", Timestamp = DateTime.UtcNow }
        );
        await _db.SaveChangesAsync();

        var results = await _tracker.GetRoomArtifactsAsync("room-1");
        Assert.Equal(2, results.Count);
        Assert.Equal("new.cs", results[0].FilePath);
        Assert.Equal("old.cs", results[1].FilePath);
    }

    [Fact]
    public async Task GetRoomArtifactsAsync_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
        {
            _db.RoomArtifacts.Add(new RoomArtifactEntity
            {
                RoomId = "room-1", AgentId = "a", FilePath = $"file{i}.cs",
                Operation = "Created", Timestamp = DateTime.UtcNow.AddMinutes(-i)
            });
        }
        await _db.SaveChangesAsync();

        var results = await _tracker.GetRoomArtifactsAsync("room-1", limit: 5);
        Assert.Equal(5, results.Count);
    }

    [Fact]
    public async Task GetRoomArtifactsAsync_LimitClampedToRange()
    {
        _db.RoomArtifacts.Add(new RoomArtifactEntity
        {
            RoomId = "room-1", AgentId = "a", FilePath = "f.cs",
            Operation = "Created", Timestamp = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        // Limit below 1 gets clamped to 1
        var results = await _tracker.GetRoomArtifactsAsync("room-1", limit: 0);
        Assert.Single(results);
    }

    [Fact]
    public async Task GetRoomArtifactsAsync_IsolatedByRoom()
    {
        _db.RoomArtifacts.Add(new RoomArtifactEntity { RoomId = "room-a", AgentId = "a", FilePath = "a.cs", Operation = "Created", Timestamp = DateTime.UtcNow });
        _db.RoomArtifacts.Add(new RoomArtifactEntity { RoomId = "room-b", AgentId = "a", FilePath = "b.cs", Operation = "Created", Timestamp = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        var results = await _tracker.GetRoomArtifactsAsync("room-a");
        Assert.Single(results);
        Assert.Equal("a.cs", results[0].FilePath);
    }

    [Fact]
    public async Task GetRoomArtifactsAsync_MapsToArtifactRecord()
    {
        _db.RoomArtifacts.Add(new RoomArtifactEntity
        {
            RoomId = "room-1", AgentId = "agent-x", FilePath = "src/Foo.cs",
            Operation = "Updated", CommitSha = "deadbeef", Timestamp = new DateTime(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc)
        });
        await _db.SaveChangesAsync();

        var results = await _tracker.GetRoomArtifactsAsync("room-1");
        Assert.Single(results);
        var record = results[0];
        Assert.Equal("agent-x", record.AgentId);
        Assert.Equal("room-1", record.RoomId);
        Assert.Equal("src/Foo.cs", record.FilePath);
        Assert.Equal("Updated", record.Operation);
    }
}
