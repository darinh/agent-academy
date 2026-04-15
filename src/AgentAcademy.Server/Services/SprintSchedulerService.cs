using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Background service that evaluates sprint schedule cron expressions and
/// creates new sprints when they become due. Follows the same IServiceScopeFactory
/// pattern as <see cref="SprintTimeoutService"/>.
/// </summary>
internal sealed class SprintSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SprintSchedulerSettings _settings;
    private readonly ILogger<SprintSchedulerService> _logger;

    public SprintSchedulerService(
        IServiceScopeFactory scopeFactory,
        IOptions<SprintSchedulerSettings> settings,
        ILogger<SprintSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _settings.Validate();
        _logger = logger;
    }

    internal TimeSpan CheckInterval => TimeSpan.FromSeconds(_settings.CheckIntervalSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Sprint scheduler is disabled");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        _logger.LogInformation(
            "Sprint scheduler started — check interval: {Interval}s",
            _settings.CheckIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await EvaluateSchedulesAsync(stoppingToken);

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task EvaluateSchedulesAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var sprintService = scope.ServiceProvider.GetRequiredService<ISprintService>();

            var now = DateTime.UtcNow;
            var dueSchedules = await db.SprintSchedules
                .Where(s => s.Enabled && s.NextRunAtUtc != null && s.NextRunAtUtc <= now)
                .ToListAsync(ct);

            foreach (var schedule in dueSchedules)
            {
                await EvaluateOneAsync(schedule, sprintService, db, now, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating sprint schedules");
        }
    }

    private async Task EvaluateOneAsync(
        SprintScheduleEntity schedule,
        ISprintService sprintService,
        AgentAcademyDbContext db,
        DateTime now,
        CancellationToken ct)
    {
        schedule.LastEvaluatedAt = now;

        try
        {
            var sprint = await sprintService.CreateSprintAsync(
                schedule.WorkspacePath, trigger: "scheduled");

            schedule.LastTriggeredAt = now;
            schedule.LastOutcome = "started";
            _logger.LogInformation(
                "Scheduled sprint {SprintNumber} created for workspace {Workspace}",
                sprint.Number, schedule.WorkspacePath);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already has an active sprint"))
        {
            schedule.LastOutcome = "skipped_active";
            _logger.LogDebug(
                "Skipped scheduled sprint for {Workspace}: active sprint exists",
                schedule.WorkspacePath);
        }
        catch (Exception ex)
        {
            schedule.LastOutcome = "error";
            _logger.LogWarning(ex,
                "Failed to create scheduled sprint for {Workspace}",
                schedule.WorkspacePath);
        }

        schedule.NextRunAtUtc = ComputeNextRun(schedule.CronExpression, schedule.TimeZoneId, now);
        schedule.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Computes the next UTC occurrence from a 5-field cron expression in the given timezone.
    /// Returns null if the expression is invalid or produces no future occurrence.
    /// </summary>
    internal static DateTime? ComputeNextRun(string cronExpression, string timeZoneId, DateTime fromUtc)
    {
        try
        {
            var cron = CronExpression.Parse(cronExpression, CronFormat.Standard);
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return cron.GetNextOccurrence(fromUtc, tz);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Validates a cron expression. Returns true if it parses as a valid 5-field cron.
    /// </summary>
    internal static bool IsValidCron(string cronExpression)
    {
        try
        {
            CronExpression.Parse(cronExpression, CronFormat.Standard);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
