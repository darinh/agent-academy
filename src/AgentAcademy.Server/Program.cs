using AgentAcademy.Server.Auth;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Startup;

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
var consultantRateLimits = builder.Services
    .ConfigureConsultantRateLimiting(builder.Configuration, authSetup);

// ── Application Services ────────────────────────────────────────────────────

builder.Services.AddAgentCatalog();
builder.Services.AddDomainServices();
builder.Services.AddAgentPipeline();
builder.Services.AddCommandSystem();
builder.Services.AddNotificationSystem();
builder.Services.AddBackgroundServices(builder.Configuration);
builder.Services.AddForge(builder.Configuration, builder.Environment);

// ── Logging ─────────────────────────────────────────────────────────────────

var logStore = new InMemoryLogStore();
builder.Services.AddSingleton(logStore);
builder.Logging.AddProvider(new InMemoryLogProvider(logStore));
builder.Services.AddSingleton<SignalRConnectionTracker>();

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
