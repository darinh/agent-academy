using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Tests;

public sealed class ConversationSessionQueryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly ConversationSessionQueryService _sut;

    public ConversationSessionQueryServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection).Options;
        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();
        _sut = new ConversationSessionQueryService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private ConversationSessionEntity CreateSession(
        string roomId = "room-1",
        string status = "Active",
        int sequenceNumber = 1,
        string? summary = null,
        string? sprintId = null,
        string? sprintStage = null,
        int messageCount = 0,
        DateTime? createdAt = null,
        string? workspacePath = null)
    {
        return new ConversationSessionEntity
        {
            Id = Guid.NewGuid().ToString(),
            RoomId = roomId,
            Status = status,
            SequenceNumber = sequenceNumber,
            Summary = summary,
            SprintId = sprintId,
            SprintStage = sprintStage,
            MessageCount = messageCount,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            ArchivedAt = status == "Archived" ? DateTime.UtcNow : null,
            WorkspacePath = workspacePath,
        };
    }

    private async Task SeedAsync(params ConversationSessionEntity[] sessions)
    {
        // Ensure referenced Sprint entities exist to satisfy FK constraints
        var sprintIds = sessions
            .Where(s => s.SprintId is not null)
            .Select(s => s.SprintId!)
            .Distinct();
        foreach (var sprintId in sprintIds)
        {
            if (!await _db.Sprints.AnyAsync(sp => sp.Id == sprintId))
            {
                _db.Sprints.Add(new SprintEntity
                {
                    Id = sprintId,
                    Number = 1,
                    WorkspacePath = "/test",
                });
            }
        }

        _db.ConversationSessions.AddRange(sessions);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    // ── GetSessionContextAsync ──────────────────────────────────────────

    [Fact]
    public async Task GetSessionContext_NoSessions_ReturnsNull()
    {
        var result = await _sut.GetSessionContextAsync("room-1");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetSessionContext_ActiveSessionOnly_ReturnsNull()
    {
        await SeedAsync(CreateSession(status: "Active", summary: "active summary"));

        var result = await _sut.GetSessionContextAsync("room-1");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetSessionContext_SingleArchivedSession_ReturnsSummary()
    {
        await SeedAsync(CreateSession(status: "Archived", summary: "archived summary"));

        var result = await _sut.GetSessionContextAsync("room-1");
        Assert.Equal("archived summary", result);
    }

    [Fact]
    public async Task GetSessionContext_MultipleArchivedSessions_ReturnsLatestBySequenceNumber()
    {
        await SeedAsync(
            CreateSession(status: "Archived", sequenceNumber: 1, summary: "old summary"),
            CreateSession(status: "Archived", sequenceNumber: 3, summary: "latest summary"),
            CreateSession(status: "Archived", sequenceNumber: 2, summary: "middle summary"));

        var result = await _sut.GetSessionContextAsync("room-1");
        Assert.Equal("latest summary", result);
    }

    [Fact]
    public async Task GetSessionContext_NullSummary_ReturnsNull()
    {
        await SeedAsync(CreateSession(status: "Archived", summary: null));

        var result = await _sut.GetSessionContextAsync("room-1");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetSessionContext_OtherRoomsIgnored()
    {
        await SeedAsync(
            CreateSession(roomId: "room-2", status: "Archived", summary: "other room"));

        var result = await _sut.GetSessionContextAsync("room-1");
        Assert.Null(result);
    }

    // ── GetStageContextAsync ────────────────────────────────────────────

    [Fact]
    public async Task GetStageContext_NoSessions_ReturnsNull()
    {
        var result = await _sut.GetStageContextAsync("sprint-1", "Planning");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetStageContext_MatchingSession_ReturnsSummary()
    {
        await SeedAsync(CreateSession(
            status: "Archived", summary: "planning done",
            sprintId: "sprint-1", sprintStage: "Planning"));

        var result = await _sut.GetStageContextAsync("sprint-1", "Planning");
        Assert.Equal("planning done", result);
    }

    [Fact]
    public async Task GetStageContext_MultipleForSameStage_ReturnsLatestBySequenceNumber()
    {
        await SeedAsync(
            CreateSession(status: "Archived", sequenceNumber: 1, summary: "first",
                sprintId: "sprint-1", sprintStage: "Planning"),
            CreateSession(status: "Archived", sequenceNumber: 5, summary: "latest",
                sprintId: "sprint-1", sprintStage: "Planning"));

        var result = await _sut.GetStageContextAsync("sprint-1", "Planning");
        Assert.Equal("latest", result);
    }

    [Fact]
    public async Task GetStageContext_DifferentStage_ReturnsNull()
    {
        await SeedAsync(CreateSession(
            status: "Archived", summary: "intake done",
            sprintId: "sprint-1", sprintStage: "Intake"));

        var result = await _sut.GetStageContextAsync("sprint-1", "Planning");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetStageContext_DifferentSprint_ReturnsNull()
    {
        await SeedAsync(CreateSession(
            status: "Archived", summary: "other sprint",
            sprintId: "sprint-2", sprintStage: "Planning"));

        var result = await _sut.GetStageContextAsync("sprint-1", "Planning");
        Assert.Null(result);
    }

    // ── GetSprintContextAsync ───────────────────────────────────────────

    [Fact]
    public async Task GetSprintContext_NoSessions_ReturnsEmpty()
    {
        var result = await _sut.GetSprintContextAsync("sprint-1");
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSprintContext_SingleStage_ReturnsSingleEntry()
    {
        await SeedAsync(CreateSession(
            status: "Archived", summary: "intake summary",
            sprintId: "sprint-1", sprintStage: "Intake"));

        var result = await _sut.GetSprintContextAsync("sprint-1");

        Assert.Single(result);
        Assert.Equal("Intake", result[0].Stage);
        Assert.Equal("intake summary", result[0].Summary);
    }

    [Fact]
    public async Task GetSprintContext_MultipleStages_OrderedByCanonicalSequence()
    {
        // Seed in non-canonical order
        await SeedAsync(
            CreateSession(status: "Archived", summary: "validation done",
                sprintId: "sprint-1", sprintStage: "Validation", sequenceNumber: 4),
            CreateSession(status: "Archived", summary: "intake done",
                sprintId: "sprint-1", sprintStage: "Intake", sequenceNumber: 1),
            CreateSession(status: "Archived", summary: "planning done",
                sprintId: "sprint-1", sprintStage: "Planning", sequenceNumber: 2));

        var result = await _sut.GetSprintContextAsync("sprint-1");

        Assert.Equal(3, result.Count);
        Assert.Equal("Intake", result[0].Stage);
        Assert.Equal("Planning", result[1].Stage);
        Assert.Equal("Validation", result[2].Stage);
    }

    [Fact]
    public async Task GetSprintContext_DuplicateStages_DeduplicatesToLatest()
    {
        await SeedAsync(
            CreateSession(status: "Archived", summary: "old intake",
                sprintId: "sprint-1", sprintStage: "Intake", sequenceNumber: 1),
            CreateSession(status: "Archived", summary: "new intake",
                sprintId: "sprint-1", sprintStage: "Intake", sequenceNumber: 3));

        var result = await _sut.GetSprintContextAsync("sprint-1");

        Assert.Single(result);
        Assert.Equal("new intake", result[0].Summary);
    }

    [Fact]
    public async Task GetSprintContext_ActiveSessionsIgnored()
    {
        await SeedAsync(CreateSession(
            status: "Active", summary: "active session",
            sprintId: "sprint-1", sprintStage: "Intake"));

        var result = await _sut.GetSprintContextAsync("sprint-1");
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSprintContext_NullSummaryIgnored()
    {
        await SeedAsync(CreateSession(
            status: "Archived", summary: null,
            sprintId: "sprint-1", sprintStage: "Intake"));

        var result = await _sut.GetSprintContextAsync("sprint-1");
        Assert.Empty(result);
    }

    // ── GetRoomSessionsAsync ────────────────────────────────────────────

    [Fact]
    public async Task GetRoomSessions_NoSessions_ReturnsEmptyWithZeroCount()
    {
        var (sessions, total) = await _sut.GetRoomSessionsAsync("room-1");

        Assert.Empty(sessions);
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task GetRoomSessions_ReturnsSessions_OrderedBySequenceDesc()
    {
        await SeedAsync(
            CreateSession(sequenceNumber: 1, summary: "first"),
            CreateSession(sequenceNumber: 3, summary: "third"),
            CreateSession(sequenceNumber: 2, summary: "second"));

        var (sessions, _) = await _sut.GetRoomSessionsAsync("room-1");

        Assert.Equal(3, sessions.Count);
        Assert.Equal(3, sessions[0].SequenceNumber);
        Assert.Equal(2, sessions[1].SequenceNumber);
        Assert.Equal(1, sessions[2].SequenceNumber);
    }

    [Fact]
    public async Task GetRoomSessions_StatusFilter_FiltersCorrectly()
    {
        await SeedAsync(
            CreateSession(status: "Active", sequenceNumber: 1),
            CreateSession(status: "Archived", sequenceNumber: 2),
            CreateSession(status: "Active", sequenceNumber: 3));

        var (sessions, total) = await _sut.GetRoomSessionsAsync("room-1", status: "Archived");

        Assert.Single(sessions);
        Assert.Equal(1, total);
        Assert.Equal("Archived", sessions[0].Status);
    }

    [Fact]
    public async Task GetRoomSessions_Pagination_LimitWorks()
    {
        await SeedAsync(
            CreateSession(sequenceNumber: 1),
            CreateSession(sequenceNumber: 2),
            CreateSession(sequenceNumber: 3));

        var (sessions, total) = await _sut.GetRoomSessionsAsync("room-1", limit: 2);

        Assert.Equal(2, sessions.Count);
        Assert.Equal(3, total);
    }

    [Fact]
    public async Task GetRoomSessions_Pagination_OffsetWorks()
    {
        await SeedAsync(
            CreateSession(sequenceNumber: 1),
            CreateSession(sequenceNumber: 2),
            CreateSession(sequenceNumber: 3));

        var (sessions, _) = await _sut.GetRoomSessionsAsync("room-1", limit: 10, offset: 1);

        Assert.Equal(2, sessions.Count);
        Assert.Equal(2, sessions[0].SequenceNumber);
    }

    [Fact]
    public async Task GetRoomSessions_TotalCount_IncludesAllMatching()
    {
        for (int i = 0; i < 5; i++)
            await SeedAsync(CreateSession(sequenceNumber: i + 1));

        var (sessions, total) = await _sut.GetRoomSessionsAsync("room-1", limit: 2);

        Assert.Equal(2, sessions.Count);
        Assert.Equal(5, total);
    }

    [Fact]
    public async Task GetRoomSessions_LimitClampedTo100()
    {
        for (int i = 0; i < 3; i++)
            await SeedAsync(CreateSession(sequenceNumber: i + 1));

        var (sessions, _) = await _sut.GetRoomSessionsAsync("room-1", limit: 200);

        Assert.Equal(3, sessions.Count);
    }

    [Fact]
    public async Task GetRoomSessions_NegativeOffset_TreatedAsZero()
    {
        await SeedAsync(
            CreateSession(sequenceNumber: 1),
            CreateSession(sequenceNumber: 2));

        var (sessions, _) = await _sut.GetRoomSessionsAsync("room-1", offset: -5);

        Assert.Equal(2, sessions.Count);
    }

    [Fact]
    public async Task GetRoomSessions_MapsSnapshotFieldsCorrectly()
    {
        var entity = CreateSession(
            roomId: "room-1", status: "Active", sequenceNumber: 7,
            summary: "test summary", messageCount: 42,
            workspacePath: "/home/test");
        await SeedAsync(entity);

        var (sessions, _) = await _sut.GetRoomSessionsAsync("room-1");

        var s = Assert.Single(sessions);
        Assert.Equal(entity.Id, s.Id);
        Assert.Equal("room-1", s.RoomId);
        Assert.Equal("Main", s.RoomType);
        Assert.Equal(7, s.SequenceNumber);
        Assert.Equal("Active", s.Status);
        Assert.Equal("test summary", s.Summary);
        Assert.Equal(42, s.MessageCount);
        Assert.Equal("/home/test", s.WorkspacePath);
    }

    // ── GetAllSessionsAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetAllSessions_NoSessions_ReturnsEmpty()
    {
        var (sessions, total) = await _sut.GetAllSessionsAsync();

        Assert.Empty(sessions);
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task GetAllSessions_ReturnsAll_OrderedByCreatedAtDesc()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            CreateSession(roomId: "room-1", createdAt: now.AddHours(-2)),
            CreateSession(roomId: "room-2", createdAt: now),
            CreateSession(roomId: "room-3", createdAt: now.AddHours(-1)));

        var (sessions, total) = await _sut.GetAllSessionsAsync();

        Assert.Equal(3, total);
        Assert.Equal("room-2", sessions[0].RoomId);
        Assert.Equal("room-3", sessions[1].RoomId);
        Assert.Equal("room-1", sessions[2].RoomId);
    }

    [Fact]
    public async Task GetAllSessions_StatusFilter()
    {
        await SeedAsync(
            CreateSession(roomId: "room-1", status: "Active"),
            CreateSession(roomId: "room-2", status: "Archived"),
            CreateSession(roomId: "room-3", status: "Active"));

        var (sessions, total) = await _sut.GetAllSessionsAsync(status: "Archived");

        Assert.Single(sessions);
        Assert.Equal(1, total);
        Assert.Equal("room-2", sessions[0].RoomId);
    }

    [Fact]
    public async Task GetAllSessions_HoursBackFilter()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            CreateSession(roomId: "room-old", createdAt: now.AddHours(-48)),
            CreateSession(roomId: "room-new", createdAt: now.AddMinutes(-30)));

        var (sessions, total) = await _sut.GetAllSessionsAsync(hoursBack: 1);

        Assert.Single(sessions);
        Assert.Equal("room-new", sessions[0].RoomId);
    }

    [Fact]
    public async Task GetAllSessions_WorkspacePathFilter()
    {
        await SeedAsync(
            CreateSession(roomId: "room-1", workspacePath: "/home/proj-a"),
            CreateSession(roomId: "room-2", workspacePath: "/home/proj-b"),
            CreateSession(roomId: "room-3", workspacePath: "/home/proj-a"));

        var (sessions, total) = await _sut.GetAllSessionsAsync(workspacePath: "/home/proj-a");

        Assert.Equal(2, sessions.Count);
        Assert.Equal(2, total);
        Assert.All(sessions, s => Assert.Equal("/home/proj-a", s.WorkspacePath));
    }

    [Fact]
    public async Task GetAllSessions_CombinedFilters()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            CreateSession(roomId: "r1", status: "Active", workspacePath: "/proj",
                createdAt: now.AddMinutes(-10)),
            CreateSession(roomId: "r2", status: "Archived", workspacePath: "/proj",
                createdAt: now.AddMinutes(-10)),
            CreateSession(roomId: "r3", status: "Active", workspacePath: "/other",
                createdAt: now.AddMinutes(-10)),
            CreateSession(roomId: "r4", status: "Active", workspacePath: "/proj",
                createdAt: now.AddHours(-48)));

        var (sessions, total) = await _sut.GetAllSessionsAsync(
            status: "Active", hoursBack: 1, workspacePath: "/proj");

        Assert.Single(sessions);
        Assert.Equal("r1", sessions[0].RoomId);
    }

    // ── GetSessionStatsAsync ────────────────────────────────────────────

    [Fact]
    public async Task GetSessionStats_NoData_ReturnsZeros()
    {
        var stats = await _sut.GetSessionStatsAsync();

        Assert.Equal(0, stats.TotalSessions);
        Assert.Equal(0, stats.ActiveSessions);
        Assert.Equal(0, stats.ArchivedSessions);
        Assert.Equal(0, stats.TotalMessages);
    }

    [Fact]
    public async Task GetSessionStats_CountsCorrectly()
    {
        await SeedAsync(
            CreateSession(status: "Active", messageCount: 10),
            CreateSession(status: "Active", messageCount: 20),
            CreateSession(status: "Archived", messageCount: 30));

        var stats = await _sut.GetSessionStatsAsync();

        Assert.Equal(3, stats.TotalSessions);
        Assert.Equal(2, stats.ActiveSessions);
        Assert.Equal(1, stats.ArchivedSessions);
        Assert.Equal(60, stats.TotalMessages);
    }

    [Fact]
    public async Task GetSessionStats_HoursBackFilter()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            CreateSession(status: "Active", messageCount: 10,
                createdAt: now.AddMinutes(-30)),
            CreateSession(status: "Active", messageCount: 5,
                createdAt: now.AddHours(-48)));

        var stats = await _sut.GetSessionStatsAsync(hoursBack: 1);

        Assert.Equal(1, stats.TotalSessions);
        Assert.Equal(10, stats.TotalMessages);
    }

    [Fact]
    public async Task GetSessionStats_WorkspacePathFilter()
    {
        await SeedAsync(
            CreateSession(workspacePath: "/proj-a", messageCount: 10),
            CreateSession(workspacePath: "/proj-b", messageCount: 20));

        var stats = await _sut.GetSessionStatsAsync(workspacePath: "/proj-a");

        Assert.Equal(1, stats.TotalSessions);
        Assert.Equal(10, stats.TotalMessages);
    }

    [Fact]
    public async Task GetSessionStats_CombinedFilters()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            CreateSession(workspacePath: "/proj", messageCount: 10,
                createdAt: now.AddMinutes(-10)),
            CreateSession(workspacePath: "/proj", messageCount: 20,
                createdAt: now.AddHours(-48)),
            CreateSession(workspacePath: "/other", messageCount: 30,
                createdAt: now.AddMinutes(-10)));

        var stats = await _sut.GetSessionStatsAsync(hoursBack: 1, workspacePath: "/proj");

        Assert.Equal(1, stats.TotalSessions);
        Assert.Equal(10, stats.TotalMessages);
    }
}
