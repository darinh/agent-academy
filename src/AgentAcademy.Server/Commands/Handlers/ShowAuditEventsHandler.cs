using AgentAcademy.Server.Data;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles SHOW_AUDIT_EVENTS — queries the activity event log with filters.
/// Returns recent activity events sorted chronologically (newest first).
/// </summary>
public sealed class ShowAuditEventsHandler : ICommandHandler
{
    public string CommandName => "SHOW_AUDIT_EVENTS";
    public bool IsRetrySafe => true;

    private const int DefaultCount = 20;
    private const int MaxCount = 100;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        using var scope = context.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var query = db.ActivityEvents.AsNoTracking().AsQueryable();

        // Filter by event type
        if (command.Args.TryGetValue("type", out var typeObj) && typeObj is string typeFilter
            && !string.IsNullOrWhiteSpace(typeFilter))
        {
            query = query.Where(e => e.Type == typeFilter);
        }

        // Filter by severity
        if (command.Args.TryGetValue("severity", out var sevObj) && sevObj is string severityFilter
            && !string.IsNullOrWhiteSpace(severityFilter))
        {
            query = query.Where(e => e.Severity == severityFilter);
        }

        // Filter by actor
        if (command.Args.TryGetValue("actorId", out var actorObj) && actorObj is string actorFilter
            && !string.IsNullOrWhiteSpace(actorFilter))
        {
            query = query.Where(e => e.ActorId == actorFilter);
        }

        // Filter by room
        if (command.Args.TryGetValue("roomId", out var roomObj) && roomObj is string roomFilter
            && !string.IsNullOrWhiteSpace(roomFilter))
        {
            query = query.Where(e => e.RoomId == roomFilter);
        }

        // Filter by time — "since" as ISO 8601 datetime
        if (command.Args.TryGetValue("since", out var sinceObj) && sinceObj is string sinceStr
            && DateTime.TryParse(sinceStr, out var sinceDate))
        {
            query = query.Where(e => e.OccurredAt >= sinceDate);
        }

        // Limit
        var count = DefaultCount;
        if (command.Args.TryGetValue("count", out var countObj) && countObj is string countStr
            && int.TryParse(countStr, out var parsedCount))
        {
            count = Math.Clamp(parsedCount, 1, MaxCount);
        }

        var events = await query
            .OrderByDescending(e => e.OccurredAt)
            .Take(count)
            .Select(e => new
            {
                e.Id,
                e.Type,
                e.Severity,
                e.ActorId,
                e.RoomId,
                e.TaskId,
                e.Message,
                e.CorrelationId,
                OccurredAt = e.OccurredAt.ToString("O"),
                e.MetadataJson
            })
            .ToListAsync();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["events"] = events,
                ["count"] = events.Count,
                ["filters"] = new Dictionary<string, object?>
                {
                    ["type"] = command.Args.GetValueOrDefault("type"),
                    ["severity"] = command.Args.GetValueOrDefault("severity"),
                    ["actorId"] = command.Args.GetValueOrDefault("actorId"),
                    ["roomId"] = command.Args.GetValueOrDefault("roomId"),
                    ["since"] = command.Args.GetValueOrDefault("since"),
                    ["limit"] = count
                }
            }
        };
    }
}
