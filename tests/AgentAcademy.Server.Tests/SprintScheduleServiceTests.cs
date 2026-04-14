using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Tests;

public class SprintScheduleServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly SprintScheduleService _sut;

    public SprintScheduleServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection).Options;
        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();
        _sut = new SprintScheduleService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private SprintScheduleEntity MakeSchedule(
        string workspacePath = "/ws/test",
        string cron = "0 9 * * 1",
        string tz = "UTC",
        bool enabled = true)
    {
        return new SprintScheduleEntity
        {
            Id = Guid.NewGuid().ToString(),
            WorkspacePath = workspacePath,
            CronExpression = cron,
            TimeZoneId = tz,
            Enabled = enabled,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    // ── GetScheduleAsync ────────────────────────────────────────

    [Fact]
    public async Task Get_ReturnsNull_WhenNoScheduleExists()
    {
        var result = await _sut.GetScheduleAsync("/ws/nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task Get_ReturnsSchedule_WhenExists()
    {
        var entity = MakeSchedule("/ws/test");
        _db.SprintSchedules.Add(entity);
        await _db.SaveChangesAsync();

        var result = await _sut.GetScheduleAsync("/ws/test");

        Assert.NotNull(result);
        Assert.Equal(entity.Id, result.Id);
        Assert.Equal("/ws/test", result.WorkspacePath);
        Assert.Equal("0 9 * * 1", result.CronExpression);
        Assert.Equal("UTC", result.TimeZoneId);
        Assert.True(result.Enabled);
    }

    [Fact]
    public async Task Get_DoesNotReturnSchedule_ForDifferentWorkspace()
    {
        _db.SprintSchedules.Add(MakeSchedule("/ws/other"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetScheduleAsync("/ws/test");
        Assert.Null(result);
    }

    // ── UpsertScheduleAsync ─────────────────────────────────────

    [Fact]
    public async Task Upsert_CreatesNewSchedule_WhenNoneExists()
    {
        var result = await _sut.UpsertScheduleAsync(
            "/ws/new", "0 9 * * 1", "UTC", enabled: true);

        Assert.Equal("/ws/new", result.WorkspacePath);
        Assert.Equal("0 9 * * 1", result.CronExpression);
        Assert.Equal("UTC", result.TimeZoneId);
        Assert.True(result.Enabled);
        Assert.NotNull(result.NextRunAtUtc);

        var count = await _db.SprintSchedules.CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Upsert_UpdatesExistingSchedule()
    {
        _db.SprintSchedules.Add(MakeSchedule("/ws/existing", "0 9 * * 1", "UTC"));
        await _db.SaveChangesAsync();

        var result = await _sut.UpsertScheduleAsync(
            "/ws/existing", "30 14 * * 5", "America/New_York", enabled: true);

        Assert.Equal("30 14 * * 5", result.CronExpression);
        Assert.Equal("America/New_York", result.TimeZoneId);

        var count = await _db.SprintSchedules.CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Upsert_SetsNextRunNull_WhenDisabled()
    {
        var result = await _sut.UpsertScheduleAsync(
            "/ws/disabled", "0 9 * * 1", "UTC", enabled: false);

        Assert.False(result.Enabled);
        Assert.Null(result.NextRunAtUtc);
    }

    [Fact]
    public async Task Upsert_ComputesNextRun_WhenEnabled()
    {
        var result = await _sut.UpsertScheduleAsync(
            "/ws/enabled", "0 9 * * 1", "UTC", enabled: true);

        Assert.True(result.Enabled);
        Assert.NotNull(result.NextRunAtUtc);
        Assert.True(result.NextRunAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task Upsert_ThrowsArgumentException_ForInvalidCron()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.UpsertScheduleAsync("/ws/test", "not-a-cron", "UTC", enabled: true));

        Assert.Contains("Invalid cron expression", ex.Message);
    }

    [Fact]
    public async Task Upsert_ThrowsArgumentException_ForInvalidTimezone()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.UpsertScheduleAsync("/ws/test", "0 9 * * 1", "Narnia/Nowhere", enabled: true));

        Assert.Contains("Unknown timezone", ex.Message);
    }

    [Theory]
    [InlineData("0 9 * * 1")]       // every Monday at 9:00
    [InlineData("*/15 * * * *")]     // every 15 minutes
    [InlineData("0 0 1 * *")]        // first of each month
    [InlineData("30 14 * * 1-5")]    // weekdays at 14:30
    public async Task Upsert_AcceptsValidCronFormats(string cron)
    {
        var result = await _sut.UpsertScheduleAsync("/ws/test", cron, "UTC", enabled: true);
        Assert.Equal(cron, result.CronExpression);
    }

    // ── DeleteScheduleAsync ─────────────────────────────────────

    [Fact]
    public async Task Delete_ReturnsFalse_WhenNoScheduleExists()
    {
        var result = await _sut.DeleteScheduleAsync("/ws/nonexistent");
        Assert.False(result);
    }

    [Fact]
    public async Task Delete_RemovesSchedule_AndReturnsTrue()
    {
        _db.SprintSchedules.Add(MakeSchedule("/ws/to-delete"));
        await _db.SaveChangesAsync();

        var result = await _sut.DeleteScheduleAsync("/ws/to-delete");

        Assert.True(result);
        var count = await _db.SprintSchedules.CountAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Delete_DoesNotAffectOtherWorkspaces()
    {
        _db.SprintSchedules.Add(MakeSchedule("/ws/keep"));
        _db.SprintSchedules.Add(MakeSchedule("/ws/remove"));
        await _db.SaveChangesAsync();

        await _sut.DeleteScheduleAsync("/ws/remove");

        var remaining = await _db.SprintSchedules.ToListAsync();
        Assert.Single(remaining);
        Assert.Equal("/ws/keep", remaining[0].WorkspacePath);
    }

    // ── Response mapping ────────────────────────────────────────

    [Fact]
    public async Task ResponseMapping_IncludesAllFields()
    {
        var now = DateTime.UtcNow;
        _db.SprintSchedules.Add(new SprintScheduleEntity
        {
            Id = "sched-1",
            WorkspacePath = "/ws/full",
            CronExpression = "0 9 * * 1",
            TimeZoneId = "Europe/London",
            Enabled = true,
            NextRunAtUtc = now.AddDays(1),
            LastTriggeredAt = now.AddDays(-7),
            LastEvaluatedAt = now.AddMinutes(-5),
            LastOutcome = "started",
            CreatedAt = now.AddMonths(-1),
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var result = await _sut.GetScheduleAsync("/ws/full");

        Assert.NotNull(result);
        Assert.Equal("sched-1", result.Id);
        Assert.Equal("/ws/full", result.WorkspacePath);
        Assert.Equal("0 9 * * 1", result.CronExpression);
        Assert.Equal("Europe/London", result.TimeZoneId);
        Assert.True(result.Enabled);
        Assert.Equal(now.AddDays(1), result.NextRunAtUtc);
        Assert.Equal(now.AddDays(-7), result.LastTriggeredAt);
        Assert.Equal(now.AddMinutes(-5), result.LastEvaluatedAt);
        Assert.Equal("started", result.LastOutcome);
        Assert.Equal(now.AddMonths(-1), result.CreatedAt);
        Assert.Equal(now, result.UpdatedAt);
    }
}
