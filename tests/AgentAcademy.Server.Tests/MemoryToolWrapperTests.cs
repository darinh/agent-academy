using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class MemoryToolWrapperTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _sp;
    private readonly MemoryToolWrapper _wrapper;
    private readonly MemoryToolWrapper _otherAgentWrapper;
    private const string AgentId = "engineer-1";
    private const string OtherAgentId = "engineer-2";

    public MemoryToolWrapperTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt =>
            opt.UseSqlite(_connection));
        _sp = services.BuildServiceProvider();

        using var scope = _sp.CreateScope();
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

        _wrapper = new MemoryToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance, AgentId);

        _otherAgentWrapper = new MemoryToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance, OtherAgentId);
    }

    public void Dispose()
    {
        _sp.Dispose();
        _connection.Dispose();
    }

    // ── RememberAsync: Input validation ─────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RememberAsync_EmptyKey_ReturnsError(string? key)
    {
        var result = await _wrapper.RememberAsync(key!, "value", "pattern");
        Assert.StartsWith("Error: key is required", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RememberAsync_EmptyValue_ReturnsError(string? value)
    {
        var result = await _wrapper.RememberAsync("key", value!, "pattern");
        Assert.StartsWith("Error: value is required", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RememberAsync_EmptyCategory_ReturnsError(string? category)
    {
        var result = await _wrapper.RememberAsync("key", "value", category!);
        Assert.StartsWith("Error: category is required", result);
    }

    [Fact]
    public async Task RememberAsync_InvalidCategory_ReturnsError()
    {
        var result = await _wrapper.RememberAsync("key", "value", "invalid-cat");
        Assert.Contains("Invalid category", result);
    }

    [Fact]
    public async Task RememberAsync_TtlTooLow_ReturnsError()
    {
        var result = await _wrapper.RememberAsync("key", "value", "pattern", ttl: 0);
        Assert.Contains("ttl must be between 1 and 87600", result);
    }

    [Fact]
    public async Task RememberAsync_TtlNegative_ReturnsError()
    {
        var result = await _wrapper.RememberAsync("key", "value", "pattern", ttl: -1);
        Assert.Contains("ttl must be between 1 and 87600", result);
    }

    [Fact]
    public async Task RememberAsync_TtlTooHigh_ReturnsError()
    {
        var result = await _wrapper.RememberAsync("key", "value", "pattern", ttl: 87601);
        Assert.Contains("ttl must be between 1 and 87600", result);
    }

    [Fact]
    public async Task RememberAsync_ValueAtLimit_Succeeds()
    {
        var value = new string('a', 500);
        var result = await _wrapper.RememberAsync("k", value, "pattern");
        Assert.StartsWith("Memory created", result);
    }

    [Fact]
    public async Task RememberAsync_ValueExceedsLimit_ReturnsError()
    {
        var value = new string('a', 501);
        var result = await _wrapper.RememberAsync("k", value, "pattern");
        Assert.Contains("exceeds the 500-character limit", result);
        Assert.Contains("received 501", result);

        // And the memory must NOT have been written.
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        Assert.Null(await db.AgentMemories.FindAsync(AgentId, "k"));
    }

    // ── RememberAsync: Create and update ────────────────────────

    [Fact]
    public async Task RememberAsync_CreatesNewMemory()
    {
        var result = await _wrapper.RememberAsync("build-cmd", "dotnet build", "pattern");
        Assert.Contains("Memory created", result);
        Assert.Contains("[pattern]", result);
        Assert.Contains("build-cmd", result);
    }

    [Fact]
    public async Task RememberAsync_UpdatesExistingMemory()
    {
        await _wrapper.RememberAsync("my-key", "old value", "decision");
        var result = await _wrapper.RememberAsync("my-key", "new value", "lesson");
        Assert.Contains("Memory updated", result);
        Assert.Contains("[lesson]", result);
    }

    [Fact]
    public async Task RememberAsync_WithTtl_IncludesExpiryInResponse()
    {
        var result = await _wrapper.RememberAsync("temp-key", "temp value", "finding", ttl: 24);
        Assert.Contains("Memory created", result);
        Assert.Contains("expires:", result);
    }

    [Fact]
    public async Task RememberAsync_PermanentFlag_IncludesPermanentInResponse()
    {
        // First create with TTL
        await _wrapper.RememberAsync("perm-key", "value", "pattern", ttl: 24);
        // Then update as permanent
        var result = await _wrapper.RememberAsync("perm-key", "value", "pattern", permanent: true);
        Assert.Contains("permanent", result);
    }

    [Fact]
    public async Task RememberAsync_PermanentFlag_ClearsTtl()
    {
        await _wrapper.RememberAsync("ttl-clear", "value", "pattern", ttl: 24);
        await _wrapper.RememberAsync("ttl-clear", "value", "pattern", permanent: true);

        // Recall should show it without TTL warning
        var result = await _wrapper.RecallAsync(key: "ttl-clear");
        Assert.DoesNotContain("expires:", result);
    }

    [Theory]
    [InlineData("decision")]
    [InlineData("lesson")]
    [InlineData("pattern")]
    [InlineData("preference")]
    [InlineData("invariant")]
    [InlineData("risk")]
    [InlineData("gotcha")]
    [InlineData("incident")]
    [InlineData("constraint")]
    [InlineData("finding")]
    [InlineData("spec-drift")]
    [InlineData("mapping")]
    [InlineData("verification")]
    [InlineData("gap-pattern")]
    [InlineData("shared")]
    public async Task RememberAsync_AllValidCategories_Succeeds(string category)
    {
        var result = await _wrapper.RememberAsync(
            $"cat-test-{category}", "value", category);
        Assert.Contains("Memory created", result);
    }

    // ── RecallAsync ─────────────────────────────────────────────

    [Fact]
    public async Task RecallAsync_NoMemories_ReturnsNotFound()
    {
        var result = await _wrapper.RecallAsync(category: "pattern");
        Assert.Equal("No memories found.", result);
    }

    [Fact]
    public async Task RecallAsync_ByCategory_FiltersCorrectly()
    {
        await _wrapper.RememberAsync("a-pattern", "value1", "pattern");
        await _wrapper.RememberAsync("a-decision", "value2", "decision");

        var result = await _wrapper.RecallAsync(category: "pattern");
        Assert.Contains("a-pattern", result);
        Assert.DoesNotContain("a-decision", result);
    }

    [Fact]
    public async Task RecallAsync_ByKey_FiltersCorrectly()
    {
        await _wrapper.RememberAsync("key-one", "value1", "pattern");
        await _wrapper.RememberAsync("key-two", "value2", "pattern");

        var result = await _wrapper.RecallAsync(key: "key-one");
        Assert.Contains("key-one", result);
        Assert.DoesNotContain("key-two", result);
    }

    [Fact]
    public async Task RecallAsync_ByQuery_UsesFullTextSearch()
    {
        await _wrapper.RememberAsync("fts-key", "dotnet build is the build command", "pattern");
        await _wrapper.RememberAsync("other-key", "something else entirely", "pattern");

        var result = await _wrapper.RecallAsync(query: "dotnet build");
        Assert.Contains("fts-key", result);
    }

    [Fact]
    public async Task RecallAsync_ExpiredMemories_ExcludedByDefault()
    {
        // Insert an expired memory directly
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = AgentId,
            Key = "expired-key",
            Category = "pattern",
            Value = "expired value",
            CreatedAt = DateTime.UtcNow.AddHours(-48),
            ExpiresAt = DateTime.UtcNow.AddHours(-1)
        });
        await db.SaveChangesAsync();

        var result = await _wrapper.RecallAsync(key: "expired-key");
        Assert.Equal("No memories found.", result);
    }

    [Fact]
    public async Task RecallAsync_ExpiredMemories_IncludedWhenRequested()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = AgentId,
            Key = "expired-incl",
            Category = "pattern",
            Value = "expired but visible",
            CreatedAt = DateTime.UtcNow.AddHours(-48),
            ExpiresAt = DateTime.UtcNow.AddHours(-1)
        });
        await db.SaveChangesAsync();

        var result = await _wrapper.RecallAsync(key: "expired-incl", includeExpired: true);
        Assert.Contains("expired-incl", result);
    }

    [Fact]
    public async Task RecallAsync_SharedMemories_VisibleToOtherAgents()
    {
        // Agent 1 stores a shared memory
        await _wrapper.RememberAsync("shared-info", "build takes 2 min", "shared");

        // Agent 2 should see it
        var result = await _otherAgentWrapper.RecallAsync(category: "shared");
        Assert.Contains("shared-info", result);
        Assert.Contains($"from {AgentId}", result);
    }

    [Fact]
    public async Task RecallAsync_DisplaysMemoryCount()
    {
        await _wrapper.RememberAsync("count-1", "val1", "pattern");
        await _wrapper.RememberAsync("count-2", "val2", "pattern");

        var result = await _wrapper.RecallAsync(category: "pattern");
        Assert.StartsWith("Memories (2):", result);
    }

    [Fact]
    public async Task RecallAsync_StaleMemories_ShowWarning()
    {
        // Insert a memory that hasn't been accessed in over 30 days
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = AgentId,
            Key = "stale-key",
            Category = "pattern",
            Value = "stale value",
            CreatedAt = DateTime.UtcNow.AddDays(-60),
            UpdatedAt = DateTime.UtcNow.AddDays(-60),
            LastAccessedAt = DateTime.UtcNow.AddDays(-35)
        });
        await db.SaveChangesAsync();

        var result = await _wrapper.RecallAsync(key: "stale-key");
        Assert.Contains("stale", result, StringComparison.OrdinalIgnoreCase);
    }
}
