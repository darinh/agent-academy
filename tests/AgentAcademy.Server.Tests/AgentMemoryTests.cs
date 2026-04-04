using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Tests;

public class AgentMemoryTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly SqliteConnection _connection;
    private readonly CommandContext _context;

    public AgentMemoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt =>
            opt.UseSqlite(_connection));

        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

        // Create FTS5 virtual table and triggers (mirrors the migration)
        db.Database.ExecuteSqlRaw("""
            CREATE VIRTUAL TABLE IF NOT EXISTS agent_memories_fts
            USING fts5(key, value, content='agent_memories', content_rowid='rowid');
        """);
        db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS agent_memories_ai AFTER INSERT ON agent_memories BEGIN
                INSERT INTO agent_memories_fts(rowid, key, value)
                VALUES (new.rowid, new.Key, new.Value);
            END;
        """);
        db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS agent_memories_ad AFTER DELETE ON agent_memories BEGIN
                INSERT INTO agent_memories_fts(agent_memories_fts, rowid, key, value)
                VALUES ('delete', old.rowid, old.Key, old.Value);
            END;
        """);
        db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS agent_memories_au AFTER UPDATE ON agent_memories BEGIN
                INSERT INTO agent_memories_fts(agent_memories_fts, rowid, key, value)
                VALUES ('delete', old.rowid, old.Key, old.Value);
                INSERT INTO agent_memories_fts(rowid, key, value)
                VALUES (new.rowid, new.Key, new.Value);
            END;
        """);

        _context = new CommandContext(
            AgentId: "engineer-1",
            AgentName: "Hephaestus",
            AgentRole: "SoftwareEngineer",
            RoomId: "room-1",
            BreakoutRoomId: null,
            Services: _serviceProvider);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private static CommandEnvelope MakeCommand(string command, Dictionary<string, string> args) =>
        new(command, args.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            CommandStatus.Success, null, null, $"cmd-{Guid.NewGuid():N}", DateTime.UtcNow, "engineer-1");

    // ── REMEMBER ───────────────────────────────────────────────

    [Fact]
    public async Task Remember_CreatesNewMemory()
    {
        var handler = new RememberHandler();
        var cmd = MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["category"] = "pattern",
            ["key"] = "di-convention",
            ["value"] = "Use constructor injection everywhere"
        });

        var result = await handler.ExecuteAsync(cmd, _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("created", result.Result?["action"]?.ToString());

        // Verify in DB
        var db = _serviceProvider.GetRequiredService<AgentAcademyDbContext>();
        var entity = await db.AgentMemories.FindAsync("engineer-1", "di-convention");
        Assert.NotNull(entity);
        Assert.Equal("pattern", entity.Category);
        Assert.Equal("Use constructor injection everywhere", entity.Value);
    }

    [Fact]
    public async Task Remember_UpdatesExistingMemory()
    {
        var handler = new RememberHandler();

        // Create
        var cmd1 = MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["category"] = "lesson", ["key"] = "test-key", ["value"] = "original"
        });
        await handler.ExecuteAsync(cmd1, _context);

        // Update
        var cmd2 = MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["category"] = "lesson", ["key"] = "test-key", ["value"] = "updated"
        });
        var result = await handler.ExecuteAsync(cmd2, _context);

        Assert.Equal("updated", result.Result?["action"]?.ToString());

        var db = _serviceProvider.GetRequiredService<AgentAcademyDbContext>();
        var entity = await db.AgentMemories.FindAsync("engineer-1", "test-key");
        Assert.Equal("updated", entity?.Value);
    }

    [Fact]
    public async Task Remember_InvalidCategory_ReturnsError()
    {
        var handler = new RememberHandler();
        var cmd = MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["category"] = "not-a-real-category",
            ["key"] = "test",
            ["value"] = "test"
        });

        var result = await handler.ExecuteAsync(cmd, _context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Invalid category", result.Error);
    }

    [Fact]
    public async Task Remember_MissingKey_ReturnsError()
    {
        var handler = new RememberHandler();
        var cmd = MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["category"] = "lesson", ["value"] = "test"
        });

        var result = await handler.ExecuteAsync(cmd, _context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("key", result.Error);
    }

    // ── RECALL ─────────────────────────────────────────────────

    [Fact]
    public async Task Recall_ByCategory_FiltersCorrectly()
    {
        var db = _serviceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "engineer-1", Key = "k1", Category = "gotcha", Value = "v1", CreatedAt = DateTime.UtcNow },
            new AgentMemoryEntity { AgentId = "engineer-1", Key = "k2", Category = "pattern", Value = "v2", CreatedAt = DateTime.UtcNow },
            new AgentMemoryEntity { AgentId = "engineer-1", Key = "k3", Category = "gotcha", Value = "v3", CreatedAt = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        var handler = new RecallHandler();
        var cmd = MakeCommand("RECALL", new Dictionary<string, string> { ["category"] = "gotcha" });
        var result = await handler.ExecuteAsync(cmd, _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(2, Convert.ToInt32(result.Result?["count"]));
    }

    [Fact]
    public async Task Recall_ByQuery_SearchesKeyAndValue()
    {
        var db = _serviceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "engineer-1", Key = "ef-core-include", Category = "pattern", Value = "Use Include() for nav props", CreatedAt = DateTime.UtcNow },
            new AgentMemoryEntity { AgentId = "engineer-1", Key = "sql-index", Category = "pattern", Value = "Always index foreign keys", CreatedAt = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        var handler = new RecallHandler();
        var cmd = MakeCommand("RECALL", new Dictionary<string, string> { ["query"] = "Include" });
        var result = await handler.ExecuteAsync(cmd, _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(1, Convert.ToInt32(result.Result?["count"]));
    }

    // ── Isolation ──────────────────────────────────────────────

    [Fact]
    public async Task Recall_AgentIsolation_OnlySeeOwnMemories()
    {
        var db = _serviceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "engineer-1", Key = "my-mem", Category = "lesson", Value = "mine", CreatedAt = DateTime.UtcNow },
            new AgentMemoryEntity { AgentId = "other-agent", Key = "their-mem", Category = "lesson", Value = "theirs", CreatedAt = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        var handler = new RecallHandler();
        var cmd = MakeCommand("RECALL", new Dictionary<string, string>());
        var result = await handler.ExecuteAsync(cmd, _context);

        Assert.Equal(1, Convert.ToInt32(result.Result?["count"]));
    }

    // ── LIST_MEMORIES ──────────────────────────────────────────

    [Fact]
    public async Task ListMemories_ReturnsAll()
    {
        var db = _serviceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "engineer-1", Key = "a", Category = "lesson", Value = "v1", CreatedAt = DateTime.UtcNow },
            new AgentMemoryEntity { AgentId = "engineer-1", Key = "b", Category = "pattern", Value = "v2", CreatedAt = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        var handler = new ListMemoriesHandler();
        var cmd = MakeCommand("LIST_MEMORIES", new Dictionary<string, string>());
        var result = await handler.ExecuteAsync(cmd, _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(2, Convert.ToInt32(result.Result?["count"]));
    }

    // ── FTS5 Search ──────────────────────────────────────────

    [Fact]
    public async Task Recall_Fts5_WordBoundaryMatching()
    {
        var db = _serviceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "engineer-1", Key = "ef-core-include", Category = "pattern", Value = "Use Include() for navigation properties", CreatedAt = DateTime.UtcNow },
            new AgentMemoryEntity { AgentId = "engineer-1", Key = "scoring-rules", Category = "lesson", Value = "Score calculation uses weighted average", CreatedAt = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        // "Include" should match "ef-core-include" but NOT "scoring-rules" (which contains "core" as a substring via LIKE but not as a word)
        var handler = new RecallHandler();
        var cmd = MakeCommand("RECALL", new Dictionary<string, string> { ["query"] = "Include" });
        var result = await handler.ExecuteAsync(cmd, _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var memories = result.Result?["memories"] as IEnumerable<object>;
        Assert.NotNull(memories);
        // FTS5 matches "Include" in the value of ef-core-include
        Assert.Contains(memories.Cast<Dictionary<string, object?>>(),
            m => m["key"]?.ToString() == "ef-core-include");
    }

    [Fact]
    public async Task Recall_Fts5_MultiWordQuery_MatchesBothTerms()
    {
        var db = _serviceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "engineer-1", Key = "build-cmd", Category = "lesson", Value = "Use dotnet build for the project", CreatedAt = DateTime.UtcNow },
            new AgentMemoryEntity { AgentId = "engineer-1", Key = "test-cmd", Category = "lesson", Value = "Use dotnet test for the project", CreatedAt = DateTime.UtcNow },
            new AgentMemoryEntity { AgentId = "engineer-1", Key = "deploy-cmd", Category = "lesson", Value = "Use kubectl apply for deployment", CreatedAt = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        // "dotnet build" should match only the entry containing both words
        var handler = new RecallHandler();
        var cmd = MakeCommand("RECALL", new Dictionary<string, string> { ["query"] = "dotnet build" });
        var result = await handler.ExecuteAsync(cmd, _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(1, Convert.ToInt32(result.Result?["count"]));
        var memories = (result.Result?["memories"] as IEnumerable<object>)?.Cast<Dictionary<string, object?>>().ToList();
        Assert.Equal("build-cmd", memories?[0]["key"]?.ToString());
    }

    [Fact]
    public async Task Recall_Fts5_WithCategoryFilter()
    {
        var db = _serviceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "engineer-1", Key = "ef-pattern", Category = "pattern", Value = "EF Core lazy loading disabled", CreatedAt = DateTime.UtcNow },
            new AgentMemoryEntity { AgentId = "engineer-1", Key = "ef-gotcha", Category = "gotcha", Value = "EF Core requires explicit Include", CreatedAt = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        var handler = new RecallHandler();
        var cmd = MakeCommand("RECALL", new Dictionary<string, string> { ["query"] = "EF Core", ["category"] = "gotcha" });
        var result = await handler.ExecuteAsync(cmd, _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(1, Convert.ToInt32(result.Result?["count"]));
        var memories = (result.Result?["memories"] as IEnumerable<object>)?.Cast<Dictionary<string, object?>>().ToList();
        Assert.Equal("ef-gotcha", memories?[0]["key"]?.ToString());
    }

    [Fact]
    public async Task Recall_Fts5_SpecialCharsInQuery_DoesNotCrash()
    {
        var db = _serviceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "engineer-1", Key = "special", Category = "lesson",
            Value = "Handle edge cases with (parentheses) and \"quotes\"", CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var handler = new RecallHandler();

        // These should not throw — FTS5 special chars are escaped
        foreach (var q in new[] { "edge (cases)", "\"quotes\"", "foo:bar", "a*b", "c^d" })
        {
            var cmd = MakeCommand("RECALL", new Dictionary<string, string> { ["query"] = q });
            var result = await handler.ExecuteAsync(cmd, _context);
            Assert.Equal(CommandStatus.Success, result.Status);
        }
    }

    [Fact]
    public async Task Recall_Fts5_AgentIsolation()
    {
        var db = _serviceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "engineer-1", Key = "my-secret", Category = "lesson", Value = "Secret knowledge", CreatedAt = DateTime.UtcNow },
            new AgentMemoryEntity { AgentId = "other-agent", Key = "their-secret", Category = "lesson", Value = "Secret knowledge", CreatedAt = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        var handler = new RecallHandler();
        var cmd = MakeCommand("RECALL", new Dictionary<string, string> { ["query"] = "Secret" });
        var result = await handler.ExecuteAsync(cmd, _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(1, Convert.ToInt32(result.Result?["count"]));
        var memories = (result.Result?["memories"] as IEnumerable<object>)?.Cast<Dictionary<string, object?>>().ToList();
        Assert.Equal("my-secret", memories?[0]["key"]?.ToString());
    }

    [Fact]
    public async Task Recall_Fts5_EmptyQuery_ReturnsAll()
    {
        var db = _serviceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "engineer-1", Key = "a", Category = "lesson", Value = "v1", CreatedAt = DateTime.UtcNow },
            new AgentMemoryEntity { AgentId = "engineer-1", Key = "b", Category = "lesson", Value = "v2", CreatedAt = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        var handler = new RecallHandler();
        // Empty/whitespace query should not use FTS5, falls through to regular query
        var cmd = MakeCommand("RECALL", new Dictionary<string, string> { ["query"] = "  " });
        var result = await handler.ExecuteAsync(cmd, _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(2, Convert.ToInt32(result.Result?["count"]));
    }

    [Fact]
    public void BuildFts5Query_EscapesSpecialCharacters()
    {
        Assert.Equal("\"hello\" \"world\"", RecallHandler.BuildFts5Query("hello world"));
        Assert.Equal("\"foo:bar\"", RecallHandler.BuildFts5Query("foo:bar"));
        Assert.Equal("\"say\"\"hi\"\"\"", RecallHandler.BuildFts5Query("say\"hi\""));
        Assert.Equal("\"a*b\"", RecallHandler.BuildFts5Query("a*b"));
        Assert.Equal("\"\"\"\"", RecallHandler.BuildFts5Query("\""));
    }

    [Fact]
    public async Task Recall_Fts5_SyncedAfterRemember()
    {
        // Verify that REMEMBER creates/updates flow through triggers to FTS5
        var rememberHandler = new RememberHandler();
        var recallHandler = new RecallHandler();

        // Create a memory via REMEMBER
        var createCmd = MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["category"] = "pattern",
            ["key"] = "fts5-sync-test",
            ["value"] = "FTS5 triggers keep the index synchronized"
        });
        await rememberHandler.ExecuteAsync(createCmd, _context);

        // Search for it via FTS5
        var recallCmd = MakeCommand("RECALL", new Dictionary<string, string> { ["query"] = "synchronized" });
        var result = await recallHandler.ExecuteAsync(recallCmd, _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(1, Convert.ToInt32(result.Result?["count"]));

        // Update via REMEMBER
        var updateCmd = MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["category"] = "pattern",
            ["key"] = "fts5-sync-test",
            ["value"] = "FTS5 triggers handle updates correctly"
        });
        await rememberHandler.ExecuteAsync(updateCmd, _context);

        // Old term should no longer match
        var recallOld = MakeCommand("RECALL", new Dictionary<string, string> { ["query"] = "synchronized" });
        var oldResult = await recallHandler.ExecuteAsync(recallOld, _context);
        Assert.Equal(0, Convert.ToInt32(oldResult.Result?["count"]));

        // New term should match
        var recallNew = MakeCommand("RECALL", new Dictionary<string, string> { ["query"] = "updates" });
        var newResult = await recallHandler.ExecuteAsync(recallNew, _context);
        Assert.Equal(1, Convert.ToInt32(newResult.Result?["count"]));
    }

    [Fact]
    public async Task Recall_Fts5_SyncedAfterForget()
    {
        var rememberHandler = new RememberHandler();
        var forgetHandler = new ForgetHandler();
        var recallHandler = new RecallHandler();

        // Create a memory
        var createCmd = MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["category"] = "lesson",
            ["key"] = "fts5-delete-test",
            ["value"] = "This memory will be forgotten"
        });
        await rememberHandler.ExecuteAsync(createCmd, _context);

        // Verify it's searchable
        var recallCmd = MakeCommand("RECALL", new Dictionary<string, string> { ["query"] = "forgotten" });
        var result = await recallHandler.ExecuteAsync(recallCmd, _context);
        Assert.Equal(1, Convert.ToInt32(result.Result?["count"]));

        // Delete it
        var forgetCmd = MakeCommand("FORGET", new Dictionary<string, string> { ["key"] = "fts5-delete-test" });
        await forgetHandler.ExecuteAsync(forgetCmd, _context);

        // Should no longer be searchable
        var result2 = await recallHandler.ExecuteAsync(recallCmd, _context);
        Assert.Equal(0, Convert.ToInt32(result2.Result?["count"]));
    }

    // ── FORGET ─────────────────────────────────────────────────

    [Fact]
    public async Task Forget_DeletesMemory()
    {
        var db = _serviceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "engineer-1", Key = "to-delete", Category = "lesson",
            Value = "old info", CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var handler = new ForgetHandler();
        var cmd = MakeCommand("FORGET", new Dictionary<string, string> { ["key"] = "to-delete" });
        var result = await handler.ExecuteAsync(cmd, _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("deleted", result.Result?["action"]?.ToString());

        var entity = await db.AgentMemories.FindAsync("engineer-1", "to-delete");
        Assert.Null(entity);
    }

    [Fact]
    public async Task Forget_NonexistentKey_ReturnsError()
    {
        var handler = new ForgetHandler();
        var cmd = MakeCommand("FORGET", new Dictionary<string, string> { ["key"] = "no-such-key" });
        var result = await handler.ExecuteAsync(cmd, _context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("No memory found", result.Error);
    }
}
