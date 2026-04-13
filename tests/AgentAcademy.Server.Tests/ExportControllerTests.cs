using System.Text.Json;
using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for <see cref="ExportController"/> — parameter validation,
/// content types, Content-Disposition headers, and end-to-end CSV/JSON output.
/// Uses real services with in-memory SQLite.
/// </summary>
public sealed class ExportControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly ExportController _controller;
    private readonly IServiceScope _exportScope;
    private static int _idCounter;

    private static readonly AgentCatalogOptions TestCatalog = new(
        DefaultRoomId: "main",
        DefaultRoomName: "Main Room",
        Agents: new List<AgentDefinition>
        {
            new("planner-1", "Planner", "Planner", "Plans tasks", "", "gpt-4", new(), new(), true),
            new("coder-1", "Coder", "Coder", "Writes code", "", "gpt-4", new(), new(), true),
        }
    );

    public ExportControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

        _exportScope = _serviceProvider.CreateScope();
        var exportDb = _exportScope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var analytics = new AgentAnalyticsService(
            scopeFactory, TestCatalog, NullLogger<AgentAnalyticsService>.Instance);
        var usageTracker = new LlmUsageTracker(
            scopeFactory, NullLogger<LlmUsageTracker>.Instance);
        var conversationExport = new ConversationExportService(exportDb);

        _controller = new ExportController(analytics, usageTracker, conversationExport);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
        };
    }

    public void Dispose()
    {
        _exportScope.Dispose();
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private AgentAcademyDbContext GetDb()
    {
        var scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
    }

    private static LlmUsageEntity MakeUsage(string agentId, string? roomId = null,
        long inputTokens = 100, long outputTokens = 50, double? cost = 0.01,
        int? durationMs = 200, string? model = "gpt-4", DateTime? recordedAt = null)
    {
        return new LlmUsageEntity
        {
            Id = Interlocked.Increment(ref _idCounter).ToString(),
            AgentId = agentId,
            RoomId = roomId ?? "room-1",
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Cost = cost,
            DurationMs = durationMs,
            Model = model,
            RecordedAt = recordedAt ?? DateTime.UtcNow,
        };
    }

    // ── ExportAgents: parameter validation ──

    [Fact]
    public async Task ExportAgents_InvalidFormat_ReturnsBadRequest()
    {
        var result = await _controller.ExportAgents(format: "xml");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ExportAgents_InvalidHoursBack_ReturnsBadRequest()
    {
        var result = await _controller.ExportAgents(hoursBack: 0);
        Assert.IsType<BadRequestObjectResult>(result);

        result = await _controller.ExportAgents(hoursBack: 9999);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── ExportAgents: CSV output ──

    [Fact]
    public async Task ExportAgents_DefaultFormat_ReturnsCsvFile()
    {
        var result = await _controller.ExportAgents();

        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Contains("text/csv", fileResult.ContentType);
        Assert.EndsWith(".csv", fileResult.FileDownloadName);
    }

    [Fact]
    public async Task ExportAgents_WithData_CsvContainsAgentRows()
    {
        using (var db = GetDb())
        {
            db.LlmUsage.Add(MakeUsage("planner-1", inputTokens: 1000, cost: 0.05));
            db.LlmUsage.Add(MakeUsage("coder-1", inputTokens: 2000, cost: 0.10));
            await db.SaveChangesAsync();
        }

        var result = await _controller.ExportAgents();
        var fileResult = Assert.IsType<FileContentResult>(result);
        var content = System.Text.Encoding.UTF8.GetString(fileResult.FileContents);

        Assert.Contains("AgentId,AgentName,TotalRequests", content);
        Assert.Contains("planner-1,Planner,", content);
        Assert.Contains("coder-1,Coder,", content);
    }

    [Fact]
    public async Task ExportAgents_EmptyDb_ReturnsHeaderOnly()
    {
        var result = await _controller.ExportAgents();
        var fileResult = Assert.IsType<FileContentResult>(result);
        var content = System.Text.Encoding.UTF8.GetString(fileResult.FileContents);

        var lines = content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines); // header only
    }

    // ── ExportAgents: JSON output ──

    [Fact]
    public async Task ExportAgents_JsonFormat_ReturnsJsonFile()
    {
        var result = await _controller.ExportAgents(format: "json");

        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Contains("application/json", fileResult.ContentType);
        Assert.EndsWith(".json", fileResult.FileDownloadName);

        // Should be valid JSON
        var json = System.Text.Encoding.UTF8.GetString(fileResult.FileContents);
        var doc = JsonDocument.Parse(json);
        Assert.NotNull(doc);
    }

    // ── ExportUsage: parameter validation ──

    [Fact]
    public async Task ExportUsage_InvalidLimit_ReturnsBadRequest()
    {
        var result = await _controller.ExportUsage(limit: 0);
        Assert.IsType<BadRequestObjectResult>(result);

        result = await _controller.ExportUsage(limit: 100000);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ExportUsage_InvalidFormat_ReturnsBadRequest()
    {
        var result = await _controller.ExportUsage(format: "xlsx");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── ExportUsage: CSV output ──

    [Fact]
    public async Task ExportUsage_DefaultFormat_ReturnsCsvFile()
    {
        var result = await _controller.ExportUsage();

        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Contains("text/csv", fileResult.ContentType);
        Assert.EndsWith(".csv", fileResult.FileDownloadName);
    }

    [Fact]
    public async Task ExportUsage_WithData_CsvContainsRecords()
    {
        using (var db = GetDb())
        {
            db.LlmUsage.Add(MakeUsage("planner-1", model: "gpt-4"));
            db.LlmUsage.Add(MakeUsage("coder-1", model: "claude-3"));
            await db.SaveChangesAsync();
        }

        var result = await _controller.ExportUsage();
        var fileResult = Assert.IsType<FileContentResult>(result);
        var content = System.Text.Encoding.UTF8.GetString(fileResult.FileContents);

        Assert.Contains("Id,AgentId,RoomId,Model,", content);
        var lines = content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // header + 2 records
    }

    [Fact]
    public async Task ExportUsage_WithAgentFilter_FiltersResults()
    {
        using (var db = GetDb())
        {
            db.LlmUsage.Add(MakeUsage("planner-1"));
            db.LlmUsage.Add(MakeUsage("coder-1"));
            db.LlmUsage.Add(MakeUsage("planner-1"));
            await db.SaveChangesAsync();
        }

        var result = await _controller.ExportUsage(agentId: "planner-1");
        var fileResult = Assert.IsType<FileContentResult>(result);
        var content = System.Text.Encoding.UTF8.GetString(fileResult.FileContents);

        Assert.Contains("planner-1", fileResult.FileDownloadName);
        var lines = content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // header + 2 planner records
    }

    [Fact]
    public async Task ExportUsage_TruncatedResult_SetsHeaders()
    {
        using (var db = GetDb())
        {
            // Insert more records than the limit to trigger truncation
            for (var i = 0; i < 5; i++)
                db.LlmUsage.Add(MakeUsage("planner-1"));
            await db.SaveChangesAsync();
        }

        // limit=3 fetches 4 (limit+1), finds 5 available → truncated
        var result = await _controller.ExportUsage(limit: 3);

        Assert.IsType<FileContentResult>(result);
        Assert.Equal("true", _controller.Response.Headers["X-Truncated"].ToString());
        Assert.Equal("3", _controller.Response.Headers["X-Record-Count"].ToString());
    }

    [Fact]
    public async Task ExportUsage_ExactlyAtLimit_NotTruncated()
    {
        using (var db = GetDb())
        {
            for (var i = 0; i < 3; i++)
                db.LlmUsage.Add(MakeUsage("planner-1"));
            await db.SaveChangesAsync();
        }

        // limit=3 fetches 4, finds only 3 → not truncated
        var result = await _controller.ExportUsage(limit: 3);

        Assert.IsType<FileContentResult>(result);
        Assert.False(_controller.Response.Headers.ContainsKey("X-Truncated"));
        Assert.Equal("3", _controller.Response.Headers["X-Record-Count"].ToString());
    }

    [Fact]
    public async Task ExportUsage_NotTruncated_NoTruncatedHeader()
    {
        using (var db = GetDb())
        {
            db.LlmUsage.Add(MakeUsage("planner-1"));
            await db.SaveChangesAsync();
        }

        var result = await _controller.ExportUsage();

        Assert.IsType<FileContentResult>(result);
        Assert.False(_controller.Response.Headers.ContainsKey("X-Truncated"));
    }

    // ── ExportUsage: JSON output ──

    [Fact]
    public async Task ExportUsage_JsonFormat_ReturnsValidJson()
    {
        using (var db = GetDb())
        {
            db.LlmUsage.Add(MakeUsage("coder-1", model: "gpt-4"));
            await db.SaveChangesAsync();
        }

        var result = await _controller.ExportUsage(format: "json");
        var fileResult = Assert.IsType<FileContentResult>(result);

        var json = System.Text.Encoding.UTF8.GetString(fileResult.FileContents);
        var parsed = JsonSerializer.Deserialize<List<LlmUsageRecord>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(parsed);
        Assert.Single(parsed!);
    }

    // ── ExportUsage: time filtering ──

    [Fact]
    public async Task ExportUsage_WithHoursBack_FiltersOldRecords()
    {
        using (var db = GetDb())
        {
            db.LlmUsage.Add(MakeUsage("planner-1", recordedAt: DateTime.UtcNow.AddHours(-2)));
            db.LlmUsage.Add(MakeUsage("planner-1", recordedAt: DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        var result = await _controller.ExportUsage(hoursBack: 1);
        var fileResult = Assert.IsType<FileContentResult>(result);
        var content = System.Text.Encoding.UTF8.GetString(fileResult.FileContents);

        var lines = content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length); // header + 1 recent record
    }

    // ── ExportRoomMessages ──

    [Fact]
    public async Task ExportRoomMessages_InvalidFormat_ReturnsBadRequest()
    {
        var result = await _controller.ExportRoomMessages("room-1", format: "xml");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ExportRoomMessages_NonexistentRoom_ReturnsNotFound()
    {
        var result = await _controller.ExportRoomMessages("nonexistent");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task ExportRoomMessages_Json_ReturnsJsonFile()
    {
        using (var db = GetDb())
        {
            db.Rooms.Add(new RoomEntity
            {
                Id = "export-room-1", Name = "Test Room",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            db.Messages.Add(new MessageEntity
            {
                Id = "em-1", RoomId = "export-room-1", SenderId = "agent-1",
                SenderName = "Planner", SenderKind = "Agent", Kind = "Chat",
                Content = "Hello world", SentAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var result = await _controller.ExportRoomMessages("export-room-1", format: "json");
        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Contains("application/json", fileResult.ContentType);
        Assert.Contains("testroom", fileResult.FileDownloadName.ToLowerInvariant());

        var json = System.Text.Encoding.UTF8.GetString(fileResult.FileContents);
        var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("messageCount").GetInt32());
    }

    [Fact]
    public async Task ExportRoomMessages_Markdown_ReturnsMarkdownFile()
    {
        using (var db = GetDb())
        {
            db.Rooms.Add(new RoomEntity
            {
                Id = "export-room-2", Name = "MD Room",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            db.Messages.Add(new MessageEntity
            {
                Id = "em-2", RoomId = "export-room-2", SenderId = "human",
                SenderName = "Human", SenderKind = "User", Kind = "Chat",
                Content = "Test content", SentAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var result = await _controller.ExportRoomMessages("export-room-2", format: "markdown");
        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Contains("text/markdown", fileResult.ContentType);

        var md = System.Text.Encoding.UTF8.GetString(fileResult.FileContents);
        Assert.Contains("# Room: MD Room", md);
        Assert.Contains("Test content", md);
    }

    // ── ExportDmMessages ──

    [Fact]
    public async Task ExportDmMessages_InvalidFormat_ReturnsBadRequest()
    {
        var result = await _controller.ExportDmMessages("agent-1", format: "csv");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ExportDmMessages_NoThread_ReturnsNotFound()
    {
        var result = await _controller.ExportDmMessages("no-such-agent");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task ExportDmMessages_Json_ReturnsJsonFile()
    {
        using (var db = GetDb())
        {
            db.Rooms.Add(new RoomEntity
            {
                Id = "dm-room", Name = "DM Room",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            db.Messages.Add(new MessageEntity
            {
                Id = "dm-export-1", RoomId = "dm-room", SenderId = "human",
                SenderName = "human", SenderKind = "User", Kind = "DirectMessage",
                Content = "DM test", SentAt = DateTime.UtcNow,
                RecipientId = "export-agent",
            });
            await db.SaveChangesAsync();
        }

        var result = await _controller.ExportDmMessages("export-agent", format: "json");
        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Contains("application/json", fileResult.ContentType);

        var json = System.Text.Encoding.UTF8.GetString(fileResult.FileContents);
        var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("export-agent", doc.RootElement.GetProperty("agentId").GetString());
    }
}
