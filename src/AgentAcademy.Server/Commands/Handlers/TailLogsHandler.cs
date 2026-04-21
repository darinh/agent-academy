using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles TAIL_LOGS — returns recent application log entries from the
/// in-memory ring buffer. Supports optional line count and text filter.
/// </summary>
public sealed class TailLogsHandler : ICommandHandler
{
    public string CommandName => "TAIL_LOGS";
    public bool IsRetrySafe => true;

    private const int DefaultLines = 100;
    private const int MaxLines = 500;

    public Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var store = context.Services.GetService<InMemoryLogStore>();
        if (store is null)
        {
            return Task.FromResult(command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = "Log store is not available."
            });
        }

        var lines = DefaultLines;
        if (command.Args.TryGetValue("lines", out var linesObj))
        {
            lines = linesObj switch
            {
                int i => i,
                long l => (int)l,
                string s when int.TryParse(s, out var parsed) => parsed,
                _ => DefaultLines
            };
            lines = Math.Clamp(lines, 1, MaxLines);
        }

        string? filter = null;
        if (command.Args.TryGetValue("filter", out var filterObj) && filterObj is string f && !string.IsNullOrWhiteSpace(f))
            filter = f;

        var entries = store.Tail(lines, filter);

        return Task.FromResult(command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["count"] = entries.Count,
                ["entries"] = entries.Select(e => new Dictionary<string, object?>
                {
                    ["timestamp"] = e.Timestamp,
                    ["level"] = e.Level,
                    ["category"] = e.Category,
                    ["message"] = e.Message,
                    ["exception"] = e.Exception
                }).ToList()
            }
        });
    }
}
