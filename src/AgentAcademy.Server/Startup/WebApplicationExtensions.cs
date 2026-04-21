using AgentAcademy.Server.Auth;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.HealthChecks;
using AgentAcademy.Server.Hubs;
using AgentAcademy.Server.Middleware;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Notifications.Contracts;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.HttpOverrides;
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
        var initialization = scope.ServiceProvider.GetRequiredService<IInitializationService>();
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
            var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentOrchestrator>();
            await orchestrator.ReconstructQueueAsync();
        }

        // 5. Crash recovery — posts system messages, so must come after queue reconstruction
        if (CrashRecoveryService.CurrentCrashDetected)
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentOrchestrator>();
            await orchestrator.HandleStartupRecoveryAsync(mainRoomId);
        }

        // 6. Configure rate limiter from persisted settings
        var settingsService = scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();
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
            var kickoff = scope.ServiceProvider.GetRequiredService<IConversationKickoffService>();
            await kickoff.TryKickoffAsync(mainRoomId, activeWorkspace);
        }

        // 8. Seed default forge methodology into catalog
        await app.SeedDefaultMethodologyAsync();
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
        var notificationManager = app.Services.GetRequiredService<INotificationManager>();
        notificationManager.RegisterProvider(app.Services.GetRequiredService<ConsoleNotificationProvider>());
        notificationManager.RegisterProvider(app.Services.GetRequiredService<DiscordNotificationProvider>());
        notificationManager.RegisterProvider(app.Services.GetRequiredService<SlackNotificationProvider>());
    }

    /// <summary>
    /// Emits a single startup log line summarizing which authentication schemes
    /// are active and — crucially — why a scheme is disabled when its config is
    /// missing. Operators running via docker-compose previously had no signal
    /// that a misspelled env var had silently disabled consultant auth; this
    /// closes that gap (issue #61).
    /// </summary>
    public static void LogAuthConfiguration(this WebApplication app, AppAuthSetup authSetup)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();

        logger.LogInformation(
            "Auth configuration: GitHub OAuth={GitHubState}, Consultant API={ConsultantState}",
            authSetup.GitHubAuthEnabled ? "enabled" : "disabled (GitHub:ClientId and/or GitHub:ClientSecret not set)",
            authSetup.ConsultantAuthEnabled ? "enabled" : "disabled (ConsultantApi:SharedSecret not set — export ConsultantApi__SharedSecret to enable)");
    }

    /// <summary>
    /// Configures the HTTP middleware + endpoint pipeline.
    /// Keeps Program.cs focused on composition while preserving ordering.
    /// </summary>
    public static void ConfigureHttpPipeline(
        this WebApplication app,
        AppAuthSetup authSetup,
        ConsultantRateLimitSettings consultantRateLimits)
    {
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        });

        // Serve SPA static files in production (no-op when wwwroot doesn't exist)
        var wwwrootPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        if (Directory.Exists(wwwrootPath))
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
        }

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseCors();

        // CSRF protection runs AFTER CORS (so preflight OPTIONS is already
        // short-circuited) and BEFORE authentication — we only need to check
        // for the cookie on the request, not a fully-materialized principal.
        app.UseMiddleware<CsrfProtectionMiddleware>();

        if (authSetup.AnyAuthEnabled)
        {
            app.UseAuthentication();

            if (authSetup.ConsultantAuthEnabled && consultantRateLimits.Enabled)
            {
                app.UseRateLimiter();
            }

            app.UseAuthorization();
        }

        if (authSetup.GitHubAuthEnabled)
        {
            app.UseCopilotTokenRefresh();
        }

        app.MapControllers();
        var activityHubEndpoint = app.MapHub<ActivityHub>("/hubs/activity");
        if (!authSetup.AnyAuthEnabled)
        {
            activityHubEndpoint.AllowAnonymous();
        }

        app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            ResponseWriter = HealthCheckResponseWriter.WriteAsync,
            ResultStatusCodes =
            {
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy] = StatusCodes.Status200OK,
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = StatusCodes.Status200OK,
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        }).AllowAnonymous();

        if (Directory.Exists(wwwrootPath))
        {
            app.MapFallback(async context =>
            {
                if (!SpaFallbackHelper.ShouldServeIndex(context.Request.Path.Value))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                context.Response.ContentType = "text/html";
                await context.Response.SendFileAsync(
                    app.Environment.WebRootFileProvider.GetFileInfo("index.html"));
            });
        }
    }
}
