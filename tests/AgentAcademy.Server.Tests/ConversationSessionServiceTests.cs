using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for ConversationSessionService — epoch lifecycle, summarization, and rotation.
/// </summary>
public class ConversationSessionServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly SystemSettingsService _settings;
    private readonly IAgentExecutor _executor;
    private readonly ConversationSessionService _service;

    public ConversationSessionServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _settings = new SystemSettingsService(_db);
        _executor = Substitute.For<IAgentExecutor>();
        _service = new ConversationSessionService(
            _db, _settings, _executor,
            NullLogger<ConversationSessionService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task SeedRoomAsync(string roomId)
    {
        if (!await _db.Rooms.AnyAsync(r => r.Id == roomId))
        {
            _db.Rooms.Add(new Data.Entities.RoomEntity
            {
                Id = roomId,
                Name = "Test Room",
                Status = "Active",
                CurrentPhase = "Intake",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();
        }
    }

    private async Task SeedBreakoutRoomAsync(string breakoutRoomId, string parentRoomId)
    {
        await SeedRoomAsync(parentRoomId);
        if (!await _db.BreakoutRooms.AnyAsync(r => r.Id == breakoutRoomId))
        {
            _db.BreakoutRooms.Add(new Data.Entities.BreakoutRoomEntity
            {
                Id = breakoutRoomId,
                Name = "Test Breakout",
                ParentRoomId = parentRoomId,
                AssignedAgentId = "agent-1",
                Status = "Active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task GetOrCreate_CreatesFirstSession()
    {
        var session = await _service.GetOrCreateActiveSessionAsync("room-1");

        Assert.NotNull(session);
        Assert.Equal("room-1", session.RoomId);
        Assert.Equal("Main", session.RoomType);
        Assert.Equal(1, session.SequenceNumber);
        Assert.Equal("Active", session.Status);
        Assert.Equal(0, session.MessageCount);
    }

    [Fact]
    public async Task GetOrCreate_ReturnsSameSession()
    {
        var s1 = await _service.GetOrCreateActiveSessionAsync("room-1");
        var s2 = await _service.GetOrCreateActiveSessionAsync("room-1");

        Assert.Equal(s1.Id, s2.Id);
    }

    [Fact]
    public async Task GetOrCreate_SeparateRooms()
    {
        var s1 = await _service.GetOrCreateActiveSessionAsync("room-1");
        var s2 = await _service.GetOrCreateActiveSessionAsync("room-2");

        Assert.NotEqual(s1.Id, s2.Id);
        Assert.Equal("room-1", s1.RoomId);
        Assert.Equal("room-2", s2.RoomId);
    }

    [Fact]
    public async Task GetOrCreate_BreakoutRoomType()
    {
        var session = await _service.GetOrCreateActiveSessionAsync("br-1", "Breakout");

        Assert.Equal("Breakout", session.RoomType);
    }

    [Fact]
    public async Task IncrementCount_IncrementsCorrectly()
    {
        var session = await _service.GetOrCreateActiveSessionAsync("room-1");
        Assert.Equal(0, session.MessageCount);

        await _service.IncrementMessageCountAsync(session.Id);
        await _service.IncrementMessageCountAsync(session.Id);

        var updated = await _db.ConversationSessions.FindAsync(session.Id);
        Assert.Equal(2, updated!.MessageCount);
    }

    [Fact]
    public async Task CheckAndRotate_NoRotationBelowThreshold()
    {
        var session = await _service.GetOrCreateActiveSessionAsync("room-1");

        // Add a few messages but stay below threshold (default 50)
        for (int i = 0; i < 10; i++)
            await _service.IncrementMessageCountAsync(session.Id);

        var rotated = await _service.CheckAndRotateAsync("room-1");

        Assert.False(rotated);
        var activeSession = await _db.ConversationSessions
            .FirstAsync(s => s.RoomId == "room-1" && s.Status == "Active");
        Assert.Equal(session.Id, activeSession.Id);
    }

    [Fact]
    public async Task CheckAndRotate_RotatesAtThreshold()
    {
        // Set threshold to 5 for testing
        await _settings.SetAsync("conversation.mainRoomEpochSize", "5");
        await SeedRoomAsync("room-1");

        var session = await _service.GetOrCreateActiveSessionAsync("room-1");

        // Add messages to reach threshold
        for (int i = 0; i < 5; i++)
        {
            await _service.IncrementMessageCountAsync(session.Id);
            // Also add actual message entities for the summarizer
            _db.Messages.Add(new MessageEntity
            {
                Id = Guid.NewGuid().ToString(),
                RoomId = "room-1",
                SessionId = session.Id,
                SenderId = "agent-1",
                SenderName = "TestAgent",
                SenderKind = "Agent",
                Kind = "Response",
                Content = $"Test message {i}",
                SentAt = DateTime.UtcNow,
            });
        }
        await _db.SaveChangesAsync();

        // Mock executor to return a summary
        _executor.IsFullyOperational.Returns(true);
        _executor.RunAsync(
            Arg.Any<AgentDefinition>(),
            Arg.Any<string>(),
            Arg.Is<string?>(x => x == null),
            Arg.Any<CancellationToken>())
            .Returns("Summary of 5 messages.");

        var rotated = await _service.CheckAndRotateAsync("room-1");

        Assert.True(rotated);

        // Old session should be archived
        var archived = await _db.ConversationSessions.FindAsync(session.Id);
        Assert.Equal("Archived", archived!.Status);
        Assert.Equal("Summary of 5 messages.", archived.Summary);
        Assert.NotNull(archived.ArchivedAt);

        // New session should be active
        var newSession = await _db.ConversationSessions
            .FirstAsync(s => s.RoomId == "room-1" && s.Status == "Active");
        Assert.Equal(2, newSession.SequenceNumber);
        Assert.Equal(0, newSession.MessageCount);

        // SDK sessions should have been invalidated
        await _executor.Received(1).InvalidateRoomSessionsAsync("room-1");
    }

    [Fact]
    public async Task CheckAndRotate_BreakoutUsesOwnThreshold()
    {
        await _settings.SetAsync("conversation.mainRoomEpochSize", "100");
        await _settings.SetAsync("conversation.breakoutEpochSize", "3");
        await SeedBreakoutRoomAsync("br-1", "parent-room-1");

        var session = await _service.GetOrCreateActiveSessionAsync("br-1", "Breakout");

        for (int i = 0; i < 3; i++)
        {
            await _service.IncrementMessageCountAsync(session.Id);
            _db.BreakoutMessages.Add(new BreakoutMessageEntity
            {
                Id = Guid.NewGuid().ToString(),
                BreakoutRoomId = "br-1",
                SessionId = session.Id,
                SenderId = "agent-1",
                SenderName = "TestAgent",
                SenderKind = "Agent",
                Kind = "Response",
                Content = $"Breakout message {i}",
                SentAt = DateTime.UtcNow,
            });
        }
        await _db.SaveChangesAsync();

        _executor.IsFullyOperational.Returns(true);
        _executor.RunAsync(
            Arg.Any<AgentDefinition>(),
            Arg.Any<string>(),
            Arg.Is<string?>(x => x == null),
            Arg.Any<CancellationToken>())
            .Returns("Breakout summary.");

        var rotated = await _service.CheckAndRotateAsync("br-1", "Breakout");

        Assert.True(rotated);
    }

    [Fact]
    public async Task GetSessionContext_ReturnsLatestArchivedSummary()
    {
        // Create and archive two sessions
        _db.ConversationSessions.Add(new ConversationSessionEntity
        {
            Id = "session-1",
            RoomId = "room-1",
            RoomType = "Main",
            SequenceNumber = 1,
            Status = "Archived",
            Summary = "First summary",
            ArchivedAt = DateTime.UtcNow.AddMinutes(-10),
        });
        _db.ConversationSessions.Add(new ConversationSessionEntity
        {
            Id = "session-2",
            RoomId = "room-1",
            RoomType = "Main",
            SequenceNumber = 2,
            Status = "Archived",
            Summary = "Second summary",
            ArchivedAt = DateTime.UtcNow.AddMinutes(-5),
        });
        _db.ConversationSessions.Add(new ConversationSessionEntity
        {
            Id = "session-3",
            RoomId = "room-1",
            RoomType = "Main",
            SequenceNumber = 3,
            Status = "Active",
        });
        await _db.SaveChangesAsync();

        var summary = await _service.GetSessionContextAsync("room-1");

        Assert.Equal("Second summary", summary);
    }

    [Fact]
    public async Task GetSessionContext_ReturnsNullWhenNoArchivedSessions()
    {
        await _service.GetOrCreateActiveSessionAsync("room-1");
        var summary = await _service.GetSessionContextAsync("room-1");
        Assert.Null(summary);
    }

    [Fact]
    public async Task Rotation_FallbackSummaryWhenOffline()
    {
        await _settings.SetAsync("conversation.mainRoomEpochSize", "2");
        await SeedRoomAsync("room-1");
        var session = await _service.GetOrCreateActiveSessionAsync("room-1");

        for (int i = 0; i < 2; i++)
        {
            await _service.IncrementMessageCountAsync(session.Id);
            _db.Messages.Add(new MessageEntity
            {
                Id = Guid.NewGuid().ToString(),
                RoomId = "room-1",
                SessionId = session.Id,
                SenderId = "agent-1",
                SenderName = "TestAgent",
                SenderKind = "Agent",
                Kind = "Response",
                Content = $"Message {i}",
                SentAt = DateTime.UtcNow,
            });
        }
        await _db.SaveChangesAsync();

        // Executor is offline
        _executor.IsFullyOperational.Returns(false);

        var rotated = await _service.CheckAndRotateAsync("room-1");

        Assert.True(rotated);
        var archived = await _db.ConversationSessions.FindAsync(session.Id);
        Assert.Contains("TestAgent", archived!.Summary);
    }

    [Fact]
    public async Task SequenceNumbers_IncrementCorrectly()
    {
        await _settings.SetAsync("conversation.mainRoomEpochSize", "1");
        await SeedRoomAsync("room-1");

        // Create and immediately rotate 3 times
        for (int epoch = 0; epoch < 3; epoch++)
        {
            var session = await _service.GetOrCreateActiveSessionAsync("room-1");
            await _service.IncrementMessageCountAsync(session.Id);
            _db.Messages.Add(new MessageEntity
            {
                Id = Guid.NewGuid().ToString(),
                RoomId = "room-1",
                SessionId = session.Id,
                SenderId = "agent-1",
                SenderName = "Agent",
                SenderKind = "Agent",
                Kind = "Response",
                Content = "msg",
                SentAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();

            _executor.IsFullyOperational.Returns(false);
            await _service.CheckAndRotateAsync("room-1");
        }

        var sessions = await _db.ConversationSessions
            .Where(s => s.RoomId == "room-1")
            .OrderBy(s => s.SequenceNumber)
            .ToListAsync();

        Assert.Equal(4, sessions.Count); // 3 archived + 1 active
        Assert.Equal(1, sessions[0].SequenceNumber);
        Assert.Equal(2, sessions[1].SequenceNumber);
        Assert.Equal(3, sessions[2].SequenceNumber);
        Assert.Equal(4, sessions[3].SequenceNumber);
        Assert.Equal("Active", sessions[3].Status);
    }

    // ── ArchiveAllActiveSessionsAsync ──

    [Fact]
    public async Task ArchiveAll_ReturnsZeroWhenNoActiveSessions()
    {
        var count = await _service.ArchiveAllActiveSessionsAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ArchiveAll_ArchivesEmptySessionWithoutSummary()
    {
        await _service.GetOrCreateActiveSessionAsync("room-1");

        var count = await _service.ArchiveAllActiveSessionsAsync();

        Assert.Equal(1, count);
        var session = await _db.ConversationSessions.FirstAsync(s => s.RoomId == "room-1");
        Assert.Equal("Archived", session.Status);
        Assert.Null(session.Summary);
        Assert.NotNull(session.ArchivedAt);
    }

    [Fact]
    public async Task ArchiveAll_SummarizesSessionsWithMessages()
    {
        await SeedRoomAsync("room-1");
        var session = await _service.GetOrCreateActiveSessionAsync("room-1");

        // Add messages
        for (int i = 0; i < 3; i++)
        {
            await _service.IncrementMessageCountAsync(session.Id);
            _db.Messages.Add(new MessageEntity
            {
                Id = Guid.NewGuid().ToString(),
                RoomId = "room-1",
                SessionId = session.Id,
                SenderId = "agent-1",
                SenderName = "TestAgent",
                SenderKind = "Agent",
                Kind = "Response",
                Content = $"Message {i}",
                SentAt = DateTime.UtcNow,
            });
        }
        await _db.SaveChangesAsync();

        _executor.IsFullyOperational.Returns(true);
        _executor.RunAsync(
            Arg.Any<AgentDefinition>(),
            Arg.Any<string>(),
            Arg.Is<string?>(x => x == null),
            Arg.Any<CancellationToken>())
            .Returns("LLM summary of workspace.");

        var count = await _service.ArchiveAllActiveSessionsAsync();

        Assert.Equal(1, count);
        var archived = await _db.ConversationSessions.FindAsync(session.Id);
        Assert.Equal("Archived", archived!.Status);
        Assert.Equal("LLM summary of workspace.", archived.Summary);
        Assert.NotNull(archived.ArchivedAt);
    }

    [Fact]
    public async Task ArchiveAll_ArchivesMultipleRooms()
    {
        var s1 = await _service.GetOrCreateActiveSessionAsync("room-1");
        var s2 = await _service.GetOrCreateActiveSessionAsync("room-2");
        var s3 = await _service.GetOrCreateActiveSessionAsync("room-3");

        var count = await _service.ArchiveAllActiveSessionsAsync();

        Assert.Equal(3, count);
        var all = await _db.ConversationSessions.ToListAsync();
        Assert.All(all, s => Assert.Equal("Archived", s.Status));
    }

    [Fact]
    public async Task ArchiveAll_DoesNotCreateReplacementSessions()
    {
        await _service.GetOrCreateActiveSessionAsync("room-1");
        await _service.GetOrCreateActiveSessionAsync("room-2");

        await _service.ArchiveAllActiveSessionsAsync();

        var activeSessions = await _db.ConversationSessions
            .Where(s => s.Status == "Active")
            .CountAsync();
        Assert.Equal(0, activeSessions);
    }

    [Fact]
    public async Task ArchiveAll_SummaryAvailableViaGetSessionContext()
    {
        await SeedRoomAsync("room-1");
        var session = await _service.GetOrCreateActiveSessionAsync("room-1");

        await _service.IncrementMessageCountAsync(session.Id);
        _db.Messages.Add(new MessageEntity
        {
            Id = Guid.NewGuid().ToString(),
            RoomId = "room-1",
            SessionId = session.Id,
            SenderId = "agent-1",
            SenderName = "Agent",
            SenderKind = "Agent",
            Kind = "Response",
            Content = "Important context",
            SentAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        _executor.IsFullyOperational.Returns(true);
        _executor.RunAsync(
            Arg.Any<AgentDefinition>(),
            Arg.Any<string>(),
            Arg.Is<string?>(x => x == null),
            Arg.Any<CancellationToken>())
            .Returns("Resume context: important decisions were made.");

        await _service.ArchiveAllActiveSessionsAsync();

        // The summary should be retrievable for session resume
        var summary = await _service.GetSessionContextAsync("room-1");
        Assert.Equal("Resume context: important decisions were made.", summary);
    }

    [Fact]
    public async Task ArchiveAll_FallbackWhenExecutorOffline()
    {
        await SeedRoomAsync("room-1");
        var session = await _service.GetOrCreateActiveSessionAsync("room-1");

        await _service.IncrementMessageCountAsync(session.Id);
        _db.Messages.Add(new MessageEntity
        {
            Id = Guid.NewGuid().ToString(),
            RoomId = "room-1",
            SessionId = session.Id,
            SenderId = "agent-1",
            SenderName = "TestAgent",
            SenderKind = "Agent",
            Kind = "Response",
            Content = "msg",
            SentAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        _executor.IsFullyOperational.Returns(false);

        await _service.ArchiveAllActiveSessionsAsync();

        var archived = await _db.ConversationSessions.FindAsync(session.Id);
        Assert.Equal("Archived", archived!.Status);
        Assert.Contains("TestAgent", archived.Summary);
    }

    [Fact]
    public async Task ArchiveAll_IgnoresAlreadyArchivedSessions()
    {
        _db.ConversationSessions.Add(new ConversationSessionEntity
        {
            Id = "already-archived",
            RoomId = "room-1",
            RoomType = "Main",
            SequenceNumber = 1,
            Status = "Archived",
            Summary = "Old summary",
            ArchivedAt = DateTime.UtcNow.AddHours(-1),
        });
        await _db.SaveChangesAsync();

        var count = await _service.ArchiveAllActiveSessionsAsync();

        Assert.Equal(0, count);
        var session = await _db.ConversationSessions.FindAsync("already-archived");
        Assert.Equal("Old summary", session!.Summary);
    }
}
