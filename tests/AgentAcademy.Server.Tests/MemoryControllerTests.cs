using System.Security.Claims;
using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class MemoryControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly MemoryController _controller;

    public MemoryControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _controller = new MemoryController(_db, NullLogger<MemoryController>.Instance);
        SetAuthenticated(true);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void SetAuthenticated(bool authenticated)
    {
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = authenticated
                    ? new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.Name, "test-user")], "Cookies"))
                    : new ClaimsPrincipal(new ClaimsIdentity())
            }
        };
    }

    // ── Export ────────────────────────────────────────────────────

    [Fact]
    public async Task Export_Unauthenticated_ReturnsUnauthorized()
    {
        SetAuthenticated(false);
        var result = await _controller.Export(agentId: "agent1", category: null);
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Export_MissingAgentId_ReturnsBadRequest()
    {
        var result = await _controller.Export(agentId: null, category: null);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        ProblemDetailsAssert.HasCode(bad.Value, "missing_agent_id");
    }

    [Fact]
    public async Task Export_EmptyDb_ReturnsEmptyList()
    {
        var result = await _controller.Export(agentId: "agent1", category: null);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"count\":0", json);
    }

    [Fact]
    public async Task Export_ReturnsMatchingMemories()
    {
        _db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "agent1", Key = "key1", Category = "decision",
            Value = "value1", CreatedAt = DateTime.UtcNow
        });
        _db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "agent2", Key = "key2", Category = "lesson",
            Value = "value2", CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var result = await _controller.Export(agentId: "agent1", category: null);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"count\":1", json);
        Assert.Contains("key1", json);
        Assert.DoesNotContain("key2", json);
    }

    [Fact]
    public async Task Export_FiltersByCategory()
    {
        _db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "agent1", Key = "k1", Category = "decision",
            Value = "v1", CreatedAt = DateTime.UtcNow
        });
        _db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "agent1", Key = "k2", Category = "lesson",
            Value = "v2", CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var result = await _controller.Export(agentId: "agent1", category: "decision");
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"count\":1", json);
        Assert.Contains("k1", json);
    }

    // ── Import ────────────────────────────────────────────────────

    [Fact]
    public async Task Import_Unauthenticated_ReturnsUnauthorized()
    {
        SetAuthenticated(false);
        var result = await _controller.Import(new MemoryController.MemoryImportRequest
        {
            Memories = [new MemoryController.MemoryImportEntry
            {
                AgentId = "a1", Key = "k1", Category = "decision", Value = "v1"
            }]
        });
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Import_NullRequest_ReturnsBadRequest()
    {
        var result = await _controller.Import(null);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Import_EmptyMemories_ReturnsBadRequest()
    {
        var result = await _controller.Import(new MemoryController.MemoryImportRequest());
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Import_TooManyEntries_ReturnsBadRequest()
    {
        var entries = Enumerable.Range(0, MemoryController.MaxImportEntries + 1)
            .Select(i => new MemoryController.MemoryImportEntry
            {
                AgentId = "a1", Key = $"k{i}", Category = "decision", Value = "v"
            }).ToList();

        var result = await _controller.Import(new MemoryController.MemoryImportRequest { Memories = entries });
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        ProblemDetailsAssert.HasCode(bad.Value, "payload_too_large");
    }

    [Fact]
    public async Task Import_ValidEntry_CreatesMemory()
    {
        var result = await _controller.Import(new MemoryController.MemoryImportRequest
        {
            Memories =
            [
                new MemoryController.MemoryImportEntry
                {
                    AgentId = "agent1", Key = "test-key", Category = "decision", Value = "test-value"
                }
            ]
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"created\":1", json);

        var memory = await _db.AgentMemories.FindAsync("agent1", "test-key");
        Assert.NotNull(memory);
        Assert.Equal("test-value", memory.Value);
    }

    [Fact]
    public async Task Import_UpsertSemantics_UpdatesExisting()
    {
        _db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "a1", Key = "k1", Category = "decision",
            Value = "old", CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var result = await _controller.Import(new MemoryController.MemoryImportRequest
        {
            Memories =
            [
                new MemoryController.MemoryImportEntry
                {
                    AgentId = "a1", Key = "k1", Category = "lesson", Value = "new"
                }
            ]
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"updated\":1", json);

        var memory = await _db.AgentMemories.FindAsync("a1", "k1");
        Assert.Equal("new", memory!.Value);
    }

    [Fact]
    public async Task Import_SkipsInvalidCategory()
    {
        var result = await _controller.Import(new MemoryController.MemoryImportRequest
        {
            Memories =
            [
                new MemoryController.MemoryImportEntry
                {
                    AgentId = "a1", Key = "k1", Category = "bogus-category", Value = "v1"
                }
            ]
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"skipped\":1", json);
    }

    [Fact]
    public async Task Import_SkipsEntryWithMissingFields()
    {
        var result = await _controller.Import(new MemoryController.MemoryImportRequest
        {
            Memories =
            [
                new MemoryController.MemoryImportEntry
                {
                    AgentId = "", Key = "k1", Category = "decision", Value = "v1"
                }
            ]
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"skipped\":1", json);
    }

    [Fact]
    public async Task Import_SkipsValueTooLong()
    {
        var result = await _controller.Import(new MemoryController.MemoryImportRequest
        {
            Memories =
            [
                new MemoryController.MemoryImportEntry
                {
                    AgentId = "a1", Key = "k1", Category = "decision",
                    Value = new string('x', 501)
                }
            ]
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"skipped\":1", json);
    }

    [Fact]
    public async Task Import_WithTtl_SetsExpiration()
    {
        var result = await _controller.Import(new MemoryController.MemoryImportRequest
        {
            Memories =
            [
                new MemoryController.MemoryImportEntry
                {
                    AgentId = "a1", Key = "ttl-key", Category = "decision",
                    Value = "v1", TtlHours = 24
                }
            ]
        });

        Assert.IsType<OkObjectResult>(result);
        var memory = await _db.AgentMemories.FindAsync("a1", "ttl-key");
        Assert.NotNull(memory!.ExpiresAt);
        Assert.True(memory.ExpiresAt > DateTime.UtcNow);
    }

    // ── Cleanup Expired ──────────────────────────────────────────

    [Fact]
    public async Task CleanupExpired_Unauthenticated_ReturnsUnauthorized()
    {
        SetAuthenticated(false);
        var result = await _controller.CleanupExpired(agentId: "a1");
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task CleanupExpired_MissingAgentId_ReturnsBadRequest()
    {
        var result = await _controller.CleanupExpired(agentId: null);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CleanupExpired_RemovesOnlyExpired()
    {
        var now = DateTime.UtcNow;
        _db.AgentMemories.AddRange(
            new AgentMemoryEntity
            {
                AgentId = "a1", Key = "expired", Category = "decision",
                Value = "v", CreatedAt = now, ExpiresAt = now.AddHours(-1)
            },
            new AgentMemoryEntity
            {
                AgentId = "a1", Key = "active", Category = "decision",
                Value = "v", CreatedAt = now, ExpiresAt = now.AddHours(24)
            },
            new AgentMemoryEntity
            {
                AgentId = "a1", Key = "no-expiry", Category = "decision",
                Value = "v", CreatedAt = now
            });
        await _db.SaveChangesAsync();

        var result = await _controller.CleanupExpired(agentId: "a1");
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"removed\":1", json);

        // Verify remaining
        var remaining = await _db.AgentMemories.Where(m => m.AgentId == "a1").ToListAsync();
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, m => m.Key == "active");
        Assert.Contains(remaining, m => m.Key == "no-expiry");
    }

    // ── Browse ───────────────────────────────────────────────────

    [Fact]
    public async Task Browse_Unauthenticated_ReturnsUnauthorized()
    {
        SetAuthenticated(false);
        var result = await _controller.Browse(agentId: "agent1", category: null, search: null);
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Browse_MissingAgentId_ReturnsBadRequest()
    {
        var result = await _controller.Browse(agentId: null, category: null, search: null);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        ProblemDetailsAssert.HasCode(bad.Value, "missing_agent_id");
    }

    [Fact]
    public async Task Browse_EmptyDb_ReturnsEmptyList()
    {
        var result = await _controller.Browse(agentId: "agent1", category: null, search: null);
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<MemoryController.BrowseResponse>(ok.Value);
        Assert.Equal(0, response.Total);
        Assert.Empty(response.Memories);
    }

    [Fact]
    public async Task Browse_ReturnsAllMemoriesForAgent()
    {
        var now = DateTime.UtcNow;
        _db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "a1", Key = "k1", Category = "decision", Value = "v1", CreatedAt = now },
            new AgentMemoryEntity { AgentId = "a1", Key = "k2", Category = "lesson", Value = "v2", CreatedAt = now },
            new AgentMemoryEntity { AgentId = "a2", Key = "k3", Category = "pattern", Value = "v3", CreatedAt = now });
        await _db.SaveChangesAsync();

        var result = await _controller.Browse(agentId: "a1", category: null, search: null);
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<MemoryController.BrowseResponse>(ok.Value);
        Assert.Equal(2, response.Total);
        Assert.All(response.Memories, m => Assert.Equal("a1", m.AgentId));
    }

    [Fact]
    public async Task Browse_FiltersByCategory()
    {
        var now = DateTime.UtcNow;
        _db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "a1", Key = "k1", Category = "decision", Value = "v1", CreatedAt = now },
            new AgentMemoryEntity { AgentId = "a1", Key = "k2", Category = "lesson", Value = "v2", CreatedAt = now });
        await _db.SaveChangesAsync();

        var result = await _controller.Browse(agentId: "a1", category: "decision", search: null);
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<MemoryController.BrowseResponse>(ok.Value);
        Assert.Single(response.Memories);
        Assert.Equal("decision", response.Memories[0].Category);
    }

    [Fact]
    public async Task Browse_ExcludesExpiredByDefault()
    {
        var now = DateTime.UtcNow;
        _db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "a1", Key = "active", Category = "decision", Value = "v", CreatedAt = now },
            new AgentMemoryEntity { AgentId = "a1", Key = "expired", Category = "lesson", Value = "v", CreatedAt = now, ExpiresAt = now.AddHours(-1) });
        await _db.SaveChangesAsync();

        var result = await _controller.Browse(agentId: "a1", category: null, search: null, includeExpired: false);
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<MemoryController.BrowseResponse>(ok.Value);
        Assert.Single(response.Memories);
        Assert.Equal("active", response.Memories[0].Key);
    }

    [Fact]
    public async Task Browse_IncludesExpiredWhenRequested()
    {
        var now = DateTime.UtcNow;
        _db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "a1", Key = "active", Category = "decision", Value = "v", CreatedAt = now },
            new AgentMemoryEntity { AgentId = "a1", Key = "expired", Category = "lesson", Value = "v", CreatedAt = now, ExpiresAt = now.AddHours(-1) });
        await _db.SaveChangesAsync();

        var result = await _controller.Browse(agentId: "a1", category: null, search: null, includeExpired: true);
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<MemoryController.BrowseResponse>(ok.Value);
        Assert.Equal(2, response.Total);
    }

    [Fact]
    public async Task Browse_OrdersByCategoryThenKey()
    {
        var now = DateTime.UtcNow;
        _db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "a1", Key = "z-key", Category = "lesson", Value = "v", CreatedAt = now },
            new AgentMemoryEntity { AgentId = "a1", Key = "a-key", Category = "decision", Value = "v", CreatedAt = now },
            new AgentMemoryEntity { AgentId = "a1", Key = "b-key", Category = "decision", Value = "v", CreatedAt = now });
        await _db.SaveChangesAsync();

        var result = await _controller.Browse(agentId: "a1", category: null, search: null);
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<MemoryController.BrowseResponse>(ok.Value);
        Assert.Equal(3, response.Total);
        Assert.Equal("a-key", response.Memories[0].Key);
        Assert.Equal("b-key", response.Memories[1].Key);
        Assert.Equal("z-key", response.Memories[2].Key);
    }

    // ── Stats ────────────────────────────────────────────────────

    [Fact]
    public async Task Stats_Unauthenticated_ReturnsUnauthorized()
    {
        SetAuthenticated(false);
        var result = await _controller.Stats(agentId: "agent1");
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Stats_MissingAgentId_ReturnsBadRequest()
    {
        var result = await _controller.Stats(agentId: null);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        ProblemDetailsAssert.HasCode(bad.Value, "missing_agent_id");
    }

    [Fact]
    public async Task Stats_ReturnsPerCategoryCounts()
    {
        var now = DateTime.UtcNow;
        _db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "a1", Key = "k1", Category = "decision", Value = "v", CreatedAt = now },
            new AgentMemoryEntity { AgentId = "a1", Key = "k2", Category = "decision", Value = "v", CreatedAt = now },
            new AgentMemoryEntity { AgentId = "a1", Key = "k3", Category = "lesson", Value = "v", CreatedAt = now },
            new AgentMemoryEntity { AgentId = "a1", Key = "k4", Category = "lesson", Value = "v", CreatedAt = now, ExpiresAt = now.AddHours(-1) });
        await _db.SaveChangesAsync();

        var result = await _controller.Stats(agentId: "a1");
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<MemoryController.StatsResponse>(ok.Value);
        Assert.Equal("a1", response.AgentId);
        Assert.Equal(4, response.TotalMemories);
        Assert.Equal(3, response.ActiveMemories);
        Assert.Equal(1, response.ExpiredMemories);
        Assert.Equal(2, response.Categories.Count);

        var decisionStat = response.Categories.First(c => c.Category == "decision");
        Assert.Equal(2, decisionStat.Active);
        Assert.Equal(0, decisionStat.Expired);
    }

    // ── Delete ───────────────────────────────────────────────────

    [Fact]
    public async Task Delete_Unauthenticated_ReturnsUnauthorized()
    {
        SetAuthenticated(false);
        var result = await _controller.Delete(agentId: "agent1", key: "k1");
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Delete_MissingAgentId_ReturnsBadRequest()
    {
        var result = await _controller.Delete(agentId: null, key: "k1");
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        ProblemDetailsAssert.HasCode(bad.Value, "missing_agent_id");
    }

    [Fact]
    public async Task Delete_MissingKey_ReturnsBadRequest()
    {
        var result = await _controller.Delete(agentId: "a1", key: null);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        ProblemDetailsAssert.HasCode(bad.Value, "missing_key");
    }

    [Fact]
    public async Task Delete_NotFound_ReturnsNotFound()
    {
        var result = await _controller.Delete(agentId: "a1", key: "nonexistent");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Delete_ExistingMemory_RemovesAndReturnsOk()
    {
        _db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "a1", Key = "to-delete", Category = "decision", Value = "v",
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var result = await _controller.Delete(agentId: "a1", key: "to-delete");
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"status\":\"deleted\"", json);

        var remaining = await _db.AgentMemories.FindAsync("a1", "to-delete");
        Assert.Null(remaining);
    }

    [Fact]
    public async Task Delete_DoesNotAffectOtherMemories()
    {
        _db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "a1", Key = "keep", Category = "lesson", Value = "v", CreatedAt = DateTime.UtcNow },
            new AgentMemoryEntity { AgentId = "a1", Key = "remove", Category = "decision", Value = "v", CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        await _controller.Delete(agentId: "a1", key: "remove");

        var remaining = await _db.AgentMemories.Where(m => m.AgentId == "a1").ToListAsync();
        Assert.Single(remaining);
        Assert.Equal("keep", remaining[0].Key);
    }

    // ── Browse Edge Cases ────────────────────────────────────────

    [Fact]
    public async Task Browse_DoesNotReturnOtherAgentMemories()
    {
        var now = DateTime.UtcNow;
        _db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "a1", Key = "mine", Category = "decision", Value = "v", CreatedAt = now },
            new AgentMemoryEntity { AgentId = "a2", Key = "theirs", Category = "decision", Value = "v", CreatedAt = now },
            new AgentMemoryEntity { AgentId = "a3", Key = "other", Category = "lesson", Value = "v", CreatedAt = now });
        await _db.SaveChangesAsync();

        var result = await _controller.Browse(agentId: "a1", category: null, search: null);
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<MemoryController.BrowseResponse>(ok.Value);

        Assert.Equal(1, response.Total);
        Assert.All(response.Memories, m => Assert.Equal("a1", m.AgentId));
    }

    [Fact]
    public async Task Browse_CategoryFilterWithExpiredCombined()
    {
        var now = DateTime.UtcNow;
        _db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "a1", Key = "active-decision", Category = "decision", Value = "v", CreatedAt = now },
            new AgentMemoryEntity { AgentId = "a1", Key = "expired-decision", Category = "decision", Value = "v", CreatedAt = now, ExpiresAt = now.AddHours(-1) },
            new AgentMemoryEntity { AgentId = "a1", Key = "active-lesson", Category = "lesson", Value = "v", CreatedAt = now });
        await _db.SaveChangesAsync();

        var result = await _controller.Browse(agentId: "a1", category: "decision", search: null, includeExpired: false);
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<MemoryController.BrowseResponse>(ok.Value);

        Assert.Single(response.Memories);
        Assert.Equal("active-decision", response.Memories[0].Key);
    }

    [Fact]
    public async Task Browse_CategoryFilterIsCaseInsensitive()
    {
        var now = DateTime.UtcNow;
        _db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "a1", Key = "k1", Category = "decision", Value = "v", CreatedAt = now },
            new AgentMemoryEntity { AgentId = "a1", Key = "k2", Category = "lesson", Value = "v", CreatedAt = now });
        await _db.SaveChangesAsync();

        var result = await _controller.Browse(agentId: "a1", category: "DECISION", search: null);
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<MemoryController.BrowseResponse>(ok.Value);

        Assert.Single(response.Memories);
        Assert.Equal("decision", response.Memories[0].Category);
        Assert.Equal("k1", response.Memories[0].Key);
    }

    [Fact]
    public async Task Browse_MapsDtoFieldsCorrectly()
    {
        var now = DateTime.UtcNow;
        var accessed = now.AddMinutes(-5);
        var expires = now.AddHours(24);
        _db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "a1", Key = "mapped", Category = "pattern", Value = "test-value",
            CreatedAt = now, UpdatedAt = now, LastAccessedAt = accessed, ExpiresAt = expires
        });
        await _db.SaveChangesAsync();

        var result = await _controller.Browse(agentId: "a1", category: null, search: null);
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<MemoryController.BrowseResponse>(ok.Value);
        var dto = Assert.Single(response.Memories);

        Assert.Equal("a1", dto.AgentId);
        Assert.Equal("mapped", dto.Key);
        Assert.Equal("pattern", dto.Category);
        Assert.Equal("test-value", dto.Value);
        Assert.NotNull(dto.UpdatedAt);
        Assert.NotNull(dto.LastAccessedAt);
        Assert.NotNull(dto.ExpiresAt);
    }

    // ── Stats Edge Cases ─────────────────────────────────────────

    [Fact]
    public async Task Stats_EmptyDb_ReturnsZeroCounts()
    {
        var result = await _controller.Stats(agentId: "empty-agent");
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<MemoryController.StatsResponse>(ok.Value);

        Assert.Equal("empty-agent", response.AgentId);
        Assert.Equal(0, response.TotalMemories);
        Assert.Equal(0, response.ActiveMemories);
        Assert.Equal(0, response.ExpiredMemories);
        Assert.Empty(response.Categories);
    }

    [Fact]
    public async Task Stats_DoesNotIncludeOtherAgents()
    {
        var now = DateTime.UtcNow;
        _db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "a1", Key = "k1", Category = "decision", Value = "v", CreatedAt = now },
            new AgentMemoryEntity { AgentId = "a2", Key = "k2", Category = "decision", Value = "v", CreatedAt = now });
        await _db.SaveChangesAsync();

        var result = await _controller.Stats(agentId: "a1");
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<MemoryController.StatsResponse>(ok.Value);

        Assert.Equal(1, response.TotalMemories);
    }

    [Fact]
    public async Task Stats_CategoriesOrderedByActiveCountDescending()
    {
        var now = DateTime.UtcNow;
        _db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "a1", Key = "k1", Category = "few", Value = "v", CreatedAt = now },
            new AgentMemoryEntity { AgentId = "a1", Key = "k2", Category = "many", Value = "v", CreatedAt = now },
            new AgentMemoryEntity { AgentId = "a1", Key = "k3", Category = "many", Value = "v", CreatedAt = now },
            new AgentMemoryEntity { AgentId = "a1", Key = "k4", Category = "many", Value = "v", CreatedAt = now });
        await _db.SaveChangesAsync();

        var result = await _controller.Stats(agentId: "a1");
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<MemoryController.StatsResponse>(ok.Value);

        Assert.Equal(2, response.Categories.Count);
        Assert.Equal("many", response.Categories[0].Category);
        Assert.Equal("few", response.Categories[1].Category);
    }

    // ── CleanupExpired Edge Cases ────────────────────────────────

    [Fact]
    public async Task CleanupExpired_DoesNotAffectOtherAgents()
    {
        var now = DateTime.UtcNow;
        _db.AgentMemories.AddRange(
            new AgentMemoryEntity { AgentId = "a1", Key = "expired-a1", Category = "c", Value = "v", CreatedAt = now, ExpiresAt = now.AddHours(-1) },
            new AgentMemoryEntity { AgentId = "a2", Key = "expired-a2", Category = "c", Value = "v", CreatedAt = now, ExpiresAt = now.AddHours(-1) });
        await _db.SaveChangesAsync();

        await _controller.CleanupExpired(agentId: "a1");

        var a2Memory = await _db.AgentMemories.FindAsync("a2", "expired-a2");
        Assert.NotNull(a2Memory);
    }

    [Fact]
    public async Task CleanupExpired_ZeroExpired_ReturnsZeroRemoved()
    {
        var now = DateTime.UtcNow;
        _db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "a1", Key = "active", Category = "c", Value = "v", CreatedAt = now, ExpiresAt = now.AddHours(24)
        });
        await _db.SaveChangesAsync();

        var result = await _controller.CleanupExpired(agentId: "a1");
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"removed\":0", json);

        var remaining = await _db.AgentMemories.CountAsync(m => m.AgentId == "a1");
        Assert.Equal(1, remaining);
    }
}
