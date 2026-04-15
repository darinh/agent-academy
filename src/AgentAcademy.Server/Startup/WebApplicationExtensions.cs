using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Startup;

/// <summary>
/// Application initialization and lifecycle extensions for <see cref="WebApplication"/>.
/// Extracted from Program.cs — the startup sequence has real ordering invariants;
/// see inline comments for why each step must come where it does.
/// </summary>
public static class WebApplicationExtensions
{
    /// <summary>
    /// Runs post-build initialization: DB migration, default state creation,
    /// queue reconstruction, crash recovery, and rate limiter configuration.
    /// Must run BEFORE <c>app.Run()</c>.
    /// </summary>
    public static async Task InitializeAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();

        // 1. Migrate DB first — everything below depends on schema being current
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.Migrate();

        // 2. Initialize startup state (create default room + agent locations)
        var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
        await initialization.InitializeAsync();

        // 3. If a workspace is already active, ensure it has a default room
        var catalog = scope.ServiceProvider.GetRequiredService<IAgentCatalog>();
        var rooms = scope.ServiceProvider.GetRequiredService<IRoomService>();
        var workspaceRooms = scope.ServiceProvider.GetRequiredService<IWorkspaceRoomService>();
        var mainRoomId = catalog.DefaultRoomId;
        var activeWorkspace = await rooms.GetActiveWorkspacePathAsync();
        if (activeWorkspace is not null)
        {
            mainRoomId = await workspaceRooms.EnsureDefaultRoomForWorkspaceAsync(activeWorkspace);
        }

        // 4. Re-enqueue rooms with unanswered human messages.
        //    Must run BEFORE crash recovery, which posts system messages that would
        //    mask pending human messages from the reconstruction query.
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<AgentOrchestrator>();
            await orchestrator.ReconstructQueueAsync();
        }

        // 5. Crash recovery — posts system messages, so must come after queue reconstruction
        if (CrashRecoveryService.CurrentCrashDetected)
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<AgentOrchestrator>();
            await orchestrator.HandleStartupRecoveryAsync(mainRoomId);
        }

        // 6. Configure rate limiter from persisted settings
        var settingsService = scope.ServiceProvider.GetRequiredService<SystemSettingsService>();
        var rateLimiter = app.Services.GetRequiredService<CommandRateLimiter>();
        var maxCmds = await settingsService.GetRateLimitMaxCommandsAsync();
        var windowSecs = await settingsService.GetRateLimitWindowSecondsAsync();
        rateLimiter.Configure(maxCmds, windowSecs);

        // 7. Conversation kickoff — if the main room has no human/agent messages yet,
        //    post a system kickoff message and trigger orchestration so agents begin
        //    collaborating without waiting for a manual human message.
        //    Idempotent: skips if agents have already spoken or if crash recovery ran.
        if (!CrashRecoveryService.CurrentCrashDetected)
        {
            var kickoff = scope.ServiceProvider.GetRequiredService<ConversationKickoffService>();
            await kickoff.TryKickoffAsync(mainRoomId, activeWorkspace);
        }
    }

    /// <summary>
    /// Registers a shutdown hook that records the server instance shutdown timestamp
    /// and exit code in the database for crash detection on next startup.
    /// </summary>
    public static void ConfigureShutdownHook(this WebApplication app)
    {
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(() =>
        {
            var logger = app.Services.GetRequiredService<ILogger<Program>>();
            logger.LogWarning("Server shutting down (exit code: {ExitCode})", Environment.ExitCode);

            try
            {
                using var shutdownScope = app.Services.CreateScope();
                var db = shutdownScope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
                var instanceId = CrashRecoveryService.CurrentInstanceId;
                if (instanceId is not null)
                {
                    var instance = db.ServerInstances.Find(instanceId);
                    if (instance is not null)
                    {
                        instance.ShutdownAt = DateTime.UtcNow;
                        instance.ExitCode = Environment.ExitCode;
                        db.SaveChanges();
                        logger.LogInformation("Server instance {InstanceId} shutdown recorded", instanceId);
                    }
                }
            }
            catch (Exception ex)
            {
                var shutdownLogger = app.Services.GetRequiredService<ILogger<Program>>();
                shutdownLogger.LogError(ex, "Failed to record server instance shutdown");
            }
        });
    }

    /// <summary>
    /// Registers built-in notification providers synchronously.
    /// Must complete before hosted services start (they use the providers).
    /// </summary>
    public static void RegisterNotificationProviders(this WebApplication app)
    {
        var notificationManager = app.Services.GetRequiredService<NotificationManager>();
        notificationManager.RegisterProvider(app.Services.GetRequiredService<ConsoleNotificationProvider>());
        notificationManager.RegisterProvider(app.Services.GetRequiredService<DiscordNotificationProvider>());
        notificationManager.RegisterProvider(app.Services.GetRequiredService<SlackNotificationProvider>());
    }
}
