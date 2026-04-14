using AgentAcademy.Server.Auth;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.HealthChecks;
using AgentAcademy.Server.Hubs;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Startup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ── Infrastructure ──────────────────────────────────────────────────────────

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentAcademy", "DataProtection-Keys")));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
    .AddCheck<AgentExecutorHealthCheck>("agent_executor", tags: ["ready"]);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// ── Authentication & Rate Limiting ──────────────────────────────────────────

var authSetup = AppAuthSetup.FromConfiguration(builder.Configuration);
builder.Services.AddAppAuthentication(authSetup);
builder.Services.AddSingleton(new GitHubAuthOptions(authSetup.GitHubAuthEnabled, authSetup.GitHubFrontendUrl));

var consultantRateLimits = builder.Configuration
    .GetSection(ConsultantRateLimitSettings.SectionName)
    .Get<ConsultantRateLimitSettings>() ?? new();

if (authSetup.ConsultantAuthEnabled && consultantRateLimits.Enabled)
{
    builder.Services.AddRateLimiter(options =>
    {
        options.GlobalLimiter = ConsultantRateLimitExtensions
            .CreateConsultantRateLimiter(consultantRateLimits);

        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (ctx, cancellationToken) =>
        {
            ctx.HttpContext.Response.ContentType = "application/problem+json";

            var retryAfter = ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfterValue)
                ? retryAfterValue
                : TimeSpan.FromSeconds(10);

            ctx.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();

            await ctx.HttpContext.Response.WriteAsJsonAsync(new
            {
                type = "https://tools.ietf.org/html/rfc6585#section-4",
                title = "Rate limit exceeded",
                status = 429,
                detail = $"Too many requests. Try again in {(int)Math.Ceiling(retryAfter.TotalSeconds)} seconds.",
            }, cancellationToken);
        };
    });
}
builder.Services.AddSingleton(consultantRateLimits);

// ── Database ────────────────────────────────────────────────────────────────

builder.Services.AddDbContext<AgentAcademyDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=agent-academy.db"));

// ── Application Services ────────────────────────────────────────────────────

builder.Services.AddAgentCatalog();
builder.Services.AddDomainServices();
builder.Services.AddAgentPipeline();
builder.Services.AddCommandSystem();
builder.Services.AddNotificationSystem();
builder.Services.AddBackgroundServices(builder.Configuration);

// ── Build & Initialize ──────────────────────────────────────────────────────

var app = builder.Build();

await app.InitializeAsync();
app.ConfigureShutdownHook();
app.RegisterNotificationProviders();

// ── Middleware Pipeline ─────────────────────────────────────────────────────

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();

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
app.MapHub<ActivityHub>("/hubs/activity");
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

app.Run();
