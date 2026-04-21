using AgentAcademy.Server.Data;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles TRACE_REQUEST — traces events by correlation ID across both
/// the activity event log and the command audit trail, returning a
/// unified chronological timeline.
/// </summary>
public sealed class TraceRequestHandler : ICommandHandler
{
    public string CommandName => "TRACE_REQUEST";
    public bool IsRetrySafe => true;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("correlationId", out var corrObj) || corrObj is not string correlationId
            || string.IsNullOrWhiteSpace(correlationId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Required argument 'correlationId' is missing."
            };
        }

        using var scope = context.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        // 1. Activity events with this correlation ID
        var activityEvents = await db.ActivityEvents.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .OrderBy(e => e.OccurredAt)
            .Select(e => new
            {
                source = "activity",
                e.Id,
                e.Type,
                e.Severity,
                e.ActorId,
                e.RoomId,
                e.Message,
                timestamp = e.OccurredAt,
                e.MetadataJson
            })
            .ToListAsync();

        // 2. Command audits with this correlation ID
        var commandAudits = await db.CommandAudits.AsNoTracking()
            .Where(a => a.CorrelationId == correlationId)
            .OrderBy(a => a.Timestamp)
            .Select(a => new
            {
                source = "command",
                a.Id,
                Type = a.Command,
                Severity = a.Status == "Error" ? "Error" : "Info",
                ActorId = a.AgentId,
                a.RoomId,
                Message = a.ErrorMessage ?? $"{a.Command} → {a.Status}",
                timestamp = a.Timestamp,
                MetadataJson = a.ArgsJson
            })
            .ToListAsync();

        // 3. Merge into unified timeline
        var timeline = activityEvents
            .Select(e => new Dictionary<string, object?>
            {
                ["source"] = e.source,
                ["id"] = e.Id,
                ["type"] = e.Type,
                ["severity"] = e.Severity,
                ["actorId"] = e.ActorId,
                ["roomId"] = e.RoomId,
                ["message"] = e.Message,
                ["timestamp"] = e.timestamp.ToString("O"),
                ["metadata"] = e.MetadataJson
            })
            .Concat(commandAudits.Select(a => new Dictionary<string, object?>
            {
                ["source"] = a.source,
                ["id"] = a.Id,
                ["type"] = a.Type,
                ["severity"] = a.Severity,
                ["actorId"] = a.ActorId,
                ["roomId"] = a.RoomId,
                ["message"] = a.Message,
                ["timestamp"] = a.timestamp.ToString("O"),
                ["metadata"] = a.MetadataJson
            }))
            .OrderBy(e => e["timestamp"])
            .ToList();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["correlationId"] = correlationId,
                ["timeline"] = timeline,
                ["activityEventCount"] = activityEvents.Count,
                ["commandAuditCount"] = commandAudits.Count,
                ["totalEvents"] = timeline.Count
            }
        };
    }
}
