using AgentAcademy.Server.Auth;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Startup;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ── Infrastructure ──────────────────────────────────────────────────────────

builder.Services
    .AddCoreInfrastructure(builder.Configuration)
    .AddPersistence(builder.Configuration);

// ── Authentication & Rate Limiting ──────────────────────────────────────────

var authSetup = AppAuthSetup.FromConfiguration(builder.Configuration);
builder.Services.AddAppAuthentication(authSetup, builder.Environment);
builder.Services.AddSingleton(authSetup);
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

// ── Application Services ────────────────────────────────────────────────────

builder.Services.AddAgentCatalog();
builder.Services.AddDomainServices();
builder.Services.AddAgentPipeline();
builder.Services.AddCommandSystem();
builder.Services.AddNotificationSystem();
builder.Services.AddBackgroundServices(builder.Configuration);

// ── Logging ─────────────────────────────────────────────────────────────────

var logStore = new InMemoryLogStore();
builder.Services.AddSingleton(logStore);
builder.Logging.AddProvider(new InMemoryLogProvider(logStore));

// ── Build & Initialize ──────────────────────────────────────────────────────

var app = builder.Build();

await app.InitializeAsync();
app.ConfigureShutdownHook();
app.RegisterNotificationProviders();
app.LogAuthConfiguration(authSetup);
app.ConfigureHttpPipeline(authSetup, consultantRateLimits);

app.Run();

// Marker class to enable WebApplicationFactory<Program> in integration tests.
// With top-level statements the compiler generates an internal Program class;
// this partial declaration makes it public.
public partial class Program { }
