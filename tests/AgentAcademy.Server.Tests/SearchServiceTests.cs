using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for SearchService — workspace-wide FTS5 search across messages and tasks.
/// </summary>
public class SearchServiceTests : IDisposable
{
    private const string TestWorkspace = "/tmp/test-workspace";
    private const string OtherWorkspace = "/tmp/other-workspace";
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly SearchService _service;

    public SearchServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        // Create FTS5 virtual tables and triggers (mirrors the migration)
        _db.Database.ExecuteSqlRaw("""
            CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts
            USING fts5(SenderName, Content, content='messages', content_rowid='rowid');
        """);
        _db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS messages_fts_ai AFTER INSERT ON messages BEGIN
                INSERT INTO messages_fts(rowid, SenderName, Content)
                VALUES (new.rowid, new.SenderName, new.Content);
            END;
        """);
        _db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS messages_fts_ad AFTER DELETE ON messages BEGIN
                INSERT INTO messages_fts(messages_fts, rowid, SenderName, Content)
                VALUES ('delete', old.rowid, old.SenderName, old.Content);
            END;
        """);
        _db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS messages_fts_au AFTER UPDATE ON messages BEGIN
                INSERT INTO messages_fts(messages_fts, rowid, SenderName, Content)
                VALUES ('delete', old.rowid, old.SenderName, old.Content);
                INSERT INTO messages_fts(rowid, SenderName, Content)
                VALUES (new.rowid, new.SenderName, new.Content);
            END;
        """);

        _db.Database.ExecuteSqlRaw("""
            CREATE VIRTUAL TABLE IF NOT EXISTS breakout_messages_fts
            USING fts5(SenderName, Content, content='breakout_messages', content_rowid='rowid');
        """);
        _db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS breakout_messages_fts_ai AFTER INSERT ON breakout_messages BEGIN
                INSERT INTO breakout_messages_fts(rowid, SenderName, Content)
                VALUES (new.rowid, new.SenderName, new.Content);
            END;
        """);
        _db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS breakout_messages_fts_ad AFTER DELETE ON breakout_messages BEGIN
                INSERT INTO breakout_messages_fts(breakout_messages_fts, rowid, SenderName, Content)
                VALUES ('delete', old.rowid, old.SenderName, old.Content);
            END;
        """);
        _db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS breakout_messages_fts_au AFTER UPDATE ON breakout_messages BEGIN
                INSERT INTO breakout_messages_fts(breakout_messages_fts, rowid, SenderName, Content)
                VALUES ('delete', old.rowid, old.SenderName, old.Content);
                INSERT INTO breakout_messages_fts(rowid, SenderName, Content)
                VALUES (new.rowid, new.SenderName, new.Content);
            END;
        """);

        _db.Database.ExecuteSqlRaw("""
            CREATE VIRTUAL TABLE IF NOT EXISTS tasks_fts
            USING fts5(Title, Description, SuccessCriteria, content='tasks', content_rowid='rowid');
        """);
        _db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS tasks_fts_ai AFTER INSERT ON tasks BEGIN
                INSERT INTO tasks_fts(rowid, Title, Description, SuccessCriteria)
                VALUES (new.rowid, new.Title, new.Description, new.SuccessCriteria);
            END;
        """);
        _db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS tasks_fts_ad AFTER DELETE ON tasks BEGIN
                INSERT INTO tasks_fts(tasks_fts, rowid, Title, Description, SuccessCriteria)
                VALUES ('delete', old.rowid, old.Title, old.Description, old.SuccessCriteria);
            END;
        """);
        _db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS tasks_fts_au AFTER UPDATE ON tasks BEGIN
                INSERT INTO tasks_fts(tasks_fts, rowid, Title, Description, SuccessCriteria)
                VALUES ('delete', old.rowid, old.Title, old.Description, old.SuccessCriteria);
                INSERT INTO tasks_fts(rowid, Title, Description, SuccessCriteria)
                VALUES (new.rowid, new.Title, new.Description, new.SuccessCriteria);
            END;
        """);

        _service = new SearchService(_db, NullLogger<SearchService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private RoomEntity CreateRoom(string id = "room-1", string name = "Main", string? workspace = TestWorkspace)
    {
        var room = new RoomEntity
        {
            Id = id, Name = name, Status = "Active", CurrentPhase = "Intake",
            WorkspacePath = workspace, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        _db.Rooms.Add(room);
        return room;
    }

    private MessageEntity CreateMessage(string roomId, string content, string senderName = "Hephaestus",
        string senderKind = "Agent", string? senderRole = "Engineer", string? sessionId = null)
    {
        var msg = new MessageEntity
        {
            Id = Guid.NewGuid().ToString(), RoomId = roomId, Content = content,
            SenderName = senderName, SenderKind = senderKind, SenderRole = senderRole,
            SenderId = senderName.ToLowerInvariant(), Kind = "message",
            SentAt = DateTime.UtcNow, SessionId = sessionId
        };
        _db.Messages.Add(msg);
        return msg;
    }

    private BreakoutRoomEntity CreateBreakoutRoom(string parentRoomId, string id = "breakout-1")
    {
        var br = new BreakoutRoomEntity
        {
            Id = id, Name = "Breakout", ParentRoomId = parentRoomId,
            AssignedAgentId = "agent-1", Status = "Active",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        _db.BreakoutRooms.Add(br);
        return br;
    }

    private BreakoutMessageEntity CreateBreakoutMessage(string breakoutRoomId, string content,
        string senderName = "Hephaestus", string senderKind = "Agent")
    {
        var msg = new BreakoutMessageEntity
        {
            Id = Guid.NewGuid().ToString(), BreakoutRoomId = breakoutRoomId, Content = content,
            SenderName = senderName, SenderKind = senderKind, SenderId = senderName.ToLowerInvariant(),
            Kind = "message", SentAt = DateTime.UtcNow
        };
        _db.BreakoutMessages.Add(msg);
        return msg;
    }

    private TaskEntity CreateTask(string title, string description, string? workspace = TestWorkspace,
        string status = "Active", string? assignedAgent = null)
    {
        var task = new TaskEntity
        {
            Id = Guid.NewGuid().ToString(), Title = title, Description = description,
            SuccessCriteria = "Tests pass", Status = status, WorkspacePath = workspace,
            AssignedAgentName = assignedAgent,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        _db.Tasks.Add(task);
        return task;
    }

    // ── Room Message Search ─────────────────────────────────────

    [Fact]
    public async Task SearchMessages_MatchesContentByKeyword()
    {
        CreateRoom();
        CreateMessage("room-1", "The authentication module needs refactoring");
        CreateMessage("room-1", "Database migration is complete");
        await _db.SaveChangesAsync();

        var results = await _service.SearchAsync("authentication", scope: "messages");

        Assert.Single(results.Messages);
        Assert.Contains("authentication", results.Messages[0].Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("room", results.Messages[0].Source);
    }

    [Fact]
    public async Task SearchMessages_MatchesBySenderName()
    {
        CreateRoom();
        CreateMessage("room-1", "Some generic content", senderName: "Socrates");
        CreateMessage("room-1", "Other content", senderName: "Hephaestus");
        await _db.SaveChangesAsync();

        var results = await _service.SearchAsync("Socrates", scope: "messages");

        Assert.Single(results.Messages);
        Assert.Equal("Socrates", results.Messages[0].SenderName);
    }

    [Fact]
    public async Task SearchMessages_ExcludesSystemMessages()
    {
        CreateRoom();
        CreateMessage("room-1", "System generated auth warning", senderKind: "System", senderRole: null);
        CreateMessage("room-1", "Auth module looks good", senderKind: "Agent");
        await _db.SaveChangesAsync();

        var results = await _service.SearchAsync("auth", scope: "messages");

        Assert.Single(results.Messages);
        Assert.Equal("Agent", results.Messages[0].SenderKind);
    }

    [Fact]
    public async Task SearchMessages_RespectsWorkspaceScope()
    {
        CreateRoom("room-1", "Main", TestWorkspace);
        CreateRoom("room-2", "Other", OtherWorkspace);
        CreateMessage("room-1", "Authentication in workspace one");
        CreateMessage("room-2", "Authentication in workspace two");
        await _db.SaveChangesAsync();

        var results = await _service.SearchAsync("authentication", scope: "messages", workspacePath: TestWorkspace);

        Assert.Single(results.Messages);
        Assert.Equal("room-1", results.Messages[0].RoomId);
    }

    [Fact]
    public async Task SearchMessages_ReturnsRoomName()
    {
        CreateRoom("room-1", "Planning Room");
        CreateMessage("room-1", "Let's plan the sprint");
        await _db.SaveChangesAsync();

        var results = await _service.SearchAsync("sprint", scope: "messages");

        Assert.Single(results.Messages);
        Assert.Equal("Planning Room", results.Messages[0].RoomName);
    }

    [Fact]
    public async Task SearchMessages_IncludesSessionId()
    {
        CreateRoom();
        CreateMessage("room-1", "Session-scoped message about deployment", sessionId: "session-42");
        await _db.SaveChangesAsync();

        var results = await _service.SearchAsync("deployment", scope: "messages");

        Assert.Single(results.Messages);
        Assert.Equal("session-42", results.Messages[0].SessionId);
    }

    [Fact]
    public async Task SearchMessages_RespectsLimit()
    {
        CreateRoom();
        for (var i = 0; i < 10; i++)
            CreateMessage("room-1", $"Authentication attempt {i}");
        await _db.SaveChangesAsync();

        var results = await _service.SearchAsync("authentication", scope: "messages", messageLimit: 3);

        Assert.Equal(3, results.Messages.Count);
    }

    // ── Breakout Message Search ─────────────────────────────────

    [Fact]
    public async Task SearchBreakoutMessages_MatchesContent()
    {
        CreateRoom();
        var br = CreateBreakoutRoom("room-1");
        CreateBreakoutMessage(br.Id, "Implementing the OAuth flow for GitHub integration");
        await _db.SaveChangesAsync();

        var results = await _service.SearchAsync("OAuth", scope: "messages");

        Assert.Single(results.Messages);
        Assert.Equal("breakout", results.Messages[0].Source);
        Assert.Contains("OAuth", results.Messages[0].Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchBreakoutMessages_RespectsWorkspaceScope()
    {
        CreateRoom("room-1", "Main", TestWorkspace);
        CreateRoom("room-2", "Other", OtherWorkspace);
        var br1 = CreateBreakoutRoom("room-1", "br-1");
        var br2 = CreateBreakoutRoom("room-2", "br-2");
        CreateBreakoutMessage(br1.Id, "OAuth in workspace one");
        CreateBreakoutMessage(br2.Id, "OAuth in workspace two");
        await _db.SaveChangesAsync();

        var results = await _service.SearchAsync("OAuth", scope: "messages", workspacePath: TestWorkspace);

        Assert.Single(results.Messages);
        Assert.Equal("room-1", results.Messages[0].RoomId);
    }

    // ── Task Search ─────────────────────────────────────────────

    [Fact]
    public async Task SearchTasks_MatchesByTitle()
    {
        CreateRoom();
        CreateTask("Implement OAuth login", "Add GitHub OAuth flow");
        CreateTask("Fix database migration", "Migration script fails");
        await _db.SaveChangesAsync();

        var results = await _service.SearchAsync("OAuth", scope: "tasks");

        Assert.Single(results.Tasks);
        Assert.Contains("OAuth", results.Tasks[0].Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchTasks_MatchesByDescription()
    {
        CreateRoom();
        CreateTask("Fix login bug", "The JWT token validation fails when the token is expired");
        await _db.SaveChangesAsync();

        var results = await _service.SearchAsync("JWT", scope: "tasks");

        Assert.Single(results.Tasks);
    }

    [Fact]
    public async Task SearchTasks_IncludesAssignedAgent()
    {
        CreateRoom();
        CreateTask("Build search feature", "FTS5 search", assignedAgent: "Hephaestus");
        await _db.SaveChangesAsync();

        var results = await _service.SearchAsync("search", scope: "tasks");

        Assert.Single(results.Tasks);
        Assert.Equal("Hephaestus", results.Tasks[0].AssignedAgentName);
    }

    [Fact]
    public async Task SearchTasks_RespectsWorkspaceScope()
    {
        CreateTask("Authentication feature", "OAuth flow", workspace: TestWorkspace);
        CreateTask("Authentication fix", "Token refresh", workspace: OtherWorkspace);
        await _db.SaveChangesAsync();

        var results = await _service.SearchAsync("authentication", scope: "tasks", workspacePath: TestWorkspace);

        Assert.Single(results.Tasks);
    }

    [Fact]
    public async Task SearchTasks_RespectsLimit()
    {
        for (var i = 0; i < 10; i++)
            CreateTask($"Task about authentication {i}", "Details");
        await _db.SaveChangesAsync();

        var results = await _service.SearchAsync("authentication", scope: "tasks", taskLimit: 3);

        Assert.Equal(3, results.Tasks.Count);
    }

    // ── Combined Search ─────────────────────────────────────────

    [Fact]
    public async Task SearchAll_ReturnsBothMessagesAndTasks()
    {
        CreateRoom();
        CreateMessage("room-1", "The authentication module is deployed");
        CreateTask("Implement authentication", "OAuth + JWT");
        await _db.SaveChangesAsync();

        var results = await _service.SearchAsync("authentication", scope: "all");

        Assert.NotEmpty(results.Messages);
        Assert.NotEmpty(results.Tasks);
        Assert.Equal(results.Messages.Count + results.Tasks.Count, results.TotalCount);
    }

    [Fact]
    public async Task SearchAll_ReturnsQueryInResults()
    {
        var results = await _service.SearchAsync("nonexistent");

        Assert.Equal("nonexistent", results.Query);
    }

    // ── Edge Cases ──────────────────────────────────────────────

    [Fact]
    public async Task Search_EmptyResults_ReturnsEmptyLists()
    {
        var results = await _service.SearchAsync("nonexistent");

        Assert.Empty(results.Messages);
        Assert.Empty(results.Tasks);
        Assert.Equal(0, results.TotalCount);
    }

    [Fact]
    public async Task Search_SpecialCharacters_DoesNotThrow()
    {
        CreateRoom();
        CreateMessage("room-1", "Some content with special chars: foo:bar [test] (parens)");
        await _db.SaveChangesAsync();

        // These should not throw — FTS5 special chars are escaped
        var r1 = await _service.SearchAsync("foo:bar");
        var r2 = await _service.SearchAsync("[test]");
        var r3 = await _service.SearchAsync("(parens)");
        var r4 = await _service.SearchAsync("\"quoted\"");

        // No assertion on result count — just verifying no exception
        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.NotNull(r3);
        Assert.NotNull(r4);
    }

    [Fact]
    public async Task Search_MultiWordQuery_MatchesAllTerms()
    {
        CreateRoom();
        CreateMessage("room-1", "The authentication module handles JWT tokens correctly");
        CreateMessage("room-1", "The authentication module is complete");
        await _db.SaveChangesAsync();

        var results = await _service.SearchAsync("authentication JWT", scope: "messages");

        // FTS5 implicit AND — only the first message matches both terms
        Assert.Single(results.Messages);
    }

    [Fact]
    public async Task Search_NoWorkspaceFilter_ReturnsAllWorkspaces()
    {
        CreateRoom("room-1", "Main", TestWorkspace);
        CreateRoom("room-2", "Other", OtherWorkspace);
        CreateMessage("room-1", "Authentication in workspace one");
        CreateMessage("room-2", "Authentication in workspace two");
        await _db.SaveChangesAsync();

        var results = await _service.SearchAsync("authentication", scope: "messages", workspacePath: null);

        Assert.Equal(2, results.Messages.Count);
    }

    [Fact]
    public async Task Search_LimitClampedToMax()
    {
        for (var i = 0; i < 5; i++)
            CreateTask($"Auth task {i}", "Details");
        await _db.SaveChangesAsync();

        // 200 exceeds max of 100, should be clamped
        var results = await _service.SearchAsync("auth", scope: "tasks", taskLimit: 200);

        Assert.Equal(5, results.Tasks.Count); // only 5 exist
    }

    // ── BuildFts5Query ──────────────────────────────────────────

    [Fact]
    public void BuildFts5Query_SingleTerm_Quoted()
    {
        var result = SearchService.BuildFts5Query("hello");
        Assert.Equal("\"hello\"", result);
    }

    [Fact]
    public void BuildFts5Query_MultipleTerms_ImplicitAnd()
    {
        var result = SearchService.BuildFts5Query("hello world");
        Assert.Equal("\"hello\" \"world\"", result);
    }

    [Fact]
    public void BuildFts5Query_EscapesInternalQuotes()
    {
        var result = SearchService.BuildFts5Query("say \"hello\"");
        Assert.Equal("\"say\" \"\"\"hello\"\"\"", result);
    }

    [Fact]
    public void BuildFts5Query_EmptyInput_ReturnsEmptyQuoted()
    {
        var result = SearchService.BuildFts5Query("");
        Assert.Equal("\"\"", result);
    }

    [Fact]
    public void BuildFts5Query_WhitespaceOnly_ReturnsEmptyQuoted()
    {
        var result = SearchService.BuildFts5Query("   ");
        Assert.Equal("\"\"", result);
    }

    [Fact]
    public void BuildFts5Query_SpecialFtsChars_Escaped()
    {
        var result = SearchService.BuildFts5Query("foo:bar");
        Assert.Equal("\"foo:bar\"", result);
    }
}
