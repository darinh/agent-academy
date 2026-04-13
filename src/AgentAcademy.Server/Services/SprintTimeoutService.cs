using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Background service that periodically checks for stale sprints and
/// auto-rejects timed-out sign-offs or auto-cancels overdue sprints.
/// </summary>
internal sealed class SprintTimeoutService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SprintTimeoutSettings _settings;
    private readonly ILogger<SprintTimeoutService> _logger;

    public SprintTimeoutService(
        IServiceScopeFactory scopeFactory,
        IOptions<SprintTimeoutSettings> settings,
        ILogger<SprintTimeoutService> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _settings.Validate();
        _logger = logger;
    }

    internal TimeSpan CheckInterval => TimeSpan.FromMinutes(_settings.CheckIntervalMinutes);
    internal TimeSpan SignOffTimeout => TimeSpan.FromMinutes(_settings.SignOffTimeoutMinutes);
    internal TimeSpan MaxSprintDuration => TimeSpan.FromHours(_settings.MaxSprintDurationHours);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Sprint timeout checking is disabled");
            return;
        }

        // Brief startup delay to let the rest of the app initialize
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        _logger.LogInformation(
            "Sprint timeout service started — sign-off timeout: {SignOff}min, max duration: {Max}h, interval: {Interval}min",
            _settings.SignOffTimeoutMinutes, _settings.MaxSprintDurationHours, _settings.CheckIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckOnceAsync(stoppingToken);

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

    internal async Task CheckOnceAsync(CancellationToken ct = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sprintService = scope.ServiceProvider.GetRequiredService<SprintService>();
            var stageService = scope.ServiceProvider.GetRequiredService<SprintStageService>();

            await CheckSignOffTimeoutsAsync(sprintService, stageService, ct);
            await CheckSprintDurationTimeoutsAsync(sprintService, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — don't log
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sprint timeout check failed");
        }
    }

    private async Task CheckSignOffTimeoutsAsync(SprintService sprintService, SprintStageService stageService, CancellationToken ct)
    {
        var stale = await sprintService.GetTimedOutSignOffSprintsAsync(SignOffTimeout, ct);
        if (stale.Count == 0) return;

        _logger.LogInformation("Found {Count} sprint(s) with timed-out sign-off", stale.Count);

        foreach (var sprint in stale)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await stageService.TimeOutSignOffAsync(sprint.Id, ct);
                _logger.LogInformation(
                    "Auto-rejected sign-off for sprint #{Number} ({Id})",
                    sprint.Number, sprint.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to auto-reject sign-off for sprint #{Number} ({Id})",
                    sprint.Number, sprint.Id);
            }
        }
    }

    private async Task CheckSprintDurationTimeoutsAsync(SprintService sprintService, CancellationToken ct)
    {
        var overdue = await sprintService.GetOverdueSprintsAsync(MaxSprintDuration, ct);
        if (overdue.Count == 0) return;

        _logger.LogInformation("Found {Count} overdue sprint(s)", overdue.Count);

        foreach (var sprint in overdue)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await sprintService.TimeOutSprintAsync(sprint.Id, ct);
                _logger.LogInformation(
                    "Auto-cancelled overdue sprint #{Number} ({Id})",
                    sprint.Number, sprint.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to auto-cancel overdue sprint #{Number} ({Id})",
                    sprint.Number, sprint.Id);
            }
        }
    }
}
