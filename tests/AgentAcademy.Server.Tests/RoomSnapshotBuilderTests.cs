using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Tests;

public sealed class RoomSnapshotBuilderTests : IDisposable
{
    private static readonly List<AgentDefinition> TestAgents =
    [
        new("agent-1", "Agent One", "Engineer", "Test agent 1", "", null, ["coding"], [], true),
        new("agent-2", "Agent Two", "Reviewer", "Test agent 2", "", null, ["review"], [], true),
    ];

    private readonly TestServiceGraph _graph = new(TestAgents);
    private RoomSnapshotBuilder Sut => _graph.RoomSnapshotBuilder;
    private AgentAcademyDbContext Db => _graph.Db;

    public void Dispose() => _graph.Dispose();

    // ── Helpers ──

    private RoomEntity SeedRoom(string id = "room-1", string name = "Test Room",
        string status = "Active", string phase = "Implementation", string? workspace = null)
    {
        var now = DateTime.UtcNow;
        var room = new RoomEntity
        {
            Id = id, Name = name, Topic = "Test topic",
            Status = status, CurrentPhase = phase,
            WorkspacePath = workspace,
            CreatedAt = now.AddHours(-1), UpdatedAt = now
        };
        Db.Rooms.Add(room);
        Db.SaveChanges();
        return room;
    }

    private ConversationSessionEntity SeedSession(string roomId, string status = "Active")
    {
        var session = new ConversationSessionEntity
        {
            Id = Guid.NewGuid().ToString(), RoomId = roomId, Status = status,
            CreatedAt = DateTime.UtcNow
        };
        Db.ConversationSessions.Add(session);
        Db.SaveChanges();
        return session;
    }

    private MessageEntity SeedMessage(string roomId, string content,
        DateTime? sentAt = null, string? sessionId = null,
        string senderKind = nameof(MessageSenderKind.Agent),
        string kind = nameof(MessageKind.Response),
        string? recipientId = null)
    {
        var msg = new MessageEntity
        {
            Id = Guid.NewGuid().ToString("N"), RoomId = roomId,
            SenderId = "sender-1", SenderName = "Sender",
            SenderKind = senderKind, Kind = kind,
            Content = content,
            SentAt = sentAt ?? DateTime.UtcNow,
            SessionId = sessionId, RecipientId = recipientId
        };
        Db.Messages.Add(msg);
        Db.SaveChanges();
        return msg;
    }

    private TaskEntity SeedTask(string roomId, string status = "Active",
        DateTime? createdAt = null, string preferredRoles = "[]")
    {
        var task = new TaskEntity
        {
            Id = Guid.NewGuid().ToString("N"), Title = "Test Task",
            Description = "Desc", SuccessCriteria = "Done",
            Status = status, Type = "Feature", CurrentPhase = "Planning",
            CurrentPlan = "", PreferredRoles = preferredRoles,
            FleetModels = "[]",
            ValidationStatus = "NotStarted", ValidationSummary = "",
            ImplementationStatus = "NotStarted", ImplementationSummary = "",
            ReviewRounds = 0, CommitCount = 0,
            RoomId = roomId,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        Db.Tasks.Add(task);
        Db.SaveChanges();
        return task;
    }

    private AgentLocationEntity SeedLocation(string agentId, string roomId,
        string? breakoutRoomId = null)
    {
        var loc = new AgentLocationEntity
        {
            AgentId = agentId, RoomId = roomId,
            State = "Idle", BreakoutRoomId = breakoutRoomId,
            UpdatedAt = DateTime.UtcNow
        };
        Db.AgentLocations.Add(loc);
        Db.SaveChanges();
        return loc;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BuildRoomSnapshotAsync
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BuildSnapshot_EmptyRoom_ReturnsEmptyMessages()
    {
        var room = SeedRoom();

        var snapshot = await Sut.BuildRoomSnapshotAsync(room);

        Assert.Empty(snapshot.RecentMessages);
    }

    [Fact]
    public async Task BuildSnapshot_MessagesOrderedBySentAt()
    {
        var room = SeedRoom();
        var session = SeedSession(room.Id);
        var now = DateTime.UtcNow;
        SeedMessage(room.Id, "second", now.AddMinutes(2), session.Id);
        SeedMessage(room.Id, "first", now.AddMinutes(1), session.Id);
        SeedMessage(room.Id, "third", now.AddMinutes(3), session.Id);

        var snapshot = await Sut.BuildRoomSnapshotAsync(room);

        Assert.Equal(3, snapshot.RecentMessages.Count);
        Assert.Equal("first", snapshot.RecentMessages[0].Content);
        Assert.Equal("second", snapshot.RecentMessages[1].Content);
        Assert.Equal("third", snapshot.RecentMessages[2].Content);
    }

    [Fact]
    public async Task BuildSnapshot_ExcludesDmMessages()
    {
        var room = SeedRoom();
        var session = SeedSession(room.Id);
        SeedMessage(room.Id, "public", sessionId: session.Id);
        SeedMessage(room.Id, "dm", sessionId: session.Id, recipientId: "agent-2");

        var snapshot = await Sut.BuildRoomSnapshotAsync(room);

        Assert.Single(snapshot.RecentMessages);
        Assert.Equal("public", snapshot.RecentMessages[0].Content);
    }

    [Fact]
    public async Task BuildSnapshot_LimitsTo200Messages()
    {
        var room = SeedRoom();
        var session = SeedSession(room.Id);
        var baseTime = DateTime.UtcNow;
        for (var i = 0; i < 210; i++)
            SeedMessage(room.Id, $"msg-{i:D3}", baseTime.AddSeconds(i), session.Id);

        var snapshot = await Sut.BuildRoomSnapshotAsync(room);

        Assert.Equal(200, snapshot.RecentMessages.Count);
        // Should keep the 200 most recent (10..209)
        Assert.Equal("msg-010", snapshot.RecentMessages[0].Content);
    }

    [Fact]
    public async Task BuildSnapshot_FiltersMessagesByActiveSession()
    {
        var room = SeedRoom();
        var activeSession = SeedSession(room.Id, "Active");
        var oldSession = SeedSession(room.Id, "Archived");

        SeedMessage(room.Id, "active-session", sessionId: activeSession.Id);
        SeedMessage(room.Id, "old-session", sessionId: oldSession.Id);
        SeedMessage(room.Id, "no-session", sessionId: null);
        SeedMessage(room.Id, "user-msg", sessionId: oldSession.Id,
            senderKind: nameof(MessageSenderKind.User));

        var snapshot = await Sut.BuildRoomSnapshotAsync(room);

        var contents = snapshot.RecentMessages.Select(m => m.Content).ToList();
        Assert.Contains("active-session", contents);
        Assert.Contains("no-session", contents);
        Assert.DoesNotContain("old-session", contents);
        // Per spec 005 §Message Management: user messages from prior sessions must NOT
        // leak into the current room snapshot. Regression test for #64.
        Assert.DoesNotContain("user-msg", contents);
    }

    [Fact]
    public async Task BuildSnapshot_DoesNotLeakUserMessagesFromPriorSession()
    {
        // Regression test for #64: a new sprint session in a room that has user
        // messages from a prior session must not return those old user messages.
        var room = SeedRoom();
        var priorSession = SeedSession(room.Id, "Archived");
        var currentSession = SeedSession(room.Id, "Active");

        SeedMessage(room.Id, "prior-user-msg", sessionId: priorSession.Id,
            senderKind: nameof(MessageSenderKind.User));
        SeedMessage(room.Id, "prior-agent-msg", sessionId: priorSession.Id,
            senderKind: nameof(MessageSenderKind.Agent));
        SeedMessage(room.Id, "current-user-msg", sessionId: currentSession.Id,
            senderKind: nameof(MessageSenderKind.User));

        var snapshot = await Sut.BuildRoomSnapshotAsync(room);

        var contents = snapshot.RecentMessages.Select(m => m.Content).ToList();
        Assert.Contains("current-user-msg", contents);
        Assert.DoesNotContain("prior-user-msg", contents);
        Assert.DoesNotContain("prior-agent-msg", contents);
    }

    [Fact]
    public async Task BuildSnapshot_NoActiveSession_ReturnsOnlyUntaggedMessages()
    {
        // Regression guard for #64: when no Active ConversationSession exists
        // (real state after ArchiveAllActiveSessionsAsync), the snapshot must NOT
        // fall back to returning all cross-session history. Only legacy untagged
        // messages should be returned.
        var room = SeedRoom();
        var archivedSession = SeedSession(room.Id, "Archived");

        SeedMessage(room.Id, "archived-agent", sessionId: archivedSession.Id);
        SeedMessage(room.Id, "archived-user", sessionId: archivedSession.Id,
            senderKind: nameof(MessageSenderKind.User));
        SeedMessage(room.Id, "legacy-untagged", sessionId: null);

        var snapshot = await Sut.BuildRoomSnapshotAsync(room);

        var contents = snapshot.RecentMessages.Select(m => m.Content).ToList();
        Assert.Contains("legacy-untagged", contents);
        Assert.DoesNotContain("archived-agent", contents);
        Assert.DoesNotContain("archived-user", contents);
    }

    [Fact]
    public async Task BuildSnapshot_NoActiveTask_ReturnsNull()
    {
        var room = SeedRoom();
        SeedTask(room.Id, status: "Completed");

        var snapshot = await Sut.BuildRoomSnapshotAsync(room);

        Assert.Null(snapshot.ActiveTask);
    }

    [Fact]
    public async Task BuildSnapshot_ActiveTask_ReturnsSnapshot()
    {
        var room = SeedRoom();
        var task = SeedTask(room.Id, status: "Active");

        var snapshot = await Sut.BuildRoomSnapshotAsync(room);

        Assert.NotNull(snapshot.ActiveTask);
        Assert.Equal(task.Id, snapshot.ActiveTask.Id);
    }

    [Fact]
    public async Task BuildSnapshot_MultipleActiveTasks_ReturnsMostRecent()
    {
        var room = SeedRoom();
        var now = DateTime.UtcNow;
        SeedTask(room.Id, status: "Active", createdAt: now.AddHours(-2));
        var newer = SeedTask(room.Id, status: "Active", createdAt: now);

        var snapshot = await Sut.BuildRoomSnapshotAsync(room);

        Assert.NotNull(snapshot.ActiveTask);
        Assert.Equal(newer.Id, snapshot.ActiveTask.Id);
    }

    [Fact]
    public async Task BuildSnapshot_UsesPreloadedLocations()
    {
        var room = SeedRoom();
        // Don't seed locations in DB — provide them directly
        var preloaded = new List<AgentLocationEntity>
        {
            new() { AgentId = "agent-1", RoomId = room.Id, State = "Idle", UpdatedAt = DateTime.UtcNow }
        };

        var snapshot = await Sut.BuildRoomSnapshotAsync(room, preloaded);

        Assert.Single(snapshot.Participants);
        Assert.Equal("agent-1", snapshot.Participants[0].AgentId);
    }

    [Fact]
    public async Task BuildSnapshot_QueriesLocationsWhenNotPreloaded()
    {
        var room = SeedRoom();
        SeedLocation("agent-1", room.Id);

        var snapshot = await Sut.BuildRoomSnapshotAsync(room, preloadedLocations: null);

        Assert.Single(snapshot.Participants);
        Assert.Equal("agent-1", snapshot.Participants[0].AgentId);
    }

    [Fact]
    public async Task BuildSnapshot_MapsRoomMetadata()
    {
        var room = SeedRoom(id: "r-meta", name: "Meta Room",
            status: "Active", phase: "Implementation");

        var snapshot = await Sut.BuildRoomSnapshotAsync(room);

        Assert.Equal("r-meta", snapshot.Id);
        Assert.Equal("Meta Room", snapshot.Name);
        Assert.Equal("Test topic", snapshot.Topic);
        Assert.Equal(RoomStatus.Active, snapshot.Status);
        Assert.Equal(CollaborationPhase.Implementation, snapshot.CurrentPhase);
        Assert.Equal(room.CreatedAt, snapshot.CreatedAt);
        Assert.Equal(room.UpdatedAt, snapshot.UpdatedAt);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BuildParticipants
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildParticipants_EmptyLocations_ReturnsEmpty()
    {
        var result = Sut.BuildParticipants([], []);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildParticipants_FiltersOutUnknownAgents()
    {
        var locations = new List<AgentLocationEntity>
        {
            new() { AgentId = "unknown-agent", RoomId = "r1", State = "Idle", UpdatedAt = DateTime.UtcNow }
        };

        var result = Sut.BuildParticipants(locations, []);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildParticipants_FiltersOutBreakoutAgents()
    {
        var locations = new List<AgentLocationEntity>
        {
            new() { AgentId = "agent-1", RoomId = "r1", State = "Idle",
                     BreakoutRoomId = "breakout-1", UpdatedAt = DateTime.UtcNow }
        };

        var result = Sut.BuildParticipants(locations, []);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildParticipants_SetsPreferredWhenRoleMatches()
    {
        var locations = new List<AgentLocationEntity>
        {
            new() { AgentId = "agent-1", RoomId = "r1", State = "Idle", UpdatedAt = DateTime.UtcNow }
        };

        var result = Sut.BuildParticipants(locations, ["Engineer"]);

        Assert.Single(result);
        Assert.Equal(AgentAvailability.Preferred, result[0].Availability);
        Assert.True(result[0].IsPreferred);
    }

    [Fact]
    public void BuildParticipants_SetsReadyWhenRoleNotPreferred()
    {
        var locations = new List<AgentLocationEntity>
        {
            new() { AgentId = "agent-2", RoomId = "r1", State = "Idle", UpdatedAt = DateTime.UtcNow }
        };

        var result = Sut.BuildParticipants(locations, ["Engineer"]);

        Assert.Single(result);
        Assert.Equal(AgentAvailability.Ready, result[0].Availability);
        Assert.False(result[0].IsPreferred);
    }

    [Fact]
    public void BuildParticipants_MapsAllAgentFields()
    {
        var now = DateTime.UtcNow;
        var locations = new List<AgentLocationEntity>
        {
            new() { AgentId = "agent-1", RoomId = "r1", State = "Idle", UpdatedAt = now }
        };

        var result = Sut.BuildParticipants(locations, []);

        var p = Assert.Single(result);
        Assert.Equal("agent-1", p.AgentId);
        Assert.Equal("Agent One", p.Name);
        Assert.Equal("Engineer", p.Role);
        Assert.Equal(now, p.LastActivityAt);
        Assert.Contains("coding", p.ActiveCapabilities);
    }

    // ── No phase filtering — participants reflect room membership ──

    [Fact]
    public void BuildParticipants_IncludesAllRolesRegardlessOfPhase()
    {
        // BuildParticipants no longer filters by phase — all agents with
        // locations in the room are included. Phase filtering for conversation
        // participation is handled by ConversationRoundRunner.
        var locations = new List<AgentLocationEntity>
        {
            new() { AgentId = "agent-1", RoomId = "r1", State = "Idle", UpdatedAt = DateTime.UtcNow },
            new() { AgentId = "agent-2", RoomId = "r1", State = "Idle", UpdatedAt = DateTime.UtcNow }
        };

        var result = Sut.BuildParticipants(locations, []);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void BuildParticipants_IncludesMixedRolesInIntakeRoom()
    {
        // Even during Intake, BuildParticipants returns all agents in the room.
        var agents = new List<AgentDefinition>
        {
            new("planner-1", "Planny", "Planner", "Planner agent", "", null, ["planning"], [], true),
            new("eng-1", "Engy", "Engineer", "Engineer agent", "", null, ["coding"], [], true),
        };
        using var plannerGraph = new TestServiceGraph(agents);
        var locations = new List<AgentLocationEntity>
        {
            new() { AgentId = "planner-1", RoomId = "r1", State = "Idle", UpdatedAt = DateTime.UtcNow },
            new() { AgentId = "eng-1", RoomId = "r1", State = "Idle", UpdatedAt = DateTime.UtcNow }
        };

        var result = plannerGraph.RoomSnapshotBuilder.BuildParticipants(locations, []);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.AgentId == "planner-1");
        Assert.Contains(result, p => p.AgentId == "eng-1");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BuildChatEnvelope
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildChatEnvelope_MapsAllFields()
    {
        var sentAt = DateTime.UtcNow;
        var entity = new MessageEntity
        {
            Id = "msg-1", RoomId = "room-1", SenderId = "agent-1",
            SenderName = "Agent One", SenderRole = "Engineer",
            SenderKind = nameof(MessageSenderKind.Agent),
            Kind = nameof(MessageKind.Response),
            Content = "Hello", SentAt = sentAt,
            CorrelationId = "corr-1", ReplyToMessageId = "reply-1"
        };

        var env = RoomSnapshotBuilder.BuildChatEnvelope(entity);

        Assert.Equal("msg-1", env.Id);
        Assert.Equal("room-1", env.RoomId);
        Assert.Equal("agent-1", env.SenderId);
        Assert.Equal("Agent One", env.SenderName);
        Assert.Equal("Engineer", env.SenderRole);
        Assert.Equal(MessageSenderKind.Agent, env.SenderKind);
        Assert.Equal(MessageKind.Response, env.Kind);
        Assert.Equal("Hello", env.Content);
        Assert.Equal(sentAt, env.SentAt);
        Assert.Equal("corr-1", env.CorrelationId);
        Assert.Equal("reply-1", env.ReplyToMessageId);
    }

    [Fact]
    public void BuildChatEnvelope_ParsesSenderKindEnum()
    {
        var entity = new MessageEntity
        {
            Id = "m1", RoomId = "r1", SenderId = "s1", SenderName = "S",
            SenderKind = nameof(MessageSenderKind.System),
            Kind = nameof(MessageKind.Response),
            Content = "x", SentAt = DateTime.UtcNow
        };

        var env = RoomSnapshotBuilder.BuildChatEnvelope(entity);

        Assert.Equal(MessageSenderKind.System, env.SenderKind);
    }

    [Fact]
    public void BuildChatEnvelope_ParsesKindEnum()
    {
        var entity = new MessageEntity
        {
            Id = "m1", RoomId = "r1", SenderId = "s1", SenderName = "S",
            SenderKind = nameof(MessageSenderKind.Agent),
            Kind = nameof(MessageKind.Coordination),
            Content = "x", SentAt = DateTime.UtcNow
        };

        var env = RoomSnapshotBuilder.BuildChatEnvelope(entity);

        Assert.Equal(MessageKind.Coordination, env.Kind);
    }
}
