using AgentAcademy.Server.Auth;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Hubs;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Data Protection — explicit key persistence for encryption-at-rest
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentAcademy", "DataProtection-Keys")));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();

// CORS — required for SignalR WebSocket connections from the Vite dev server
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // Required for SignalR
    });
});

// Authentication — precompute config, then register schemes
var authSetup = AppAuthSetup.FromConfiguration(builder.Configuration);
builder.Services.AddAppAuthentication(authSetup);

// Flag for controllers to check
builder.Services.AddSingleton(new GitHubAuthOptions(authSetup.GitHubAuthEnabled, authSetup.GitHubFrontendUrl));

// Database
builder.Services.AddDbContext<AgentAcademyDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=agent-academy.db"));

// Agent catalog (singleton — loaded from Config/agents.json)
builder.Services.AddAgentCatalog();

// Activity broadcaster (singleton — shared across scoped service instances)
builder.Services.AddSingleton<ActivityBroadcaster>();

// Domain services (scoped — one per request, uses scoped DbContext)
builder.Services.AddDomainServices();

// Copilot token provider (singleton — captures OAuth token for SDK activation)
builder.Services.AddSingleton<CopilotTokenProvider>();
builder.Services.AddSingleton<TokenPersistenceService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TokenPersistenceService>());

// LLM usage tracking (singleton — captures AssistantUsageEvent from SDK)
builder.Services.AddSingleton<LlmUsageTracker>();
builder.Services.AddSingleton<AgentErrorTracker>();
builder.Services.AddSingleton<AgentQuotaService>();
builder.Services.AddSingleton<AgentAnalyticsService>();

// SDK tool calling — tool functions use IServiceScopeFactory for scoped service access
builder.Services.AddSingleton<AgentToolFunctions>();
builder.Services.AddSingleton<IAgentToolRegistry, AgentToolRegistry>();

// Agent execution — CopilotClientFactory manages client lifecycle;
// CopilotSessionPool manages session caching with TTL and send serialization;
// CopilotSdkSender handles retry logic and streamed response collection;
// CopilotExecutor coordinates them and owns auth-state and circuit breaker.
builder.Services.AddSingleton<CopilotClientFactory>();
builder.Services.AddSingleton<CopilotSessionPool>();
builder.Services.AddSingleton<CopilotSdkSender>();
builder.Services.AddSingleton<CopilotExecutor>();
builder.Services.AddSingleton<IAgentExecutor>(sp => sp.GetRequiredService<CopilotExecutor>());

builder.Services.AddHttpClient<ICopilotAuthProbe, GitHubCopilotAuthProbe>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AgentAcademy.AuthProbe/1.0");
});

// Spec manager (singleton — reads specs/ directory for prompt injection)
builder.Services.AddSingleton<SpecManager>();

// Project scanner (singleton — stateless directory scanner)
builder.Services.AddSingleton<ProjectScanner>();

// Git service (singleton — branch management for breakout rooms)
builder.Services.AddSingleton<GitService>();
builder.Services.AddSingleton<WorktreeService>();

// GitHub integration (singleton — PR creation via gh CLI)
builder.Services.AddSingleton<GitHubService>(sp =>
    new GitHubService(
        sp.GetRequiredService<ILogger<GitHubService>>(),
        tokenProvider: sp.GetRequiredService<CopilotTokenProvider>()));
builder.Services.AddSingleton<IGitHubService>(sp => sp.GetRequiredService<GitHubService>());

// Orchestrator (singleton — drives multi-agent conversation lifecycle)
builder.Services.AddSingleton<AgentMemoryLoader>();
builder.Services.AddSingleton<BreakoutLifecycleService>();
builder.Services.AddSingleton<TaskAssignmentHandler>();
builder.Services.AddSingleton<AgentTurnRunner>();
builder.Services.AddSingleton<AgentOrchestrator>();

// Command system (auto-discovers all ICommandHandler implementations)
builder.Services.AddCommandSystem();

// Notification system
builder.Services.AddNotificationSystem();

// SignalR hub broadcaster (hosted service — bridges ActivityBroadcaster → SignalR)
builder.Services.AddHostedService<ActivityHubBroadcaster>();

// Notification broadcaster (hosted service — bridges ActivityBroadcaster → NotificationManager)
builder.Services.AddHostedService<ActivityNotificationBroadcaster>();

// Notification config auto-restore (hosted service — restores saved provider configs from DB)
builder.Services.AddHostedService<NotificationRestoreService>();

// Proactive auth health probe (hosted service — checks GitHub /user every 5 minutes)
builder.Services.AddHostedService<CopilotAuthMonitorService>();

// PR status sync (polls GitHub every 2 minutes for PR state changes)
builder.Services.AddHostedService<PullRequestSyncService>();

// Sprint timeout checking (auto-rejects stale sign-offs, auto-cancels overdue sprints)
builder.Services.Configure<SprintTimeoutSettings>(
    builder.Configuration.GetSection(SprintTimeoutSettings.SectionName));
builder.Services.AddHostedService<SprintTimeoutService>();

var app = builder.Build();

// Auto-migrate database on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
    db.Database.Migrate();

    // Initialize startup state (create default room + agent locations)
    var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
    await initialization.InitializeAsync();

    // If a workspace is already active, ensure it has a default room
    var catalog = scope.ServiceProvider.GetRequiredService<AgentCatalogOptions>();
    var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
    var workspaceRooms = scope.ServiceProvider.GetRequiredService<WorkspaceRoomService>();
    var mainRoomId = catalog.DefaultRoomId;
    var activeWorkspace = await rooms.GetActiveWorkspacePathAsync();
    if (activeWorkspace is not null)
    {
        mainRoomId = await workspaceRooms.EnsureDefaultRoomForWorkspaceAsync(activeWorkspace);
    }

    // Re-enqueue rooms with unanswered human messages (covers crash and clean restart).
    // Must run BEFORE crash recovery, which posts system messages that would mask
    // pending human messages from the reconstruction query.
    {
        var orchestrator = scope.ServiceProvider.GetRequiredService<AgentOrchestrator>();
        await orchestrator.ReconstructQueueAsync();
    }

    if (CrashRecoveryService.CurrentCrashDetected)
    {
        var orchestrator = scope.ServiceProvider.GetRequiredService<AgentOrchestrator>();
        await orchestrator.HandleStartupRecoveryAsync(mainRoomId);
    }

    // Configure rate limiter from persisted settings
    var settingsService = scope.ServiceProvider.GetRequiredService<SystemSettingsService>();
    var rateLimiter = app.Services.GetRequiredService<CommandRateLimiter>();
    var maxCmds = await settingsService.GetRateLimitMaxCommandsAsync();
    var windowSecs = await settingsService.GetRateLimitWindowSecondsAsync();
    rateLimiter.Configure(maxCmds, windowSecs);
}

// Register shutdown hook for graceful cleanup
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogWarning("Server shutting down (exit code: {ExitCode})", Environment.ExitCode);

    // Update the current server instance record with shutdown timestamp
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

// Register built-in notification providers (synchronous — must complete before hosted services start)
var notificationManager = app.Services.GetRequiredService<NotificationManager>();
notificationManager.RegisterProvider(app.Services.GetRequiredService<ConsoleNotificationProvider>());
notificationManager.RegisterProvider(app.Services.GetRequiredService<DiscordNotificationProvider>());
notificationManager.RegisterProvider(app.Services.GetRequiredService<SlackNotificationProvider>());

// Notification config restore runs as a hosted service (non-blocking — see NotificationRestoreService)

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();

if (authSetup.AnyAuthEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

if (authSetup.GitHubAuthEnabled)
{
    app.UseCopilotTokenRefresh();
}

app.MapControllers();
app.MapHub<ActivityHub>("/hubs/activity");

app.Run();
