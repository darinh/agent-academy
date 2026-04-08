using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for SystemSettingsService — key-value settings with typed defaults.
/// </summary>
public class SystemSettingsServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly SystemSettingsService _service;

    public SystemSettingsServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _service = new SystemSettingsService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Get_ReturnsDefault_WhenNotSet()
    {
        var result = await _service.GetAsync("nonexistent", 42);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Set_ThenGet_ReturnsValue()
    {
        await _service.SetAsync("test.key", "100");
        var result = await _service.GetAsync("test.key", 0);
        Assert.Equal(100, result);
    }

    [Fact]
    public async Task Set_Upserts_OnExistingKey()
    {
        await _service.SetAsync("test.key", "first");
        await _service.SetAsync("test.key", "second");

        var result = await _service.GetAsync("test.key", "default");
        Assert.Equal("second", result);

        var count = await _db.SystemSettings.CountAsync(s => s.Key == "test.key");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetAll_ReturnsEmpty_Initially()
    {
        var all = await _service.GetAllAsync();
        Assert.Empty(all);
    }

    [Fact]
    public async Task GetAllWithDefaults_IncludesKnownKeys()
    {
        var all = await _service.GetAllWithDefaultsAsync();

        Assert.True(all.ContainsKey(SystemSettingsService.MainRoomEpochSizeKey));
        Assert.True(all.ContainsKey(SystemSettingsService.BreakoutEpochSizeKey));
        Assert.Equal("50", all[SystemSettingsService.MainRoomEpochSizeKey]);
        Assert.Equal("30", all[SystemSettingsService.BreakoutEpochSizeKey]);
    }

    [Fact]
    public async Task GetAllWithDefaults_StoredOverridesDefault()
    {
        await _service.SetAsync(SystemSettingsService.MainRoomEpochSizeKey, "75");
        var all = await _service.GetAllWithDefaultsAsync();

        Assert.Equal("75", all[SystemSettingsService.MainRoomEpochSizeKey]);
        Assert.Equal("30", all[SystemSettingsService.BreakoutEpochSizeKey]);
    }

    [Fact]
    public async Task GetMainRoomEpochSize_ReturnsDefault()
    {
        var size = await _service.GetMainRoomEpochSizeAsync();
        Assert.Equal(SystemSettingsService.DefaultMainRoomEpochSize, size);
    }

    [Fact]
    public async Task GetBreakoutEpochSize_ReturnsDefault()
    {
        var size = await _service.GetBreakoutEpochSizeAsync();
        Assert.Equal(SystemSettingsService.DefaultBreakoutEpochSize, size);
    }

    [Fact]
    public async Task GetMainRoomEpochSize_ReturnsOverride()
    {
        await _service.SetAsync(SystemSettingsService.MainRoomEpochSizeKey, "100");
        var size = await _service.GetMainRoomEpochSizeAsync();
        Assert.Equal(100, size);
    }

    [Fact]
    public async Task Get_ReturnsDefault_OnInvalidValue()
    {
        await _service.SetAsync("test.key", "not-a-number");
        var result = await _service.GetAsync("test.key", 42);
        Assert.Equal(42, result);
    }
}
