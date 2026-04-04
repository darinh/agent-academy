using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Tests;

public class MemoryImportExportTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly SqliteConnection _connection;
    private readonly CommandContext _context;

    public MemoryImportExportTests()
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

    private async Task SeedMemories(params (string key, string category, string value)[] memories)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        foreach (var (key, category, value) in memories)
        {
            db.AgentMemories.Add(new AgentMemoryEntity
            {
                AgentId = "engineer-1",
                Key = key,
                Category = category,
                Value = value,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    // ── EXPORT_MEMORIES ───────────────────────────────────────────

    [Fact]
    public async Task Export_returns_all_agent_memories()
    {
        await SeedMemories(
            ("build-cmd", "pattern", "dotnet build"),
            ("test-cmd", "pattern", "dotnet test"),
            ("sqlite-choice", "decision", "We chose SQLite"));

        var handler = new ExportMemoriesHandler();
        var result = await handler.ExecuteAsync(MakeCommand("EXPORT_MEMORIES", new()), _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(3, (int)data["count"]!);
        Assert.Equal("engineer-1", (string)data["agentId"]!);
        var memories = Assert.IsAssignableFrom<IEnumerable<object>>(data["memories"]);
        Assert.Equal(3, memories.Count());
    }

    [Fact]
    public async Task Export_filters_by_category()
    {
        await SeedMemories(
            ("build-cmd", "pattern", "dotnet build"),
            ("sqlite-choice", "decision", "We chose SQLite"));

        var handler = new ExportMemoriesHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("EXPORT_MEMORIES", new() { ["category"] = "pattern" }), _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(1, (int)data["count"]!);
    }

    [Fact]
    public async Task Export_returns_empty_for_no_memories()
    {
        var handler = new ExportMemoriesHandler();
        var result = await handler.ExecuteAsync(MakeCommand("EXPORT_MEMORIES", new()), _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(0, (int)data["count"]!);
    }

    [Fact]
    public async Task Export_only_returns_own_memories()
    {
        await SeedMemories(("build-cmd", "pattern", "dotnet build"));

        // Add memory for different agent
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.AgentMemories.Add(new AgentMemoryEntity
            {
                AgentId = "reviewer-1",
                Key = "review-pattern",
                Category = "pattern",
                Value = "Always check edge cases",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var handler = new ExportMemoriesHandler();
        var result = await handler.ExecuteAsync(MakeCommand("EXPORT_MEMORIES", new()), _context);

        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(1, (int)data["count"]!);
    }

    // ── IMPORT_MEMORIES ───────────────────────────────────────────

    [Fact]
    public async Task Import_creates_new_memories()
    {
        var json = """[{"category":"pattern","key":"build-cmd","value":"dotnet build"},{"category":"decision","key":"db-choice","value":"SQLite"}]""";

        var handler = new ImportMemoriesHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("IMPORT_MEMORIES", new() { ["memories"] = json }), _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(2, (int)data["created"]!);
        Assert.Equal(0, (int)data["updated"]!);
        Assert.Equal(0, (int)data["skipped"]!);

        // Verify in DB
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var count = await db.AgentMemories.CountAsync(m => m.AgentId == "engineer-1");
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Import_updates_existing_memories()
    {
        await SeedMemories(("build-cmd", "pattern", "old build command"));

        var json = """[{"category":"pattern","key":"build-cmd","value":"dotnet build AgentAcademy.sln"}]""";

        var handler = new ImportMemoriesHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("IMPORT_MEMORIES", new() { ["memories"] = json }), _context);

        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(0, (int)data["created"]!);
        Assert.Equal(1, (int)data["updated"]!);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var memory = await db.AgentMemories.FindAsync("engineer-1", "build-cmd");
        Assert.Equal("dotnet build AgentAcademy.sln", memory!.Value);
    }

    [Fact]
    public async Task Import_skips_invalid_category()
    {
        var json = """[{"category":"invalid-cat","key":"test","value":"test value"}]""";

        var handler = new ImportMemoriesHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("IMPORT_MEMORIES", new() { ["memories"] = json }), _context);

        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(0, (int)data["created"]!);
        Assert.Equal(1, (int)data["skipped"]!);
    }

    [Fact]
    public async Task Import_skips_entries_exceeding_value_limit()
    {
        var longValue = new string('x', 501);
        var json = "[{\"category\":\"pattern\",\"key\":\"long\",\"value\":\"" + longValue + "\"}]";

        var handler = new ImportMemoriesHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("IMPORT_MEMORIES", new() { ["memories"] = json }), _context);

        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(0, (int)data["created"]!);
        Assert.Equal(1, (int)data["skipped"]!);
    }

    [Fact]
    public async Task Import_skips_entries_with_missing_fields()
    {
        var json = """[{"category":"pattern","key":"","value":"test value"}]""";

        var handler = new ImportMemoriesHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("IMPORT_MEMORIES", new() { ["memories"] = json }), _context);

        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(0, (int)data["created"]!);
        Assert.Equal(1, (int)data["skipped"]!);
    }

    [Fact]
    public async Task Import_rejects_invalid_json()
    {
        var handler = new ImportMemoriesHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("IMPORT_MEMORIES", new() { ["memories"] = "not json" }), _context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Invalid JSON", result.Error);
    }

    [Fact]
    public async Task Import_rejects_missing_memories_arg()
    {
        var handler = new ImportMemoriesHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("IMPORT_MEMORIES", new()), _context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Missing required argument", result.Error);
    }

    [Fact]
    public async Task Import_normalizes_category_to_lowercase()
    {
        var json = """[{"category":"PATTERN","key":"upper","value":"normalized"}]""";

        var handler = new ImportMemoriesHandler();
        await handler.ExecuteAsync(
            MakeCommand("IMPORT_MEMORIES", new() { ["memories"] = json }), _context);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var memory = await db.AgentMemories.FindAsync("engineer-1", "upper");
        Assert.Equal("pattern", memory!.Category);
    }

    // ── Round-trip: export then import ────────────────────────────

    [Fact]
    public async Task Export_then_import_round_trips()
    {
        await SeedMemories(
            ("build-cmd", "pattern", "dotnet build"),
            ("db-choice", "decision", "SQLite for simplicity"));

        // Export
        var exportHandler = new ExportMemoriesHandler();
        var exportResult = await exportHandler.ExecuteAsync(
            MakeCommand("EXPORT_MEMORIES", new()), _context);

        var exportData = Assert.IsType<Dictionary<string, object?>>(exportResult.Result);
        var memories = Assert.IsAssignableFrom<IEnumerable<object>>(exportData["memories"]);
        Assert.Equal(2, memories.Count());

        // Clear memories using raw SQL to avoid change tracker issues
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM agent_memories WHERE AgentId = 'engineer-1'");
            db.ChangeTracker.Clear();
        }

        // Also clear the main scope's change tracker so FindAsync hits DB
        {
            var db = _serviceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.ChangeTracker.Clear();
        }

        // Build import JSON from exported data
        var memList = memories.Cast<Dictionary<string, object?>>().ToList();
        var json = System.Text.Json.JsonSerializer.Serialize(memList.Select(m => new
        {
            category = (string)m["category"]!,
            key = (string)m["key"]!,
            value = (string)m["value"]!,
        }));

        // Import
        var importHandler = new ImportMemoriesHandler();
        var importResult = await importHandler.ExecuteAsync(
            MakeCommand("IMPORT_MEMORIES", new() { ["memories"] = json }), _context);

        var importData = Assert.IsType<Dictionary<string, object?>>(importResult.Result);
        Assert.Equal(2, (int)importData["created"]!);
        Assert.Equal(0, (int)importData["skipped"]!);

        // Verify
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var count = await db.AgentMemories.CountAsync(m => m.AgentId == "engineer-1");
            Assert.Equal(2, count);
        }
    }

    [Fact]
    public async Task Import_mixed_creates_updates_and_skips()
    {
        await SeedMemories(("existing", "pattern", "old value"));

        var json = "[" +
            """{"category":"pattern","key":"existing","value":"new value"},""" +
            """{"category":"lesson","key":"new-key","value":"new lesson"},""" +
            """{"category":"bogus","key":"bad","value":"bad category"},""" +
            """{"category":"pattern","key":"","value":"missing key"}""" +
            "]";

        var handler = new ImportMemoriesHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("IMPORT_MEMORIES", new() { ["memories"] = json }), _context);

        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(1, (int)data["created"]!);
        Assert.Equal(1, (int)data["updated"]!);
        Assert.Equal(2, (int)data["skipped"]!);
        Assert.Equal(4, (int)data["total"]!);
    }
}
