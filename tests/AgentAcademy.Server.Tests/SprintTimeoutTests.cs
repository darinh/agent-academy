using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for sprint timeout behavior: sign-off timeout auto-reject,
/// sprint max duration auto-cancel, and the SprintTimeoutService background loop.
/// </summary>
public class SprintTimeoutTests : IDisposable
{
    private const string TestWorkspace = "/tmp/test-workspace";
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly SprintService _sprintService;
    private readonly SprintStageService _sprintStageService;
    private readonly SprintArtifactService _artifactService;

    public SprintTimeoutTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _sprintService = new SprintService(_db, new ActivityBroadcaster(), new SystemSettingsService(_db), NullLogger<SprintService>.Instance);
        _sprintStageService = new SprintStageService(_db, new ActivityBroadcaster(), NullLogger<SprintStageService>.Instance);
        _artifactService = new SprintArtifactService(_db, new ActivityBroadcaster(), NullLogger<SprintArtifactService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Helper ───────────────────────────────────────────────────

    private async Task<SprintEntity> CreateSprintInSignOff()
    {
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        await _artifactService.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument",
            """{"Title":"T","Description":"D","InScope":[],"OutOfScope":[],"AcceptanceCriteria":[]}""");
        await _sprintStageService.AdvanceStageAsync(sprint.Id);
        // Sprint is now AwaitingSignOff with PendingStage = Planning
        return sprint;
    }

    // ── SignOffRequestedAt tracking ──────────────────────────────

    [Fact]
    public async Task AdvanceStage_SetsSignOffRequestedAt_WhenEnteringSignOff()
    {
        var sprint = await CreateSprintInSignOff();

        Assert.True(sprint.AwaitingSignOff);
        Assert.NotNull(sprint.SignOffRequestedAt);
        Assert.True((DateTime.UtcNow - sprint.SignOffRequestedAt.Value).TotalSeconds < 5);
    }

    [Fact]
    public async Task ApproveAdvance_ClearsSignOffRequestedAt()
    {
        var sprint = await CreateSprintInSignOff();
        Assert.NotNull(sprint.SignOffRequestedAt);

        var approved = await _sprintStageService.ApproveAdvanceAsync(sprint.Id);

        Assert.False(approved.AwaitingSignOff);
        Assert.Null(approved.SignOffRequestedAt);
    }

    [Fact]
    public async Task RejectAdvance_ClearsSignOffRequestedAt()
    {
        var sprint = await CreateSprintInSignOff();
        Assert.NotNull(sprint.SignOffRequestedAt);

        var rejected = await _sprintStageService.RejectAdvanceAsync(sprint.Id);

        Assert.False(rejected.AwaitingSignOff);
        Assert.Null(rejected.SignOffRequestedAt);
    }

    // ── TimeOutSignOffAsync ──────────────────────────────────────

    [Fact]
    public async Task TimeOutSignOff_RejectsAndClearsState()
    {
        var sprint = await CreateSprintInSignOff();

        var result = await _sprintStageService.TimeOutSignOffAsync(sprint.Id);

        Assert.False(result.AwaitingSignOff);
        Assert.Null(result.PendingStage);
        Assert.Null(result.SignOffRequestedAt);
        Assert.Equal("Active", result.Status);
        Assert.Equal("Intake", result.CurrentStage);
    }

    [Fact]
    public async Task TimeOutSignOff_EmitsEventWithTimeoutReason()
    {
        var sprint = await CreateSprintInSignOff();

        await _sprintStageService.TimeOutSignOffAsync(sprint.Id);

        var evt = await _db.ActivityEvents
            .OrderByDescending(e => e.OccurredAt)
            .FirstAsync(e => e.Type == "SprintStageAdvanced"
                && e.MetadataJson != null
                && e.MetadataJson.Contains("timeout_rejected"));

        Assert.Contains("timeout", evt.MetadataJson!);
        Assert.Contains("auto-rejected", evt.Message);
    }

    [Fact]
    public async Task TimeOutSignOff_ThrowsWhenNotAwaitingSignOff()
    {
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sprintStageService.TimeOutSignOffAsync(sprint.Id));
        Assert.Contains("not awaiting sign-off", ex.Message);
    }

    // ── TimeOutSprintAsync ───────────────────────────────────────

    [Fact]
    public async Task TimeOutSprint_CancelsAndSetsCompletedAt()
    {
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);

        var result = await _sprintService.TimeOutSprintAsync(sprint.Id);

        Assert.Equal("Cancelled", result.Status);
        Assert.NotNull(result.CompletedAt);
    }

    [Fact]
    public async Task TimeOutSprint_EmitsEventWithTimeoutReason()
    {
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);

        await _sprintService.TimeOutSprintAsync(sprint.Id);

        var evt = await _db.ActivityEvents
            .OrderByDescending(e => e.OccurredAt)
            .FirstAsync(e => e.Type == "SprintCancelled"
                && e.MetadataJson != null
                && e.MetadataJson.Contains("timeout"));

        Assert.Contains("timeout", evt.MetadataJson!);
        Assert.Contains("auto-cancelled", evt.Message);
    }

    [Fact]
    public async Task TimeOutSprint_ClearsSignOffStateIfActive()
    {
        var sprint = await CreateSprintInSignOff();

        var result = await _sprintService.TimeOutSprintAsync(sprint.Id);

        Assert.Equal("Cancelled", result.Status);
        Assert.False(result.AwaitingSignOff);
        Assert.Null(result.PendingStage);
        Assert.Null(result.SignOffRequestedAt);
    }

    [Fact]
    public async Task TimeOutSprint_ThrowsWhenAlreadyCancelled()
    {
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        await _sprintService.CancelSprintAsync(sprint.Id);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sprintService.TimeOutSprintAsync(sprint.Id));
        Assert.Contains("already Cancelled", ex.Message);
    }

    // ── Query methods ────────────────────────────────────────────

    [Fact]
    public async Task GetTimedOutSignOffSprints_FindsStaleSignOff()
    {
        var sprint = await CreateSprintInSignOff();

        // Backdate the sign-off request
        sprint.SignOffRequestedAt = DateTime.UtcNow.AddHours(-5);
        await _db.SaveChangesAsync();

        var stale = await _sprintService.GetTimedOutSignOffSprintsAsync(TimeSpan.FromHours(4));

        Assert.Single(stale);
        Assert.Equal(sprint.Id, stale[0].Id);
    }

    [Fact]
    public async Task GetTimedOutSignOffSprints_IgnoresRecentSignOff()
    {
        await CreateSprintInSignOff();

        var stale = await _sprintService.GetTimedOutSignOffSprintsAsync(TimeSpan.FromHours(4));

        Assert.Empty(stale);
    }

    [Fact]
    public async Task GetOverdueSprints_FindsOldSprints()
    {
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);

        sprint.CreatedAt = DateTime.UtcNow.AddHours(-50);
        await _db.SaveChangesAsync();

        var overdue = await _sprintService.GetOverdueSprintsAsync(TimeSpan.FromHours(48));

        Assert.Single(overdue);
        Assert.Equal(sprint.Id, overdue[0].Id);
    }

    [Fact]
    public async Task GetOverdueSprints_IgnoresRecentSprints()
    {
        await _sprintService.CreateSprintAsync(TestWorkspace);

        var overdue = await _sprintService.GetOverdueSprintsAsync(TimeSpan.FromHours(48));

        Assert.Empty(overdue);
    }

    [Fact]
    public async Task GetOverdueSprints_IgnoresCompletedSprints()
    {
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        sprint.CreatedAt = DateTime.UtcNow.AddHours(-50);
        await _sprintService.CompleteSprintAsync(sprint.Id, force: true);

        var overdue = await _sprintService.GetOverdueSprintsAsync(TimeSpan.FromHours(48));

        Assert.Empty(overdue);
    }

    // ── SprintTimeoutService ─────────────────────────────────────

    [Fact]
    public async Task CheckOnce_AutoRejectsTimedOutSignOff()
    {
        var sprint = await CreateSprintInSignOff();
        sprint.SignOffRequestedAt = DateTime.UtcNow.AddMinutes(-10);
        await _db.SaveChangesAsync();

        var service = CreateTimeoutService(signOffTimeoutMinutes: 5);

        await service.CheckOnceAsync();

        var updated = await _db.Sprints.FindAsync(sprint.Id);
        Assert.False(updated!.AwaitingSignOff);
        Assert.Equal("Active", updated.Status);
    }

    [Fact]
    public async Task CheckOnce_AutoCancelsOverdueSprint()
    {
        var sprint = await _sprintService.CreateSprintAsync("/tmp/ws2");
        sprint.CreatedAt = DateTime.UtcNow.AddHours(-3);
        await _db.SaveChangesAsync();

        var service = CreateTimeoutService(maxSprintDurationHours: 2);

        await service.CheckOnceAsync();

        var updated = await _db.Sprints.FindAsync(sprint.Id);
        Assert.Equal("Cancelled", updated!.Status);
    }

    [Fact]
    public async Task CheckOnce_DoesNothingWhenNothingStale()
    {
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);

        var service = CreateTimeoutService();

        await service.CheckOnceAsync();

        var updated = await _db.Sprints.FindAsync(sprint.Id);
        Assert.Equal("Active", updated!.Status);
    }

    [Fact]
    public async Task CheckOnce_HandlesMultipleWorkspaces()
    {
        var s1 = await CreateSprintInSignOff();
        s1.SignOffRequestedAt = DateTime.UtcNow.AddMinutes(-10);
        await _db.SaveChangesAsync();

        var s2 = await _sprintService.CreateSprintAsync("/tmp/ws2");
        s2.CreatedAt = DateTime.UtcNow.AddHours(-100);
        await _db.SaveChangesAsync();

        var service = CreateTimeoutService(signOffTimeoutMinutes: 5, maxSprintDurationHours: 48);

        await service.CheckOnceAsync();

        var u1 = await _db.Sprints.FindAsync(s1.Id);
        Assert.False(u1!.AwaitingSignOff);
        Assert.Equal("Active", u1.Status);

        var u2 = await _db.Sprints.FindAsync(s2.Id);
        Assert.Equal("Cancelled", u2!.Status);
    }

    // ── Settings validation ──────────────────────────────────────

    [Theory]
    [InlineData(0, 48, 5)]
    [InlineData(240, 0, 5)]
    [InlineData(240, 48, 0)]
    [InlineData(-1, 48, 5)]
    public void Settings_Validate_ThrowsOnInvalidValues(int signOff, int maxDuration, int interval)
    {
        var settings = new SprintTimeoutSettings
        {
            SignOffTimeoutMinutes = signOff,
            MaxSprintDurationHours = maxDuration,
            CheckIntervalMinutes = interval,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
    }

    [Fact]
    public void Settings_Validate_AcceptsValidDefaults()
    {
        var settings = new SprintTimeoutSettings();
        var ex = Record.Exception(() => settings.Validate());
        Assert.Null(ex);
    }

    // ── Helper: create timeout service with test scope factory ───

    private SprintTimeoutService CreateTimeoutService(
        int signOffTimeoutMinutes = 240,
        int maxSprintDurationHours = 48)
    {
        var settings = new SprintTimeoutSettings
        {
            Enabled = true,
            SignOffTimeoutMinutes = signOffTimeoutMinutes,
            MaxSprintDurationHours = maxSprintDurationHours,
            CheckIntervalMinutes = 1,
        };

        // Build a minimal service provider that resolves SprintService
        // using the same in-memory DB.
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<IActivityBroadcaster>(sp => sp.GetRequiredService<ActivityBroadcaster>());
        services.AddSingleton<ILogger<SprintService>>(NullLogger<SprintService>.Instance);
        services.AddSingleton<ILogger<SprintStageService>>(NullLogger<SprintStageService>.Instance);
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<SprintService>(sp =>
            new SprintService(
                sp.GetRequiredService<AgentAcademyDbContext>(),
                sp.GetRequiredService<ActivityBroadcaster>(),
                sp.GetRequiredService<SystemSettingsService>(),
                sp.GetRequiredService<ILogger<SprintService>>()));
        services.AddScoped<ISprintService>(sp => sp.GetRequiredService<SprintService>());
        services.AddScoped<SprintStageService>(sp =>
            new SprintStageService(
                sp.GetRequiredService<AgentAcademyDbContext>(),
                sp.GetRequiredService<ActivityBroadcaster>(),
                sp.GetRequiredService<ILogger<SprintStageService>>()));
        services.AddScoped<ISprintStageService>(sp => sp.GetRequiredService<SprintStageService>());
        var provider = services.BuildServiceProvider();

        return new SprintTimeoutService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(settings),
            NullLogger<SprintTimeoutService>.Instance);
    }
}
