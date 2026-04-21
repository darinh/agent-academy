using System.Diagnostics;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles PLATFORM_STATUS — returns a comprehensive platform status overview combining
/// server health, executor state, agent locations, task pipeline, sprint progress,
/// and connection counts. Useful for agent orientation after context loss.
/// </summary>
public sealed class PlatformStatusHandler : ICommandHandler
{
    public string CommandName => "PLATFORM_STATUS";
    public bool IsRetrySafe => true;

    private static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var result = new Dictionary<string, object?>();
        var overallHealthy = true;

        // 1. Server info
        try
        {
            var uptime = DateTime.UtcNow - ProcessStart;
            var process = Process.GetCurrentProcess();
            result["server"] = new Dictionary<string, object?>
            {
                ["uptime"] = FormatUptime(uptime),
                ["startedAt"] = ProcessStart.ToString("O"),
                ["version"] = typeof(PlatformStatusHandler).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                ["workingSetMB"] = Math.Round(process.WorkingSet64 / (1024.0 * 1024.0), 1),
                ["instanceId"] = CrashRecoveryService.CurrentInstanceId ?? "unknown",
                ["crashDetected"] = CrashRecoveryService.CurrentCrashDetected
            };
            if (CrashRecoveryService.CurrentCrashDetected) overallHealthy = false;
        }
        catch (Exception ex)
        {
            result["server"] = new Dictionary<string, object?> { ["error"] = ex.Message };
            overallHealthy = false;
        }

        // 2. Executor status
        try
        {
            var executor = context.Services.GetService<IAgentExecutor>();
            if (executor is not null)
            {
                result["executor"] = new Dictionary<string, object?>
                {
                    ["operational"] = executor.IsFullyOperational,
                    ["authFailed"] = executor.IsAuthFailed,
                    ["circuitBreakerState"] = executor.CircuitBreakerState.ToString()
                };
                if (!executor.IsFullyOperational || executor.IsAuthFailed) overallHealthy = false;
            }
            else
            {
                result["executor"] = new Dictionary<string, object?> { ["operational"] = false, ["note"] = "No executor registered" };
                overallHealthy = false;
            }
        }
        catch (Exception ex)
        {
            result["executor"] = new Dictionary<string, object?> { ["error"] = ex.Message };
            overallHealthy = false;
        }

        // 3. Agent locations
        try
        {
            var catalog = context.Services.GetService<IAgentCatalog>();
            var locationService = context.Services.GetService<IAgentLocationService>();
            var locations = locationService is not null
                ? await locationService.GetAgentLocationsAsync()
                : new List<AgentLocation>();

            result["agents"] = new Dictionary<string, object?>
            {
                ["configured"] = catalog?.Agents?.Count ?? 0,
                ["tracked"] = locations.Count,
                ["locations"] = locations.Select(l => new Dictionary<string, object?>
                {
                    ["agentId"] = l.AgentId,
                    ["state"] = l.State.ToString(),
                    ["roomId"] = l.RoomId
                }).ToList()
            };
        }
        catch (Exception ex)
        {
            result["agents"] = new Dictionary<string, object?> { ["error"] = ex.Message };
            overallHealthy = false;
        }

        // 4. Room counts (grouped by actual status, not just active/non-active)
        try
        {
            using var scope = context.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var roomCounts = await db.Rooms
                .GroupBy(r => r.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync();

            var breakdown = roomCounts.ToDictionary(r => r.Status, r => (object?)r.Count);
            breakdown["total"] = roomCounts.Sum(r => r.Count);

            result["rooms"] = breakdown;
        }
        catch (Exception ex)
        {
            result["rooms"] = new Dictionary<string, object?> { ["error"] = ex.Message };
            overallHealthy = false;
        }

        // 5. Task pipeline
        try
        {
            using var scope = context.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var taskCounts = await db.Tasks
                .GroupBy(t => t.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync();

            var breakdown = taskCounts.ToDictionary(t => t.Status, t => (object?)t.Count);
            breakdown["total"] = taskCounts.Sum(t => t.Count);

            result["tasks"] = breakdown;
        }
        catch (Exception ex)
        {
            result["tasks"] = new Dictionary<string, object?> { ["error"] = ex.Message };
            overallHealthy = false;
        }

        // 6. Active sprint
        try
        {
            var roomService = context.Services.GetService<IRoomService>();
            var sprintService = context.Services.GetService<ISprintService>();
            if (roomService is not null && sprintService is not null)
            {
                var workspacePath = await roomService.GetActiveWorkspacePathAsync();
                if (workspacePath is not null)
                {
                    var sprint = await sprintService.GetActiveSprintAsync(workspacePath);
                    result["sprint"] = sprint is not null
                        ? new Dictionary<string, object?>
                        {
                            ["id"] = sprint.Id,
                            ["number"] = sprint.Number,
                            ["stage"] = sprint.CurrentStage,
                            ["status"] = sprint.Status,
                            ["awaitingSignOff"] = sprint.AwaitingSignOff
                        }
                        : null;
                }
                else
                {
                    result["sprint"] = null;
                }
            }
            else
            {
                result["sprint"] = null;
            }
        }
        catch (Exception ex)
        {
            result["sprint"] = new Dictionary<string, object?> { ["error"] = ex.Message };
        }

        // 7. SignalR connections
        try
        {
            var tracker = context.Services.GetService<SignalRConnectionTracker>();
            result["connections"] = new Dictionary<string, object?>
            {
                ["signalr"] = tracker?.Count ?? 0
            };
        }
        catch
        {
            result["connections"] = new Dictionary<string, object?> { ["signalr"] = 0 };
        }

        result["status"] = overallHealthy ? "healthy" : "degraded";
        result["timestamp"] = DateTime.UtcNow.ToString("O");

        return command with
        {
            Status = CommandStatus.Success,
            Result = result
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
