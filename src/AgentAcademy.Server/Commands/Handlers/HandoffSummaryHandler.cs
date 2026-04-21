using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles HANDOFF_SUMMARY — generates a structured snapshot of the calling agent's
/// current state for handoff to another agent or session persistence. Includes
/// identity, location, assigned tasks, review queue, and recent memories.
/// </summary>
public sealed class HandoffSummaryHandler : ICommandHandler
{
    public string CommandName => "HANDOFF_SUMMARY";
    public bool IsRetrySafe => true;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var result = new Dictionary<string, object?>();

        // 1. Agent identity
        result["agent"] = new Dictionary<string, object?>
        {
            ["id"] = context.AgentId,
            ["name"] = context.AgentName,
            ["role"] = context.AgentRole
        };

        // 2. Location — prefer AgentLocationService, fall back to CommandContext
        try
        {
            var locationService = context.Services.GetService<IAgentLocationService>();
            var location = locationService is not null
                ? await locationService.GetAgentLocationAsync(context.AgentId)
                : null;

            result["location"] = new Dictionary<string, object?>
            {
                ["roomId"] = location?.RoomId ?? context.RoomId,
                ["breakoutRoomId"] = location?.BreakoutRoomId ?? context.BreakoutRoomId,
                ["state"] = location?.State.ToString() ?? "Unknown",
                ["workingDirectory"] = context.WorkingDirectory
            };
        }
        catch
        {
            result["location"] = new Dictionary<string, object?>
            {
                ["roomId"] = context.RoomId,
                ["breakoutRoomId"] = context.BreakoutRoomId,
                ["state"] = "Unknown",
                ["workingDirectory"] = context.WorkingDirectory
            };
        }

        // 3. Tasks assigned to this agent
        try
        {
            var taskService = context.Services.GetRequiredService<ITaskQueryService>();
            var allTasks = await taskService.GetTasksAsync();
            var myTasks = allTasks
                .Where(t => string.Equals(t.AssignedAgentId, context.AgentId, StringComparison.OrdinalIgnoreCase))
                .Select(t => new Dictionary<string, object?>
                {
                    ["id"] = t.Id,
                    ["title"] = t.Title,
                    ["status"] = t.Status.ToString(),
                    ["branchName"] = t.BranchName,
                    ["currentPhase"] = t.CurrentPhase.ToString(),
                    ["priority"] = t.Priority.ToString()
                })
                .ToList();

            result["assignedTasks"] = myTasks;
            result["assignedTaskCount"] = myTasks.Count;
        }
        catch (Exception ex)
        {
            result["assignedTasks"] = new List<object>();
            result["assignedTaskCount"] = 0;
            result["assignedTasksError"] = ex.Message;
        }

        // 4. Review queue — tasks where this agent is the reviewer
        try
        {
            var taskService = context.Services.GetRequiredService<ITaskQueryService>();
            var reviewQueue = await taskService.GetReviewQueueAsync();
            var myReviews = reviewQueue
                .Where(t => string.Equals(t.ReviewerAgentId, context.AgentId, StringComparison.OrdinalIgnoreCase))
                .Select(t => new Dictionary<string, object?>
                {
                    ["id"] = t.Id,
                    ["title"] = t.Title,
                    ["status"] = t.Status.ToString(),
                    ["assignedAgent"] = t.AssignedAgentName
                })
                .ToList();

            result["reviewQueue"] = myReviews;
            result["reviewQueueCount"] = myReviews.Count;
        }
        catch (Exception ex)
        {
            result["reviewQueue"] = new List<object>();
            result["reviewQueueCount"] = 0;
            result["reviewQueueError"] = ex.Message;
        }

        // 5. Recent memories (last 10 by UpdatedAt, read-only — no LastAccessedAt mutation)
        try
        {
            using var scope = context.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var memories = await db.AgentMemories
                .AsNoTracking()
                .Where(m => m.AgentId == context.AgentId)
                .Where(m => m.ExpiresAt == null || m.ExpiresAt > DateTime.UtcNow)
                .OrderByDescending(m => m.UpdatedAt)
                .Take(10)
                .Select(m => new { m.Category, m.Key, m.Value })
                .ToListAsync();

            result["recentMemories"] = memories
                .Select(m => new Dictionary<string, object?>
                {
                    ["category"] = m.Category,
                    ["key"] = m.Key,
                    ["value"] = m.Value
                })
                .ToList();
            result["memoryCount"] = memories.Count;
        }
        catch (Exception ex)
        {
            result["recentMemories"] = new List<object>();
            result["memoryCount"] = 0;
            result["memoriesError"] = ex.Message;
        }

        // 6. Summary line
        var taskCount = result.TryGetValue("assignedTaskCount", out var tc) ? (int)tc! : 0;
        var reviewCount = result.TryGetValue("reviewQueueCount", out var rc) ? (int)rc! : 0;
        var roomId = context.RoomId ?? "unknown";
        result["summary"] = $"Agent {context.AgentName} ({context.AgentRole}) in room '{roomId}' " +
                            $"with {taskCount} assigned task(s) and {reviewCount} pending review(s)";

        return command with
        {
            Status = CommandStatus.Success,
            Result = result
        };
    }
}
