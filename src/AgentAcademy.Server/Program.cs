using AgentAcademy.Server.Data;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Database
builder.Services.AddDbContext<AgentAcademyDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=agent-academy.db"));

// Agent execution — CopilotExecutor falls back to StubExecutor internally
// if the Copilot CLI is not available.
builder.Services.AddSingleton<IAgentExecutor, CopilotExecutor>();

// Notification system
builder.Services.AddSingleton<NotificationManager>();
builder.Services.AddSingleton<ConsoleNotificationProvider>();
builder.Services.AddSingleton<DiscordNotificationProvider>();

var app = builder.Build();

// Auto-migrate database on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
    db.Database.Migrate();
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

app.MapControllers();

app.Run();
