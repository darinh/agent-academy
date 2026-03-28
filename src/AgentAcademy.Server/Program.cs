using AgentAcademy.Server.Config;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Hubs;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Services;
using Microsoft.EntityFrameworkCore;

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

// Agent execution — CopilotExecutor falls back to StubExecutor internally
// if the Copilot CLI is not available.
builder.Services.AddSingleton<IAgentExecutor, CopilotExecutor>();

// Spec manager (singleton — reads specs/ directory for prompt injection)
builder.Services.AddSingleton<SpecManager>();

// Project scanner (singleton — stateless directory scanner)
builder.Services.AddSingleton<ProjectScanner>();

// Orchestrator (singleton — drives multi-agent conversation lifecycle)
builder.Services.AddSingleton<AgentOrchestrator>();

// Notification system
builder.Services.AddSingleton<NotificationManager>();
builder.Services.AddSingleton<ConsoleNotificationProvider>();
builder.Services.AddSingleton<DiscordNotificationProvider>();

// SignalR hub broadcaster (hosted service — bridges ActivityBroadcaster → SignalR)
builder.Services.AddHostedService<ActivityHubBroadcaster>();

var app = builder.Build();

// Auto-migrate database on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
    db.Database.Migrate();

    // Initialize workspace runtime (create default room + agent locations)
    var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
    await runtime.InitializeAsync();
}

// Register built-in notification providers
var notificationManager = app.Services.GetRequiredService<NotificationManager>();
var consoleProvider = app.Services.GetRequiredService<ConsoleNotificationProvider>();
notificationManager.RegisterProvider(consoleProvider);

var discordProvider = app.Services.GetRequiredService<DiscordNotificationProvider>();
notificationManager.RegisterProvider(discordProvider);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();

app.MapControllers();
app.MapHub<ActivityHub>("/hubs/activity");

app.Run();
