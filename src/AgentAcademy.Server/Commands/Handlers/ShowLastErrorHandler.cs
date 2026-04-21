using AgentAcademy.Server.Data;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles SHOW_LAST_ERROR — returns the most recent errors from both
/// activity events (runtime errors) and command audits (command failures),
/// merged chronologically.
/// </summary>
public sealed class ShowLastErrorHandler : ICommandHandler
{
    public string CommandName => "SHOW_LAST_ERROR";
    public bool IsRetrySafe => true;

    private const int DefaultCount = 5;
    private const int MaxCount = 25;

    private static readonly HashSet<string> ErrorEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AgentErrorOccurred",
        "CommandFailed",
        "SubagentFailed"
    };

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var count = DefaultCount;
        if (command.Args.TryGetValue("count", out var countObj) && countObj is string countStr
            && int.TryParse(countStr, out var parsed))
        {
            count = Math.Clamp(parsed, 1, MaxCount);
        }

        string? agentFilter = null;
        if (command.Args.TryGetValue("agentId", out var agentObj) && agentObj is string agent
            && !string.IsNullOrWhiteSpace(agent))
        {
            agentFilter = agent;
        }

        using var scope = context.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        // 1. Query activity events with error severity or error event types
        var activityQuery = db.ActivityEvents.AsNoTracking()
            .Where(e => e.Severity == "Error"
                || ErrorEventTypes.Contains(e.Type));

        if (agentFilter is not null)
            activityQuery = activityQuery.Where(e => e.ActorId == agentFilter);

        var activityErrors = await activityQuery
            .OrderByDescending(e => e.OccurredAt)
            .Take(count)
            .Select(e => new ErrorEntry
            {
                Source = "activity",
                Id = e.Id,
                Type = e.Type,
                Severity = e.Severity,
                AgentId = e.ActorId,
                RoomId = e.RoomId,
                Message = e.Message,
                CorrelationId = e.CorrelationId,
                OccurredAt = e.OccurredAt
            })
            .ToListAsync();

        // 2. Query command audits with Error status
        var auditQuery = db.CommandAudits.AsNoTracking()
            .Where(a => a.Status == "Error");

        if (agentFilter is not null)
            auditQuery = auditQuery.Where(a => a.AgentId == agentFilter);

        var commandErrors = await auditQuery
            .OrderByDescending(a => a.Timestamp)
            .Take(count)
            .Select(a => new ErrorEntry
            {
                Source = "command",
                Id = a.Id,
                Type = a.Command,
                Severity = "Error",
                AgentId = a.AgentId,
                RoomId = a.RoomId,
                Message = a.ErrorMessage ?? $"{a.Command} failed",
                ErrorCode = a.ErrorCode,
                CorrelationId = a.CorrelationId,
                OccurredAt = a.Timestamp
            })
            .ToListAsync();

        // 3. Merge and sort chronologically (newest first), take top N
        var merged = activityErrors
            .Concat(commandErrors)
            .OrderByDescending(e => e.OccurredAt)
            .Take(count)
            .Select(e => new Dictionary<string, object?>
            {
                ["source"] = e.Source,
                ["id"] = e.Id,
                ["type"] = e.Type,
                ["severity"] = e.Severity,
                ["agentId"] = e.AgentId,
                ["roomId"] = e.RoomId,
                ["message"] = e.Message,
                ["errorCode"] = e.ErrorCode,
                ["correlationId"] = e.CorrelationId,
                ["occurredAt"] = e.OccurredAt.ToString("O")
            })
            .ToList();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["errors"] = merged,
                ["count"] = merged.Count,
                ["agentFilter"] = agentFilter
            }
        };
    }

    private sealed class ErrorEntry
    {
        public string Source { get; init; } = "";
        public string Id { get; init; } = "";
        public string Type { get; init; } = "";
        public string Severity { get; init; } = "";
        public string? AgentId { get; init; }
        public string? RoomId { get; init; }
        public string Message { get; init; } = "";
        public string? ErrorCode { get; init; }
        public string? CorrelationId { get; init; }
        public DateTime OccurredAt { get; init; }
    }
}
