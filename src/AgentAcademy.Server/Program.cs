using AgentAcademy.Server.Auth;
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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;

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

// GitHub OAuth — opt-in: only enabled when ClientId + ClientSecret are configured
var gitHubClientId = builder.Configuration["GitHub:ClientId"] ?? "";
var gitHubClientSecret = builder.Configuration["GitHub:ClientSecret"] ?? "";
var gitHubAuthEnabled = !string.IsNullOrEmpty(gitHubClientId) && !string.IsNullOrEmpty(gitHubClientSecret);

// Consultant API — opt-in: only enabled when a shared secret is configured
var consultantSecret = builder.Configuration["ConsultantApi:SharedSecret"] ?? "";
var consultantAuthEnabled = !string.IsNullOrEmpty(consultantSecret);

var anyAuthEnabled = gitHubAuthEnabled || consultantAuthEnabled;

if (anyAuthEnabled)
{
    var authBuilder = builder.Services.AddAuthentication(options =>
    {
        if (gitHubAuthEnabled && consultantAuthEnabled)
        {
            // PolicyScheme selects the right handler per-request
            options.DefaultScheme = "MultiAuth";
            options.DefaultChallengeScheme = "GitHub";
        }
        else if (gitHubAuthEnabled)
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = "GitHub";
        }
        else
        {
            options.DefaultScheme = ConsultantKeyAuthHandler.SchemeName;
        }
    });

    if (gitHubAuthEnabled)
    {
        authBuilder
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

                        // Capture the OAuth tokens for the Copilot SDK.
                        // This makes the token available to CopilotExecutor
                        // during background orchestration (where HttpContext is null).
                        if (!string.IsNullOrEmpty(context.AccessToken))
                        {
                            var tokenProvider = context.HttpContext.RequestServices
                                .GetRequiredService<CopilotTokenProvider>();
                            // GitHub App refresh tokens are valid for 6 months (15,811,200 seconds).
                            // The OAuthCreatingTicketContext doesn't expose refresh_token_expires_in,
                            // so we use GitHub's documented default.
                            var refreshTokenExpiry = !string.IsNullOrEmpty(context.RefreshToken)
                                ? TimeSpan.FromDays(180)
                                : (TimeSpan?)null;
                            tokenProvider.SetTokens(
                                context.AccessToken,
                                context.RefreshToken,
                                context.ExpiresIn,
                                refreshTokenExpiry);
                        }
                    }
                };
            });
    }

    if (consultantAuthEnabled)
    {
        authBuilder.AddScheme<AuthenticationSchemeOptions, ConsultantKeyAuthHandler>(
            ConsultantKeyAuthHandler.SchemeName, null);
    }

    // When both schemes are active, a PolicyScheme routes to the right one per-request
    if (gitHubAuthEnabled && consultantAuthEnabled)
    {
        authBuilder.AddPolicyScheme("MultiAuth", "Multi-Auth Policy", options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                var header = context.Request.Headers[ConsultantKeyAuthHandler.HeaderName].ToString();
                if (!string.IsNullOrEmpty(header))
                    return ConsultantKeyAuthHandler.SchemeName;
                return CookieAuthenticationDefaults.AuthenticationScheme;
            };
        });
    }

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

// LLM usage tracking (singleton — captures AssistantUsageEvent from SDK)
builder.Services.AddSingleton<LlmUsageTracker>();
builder.Services.AddSingleton<AgentErrorTracker>();

// SDK tool calling — tool functions use IServiceScopeFactory for scoped service access
builder.Services.AddSingleton<AgentToolFunctions>();
builder.Services.AddSingleton<IAgentToolRegistry, AgentToolRegistry>();

// Agent execution — CopilotExecutor falls back to StubExecutor internally
// if the Copilot CLI is not available.
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

// GitHub integration (singleton — PR creation via gh CLI)
builder.Services.AddSingleton<GitHubService>();
builder.Services.AddSingleton<IGitHubService>(sp => sp.GetRequiredService<GitHubService>());

// Orchestrator (singleton — drives multi-agent conversation lifecycle)
builder.Services.AddSingleton<AgentOrchestrator>();

// Command system (singleton pipeline + handlers registered via interface)
builder.Services.AddSingleton<CommandRateLimiter>();
builder.Services.AddSingleton<CommandPipeline>();
builder.Services.AddSingleton<ICommandHandler, ListRoomsHandler>();
builder.Services.AddSingleton<ICommandHandler, ListAgentsHandler>();
builder.Services.AddSingleton<ICommandHandler, ListCommandsHandler>();
builder.Services.AddSingleton<ICommandHandler, ListTasksHandler>();
builder.Services.AddSingleton<ICommandHandler, ReadFileHandler>();
builder.Services.AddSingleton<ICommandHandler, SearchCodeHandler>();
builder.Services.AddSingleton<ICommandHandler, RememberHandler>();
builder.Services.AddSingleton<ICommandHandler, RecallHandler>();
builder.Services.AddSingleton<ICommandHandler, ListMemoriesHandler>();
builder.Services.AddSingleton<ICommandHandler, ForgetHandler>();
builder.Services.AddSingleton<ICommandHandler, ExportMemoriesHandler>();
builder.Services.AddSingleton<ICommandHandler, ImportMemoriesHandler>();
builder.Services.AddSingleton<ICommandHandler, DmHandler>();
builder.Services.AddSingleton<ICommandHandler, ClaimTaskHandler>();
builder.Services.AddSingleton<ICommandHandler, ReleaseTaskHandler>();
builder.Services.AddSingleton<ICommandHandler, UpdateTaskHandler>();
builder.Services.AddSingleton<ICommandHandler, ApproveTaskHandler>();
builder.Services.AddSingleton<ICommandHandler, RequestChangesHandler>();
builder.Services.AddSingleton<ICommandHandler, RejectTaskHandler>();
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
builder.Services.AddSingleton<ICommandHandler, CloseRoomHandler>();
builder.Services.AddSingleton<ICommandHandler, CleanupRoomsHandler>();
builder.Services.AddSingleton<ICommandHandler, CreateRoomHandler>();
builder.Services.AddSingleton<ICommandHandler, ReopenRoomHandler>();
builder.Services.AddSingleton<ICommandHandler, RoomTopicHandler>();
builder.Services.AddSingleton<ICommandHandler, InviteToRoomHandler>();
builder.Services.AddSingleton<ICommandHandler, ReturnToMainHandler>();
builder.Services.AddSingleton<ICommandHandler, MergeTaskHandler>();
builder.Services.AddSingleton<ICommandHandler, RebaseTaskHandler>();
builder.Services.AddSingleton<ICommandHandler, CancelTaskHandler>();
builder.Services.AddSingleton<ICommandHandler, CreateTaskItemHandler>();
builder.Services.AddSingleton<ICommandHandler, UpdateTaskItemHandler>();
builder.Services.AddSingleton<ICommandHandler, ListTaskItemsHandler>();
builder.Services.AddSingleton<ICommandHandler, ShellCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, RestartServerHandler>();
builder.Services.AddSingleton<ICommandHandler, CreatePrHandler>();
builder.Services.AddSingleton<ICommandHandler, MergePrHandler>();
builder.Services.AddSingleton<ICommandHandler, PostPrReviewHandler>();
builder.Services.AddSingleton<ICommandHandler, GetPrReviewsHandler>();
builder.Services.AddSingleton<ICommandHandler, LinkTaskToSpecHandler>();
builder.Services.AddSingleton<ICommandHandler, ShowUnlinkedChangesHandler>();

// Notification system
builder.Services.AddSingleton<ConfigEncryptionService>();
builder.Services.AddSingleton<NotificationDeliveryTracker>();
builder.Services.AddSingleton<NotificationManager>();
builder.Services.AddSingleton<ConsoleNotificationProvider>();
builder.Services.AddSingleton<DiscordNotificationProvider>();
builder.Services.AddSingleton<SlackNotificationProvider>();
builder.Services.AddHttpClient("Slack");

// SignalR hub broadcaster (hosted service — bridges ActivityBroadcaster → SignalR)
builder.Services.AddHostedService<ActivityHubBroadcaster>();

// Notification broadcaster (hosted service — bridges ActivityBroadcaster → NotificationManager)
builder.Services.AddHostedService<ActivityNotificationBroadcaster>();

// Proactive auth health probe (hosted service — checks GitHub /user every 5 minutes)
builder.Services.AddHostedService<CopilotAuthMonitorService>();

// PR status sync (polls GitHub every 2 minutes for PR state changes)
builder.Services.AddHostedService<PullRequestSyncService>();

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
    var mainRoomId = runtime.DefaultRoomId;
    var activeWorkspace = await runtime.GetActiveWorkspacePathAsync();
    if (activeWorkspace is not null)
    {
        mainRoomId = await runtime.EnsureDefaultRoomForWorkspaceAsync(activeWorkspace);
    }

    // Re-enqueue rooms with unanswered human messages (covers crash and clean restart).
    // Must run BEFORE crash recovery, which posts system messages that would mask
    // pending human messages from the reconstruction query.
    {
        var orchestrator = scope.ServiceProvider.GetRequiredService<AgentOrchestrator>();
        await orchestrator.ReconstructQueueAsync();
    }

    if (WorkspaceRuntime.CurrentCrashDetected)
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

var slackProvider = app.Services.GetRequiredService<SlackNotificationProvider>();
notificationManager.RegisterProvider(slackProvider);

// Auto-restore saved notification provider configs from DB (non-blocking)
_ = Task.Run(async () =>
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
    var encryption = scope.ServiceProvider.GetRequiredService<ConfigEncryptionService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    var savedConfigs = db.NotificationConfigs
        .GroupBy(c => c.ProviderId)
        .ToList();

    foreach (var group in savedConfigs)
    {
        var provider = notificationManager.GetProvider(group.Key);
        if (provider is null)
            continue;

        // Determine which fields are secrets from the provider schema
        var schema = provider.GetConfigSchema();
        var secretKeys = schema.Fields
            .Where(f => string.Equals(f.Type, "secret", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Decrypt secret values before passing to the provider
        var config = new Dictionary<string, string>();
        var failedKeys = new List<string>();
        foreach (var entry in group)
        {
            if (secretKeys.Contains(entry.Key))
            {
                if (encryption.TryDecrypt(entry.Value, out var decrypted))
                    config[entry.Key] = decrypted;
                else
                    failedKeys.Add(entry.Key);
            }
            else
            {
                config[entry.Key] = entry.Value;
            }
        }

        if (failedKeys.Count > 0)
        {
            logger.LogWarning(
                "Notification provider '{ProviderId}' has undecryptable config keys: {Keys}. Reconfiguration required.",
                group.Key, string.Join(", ", failedKeys));
            continue;
        }

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

if (anyAuthEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

if (gitHubAuthEnabled)
{
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
            var refreshToken = await context.GetTokenAsync("refresh_token");
            var expiresAtStr = await context.GetTokenAsync("expires_at");

            if (!string.IsNullOrEmpty(accessToken))
            {
                TimeSpan? expiresIn = null;
                if (DateTimeOffset.TryParse(expiresAtStr, out var expiresAt))
                {
                    var remaining = expiresAt - DateTimeOffset.UtcNow;
                    if (remaining > TimeSpan.Zero)
                        expiresIn = remaining;
                }

                tokenProvider.SetTokens(accessToken, refreshToken, expiresIn);
            }
        }

        // Write back refreshed tokens to the auth cookie so they survive server restarts
        if (tokenProvider.HasPendingCookieUpdate
            && context.User.Identity?.IsAuthenticated == true)
        {
            try
            {
                var authenticateResult = await context.AuthenticateAsync();
                if (authenticateResult.Succeeded && authenticateResult.Properties is not null)
                {
                    // Merge with existing tokens to avoid clobbering token_type, scope, etc.
                    var existingTokens = authenticateResult.Properties.GetTokens()
                        .Where(t => t.Name is not ("access_token" or "refresh_token" or "expires_at"))
                        .ToList();
                    existingTokens.Add(new AuthenticationToken { Name = "access_token", Value = tokenProvider.Token ?? "" });
                    existingTokens.Add(new AuthenticationToken { Name = "refresh_token", Value = tokenProvider.RefreshToken ?? "" });
                    existingTokens.Add(new AuthenticationToken { Name = "expires_at", Value = tokenProvider.ExpiresAtUtc?.ToString("o") ?? "" });
                    authenticateResult.Properties.StoreTokens(existingTokens);
                    await context.SignInAsync(
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        authenticateResult.Principal!,
                        authenticateResult.Properties);
                    tokenProvider.ClearCookieUpdatePending();
                }
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Failed to write refreshed tokens to auth cookie — will retry on next request");
            }
        }

        await next();
    });
}

app.MapControllers();
app.MapHub<ActivityHub>("/hubs/activity");

app.Run();
