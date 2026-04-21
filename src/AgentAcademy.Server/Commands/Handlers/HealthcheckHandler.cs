using System.Diagnostics;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles HEALTHCHECK — returns a system health summary including
/// database connectivity, migration status, server uptime, resource
/// usage, and entity counts.
/// </summary>
public sealed class HealthcheckHandler : ICommandHandler
{
    public string CommandName => "HEALTHCHECK";
    public bool IsRetrySafe => true;

    private static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var checks = new Dictionary<string, object?>();
        var overallHealthy = true;

        // 1. Database connectivity
        try
        {
            using var scope = context.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetService<AgentAcademyDbContext>();
            if (dbContext is not null)
            {
                var canConnect = await dbContext.Database.CanConnectAsync();
                checks["database"] = new Dictionary<string, object?>
                {
                    ["status"] = canConnect ? "healthy" : "unhealthy",
                    ["provider"] = "SQLite"
                };
                if (!canConnect) overallHealthy = false;
            }
            else
            {
                checks["database"] = new Dictionary<string, object?>
                {
                    ["status"] = "unavailable"
                };
                overallHealthy = false;
            }
        }
        catch (Exception ex)
        {
            checks["database"] = new Dictionary<string, object?>
            {
                ["status"] = "unhealthy",
                ["error"] = ex.Message
            };
            overallHealthy = false;
        }

        // 2. Pending migrations
        try
        {
            using var scope = context.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var pending = (await dbContext.Database.GetPendingMigrationsAsync()).ToList();
            checks["migrations"] = new Dictionary<string, object?>
            {
                ["status"] = pending.Count == 0 ? "healthy" : "warning",
                ["pendingCount"] = pending.Count
            };
            if (pending.Count > 0) overallHealthy = false;
        }
        catch (Exception ex)
        {
            checks["migrations"] = new Dictionary<string, object?>
            {
                ["status"] = "unknown",
                ["error"] = ex.Message
            };
        }

        // 3. Server uptime
        var uptime = DateTime.UtcNow - ProcessStart;
        checks["server"] = new Dictionary<string, object?>
        {
            ["status"] = "healthy",
            ["uptime"] = FormatUptime(uptime),
            ["startedAt"] = ProcessStart.ToString("O")
        };

        // 4. Entity counts
        try
        {
            using var scope = context.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var activeRooms = await dbContext.Rooms.CountAsync(r => r.Status == "Active");
            var totalTasks = await dbContext.Tasks.CountAsync();
            var activeTasks = await dbContext.Tasks.CountAsync(t =>
                t.Status == "Active" || t.Status == "InReview" || t.Status == "Blocked");

            checks["entities"] = new Dictionary<string, object?>
            {
                ["activeRooms"] = activeRooms,
                ["totalTasks"] = totalTasks,
                ["activeTasks"] = activeTasks
            };
        }
        catch (Exception ex)
        {
            checks["entities"] = new Dictionary<string, object?>
            {
                ["error"] = ex.Message
            };
        }

        // 5. Agent catalog
        try
        {
            var catalog = context.Services.GetService<IAgentCatalog>();
            checks["agents"] = new Dictionary<string, object?>
            {
                ["registeredCount"] = catalog?.Agents?.Count ?? 0
            };
        }
        catch
        {
            checks["agents"] = new Dictionary<string, object?> { ["registeredCount"] = 0 };
        }

        // 6. Memory usage
        var process = Process.GetCurrentProcess();
        checks["resources"] = new Dictionary<string, object?>
        {
            ["workingSetMB"] = Math.Round(process.WorkingSet64 / (1024.0 * 1024.0), 1),
            ["gcTotalMemoryMB"] = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 1)
        };

        // 7. SignalR connections
        var tracker = context.Services.GetService<SignalRConnectionTracker>();
        if (tracker is not null)
        {
            checks["signalr"] = new Dictionary<string, object?>
            {
                ["activeConnections"] = tracker.Count
            };
        }

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["status"] = overallHealthy ? "healthy" : "degraded",
                ["checks"] = checks,
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            }
        };
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
            return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
        if (uptime.TotalHours >= 1)
            return $"{(int)uptime.TotalHours}h {uptime.Minutes}m";
        return $"{(int)uptime.TotalMinutes}m {uptime.Seconds}s";
    }
}
