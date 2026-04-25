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
/// Tests for SprintTimeoutService: the background service itself,
/// its settings validation, and end-to-end timeout behavior via CheckOnceAsync.
/// Lower-level timeout tests (sign-off auto-reject, sprint auto-cancel, queries)
/// live in SprintTimeoutTests.cs.
/// </summary>
public class SprintTimeoutServiceTests : IDisposable
{
    private const string TestWorkspace = "/workspace/timeout-test";

    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly SprintService _sprintService;
    private readonly SprintStageService _sprintStageService;
    private readonly SprintArtifactService _artifactService;

    public SprintTimeoutServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        var broadcaster = new ActivityBroadcaster();
        _sprintService = new SprintService(_db, broadcaster, new SystemSettingsService(_db), NullLogger<SprintService>.Instance);
        _sprintStageService = new SprintStageService(_db, broadcaster, NullLogger<SprintStageService>.Instance);
        _artifactService = new SprintArtifactService(_db, broadcaster, NullLogger<SprintArtifactService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private SprintTimeoutService CreateService(
        int signOffTimeoutMinutes = 240,
        int maxSprintDurationHours = 48,
        int checkIntervalMinutes = 1,
        bool enabled = true)
    {
        var settings = new SprintTimeoutSettings
        {
            Enabled = enabled,
            SignOffTimeoutMinutes = signOffTimeoutMinutes,
            MaxSprintDurationHours = maxSprintDurationHours,
            CheckIntervalMinutes = checkIntervalMinutes,
        };

        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<IActivityBroadcaster>(sp => sp.GetRequiredService<ActivityBroadcaster>());
        services.AddSingleton<ILogger<SprintService>>(NullLogger<SprintService>.Instance);
        services.AddSingleton<ILogger<SprintStageService>>(NullLogger<SprintStageService>.Instance);
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<ISystemSettingsService>(sp => sp.GetRequiredService<SystemSettingsService>());
        services.AddScoped<SprintService>(sp =>
            new SprintService(
                sp.GetRequiredService<AgentAcademyDbContext>(),
                sp.GetRequiredService<ActivityBroadcaster>(),
                sp.GetRequiredService<SystemSettingsService>(),
                sp.GetRequiredService<ILogger<SprintService>>()));
        services.AddScoped<ISprintService>(sp => sp.GetRequiredService<SprintService>());
        services.AddScoped<ISprintStageService>(sp => sp.GetRequiredService<SprintStageService>());
        services.AddScoped<SprintStageService>(sp =>
            new SprintStageService(
                sp.GetRequiredService<AgentAcademyDbContext>(),
                sp.GetRequiredService<ActivityBroadcaster>(),
                sp.GetRequiredService<ILogger<SprintStageService>>()));
        var provider = services.BuildServiceProvider();

        return new SprintTimeoutService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(settings),
            NullLogger<SprintTimeoutService>.Instance);
    }

    private async Task<SprintEntity> CreateSprintInSignOff()
    {
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        await _artifactService.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument",
            """{"Title":"T","Description":"D","InScope":[],"OutOfScope":[]}""");
        await _sprintStageService.AdvanceStageAsync(sprint.Id);
        return sprint;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Settings Validation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Settings_DefaultValues_AreValid()
    {
        var settings = new SprintTimeoutSettings();
        var ex = Record.Exception(() => settings.Validate());
        Assert.Null(ex);
    }

    [Fact]
    public void Settings_Validate_ThrowsOnZeroCheckInterval()
    {
        var settings = new SprintTimeoutSettings { CheckIntervalMinutes = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
    }

    [Fact]
    public void Settings_Validate_ThrowsOnNegativeCheckInterval()
    {
        var settings = new SprintTimeoutSettings { CheckIntervalMinutes = -5 };
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
    }

    [Fact]
    public void Settings_Validate_ThrowsOnZeroSignOffTimeout()
    {
        var settings = new SprintTimeoutSettings { SignOffTimeoutMinutes = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
    }

    [Fact]
    public void Settings_Validate_ThrowsOnNegativeSignOffTimeout()
    {
        var settings = new SprintTimeoutSettings { SignOffTimeoutMinutes = -1 };
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
    }

    [Fact]
    public void Settings_Validate_ThrowsOnZeroMaxDuration()
    {
        var settings = new SprintTimeoutSettings { MaxSprintDurationHours = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
    }

    [Fact]
    public void Settings_Validate_ThrowsOnNegativeMaxDuration()
    {
        var settings = new SprintTimeoutSettings { MaxSprintDurationHours = -10 };
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
    }

    [Fact]
    public void Settings_DefaultEnabled_IsTrue()
    {
        var settings = new SprintTimeoutSettings();
        Assert.True(settings.Enabled);
    }

    [Fact]
    public void Settings_DefaultSignOffTimeoutMinutes_Is240()
    {
        var settings = new SprintTimeoutSettings();
        Assert.Equal(240, settings.SignOffTimeoutMinutes);
    }

    [Fact]
    public void Settings_DefaultMaxSprintDurationHours_Is48()
    {
        var settings = new SprintTimeoutSettings();
        Assert.Equal(48, settings.MaxSprintDurationHours);
    }

    [Fact]
    public void Settings_DefaultCheckIntervalMinutes_Is5()
    {
        var settings = new SprintTimeoutSettings();
        Assert.Equal(5, settings.CheckIntervalMinutes);
    }

    // ═══════════════════════════════════════════════════════════════
    //  TimeSpan Property Accessors
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void CheckInterval_ReturnsCorrectTimeSpan()
    {
        var service = CreateService(checkIntervalMinutes: 7);
        Assert.Equal(TimeSpan.FromMinutes(7), service.CheckInterval);
    }

    [Fact]
    public void SignOffTimeout_ReturnsCorrectTimeSpan()
    {
        var service = CreateService(signOffTimeoutMinutes: 120);
        Assert.Equal(TimeSpan.FromMinutes(120), service.SignOffTimeout);
    }

    [Fact]
    public void MaxSprintDuration_ReturnsCorrectTimeSpan()
    {
        var service = CreateService(maxSprintDurationHours: 72);
        Assert.Equal(TimeSpan.FromHours(72), service.MaxSprintDuration);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ExecuteAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_ReturnsImmediately_WhenDisabled()
    {
        // Seed a sprint that would normally be acted on by the timeout service
        var sprint = await CreateSprintInSignOff();
        sprint.SignOffRequestedAt = DateTime.UtcNow.AddHours(-24);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var service = CreateService(enabled: false, signOffTimeoutMinutes: 1);
        using var cts = new CancellationTokenSource();

        await service.StartAsync(cts.Token);
        await Task.Delay(200);
        await service.StopAsync(CancellationToken.None);

        // The sprint should NOT have been touched because the service is disabled
        _db.ChangeTracker.Clear();
        var unchanged = await _db.Sprints.FindAsync(sprint.Id);
        Assert.True(unchanged!.AwaitingSignOff, "Sign-off should still be pending — disabled service must not process timeouts");
    }

    [Fact]
    public async Task ExecuteAsync_StopsOnCancellation()
    {
        var service = CreateService();
        using var cts = new CancellationTokenSource();

        await service.StartAsync(cts.Token);

        // Cancel quickly — should exit gracefully
        cts.Cancel();
        await Task.Delay(200);

        // StopAsync should complete without throwing
        var ex = await Record.ExceptionAsync(() => service.StopAsync(CancellationToken.None));
        Assert.Null(ex);
    }

    // ═══════════════════════════════════════════════════════════════
    //  CheckOnceAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckOnce_NoOp_WhenNoSprints()
    {
        var service = CreateService();

        // Should not throw
        var ex = await Record.ExceptionAsync(() => service.CheckOnceAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task CheckOnce_NoOp_WhenSprintsAreRecent()
    {
        var service = CreateService();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);

        await service.CheckOnceAsync();

        _db.ChangeTracker.Clear();
        var updated = await _db.Sprints.FindAsync(sprint.Id);
        Assert.Equal("Active", updated!.Status);
    }

    [Fact]
    public async Task CheckOnce_AutoRejectsTimedOutSignOff()
    {
        var sprint = await CreateSprintInSignOff();
        sprint.SignOffRequestedAt = DateTime.UtcNow.AddMinutes(-10);
        await _db.SaveChangesAsync();

        var service = CreateService(signOffTimeoutMinutes: 5);
        await service.CheckOnceAsync();

        _db.ChangeTracker.Clear();
        var updated = await _db.Sprints.FindAsync(sprint.Id);
        Assert.False(updated!.AwaitingSignOff);
        Assert.Equal("Active", updated.Status);
    }

    [Fact]
    public async Task CheckOnce_DoesNotRejectRecentSignOff()
    {
        var sprint = await CreateSprintInSignOff();
        // SignOffRequestedAt was set to ~now by AdvanceStageAsync

        var service = CreateService(signOffTimeoutMinutes: 60);
        await service.CheckOnceAsync();

        _db.ChangeTracker.Clear();
        var updated = await _db.Sprints.FindAsync(sprint.Id);
        Assert.True(updated!.AwaitingSignOff);
    }

    [Fact]
    public async Task CheckOnce_AutoCancelsOverdueSprint()
    {
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        sprint.CreatedAt = DateTime.UtcNow.AddHours(-50);
        await _db.SaveChangesAsync();

        var service = CreateService(maxSprintDurationHours: 48);
        await service.CheckOnceAsync();

        _db.ChangeTracker.Clear();
        var updated = await _db.Sprints.FindAsync(sprint.Id);
        Assert.Equal("Cancelled", updated!.Status);
    }

    [Fact]
    public async Task CheckOnce_DoesNotCancelRecentSprint()
    {
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        // CreatedAt is ~now

        var service = CreateService(maxSprintDurationHours: 48);
        await service.CheckOnceAsync();

        _db.ChangeTracker.Clear();
        var updated = await _db.Sprints.FindAsync(sprint.Id);
        Assert.Equal("Active", updated!.Status);
    }

    [Fact]
    public async Task CheckOnce_HandlesBothTimeoutsInSingleCheck()
    {
        // Sprint 1: timed-out sign-off
        var s1 = await CreateSprintInSignOff();
        s1.SignOffRequestedAt = DateTime.UtcNow.AddMinutes(-10);
        await _db.SaveChangesAsync();

        // Sprint 2: overdue
        var s2 = await _sprintService.CreateSprintAsync("/workspace/other");
        s2.CreatedAt = DateTime.UtcNow.AddHours(-100);
        await _db.SaveChangesAsync();

        var service = CreateService(signOffTimeoutMinutes: 5, maxSprintDurationHours: 48);
        await service.CheckOnceAsync();

        _db.ChangeTracker.Clear();
        var u1 = await _db.Sprints.FindAsync(s1.Id);
        Assert.False(u1!.AwaitingSignOff);
        Assert.Equal("Active", u1.Status);

        var u2 = await _db.Sprints.FindAsync(s2.Id);
        Assert.Equal("Cancelled", u2!.Status);
    }

    [Fact]
    public async Task CheckOnce_DoesNotCancelAlreadyCancelledSprint()
    {
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        sprint.CreatedAt = DateTime.UtcNow.AddHours(-100);
        await _sprintService.CancelSprintAsync(sprint.Id);
        _db.ChangeTracker.Clear();

        var service = CreateService(maxSprintDurationHours: 48);

        // Should not throw even though sprint is overdue and already cancelled
        var ex = await Record.ExceptionAsync(() => service.CheckOnceAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task CheckOnce_DoesNotCancelCompletedSprint()
    {
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        sprint.CreatedAt = DateTime.UtcNow.AddHours(-100);
        await _sprintService.CompleteSprintAsync(sprint.Id, force: true);
        _db.ChangeTracker.Clear();

        var service = CreateService(maxSprintDurationHours: 48);
        await service.CheckOnceAsync();

        _db.ChangeTracker.Clear();
        var updated = await _db.Sprints.FindAsync(sprint.Id);
        Assert.Equal("Completed", updated!.Status);
    }

    [Fact]
    public async Task CheckOnce_HandlesExceptionsGracefully()
    {
        // Dispose the DB to cause exceptions in the scoped service
        var badConnection = new SqliteConnection("Data Source=:memory:");
        badConnection.Open();
        var badOptions = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(badConnection).Options;
        var badDb = new AgentAcademyDbContext(badOptions);
        badDb.Database.EnsureCreated();

        var settings = new SprintTimeoutSettings
        {
            Enabled = true,
            SignOffTimeoutMinutes = 5,
            MaxSprintDurationHours = 1,
            CheckIntervalMinutes = 1,
        };

        // Build a service provider that will fail on scope creation
        var services = new ServiceCollection();
        services.AddSingleton(badDb);
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<IActivityBroadcaster>(sp => sp.GetRequiredService<ActivityBroadcaster>());
        services.AddSingleton<ILogger<SprintService>>(NullLogger<SprintService>.Instance);
        services.AddSingleton<ILogger<SprintStageService>>(NullLogger<SprintStageService>.Instance);
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<ISystemSettingsService>(sp => sp.GetRequiredService<SystemSettingsService>());
        services.AddScoped<SprintService>(sp =>
            new SprintService(
                sp.GetRequiredService<AgentAcademyDbContext>(),
                sp.GetRequiredService<ActivityBroadcaster>(),
                sp.GetRequiredService<SystemSettingsService>(),
                sp.GetRequiredService<ILogger<SprintService>>()));
        services.AddScoped<ISprintService>(sp => sp.GetRequiredService<SprintService>());
        services.AddScoped<ISprintStageService>(sp => sp.GetRequiredService<SprintStageService>());
        services.AddScoped<SprintStageService>(sp =>
            new SprintStageService(
                sp.GetRequiredService<AgentAcademyDbContext>(),
                sp.GetRequiredService<ActivityBroadcaster>(),
                sp.GetRequiredService<ILogger<SprintStageService>>()));
        var provider = services.BuildServiceProvider();

        // Close the connection to cause errors on query
        badDb.Dispose();
        badConnection.Dispose();

        var service = new SprintTimeoutService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(settings),
            NullLogger<SprintTimeoutService>.Instance);

        // Should log warning but not throw
        var ex = await Record.ExceptionAsync(() => service.CheckOnceAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task CheckOnce_RespectsCancellationToken()
    {
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Should not throw — cancellation is handled gracefully
        var ex = await Record.ExceptionAsync(() => service.CheckOnceAsync(cts.Token));
        Assert.Null(ex);
    }

    [Fact]
    public async Task CheckOnce_MultipleSprintsWithTimedOutSignOffs()
    {
        var s1 = await CreateSprintInSignOff();
        s1.SignOffRequestedAt = DateTime.UtcNow.AddHours(-5);
        await _db.SaveChangesAsync();

        var s2Workspace = "/workspace/other";
        var s2 = await _sprintService.CreateSprintAsync(s2Workspace);
        await _artifactService.StoreArtifactAsync(s2.Id, "Intake", "RequirementsDocument",
            """{"Title":"T","Description":"D","InScope":[],"OutOfScope":[]}""");
        await _sprintStageService.AdvanceStageAsync(s2.Id);
        s2.SignOffRequestedAt = DateTime.UtcNow.AddHours(-5);
        await _db.SaveChangesAsync();

        var service = CreateService(signOffTimeoutMinutes: 60);
        await service.CheckOnceAsync();

        _db.ChangeTracker.Clear();
        var u1 = await _db.Sprints.FindAsync(s1.Id);
        var u2 = await _db.Sprints.FindAsync(s2.Id);
        Assert.False(u1!.AwaitingSignOff);
        Assert.False(u2!.AwaitingSignOff);
    }

    [Fact]
    public async Task CheckOnce_MultipleOverdueSprints()
    {
        var s1 = await _sprintService.CreateSprintAsync(TestWorkspace);
        s1.CreatedAt = DateTime.UtcNow.AddHours(-100);
        await _db.SaveChangesAsync();

        var s2 = await _sprintService.CreateSprintAsync("/workspace/other");
        s2.CreatedAt = DateTime.UtcNow.AddHours(-200);
        await _db.SaveChangesAsync();

        var service = CreateService(maxSprintDurationHours: 48);
        await service.CheckOnceAsync();

        _db.ChangeTracker.Clear();
        var u1 = await _db.Sprints.FindAsync(s1.Id);
        var u2 = await _db.Sprints.FindAsync(s2.Id);
        Assert.Equal("Cancelled", u1!.Status);
        Assert.Equal("Cancelled", u2!.Status);
    }

    [Fact]
    public async Task CheckOnce_LeavesActiveSprintUnchanged_WhenBelowThreshold()
    {
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        sprint.CreatedAt = DateTime.UtcNow.AddHours(-10);
        await _db.SaveChangesAsync();

        var service = CreateService(maxSprintDurationHours: 48);
        await service.CheckOnceAsync();

        _db.ChangeTracker.Clear();
        var updated = await _db.Sprints.FindAsync(sprint.Id);
        Assert.Equal("Active", updated!.Status);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Constructor Validation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Constructor_ThrowsOnInvalidSettings()
    {
        var settings = new SprintTimeoutSettings { CheckIntervalMinutes = 0 };

        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<IActivityBroadcaster>(sp => sp.GetRequiredService<ActivityBroadcaster>());
        services.AddSingleton<ILogger<SprintService>>(NullLogger<SprintService>.Instance);
        services.AddSingleton<ILogger<SprintStageService>>(NullLogger<SprintStageService>.Instance);
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<ISystemSettingsService>(sp => sp.GetRequiredService<SystemSettingsService>());
        services.AddScoped<SprintService>(sp =>
            new SprintService(
                sp.GetRequiredService<AgentAcademyDbContext>(),
                sp.GetRequiredService<ActivityBroadcaster>(),
                sp.GetRequiredService<SystemSettingsService>(),
                sp.GetRequiredService<ILogger<SprintService>>()));
        services.AddScoped<ISprintService>(sp => sp.GetRequiredService<SprintService>());
        services.AddScoped<ISprintStageService>(sp => sp.GetRequiredService<SprintStageService>());
        services.AddScoped<SprintStageService>(sp =>
            new SprintStageService(
                sp.GetRequiredService<AgentAcademyDbContext>(),
                sp.GetRequiredService<ActivityBroadcaster>(),
                sp.GetRequiredService<ILogger<SprintStageService>>()));
        var provider = services.BuildServiceProvider();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SprintTimeoutService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(settings),
                NullLogger<SprintTimeoutService>.Instance));
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(240, 48, 5)]
    [InlineData(999, 999, 999)]
    public void Constructor_AcceptsValidSettings(int signOff, int maxDuration, int interval)
    {
        var service = CreateService(signOff, maxDuration, interval);
        Assert.NotNull(service);
    }
}
