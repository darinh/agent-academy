using AgentAcademy.Server.Hubs;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services;

/// <summary>
/// DI registration for hosted background services — SignalR broadcasting,
/// notification delivery, auth monitoring, PR sync, and sprint scheduling.
/// Extracted from Program.cs to reduce churn.
/// </summary>
public static class BackgroundServiceExtensions
{
    /// <summary>
    /// Registers all <see cref="IHostedService"/> implementations and their
    /// associated configuration sections.
    /// </summary>
    public static IServiceCollection AddBackgroundServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        // SignalR hub broadcaster — bridges ActivityBroadcaster → SignalR clients
        services.AddHostedService<ActivityHubBroadcaster>();

        // Notification broadcaster — bridges ActivityBroadcaster → NotificationManager
        services.AddHostedService<ActivityNotificationBroadcaster>();

        // Notification config restore — restores saved provider configs from DB on startup
        services.AddHostedService<NotificationRestoreService>();

        // Auth health probe — checks GitHub /user every 5 minutes
        services.AddHostedService<CopilotAuthMonitorService>();

        // PR status sync — polls GitHub every 2 minutes for PR state changes
        services.AddHostedService<PullRequestSyncService>();

        // Sprint timeout checking — auto-rejects stale sign-offs, auto-cancels overdue sprints
        services.Configure<SprintTimeoutSettings>(
            configuration.GetSection(SprintTimeoutSettings.SectionName));
        services.AddHostedService<SprintTimeoutService>();

        // Sprint scheduler — cron-based periodic sprint creation
        services.Configure<SprintSchedulerSettings>(
            configuration.GetSection(SprintSchedulerSettings.SectionName));
        services.AddHostedService<SprintSchedulerService>();

        // Keep hosted-service faults isolated from host lifecycle so transient
        // background errors do not crash the server process.
        services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
        });

        return services;
    }
}
