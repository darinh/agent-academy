using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for SettingsController — system settings API endpoints.
/// </summary>
public class SettingsControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly SystemSettingsService _settingsService;
    private readonly SettingsController _controller;

    public SettingsControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _settingsService = new SystemSettingsService(_db);
        _controller = new SettingsController(_settingsService, new CommandRateLimiter());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task GetAll_ReturnsDefaults()
    {
        var result = await _controller.GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var settings = Assert.IsType<Dictionary<string, string>>(ok.Value);

        Assert.True(settings.ContainsKey("conversation.mainRoomEpochSize"));
        Assert.Equal("50", settings["conversation.mainRoomEpochSize"]);
    }

    [Fact]
    public async Task GetSetting_ReturnsKnownKey()
    {
        var result = await _controller.GetSetting("conversation.mainRoomEpochSize");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var setting = Assert.IsType<SettingResponse>(ok.Value);

        Assert.Equal("conversation.mainRoomEpochSize", setting.Key);
        Assert.Equal("50", setting.Value);
    }

    [Fact]
    public async Task GetSetting_ReturnsNotFound_ForUnknownKey()
    {
        var result = await _controller.GetSetting("nonexistent.key");
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpsertSettings_PersistsValues()
    {
        var updates = new Dictionary<string, string>
        {
            ["conversation.mainRoomEpochSize"] = "75",
            ["conversation.breakoutEpochSize"] = "25",
        };

        var result = await _controller.UpsertSettings(updates);
        var ok = Assert.IsType<OkObjectResult>(result);
        var settings = Assert.IsType<Dictionary<string, string>>(ok.Value);

        Assert.Equal("75", settings["conversation.mainRoomEpochSize"]);
        Assert.Equal("25", settings["conversation.breakoutEpochSize"]);
    }

    [Fact]
    public async Task UpsertSettings_OverwritesPrevious()
    {
        await _controller.UpsertSettings(new Dictionary<string, string>
        {
            ["conversation.mainRoomEpochSize"] = "100",
        });

        await _controller.UpsertSettings(new Dictionary<string, string>
        {
            ["conversation.mainRoomEpochSize"] = "200",
        });

        var result = await _controller.GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var settings = Assert.IsType<Dictionary<string, string>>(ok.Value);

        Assert.Equal("200", settings["conversation.mainRoomEpochSize"]);
    }

    [Fact]
    public async Task UpsertSettings_ReconfiguresRateLimiter()
    {
        var rateLimiter = new CommandRateLimiter();
        var controller = new SettingsController(_settingsService, rateLimiter);

        Assert.Equal(30, rateLimiter.MaxCommands);
        Assert.Equal(60, rateLimiter.WindowSeconds);

        await controller.UpsertSettings(new Dictionary<string, string>
        {
            ["commands.rateLimitMaxCommands"] = "10",
            ["commands.rateLimitWindowSeconds"] = "30",
        });

        Assert.Equal(10, rateLimiter.MaxCommands);
        Assert.Equal(30, rateLimiter.WindowSeconds);
    }

    [Fact]
    public async Task GetAll_IncludesRateLimitDefaults()
    {
        var result = await _controller.GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var settings = Assert.IsType<Dictionary<string, string>>(ok.Value);

        Assert.Equal("30", settings["commands.rateLimitMaxCommands"]);
        Assert.Equal("60", settings["commands.rateLimitWindowSeconds"]);
    }
}
