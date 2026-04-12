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
        Assert.Contains("missing_agent_id", bad.Value!.ToString()!);
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
        Assert.Contains("payload_too_large", bad.Value!.ToString()!);
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
}
