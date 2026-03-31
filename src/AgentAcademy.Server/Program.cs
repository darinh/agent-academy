using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Hubs;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

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

// GitHub OAuth — opt-in: only enabled when ClientId + ClientSecret are configured
var gitHubClientId = builder.Configuration["GitHub:ClientId"] ?? "";
var gitHubClientSecret = builder.Configuration["GitHub:ClientSecret"] ?? "";
var gitHubAuthEnabled = !string.IsNullOrEmpty(gitHubClientId) && !string.IsNullOrEmpty(gitHubClientSecret);

if (gitHubAuthEnabled)
{
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = "GitHub";
    })
    .AddCookie(options =>
    {
        options.LoginPath = "/api/auth/login";
        options.LogoutPath = "/api/auth/logout";
        options.Cookie.Name = "AgentAcademy.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;

        // Return 401 for API calls instead of redirecting
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = 401;
            return Task.CompletedTask;
        };
    })
    .AddOAuth("GitHub", options =>
    {
        options.ClientId = gitHubClientId;
        options.ClientSecret = gitHubClientSecret;
        options.CallbackPath = builder.Configuration["GitHub:CallbackPath"] ?? "/api/auth/callback";
        options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
        options.TokenEndpoint = "https://github.com/login/oauth/access_token";
        options.UserInformationEndpoint = "https://api.github.com/user";
        options.Scope.Add("read:user");
        options.Scope.Add("user:email");
        options.SaveTokens = true;
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;

        options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
        options.ClaimActions.MapJsonKey(ClaimTypes.Name, "login");
        options.ClaimActions.MapJsonKey("urn:github:name", "name");
        options.ClaimActions.MapJsonKey("urn:github:avatar", "avatar_url");

        options.Events = new OAuthEvents
        {
            OnCreatingTicket = async context =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);

                using var response = await context.Backchannel.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.HttpContext.RequestAborted);
                response.EnsureSuccessStatusCode();

                var user = await response.Content.ReadFromJsonAsync<JsonElement>();
                context.RunClaimActions(user);

                // Capture the OAuth token for the Copilot SDK.
                // This makes the token available to CopilotExecutor
                // during background orchestration (where HttpContext is null).
                if (!string.IsNullOrEmpty(context.AccessToken))
                {
                    var tokenProvider = context.HttpContext.RequestServices
                        .GetRequiredService<CopilotTokenProvider>();
                    tokenProvider.SetToken(context.AccessToken);
                }
            }
        };
    });

    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    });
}

// Flag for controllers to check
var gitHubFrontendUrl = builder.Configuration["GitHub:FrontendUrl"] ?? "http://localhost:5173";
builder.Services.AddSingleton(new GitHubAuthOptions(gitHubAuthEnabled, gitHubFrontendUrl));

// Database
builder.Services.AddDbContext<AgentAcademyDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=agent-academy.db"));

// Agent catalog (singleton — loaded from Config/agents.json)
builder.Services.AddAgentCatalog();

// Activity broadcaster (singleton — shared across scoped WorkspaceRuntime instances)
builder.Services.AddSingleton<ActivityBroadcaster>();

// Workspace runtime (scoped — one per request, uses scoped DbContext)
builder.Services.AddScoped<WorkspaceRuntime>();

// Agent config service (scoped — merges catalog defaults with DB overrides)
builder.Services.AddScoped<AgentConfigService>();

// System settings (scoped — typed access to system_settings table)
builder.Services.AddScoped<SystemSettingsService>();

// Conversation session management (scoped — epoch lifecycle and summarization)
builder.Services.AddScoped<ConversationSessionService>();

// Copilot token provider (singleton — captures OAuth token for SDK activation)
builder.Services.AddSingleton<CopilotTokenProvider>();

// Agent execution — CopilotExecutor falls back to StubExecutor internally
// if the Copilot CLI is not available.
builder.Services.AddSingleton<IAgentExecutor, CopilotExecutor>();

// Spec manager (singleton — reads specs/ directory for prompt injection)
builder.Services.AddSingleton<SpecManager>();

// Project scanner (singleton — stateless directory scanner)
builder.Services.AddSingleton<ProjectScanner>();

// Git service (singleton — branch management for breakout rooms)
builder.Services.AddSingleton<GitService>();

// Orchestrator (singleton — drives multi-agent conversation lifecycle)
builder.Services.AddSingleton<AgentOrchestrator>();

// Command system (singleton pipeline + handlers registered via interface)
builder.Services.AddSingleton<CommandPipeline>();
builder.Services.AddSingleton<ICommandHandler, ListRoomsHandler>();
builder.Services.AddSingleton<ICommandHandler, ListAgentsHandler>();
builder.Services.AddSingleton<ICommandHandler, ListTasksHandler>();
builder.Services.AddSingleton<ICommandHandler, ReadFileHandler>();
builder.Services.AddSingleton<ICommandHandler, SearchCodeHandler>();
builder.Services.AddSingleton<ICommandHandler, RememberHandler>();
builder.Services.AddSingleton<ICommandHandler, RecallHandler>();
builder.Services.AddSingleton<ICommandHandler, ListMemoriesHandler>();
builder.Services.AddSingleton<ICommandHandler, ForgetHandler>();
builder.Services.AddSingleton<ICommandHandler, DmHandler>();
builder.Services.AddSingleton<ICommandHandler, ClaimTaskHandler>();
builder.Services.AddSingleton<ICommandHandler, ReleaseTaskHandler>();
builder.Services.AddSingleton<ICommandHandler, UpdateTaskHandler>();
builder.Services.AddSingleton<ICommandHandler, ApproveTaskHandler>();
builder.Services.AddSingleton<ICommandHandler, RequestChangesHandler>();
builder.Services.AddSingleton<ICommandHandler, ShowReviewQueueHandler>();
builder.Services.AddSingleton<ICommandHandler, RunBuildHandler>();
builder.Services.AddSingleton<ICommandHandler, RunTestsHandler>();
builder.Services.AddSingleton<ICommandHandler, ShowDiffHandler>();
builder.Services.AddSingleton<ICommandHandler, GitLogHandler>();
builder.Services.AddSingleton<ICommandHandler, RoomHistoryHandler>();
builder.Services.AddSingleton<ICommandHandler, MoveToRoomHandler>();
builder.Services.AddSingleton<ICommandHandler, SetPlanHandler>();
builder.Services.AddSingleton<ICommandHandler, AddTaskCommentHandler>();
builder.Services.AddSingleton<ICommandHandler, RecallAgentHandler>();
builder.Services.AddSingleton<ICommandHandler, MergeTaskHandler>();
builder.Services.AddSingleton<ICommandHandler, RestartServerHandler>();

// Notification system
builder.Services.AddSingleton<NotificationManager>();
builder.Services.AddSingleton<ConsoleNotificationProvider>();
builder.Services.AddSingleton<DiscordNotificationProvider>();

// SignalR hub broadcaster (hosted service — bridges ActivityBroadcaster → SignalR)
builder.Services.AddHostedService<ActivityHubBroadcaster>();

// Notification broadcaster (hosted service — bridges ActivityBroadcaster → NotificationManager)
builder.Services.AddHostedService<ActivityNotificationBroadcaster>();

var app = builder.Build();

// Auto-migrate database on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
    db.Database.Migrate();

    // Initialize workspace runtime (create default room + agent locations)
    var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
    await runtime.InitializeAsync();

    // If a workspace is already active, ensure it has a default room
    var activeWorkspace = await runtime.GetActiveWorkspacePathAsync();
    if (activeWorkspace is not null)
    {
        await runtime.EnsureDefaultRoomForWorkspaceAsync(activeWorkspace);
    }
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
        var instanceId = WorkspaceRuntime.CurrentInstanceId;
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

// Register built-in notification providers
var notificationManager = app.Services.GetRequiredService<NotificationManager>();
var consoleProvider = app.Services.GetRequiredService<ConsoleNotificationProvider>();
notificationManager.RegisterProvider(consoleProvider);

var discordProvider = app.Services.GetRequiredService<DiscordNotificationProvider>();
notificationManager.RegisterProvider(discordProvider);

// Auto-restore saved notification provider configs from DB (non-blocking)
_ = Task.Run(async () =>
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    var savedConfigs = db.NotificationConfigs
        .GroupBy(c => c.ProviderId)
        .ToList();

    foreach (var group in savedConfigs)
    {
        var provider = notificationManager.GetProvider(group.Key);
        if (provider is null)
            continue;

        var config = group.ToDictionary(c => c.Key, c => c.Value);

        try
        {
            await provider.ConfigureAsync(config);
            await provider.ConnectAsync();
            logger.LogInformation("Auto-restored notification provider '{ProviderId}' from saved config",
                group.Key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to auto-restore notification provider '{ProviderId}'",
                group.Key);
        }
    }
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();

if (gitHubAuthEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();

    // Restore the Copilot SDK token from the auth cookie on the first
    // authenticated request after a server restart. Without this, the
    // user would need to log out and back in every time the server restarts.
    var tokenProvider = app.Services.GetRequiredService<CopilotTokenProvider>();
    app.Use(async (context, next) =>
    {
        if (tokenProvider.Token is null
            && context.User.Identity?.IsAuthenticated == true)
        {
            var accessToken = await context.GetTokenAsync("access_token");
            if (!string.IsNullOrEmpty(accessToken))
                tokenProvider.SetToken(accessToken);
        }
        await next();
    });
}

app.MapControllers();
app.MapHub<ActivityHub>("/hubs/activity");

app.Run();
