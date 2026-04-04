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

    // ── SHARED MEMORY ─────────────────────────────────────────

    [Fact]
    public async Task Remember_SharedCategory_Accepted()
    {
        var handler = new RememberHandler();
        var cmd = MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["key"] = "build-command",
            ["category"] = "shared",
            ["value"] = "dotnet build AgentAcademy.sln"
        });
        var result = await handler.ExecuteAsync(cmd, _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var resultDict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("shared", resultDict["category"]);
    }

    [Fact]
    public async Task Remember_SharedCategory_CaseNormalized()
    {
        var handler = new RememberHandler();
        var cmd = MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["key"] = "case-test",
            ["category"] = "Shared",
            ["value"] = "Should be normalized to lowercase"
        });
        var result = await handler.ExecuteAsync(cmd, _context);

        Assert.Equal(CommandStatus.Success, result.Status);

        // Verify stored as lowercase
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var entity = await db.AgentMemories.FindAsync("engineer-1", "case-test");
        Assert.NotNull(entity);
        Assert.Equal("shared", entity!.Category);
    }

    [Fact]
    public async Task Recall_SharedMemory_VisibleToOtherAgents()
    {
        // Agent 1 stores a shared memory
        var handler = new RememberHandler();
        var cmd = MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["key"] = "project-convention",
            ["category"] = "shared",
            ["value"] = "Use conventional commits"
        });
        await handler.ExecuteAsync(cmd, _context);

        // Agent 2 recalls — should see agent 1's shared memory
        var agent2Context = new CommandContext(
            AgentId: "reviewer-1",
            AgentName: "Athena",
            AgentRole: "Reviewer",
            RoomId: "room-1",
            BreakoutRoomId: null,
            Services: _serviceProvider);

        var recallHandler = new RecallHandler();
        var recallCmd = MakeCommand("RECALL", new Dictionary<string, string>());
        var result = await recallHandler.ExecuteAsync(recallCmd, agent2Context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var resultDict = (Dictionary<string, object?>)result.Result!;
        var memories = (List<Dictionary<string, object?>>)resultDict["memories"]!;
        Assert.Contains(memories, m => (string)m["key"]! == "project-convention");
    }

    [Fact]
    public async Task Recall_SharedMemory_IncludesSourceAgentId()
    {
        // Agent 1 stores a shared memory
        var handler = new RememberHandler();
        await handler.ExecuteAsync(MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["key"] = "test-command",
            ["category"] = "shared",
            ["value"] = "dotnet test"
        }), _context);

        // Agent 2 recalls — agentId should be populated for shared memories from others
        var agent2Context = new CommandContext(
            AgentId: "reviewer-1",
            AgentName: "Athena",
            AgentRole: "Reviewer",
            RoomId: "room-1",
            BreakoutRoomId: null,
            Services: _serviceProvider);

        var recallHandler = new RecallHandler();
        var result = await recallHandler.ExecuteAsync(
            MakeCommand("RECALL", new Dictionary<string, string>()), agent2Context);

        var resultDict = (Dictionary<string, object?>)result.Result!;
        var memories = (List<Dictionary<string, object?>>)resultDict["memories"]!;
        var sharedMem = memories.First(m => (string)m["key"]! == "test-command");
        Assert.Equal("engineer-1", sharedMem["agentId"]);
    }

    [Fact]
    public async Task Recall_OwnSharedMemory_AgentIdIsNull()
    {
        // Agent stores a shared memory
        var handler = new RememberHandler();
        await handler.ExecuteAsync(MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["key"] = "my-shared-tip",
            ["category"] = "shared",
            ["value"] = "Use async/await throughout"
        }), _context);

        // Same agent recalls — agentId should be null (it's their own)
        var recallHandler = new RecallHandler();
        var result = await recallHandler.ExecuteAsync(
            MakeCommand("RECALL", new Dictionary<string, string>()), _context);

        var resultDict = (Dictionary<string, object?>)result.Result!;
        var memories = (List<Dictionary<string, object?>>)resultDict["memories"]!;
        var sharedMem = memories.First(m => (string)m["key"]! == "my-shared-tip");
        Assert.Null(sharedMem["agentId"]);
    }

    [Fact]
    public async Task Recall_NonSharedMemory_StillIsolated()
    {
        // Agent 1 stores a private memory
        var handler = new RememberHandler();
        await handler.ExecuteAsync(MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["key"] = "private-note",
            ["category"] = "lesson",
            ["value"] = "This is private"
        }), _context);

        // Agent 2 recalls — should NOT see agent 1's private memory
        var agent2Context = new CommandContext(
            AgentId: "reviewer-1",
            AgentName: "Athena",
            AgentRole: "Reviewer",
            RoomId: "room-1",
            BreakoutRoomId: null,
            Services: _serviceProvider);

        var recallHandler = new RecallHandler();
        var result = await recallHandler.ExecuteAsync(
            MakeCommand("RECALL", new Dictionary<string, string>()), agent2Context);

        var resultDict = (Dictionary<string, object?>)result.Result!;
        var memories = (List<Dictionary<string, object?>>)resultDict["memories"]!;
        Assert.DoesNotContain(memories, m => (string)m["key"]! == "private-note");
    }

    [Fact]
    public async Task Recall_Fts5_SharedMemoryVisibleAcrossAgents()
    {
        // Agent 1 stores a shared memory
        var handler = new RememberHandler();
        await handler.ExecuteAsync(MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["key"] = "sqlite-choice",
            ["category"] = "shared",
            ["value"] = "We chose SQLite over Postgres for single-user simplicity"
        }), _context);

        // Agent 2 searches via FTS5 — should find shared memory
        var agent2Context = new CommandContext(
            AgentId: "planner-1",
            AgentName: "Apollo",
            AgentRole: "Planner",
            RoomId: "room-1",
            BreakoutRoomId: null,
            Services: _serviceProvider);

        var recallHandler = new RecallHandler();
        var result = await recallHandler.ExecuteAsync(
            MakeCommand("RECALL", new Dictionary<string, string> { ["query"] = "SQLite" }), agent2Context);

        var resultDict = (Dictionary<string, object?>)result.Result!;
        var memories = (List<Dictionary<string, object?>>)resultDict["memories"]!;
        Assert.Contains(memories, m => (string)m["key"]! == "sqlite-choice");
    }

    [Fact]
    public async Task Recall_Fts5_PrivateMemoryStillIsolated()
    {
        // Agent 1 stores a private memory
        var handler = new RememberHandler();
        await handler.ExecuteAsync(MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["key"] = "secret-pattern",
            ["category"] = "pattern",
            ["value"] = "Use repository pattern for data access"
        }), _context);

        // Agent 2 searches via FTS5 — should NOT find agent 1's private memory
        var agent2Context = new CommandContext(
            AgentId: "planner-1",
            AgentName: "Apollo",
            AgentRole: "Planner",
            RoomId: "room-1",
            BreakoutRoomId: null,
            Services: _serviceProvider);

        var recallHandler = new RecallHandler();
        var result = await recallHandler.ExecuteAsync(
            MakeCommand("RECALL", new Dictionary<string, string> { ["query"] = "repository" }), agent2Context);

        var resultDict = (Dictionary<string, object?>)result.Result!;
        var memories = (List<Dictionary<string, object?>>)resultDict["memories"]!;
        Assert.DoesNotContain(memories, m => (string)m["key"]! == "secret-pattern");
    }

    [Fact]
    public async Task ListMemories_IncludesSharedFromOtherAgents()
    {
        // Agent 1 stores shared + private
        var handler = new RememberHandler();
        await handler.ExecuteAsync(MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["key"] = "shared-tip",
            ["category"] = "shared",
            ["value"] = "Use EF Core Include()"
        }), _context);
        await handler.ExecuteAsync(MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["key"] = "private-tip",
            ["category"] = "pattern",
            ["value"] = "This is private"
        }), _context);

        // Agent 2 lists memories — should see only shared
        var agent2Context = new CommandContext(
            AgentId: "reviewer-1",
            AgentName: "Athena",
            AgentRole: "Reviewer",
            RoomId: "room-1",
            BreakoutRoomId: null,
            Services: _serviceProvider);

        var listHandler = new ListMemoriesHandler();
        var result = await listHandler.ExecuteAsync(
            MakeCommand("LIST_MEMORIES", new Dictionary<string, string>()), agent2Context);

        var resultDict = (Dictionary<string, object?>)result.Result!;
        var memories = (List<Dictionary<string, object?>>)resultDict["memories"]!;
        Assert.Contains(memories, m => (string)m["key"]! == "shared-tip");
        Assert.DoesNotContain(memories, m => (string)m["key"]! == "private-tip");
    }

    [Fact]
    public async Task ListMemories_CategoryFilter_SharedOnly()
    {
        // Agent 1 stores a shared memory
        var handler = new RememberHandler();
        await handler.ExecuteAsync(MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["key"] = "universal-rule",
            ["category"] = "shared",
            ["value"] = "Always use conventional commits"
        }), _context);

        // Agent 2 lists only shared category
        var agent2Context = new CommandContext(
            AgentId: "reviewer-1",
            AgentName: "Athena",
            AgentRole: "Reviewer",
            RoomId: "room-1",
            BreakoutRoomId: null,
            Services: _serviceProvider);

        var listHandler = new ListMemoriesHandler();
        var result = await listHandler.ExecuteAsync(
            MakeCommand("LIST_MEMORIES", new Dictionary<string, string> { ["category"] = "shared" }),
            agent2Context);

        var resultDict = (Dictionary<string, object?>)result.Result!;
        var memories = (List<Dictionary<string, object?>>)resultDict["memories"]!;
        Assert.Contains(memories, m => (string)m["key"]! == "universal-rule");
    }

    [Fact]
    public async Task Forget_SharedMemory_OnlyOwnerCanForget()
    {
        // Agent 1 stores a shared memory
        var handler = new RememberHandler();
        await handler.ExecuteAsync(MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["key"] = "to-forget",
            ["category"] = "shared",
            ["value"] = "temporary"
        }), _context);

        // Agent 2 tries to forget it — should fail (PK is AgentId+Key, scoped to own)
        var agent2Context = new CommandContext(
            AgentId: "reviewer-1",
            AgentName: "Athena",
            AgentRole: "Reviewer",
            RoomId: "room-1",
            BreakoutRoomId: null,
            Services: _serviceProvider);

        var forgetHandler = new ForgetHandler();
        var result = await forgetHandler.ExecuteAsync(
            MakeCommand("FORGET", new Dictionary<string, string> { ["key"] = "to-forget" }),
            agent2Context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("No memory found", result.Error);

        // Original agent can still forget it
        var ownerResult = await forgetHandler.ExecuteAsync(
            MakeCommand("FORGET", new Dictionary<string, string> { ["key"] = "to-forget" }),
            _context);
        Assert.Equal(CommandStatus.Success, ownerResult.Status);
    }

    [Fact]
    public async Task Recall_SharedMemory_NoDuplicatesForOwner()
    {
        // Agent stores a shared memory
        var handler = new RememberHandler();
        await handler.ExecuteAsync(MakeCommand("REMEMBER", new Dictionary<string, string>
        {
            ["key"] = "no-dup-test",
            ["category"] = "shared",
            ["value"] = "Should appear once"
        }), _context);

        // Same agent recalls — should see it exactly once (not duplicated)
        var recallHandler = new RecallHandler();
        var result = await recallHandler.ExecuteAsync(
            MakeCommand("RECALL", new Dictionary<string, string>()), _context);

        var resultDict = (Dictionary<string, object?>)result.Result!;
        var memories = (List<Dictionary<string, object?>>)resultDict["memories"]!;
        var matches = memories.Where(m => (string)m["key"]! == "no-dup-test").ToList();
        Assert.Single(matches);
    }

    // ── MEMORY DECAY / TTL ────────────────────────────────────

    [Fact]
    public async Task Remember_WithTtl_SetsExpiresAt()
    {
        var handler = new RememberHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("REMEMBER", new Dictionary<string, string>
            {
                ["category"] = "lesson",
                ["key"] = "ttl-test",
                ["value"] = "This memory expires",
                ["ttl"] = "24"
            }), _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var resultDict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("created", resultDict["action"]);
        Assert.NotNull(resultDict["expiresAt"]);

        // Verify in DB
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var entity = await db.AgentMemories.FindAsync("engineer-1", "ttl-test");
        Assert.NotNull(entity!.ExpiresAt);
        Assert.True(entity.ExpiresAt > DateTime.UtcNow);
        Assert.True(entity.ExpiresAt < DateTime.UtcNow.AddHours(25));
    }

    [Fact]
    public async Task Remember_WithoutTtl_NoExpiresAt()
    {
        var handler = new RememberHandler();
        await handler.ExecuteAsync(
            MakeCommand("REMEMBER", new Dictionary<string, string>
            {
                ["category"] = "lesson",
                ["key"] = "no-ttl-test",
                ["value"] = "This memory never expires"
            }), _context);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var entity = await db.AgentMemories.FindAsync("engineer-1", "no-ttl-test");
        Assert.Null(entity!.ExpiresAt);
    }

    [Fact]
    public async Task Remember_InvalidTtl_ReturnsError()
    {
        var handler = new RememberHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("REMEMBER", new Dictionary<string, string>
            {
                ["category"] = "lesson",
                ["key"] = "bad-ttl",
                ["value"] = "test",
                ["ttl"] = "-5"
            }), _context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("positive integer", result.Error);
    }

    [Fact]
    public async Task Remember_ZeroTtl_ReturnsError()
    {
        var handler = new RememberHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("REMEMBER", new Dictionary<string, string>
            {
                ["category"] = "lesson",
                ["key"] = "zero-ttl",
                ["value"] = "test",
                ["ttl"] = "0"
            }), _context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("positive integer", result.Error);
    }

    [Fact]
    public async Task Remember_UpdateWithTtl_OverwritesExpiresAt()
    {
        var handler = new RememberHandler();
        // Create without TTL
        await handler.ExecuteAsync(
            MakeCommand("REMEMBER", new Dictionary<string, string>
            {
                ["category"] = "lesson",
                ["key"] = "ttl-overwrite",
                ["value"] = "original"
            }), _context);

        // Update with TTL
        var result = await handler.ExecuteAsync(
            MakeCommand("REMEMBER", new Dictionary<string, string>
            {
                ["category"] = "lesson",
                ["key"] = "ttl-overwrite",
                ["value"] = "updated",
                ["ttl"] = "48"
            }), _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var resultDict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("updated", resultDict["action"]);
        Assert.NotNull(resultDict["expiresAt"]);
    }

    [Fact]
    public async Task Remember_UpdateWithoutTtl_PreservesExistingExpiry()
    {
        var handler = new RememberHandler();
        // Create with TTL
        await handler.ExecuteAsync(
            MakeCommand("REMEMBER", new Dictionary<string, string>
            {
                ["category"] = "lesson",
                ["key"] = "ttl-preserve",
                ["value"] = "original",
                ["ttl"] = "24"
            }), _context);

        // Get original expiry
        DateTime? originalExpiry;
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var entity = await db.AgentMemories.FindAsync("engineer-1", "ttl-preserve");
            originalExpiry = entity!.ExpiresAt;
            Assert.NotNull(originalExpiry);
        }

        // Update without TTL — should preserve existing expiry
        await handler.ExecuteAsync(
            MakeCommand("REMEMBER", new Dictionary<string, string>
            {
                ["category"] = "lesson",
                ["key"] = "ttl-preserve",
                ["value"] = "updated value"
            }), _context);

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var entity = await db.AgentMemories.FindAsync("engineer-1", "ttl-preserve");
            Assert.Equal(originalExpiry, entity!.ExpiresAt);
        }
    }

    [Fact]
    public async Task Recall_ExcludesExpiredMemories()
    {
        // Seed an expired memory directly in DB
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "engineer-1",
            Key = "expired-mem",
            Category = "lesson",
            Value = "This is expired",
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            ExpiresAt = DateTime.UtcNow.AddHours(-1) // expired 1 hour ago
        });
        db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "engineer-1",
            Key = "valid-mem",
            Category = "lesson",
            Value = "This is valid",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var handler = new RecallHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("RECALL", new Dictionary<string, string> { ["category"] = "lesson" }), _context);

        var resultDict = (Dictionary<string, object?>)result.Result!;
        var memories = (List<Dictionary<string, object?>>)resultDict["memories"]!;
        Assert.DoesNotContain(memories, m => (string)m["key"]! == "expired-mem");
        Assert.Contains(memories, m => (string)m["key"]! == "valid-mem");
    }

    [Fact]
    public async Task Recall_IncludeExpired_ShowsExpiredMemories()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "engineer-1",
            Key = "expired-visible",
            Category = "pattern",
            Value = "Expired but requested",
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            ExpiresAt = DateTime.UtcNow.AddHours(-1)
        });
        await db.SaveChangesAsync();

        var handler = new RecallHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("RECALL", new Dictionary<string, string>
            {
                ["category"] = "pattern",
                ["include_expired"] = "true"
            }), _context);

        var resultDict = (Dictionary<string, object?>)result.Result!;
        var memories = (List<Dictionary<string, object?>>)resultDict["memories"]!;
        Assert.Contains(memories, m => (string)m["key"]! == "expired-visible");
    }

    [Fact]
    public async Task Recall_UpdatesLastAccessedAt()
    {
        var rememberHandler = new RememberHandler();
        await rememberHandler.ExecuteAsync(
            MakeCommand("REMEMBER", new Dictionary<string, string>
            {
                ["category"] = "lesson",
                ["key"] = "access-track",
                ["value"] = "test access tracking"
            }), _context);

        // Verify LastAccessedAt is initially null
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var entity = await db.AgentMemories.FindAsync("engineer-1", "access-track");
            Assert.Null(entity!.LastAccessedAt);
        }

        var recallHandler = new RecallHandler();
        await recallHandler.ExecuteAsync(
            MakeCommand("RECALL", new Dictionary<string, string> { ["key"] = "access-track" }), _context);

        // Verify LastAccessedAt is now set
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var entity = await db.AgentMemories.FindAsync("engineer-1", "access-track");
            Assert.NotNull(entity!.LastAccessedAt);
            Assert.True(entity.LastAccessedAt >= DateTime.UtcNow.AddSeconds(-5));
        }
    }

    [Fact]
    public async Task ListMemories_ExcludesExpiredMemories()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "engineer-1",
            Key = "list-expired",
            Category = "risk",
            Value = "Expired risk",
            CreatedAt = DateTime.UtcNow.AddDays(-5),
            ExpiresAt = DateTime.UtcNow.AddHours(-2)
        });
        db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "engineer-1",
            Key = "list-valid",
            Category = "risk",
            Value = "Valid risk",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var handler = new ListMemoriesHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("LIST_MEMORIES", new Dictionary<string, string> { ["category"] = "risk" }), _context);

        var resultDict = (Dictionary<string, object?>)result.Result!;
        var memories = (List<Dictionary<string, object?>>)resultDict["memories"]!;
        Assert.DoesNotContain(memories, m => (string)m["key"]! == "list-expired");
        Assert.Contains(memories, m => (string)m["key"]! == "list-valid");
    }

    [Fact]
    public void IsStale_NoAccess_OlderThan30Days_ReturnsTrue()
    {
        var entity = new AgentMemoryEntity
        {
            AgentId = "test",
            Key = "old-mem",
            Category = "lesson",
            Value = "old stuff",
            CreatedAt = DateTime.UtcNow.AddDays(-45),
            LastAccessedAt = null,
            ExpiresAt = null
        };
        Assert.True(RecallHandler.IsStale(entity, DateTime.UtcNow));
    }

    [Fact]
    public void IsStale_RecentAccess_ReturnsFalse()
    {
        var entity = new AgentMemoryEntity
        {
            AgentId = "test",
            Key = "recent-mem",
            Category = "lesson",
            Value = "still relevant",
            CreatedAt = DateTime.UtcNow.AddDays(-45),
            LastAccessedAt = DateTime.UtcNow.AddDays(-5),
            ExpiresAt = null
        };
        Assert.False(RecallHandler.IsStale(entity, DateTime.UtcNow));
    }

    [Fact]
    public void IsStale_HasTtl_NeverMarkedStale()
    {
        var entity = new AgentMemoryEntity
        {
            AgentId = "test",
            Key = "ttl-mem",
            Category = "lesson",
            Value = "has TTL",
            CreatedAt = DateTime.UtcNow.AddDays(-60),
            LastAccessedAt = null,
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        };
        Assert.False(RecallHandler.IsStale(entity, DateTime.UtcNow));
    }

    [Fact]
    public void IsStale_UpdatedRecently_ReturnsFalse()
    {
        var entity = new AgentMemoryEntity
        {
            AgentId = "test",
            Key = "updated-mem",
            Category = "lesson",
            Value = "updated recently",
            CreatedAt = DateTime.UtcNow.AddDays(-60),
            UpdatedAt = DateTime.UtcNow.AddDays(-5),
            LastAccessedAt = null,
            ExpiresAt = null
        };
        Assert.False(RecallHandler.IsStale(entity, DateTime.UtcNow));
    }

    [Fact]
    public async Task Recall_ShowsStaleFlag()
    {
        // Seed a stale memory directly
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "engineer-1",
            Key = "stale-flag-test",
            Category = "gotcha",
            Value = "Very old gotcha",
            CreatedAt = DateTime.UtcNow.AddDays(-45),
            LastAccessedAt = null,
            ExpiresAt = null
        });
        await db.SaveChangesAsync();

        var handler = new RecallHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("RECALL", new Dictionary<string, string> { ["key"] = "stale-flag-test" }), _context);

        var resultDict = (Dictionary<string, object?>)result.Result!;
        var memories = (List<Dictionary<string, object?>>)resultDict["memories"]!;
        var staleMem = memories.First(m => (string)m["key"]! == "stale-flag-test");
        // On the first recall, memory was stale before access tracking updated it.
        // The stale flag is computed at query time based on the state before update.
        // Since CreatedAt is 45 days ago and LastAccessedAt was null, it IS stale.
        Assert.True((bool?)staleMem["stale"] == true);
    }

    [Fact]
    public async Task Recall_FutureExpiry_IncludesMemory()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "engineer-1",
            Key = "future-expiry",
            Category = "constraint",
            Value = "Still valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        await db.SaveChangesAsync();

        var handler = new RecallHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("RECALL", new Dictionary<string, string> { ["key"] = "future-expiry" }), _context);

        var resultDict = (Dictionary<string, object?>)result.Result!;
        var memories = (List<Dictionary<string, object?>>)resultDict["memories"]!;
        Assert.Contains(memories, m => (string)m["key"]! == "future-expiry");
        var mem = memories.First(m => (string)m["key"]! == "future-expiry");
        Assert.NotNull(mem["expiresAt"]);
    }

    [Fact]
    public async Task Export_ShowsExpiresAtAndStale()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "engineer-1",
            Key = "export-ttl",
            Category = "finding",
            Value = "has expiry",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        });
        db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "engineer-1",
            Key = "export-stale",
            Category = "finding",
            Value = "very old",
            CreatedAt = DateTime.UtcNow.AddDays(-60)
        });
        await db.SaveChangesAsync();

        var handler = new ExportMemoriesHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("EXPORT_MEMORIES", new Dictionary<string, string> { ["category"] = "finding" }), _context);

        var resultDict = (Dictionary<string, object?>)result.Result!;
        var memories = (List<Dictionary<string, object?>>)resultDict["memories"]!;

        var ttlMem = memories.First(m => (string)m["key"]! == "export-ttl");
        Assert.NotNull(ttlMem["expiresAt"]);

        var staleMem = memories.First(m => (string)m["key"]! == "export-stale");
        Assert.True((bool?)staleMem["stale"] == true);
    }

    [Fact]
    public async Task Import_WithTtl_SetsExpiresAt()
    {
        var handler = new ImportMemoriesHandler();
        var json = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { category = "lesson", key = "import-ttl", value = "imported with ttl", ttl = 72 }
        });

        var result = await handler.ExecuteAsync(
            MakeCommand("IMPORT_MEMORIES", new Dictionary<string, string> { ["memories"] = json }), _context);

        Assert.Equal(CommandStatus.Success, result.Status);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var entity = await db.AgentMemories.FindAsync("engineer-1", "import-ttl");
        Assert.NotNull(entity!.ExpiresAt);
        Assert.True(entity.ExpiresAt > DateTime.UtcNow.AddHours(70));
    }
}
