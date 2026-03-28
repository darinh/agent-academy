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
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();

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
