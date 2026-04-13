using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for SprintSchedulerService — cron-based sprint scheduling.
/// </summary>
public class SprintSchedulerServiceTests : IDisposable
{
    private const string TestWorkspace = "/tmp/test-workspace";
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly SprintService _sprintService;
    private readonly SprintSchedulerService _scheduler;
    private readonly IServiceScopeFactory _scopeFactory;

    public SprintSchedulerServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        var broadcaster = new ActivityBroadcaster();
        var settings = new SystemSettingsService(_db);
        _sprintService = new SprintService(_db, broadcaster, settings, NullLogger<SprintService>.Instance);

        // Build a minimal DI container so the scheduler can resolve scoped services
        var services = new ServiceCollection();
        services.AddSingleton<AgentAcademyDbContext>(_db);
        services.AddSingleton<SprintService>(_sprintService);
        var provider = services.BuildServiceProvider();
        _scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var schedulerSettings = Options.Create(new SprintSchedulerSettings
        {
            Enabled = true,
            CheckIntervalSeconds = 60,
        });
        _scheduler = new SprintSchedulerService(
            _scopeFactory, schedulerSettings, NullLogger<SprintSchedulerService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── ComputeNextRun ───────────────────────────────────────────

    [Fact]
    public void ComputeNextRun_ValidCron_ReturnsUtcTime()
    {
        // "every hour on the hour" from a known time
        var from = new DateTime(2026, 4, 13, 10, 30, 0, DateTimeKind.Utc);
        var next = SprintSchedulerService.ComputeNextRun("0 * * * *", "UTC", from);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 4, 13, 11, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void ComputeNextRun_WithTimezone_ConvertsCorrectly()
    {
        // "every day at 9 AM" in US Eastern
        var from = new DateTime(2026, 4, 13, 12, 0, 0, DateTimeKind.Utc); // 8 AM EDT
        var next = SprintSchedulerService.ComputeNextRun("0 9 * * *", "America/New_York", from);

        Assert.NotNull(next);
        // 9 AM EDT = 1 PM UTC (during daylight saving)
        Assert.Equal(new DateTime(2026, 4, 13, 13, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void ComputeNextRun_InvalidCron_ReturnsNull()
    {
        var result = SprintSchedulerService.ComputeNextRun("not-a-cron", "UTC", DateTime.UtcNow);
        Assert.Null(result);
    }

    [Fact]
    public void ComputeNextRun_InvalidTimezone_ReturnsNull()
    {
        var result = SprintSchedulerService.ComputeNextRun("0 * * * *", "Fake/Timezone", DateTime.UtcNow);
        Assert.Null(result);
    }

    // ── IsValidCron ──────────────────────────────────────────────

    [Theory]
    [InlineData("0 * * * *", true)]       // every hour
    [InlineData("*/15 * * * *", true)]     // every 15 min
    [InlineData("0 9 * * 1", true)]        // Mondays at 9
    [InlineData("0 0 1 * *", true)]        // first of month
    [InlineData("bad", false)]
    [InlineData("", false)]
    [InlineData("* * * * * *", false)]     // 6 fields (seconds-based) — rejected
    public void IsValidCron_ReturnsExpected(string expression, bool expected)
    {
        Assert.Equal(expected, SprintSchedulerService.IsValidCron(expression));
    }

    // ── EvaluateSchedulesAsync ───────────────────────────────────

    [Fact]
    public async Task Evaluate_DueSchedule_CreatesSprint()
    {
        var schedule = new SprintScheduleEntity
        {
            Id = Guid.NewGuid().ToString(),
            WorkspacePath = TestWorkspace,
            CronExpression = "0 * * * *",
            TimeZoneId = "UTC",
            Enabled = true,
            NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1),
        };
        _db.SprintSchedules.Add(schedule);
        await _db.SaveChangesAsync();

        await _scheduler.EvaluateSchedulesAsync(CancellationToken.None);

        // Sprint should have been created
        var sprint = await _db.Sprints.FirstOrDefaultAsync();
        Assert.NotNull(sprint);
        Assert.Equal(TestWorkspace, sprint.WorkspacePath);
        Assert.Equal("Active", sprint.Status);

        // Schedule should be updated
        var updated = await _db.SprintSchedules.FindAsync(schedule.Id);
        Assert.NotNull(updated);
        Assert.Equal("started", updated!.LastOutcome);
        Assert.NotNull(updated.LastTriggeredAt);
        Assert.NotNull(updated.LastEvaluatedAt);
        Assert.NotNull(updated.NextRunAtUtc);
        Assert.True(updated.NextRunAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task Evaluate_ActiveSprintExists_SkipsAndRecordsOutcome()
    {
        // Create an active sprint first
        await _sprintService.CreateSprintAsync(TestWorkspace);

        var schedule = new SprintScheduleEntity
        {
            Id = Guid.NewGuid().ToString(),
            WorkspacePath = TestWorkspace,
            CronExpression = "0 * * * *",
            TimeZoneId = "UTC",
            Enabled = true,
            NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1),
        };
        _db.SprintSchedules.Add(schedule);
        await _db.SaveChangesAsync();

        await _scheduler.EvaluateSchedulesAsync(CancellationToken.None);

        var updated = await _db.SprintSchedules.FindAsync(schedule.Id);
        Assert.Equal("skipped_active", updated!.LastOutcome);
        Assert.Null(updated.LastTriggeredAt); // No successful trigger
        Assert.NotNull(updated.LastEvaluatedAt);
    }

    [Fact]
    public async Task Evaluate_DisabledSchedule_IsIgnored()
    {
        var schedule = new SprintScheduleEntity
        {
            Id = Guid.NewGuid().ToString(),
            WorkspacePath = TestWorkspace,
            CronExpression = "0 * * * *",
            TimeZoneId = "UTC",
            Enabled = false,
            NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1),
        };
        _db.SprintSchedules.Add(schedule);
        await _db.SaveChangesAsync();

        await _scheduler.EvaluateSchedulesAsync(CancellationToken.None);

        var sprint = await _db.Sprints.FirstOrDefaultAsync();
        Assert.Null(sprint); // No sprint created
    }

    [Fact]
    public async Task Evaluate_FutureSchedule_IsNotTriggered()
    {
        var schedule = new SprintScheduleEntity
        {
            Id = Guid.NewGuid().ToString(),
            WorkspacePath = TestWorkspace,
            CronExpression = "0 * * * *",
            TimeZoneId = "UTC",
            Enabled = true,
            NextRunAtUtc = DateTime.UtcNow.AddHours(1),
        };
        _db.SprintSchedules.Add(schedule);
        await _db.SaveChangesAsync();

        await _scheduler.EvaluateSchedulesAsync(CancellationToken.None);

        var sprint = await _db.Sprints.FirstOrDefaultAsync();
        Assert.Null(sprint);
    }

    [Fact]
    public async Task Evaluate_NullNextRunAt_IsNotTriggered()
    {
        var schedule = new SprintScheduleEntity
        {
            Id = Guid.NewGuid().ToString(),
            WorkspacePath = TestWorkspace,
            CronExpression = "0 * * * *",
            TimeZoneId = "UTC",
            Enabled = true,
            NextRunAtUtc = null,
        };
        _db.SprintSchedules.Add(schedule);
        await _db.SaveChangesAsync();

        await _scheduler.EvaluateSchedulesAsync(CancellationToken.None);

        var sprint = await _db.Sprints.FirstOrDefaultAsync();
        Assert.Null(sprint);
    }

    [Fact]
    public async Task Evaluate_MultipleDueSchedules_CreatesSprintsForEach()
    {
        var ws2 = "/tmp/test-workspace-2";

        _db.SprintSchedules.AddRange(
            new SprintScheduleEntity
            {
                Id = Guid.NewGuid().ToString(),
                WorkspacePath = TestWorkspace,
                CronExpression = "0 * * * *",
                TimeZoneId = "UTC",
                Enabled = true,
                NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1),
            },
            new SprintScheduleEntity
            {
                Id = Guid.NewGuid().ToString(),
                WorkspacePath = ws2,
                CronExpression = "0 * * * *",
                TimeZoneId = "UTC",
                Enabled = true,
                NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1),
            });
        await _db.SaveChangesAsync();

        await _scheduler.EvaluateSchedulesAsync(CancellationToken.None);

        var sprints = await _db.Sprints.ToListAsync();
        Assert.Equal(2, sprints.Count);
        Assert.Contains(sprints, s => s.WorkspacePath == TestWorkspace);
        Assert.Contains(sprints, s => s.WorkspacePath == ws2);
    }

    [Fact]
    public async Task Evaluate_AfterTrigger_NextRunIsInFuture()
    {
        var schedule = new SprintScheduleEntity
        {
            Id = Guid.NewGuid().ToString(),
            WorkspacePath = TestWorkspace,
            CronExpression = "0 * * * *", // every hour
            TimeZoneId = "UTC",
            Enabled = true,
            NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1),
        };
        _db.SprintSchedules.Add(schedule);
        await _db.SaveChangesAsync();

        await _scheduler.EvaluateSchedulesAsync(CancellationToken.None);

        var updated = await _db.SprintSchedules.FindAsync(schedule.Id);
        Assert.NotNull(updated!.NextRunAtUtc);
        Assert.True(updated.NextRunAtUtc > DateTime.UtcNow,
            "NextRunAtUtc should be recomputed to a future time");
    }

    [Fact]
    public async Task Evaluate_SprintHasScheduledTrigger()
    {
        var schedule = new SprintScheduleEntity
        {
            Id = Guid.NewGuid().ToString(),
            WorkspacePath = TestWorkspace,
            CronExpression = "0 * * * *",
            TimeZoneId = "UTC",
            Enabled = true,
            NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1),
        };
        _db.SprintSchedules.Add(schedule);
        await _db.SaveChangesAsync();

        await _scheduler.EvaluateSchedulesAsync(CancellationToken.None);

        // The sprint trigger metadata is verified by checking the activity event
        var sprint = await _db.Sprints.FirstOrDefaultAsync();
        Assert.NotNull(sprint);
        var evt = await _db.ActivityEvents
            .Where(e => e.Type == "SprintStarted")
            .FirstOrDefaultAsync();
        Assert.NotNull(evt);
        Assert.Contains("scheduled", evt!.MetadataJson ?? "");
    }

    // ── Settings Validation ──────────────────────────────────────

    [Fact]
    public void Settings_ZeroInterval_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SprintSchedulerSettings { CheckIntervalSeconds = 0 }.Validate());
    }

    [Fact]
    public void Settings_NegativeInterval_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SprintSchedulerSettings { CheckIntervalSeconds = -1 }.Validate());
    }

    [Fact]
    public void Settings_DefaultValues_AreValid()
    {
        var settings = new SprintSchedulerSettings();
        settings.Validate(); // should not throw
        Assert.True(settings.Enabled);
        Assert.Equal(60, settings.CheckIntervalSeconds);
    }

    // ── CheckInterval property ───────────────────────────────────

    [Fact]
    public void CheckInterval_ReflectsSettings()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), _scheduler.CheckInterval);
    }
}
