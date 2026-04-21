using AgentAcademy.Server.Config;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles RETRY_FAILED_JOB — re-executes a previously failed command
/// from the audit trail. Only commands whose handler is marked
/// <see cref="ICommandHandler.IsRetrySafe"/> can be retried. The current
/// agent's permissions are checked against the target command.
/// Restricted to Planner and Human roles.
/// </summary>
public sealed class RetryFailedJobHandler : ICommandHandler
{
    public string CommandName => "RETRY_FAILED_JOB";
    public bool IsRetrySafe => false;

    private static readonly HashSet<string> AllowedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Planner", "Human"
    };

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        // Hard role gate — beyond agents.json permission
        if (!AllowedRoles.Contains(context.AgentRole))
        {
            return command with
            {
                Status = CommandStatus.Denied,
                ErrorCode = CommandErrorCode.Permission,
                Error = $"RETRY_FAILED_JOB is restricted to Planner and Human roles. Your role: {context.AgentRole}."
            };
        }

        if (!command.Args.TryGetValue("auditId", out var auditObj) || auditObj is not string auditId
            || string.IsNullOrWhiteSpace(auditId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Required argument 'auditId' is missing."
            };
        }

        using var scope = context.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        // Look up the original failed command
        var audit = await db.CommandAudits.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == auditId);

        if (audit is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Audit entry '{auditId}' not found."
            };
        }

        if (audit.Status != "Error")
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = $"Audit entry '{auditId}' has status '{audit.Status}' — only failed commands (Status=Error) can be retried."
            };
        }

        // Find the handler for the original command
        var handlers = context.Services.GetServices<ICommandHandler>()
            .ToDictionary(h => h.CommandName, StringComparer.OrdinalIgnoreCase);

        if (!handlers.TryGetValue(audit.Command, out var targetHandler))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"No handler found for command '{audit.Command}'."
            };
        }

        // Only retry-safe commands can be retried
        if (!targetHandler.IsRetrySafe)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = $"Command '{audit.Command}' is not retry-safe (IsRetrySafe=false). Only idempotent/read-only commands can be retried."
            };
        }

        // Check that the current agent has permission for the target command.
        // Fail closed: if agent is not in catalog, deny.
        var catalog = context.Services.GetRequiredService<IAgentCatalog>();
        var agentDef = catalog.Agents.FirstOrDefault(a => a.Id == context.AgentId);
        if (agentDef is null)
        {
            // Human role callers via CommandController won't have a catalog entry,
            // but they've already passed the role gate above. Allow Human role through.
            if (!string.Equals(context.AgentRole, "Human", StringComparison.OrdinalIgnoreCase))
            {
                return command with
                {
                    Status = CommandStatus.Denied,
                    ErrorCode = CommandErrorCode.Permission,
                    Error = $"Agent '{context.AgentId}' is not in the agent catalog and cannot retry commands."
                };
            }
        }
        else
        {
            var authorizer = new CommandAuthorizer();
            var testEnvelope = new CommandEnvelope(
                Command: audit.Command,
                Args: new Dictionary<string, object?>(),
                Status: CommandStatus.Success,
                Result: null,
                Error: null,
                CorrelationId: "",
                Timestamp: DateTime.UtcNow,
                ExecutedBy: context.AgentId
            );

            var denied = authorizer.Authorize(testEnvelope, agentDef);
            if (denied is not null)
            {
                return command with
                {
                    Status = CommandStatus.Denied,
                    ErrorCode = CommandErrorCode.Permission,
                    Error = $"You do not have permission to execute '{audit.Command}': {denied.Error}"
                };
            }
        }

        // Reconstruct the args from the audit trail.
        // Normalize JsonElement values to strings so handlers' `is string` checks work.
        Dictionary<string, object?> args;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(audit.ArgsJson);
            args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (raw is not null)
            {
                foreach (var (key, element) in raw)
                {
                    args[key] = element.ValueKind switch
                    {
                        JsonValueKind.String => element.GetString(),
                        JsonValueKind.Number => element.GetRawText(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.Null => null,
                        _ => element.GetRawText()
                    };
                }
            }
        }
        catch
        {
            args = new Dictionary<string, object?>();
        }

        // Create new envelope with fresh correlation ID
        var newCorrelationId = $"retry-{Guid.NewGuid():N}";
        var retryEnvelope = new CommandEnvelope(
            Command: audit.Command,
            Args: args,
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: newCorrelationId,
            Timestamp: DateTime.UtcNow,
            ExecutedBy: context.AgentId
        );

        // Execute through the handler
        var result = await targetHandler.ExecuteAsync(retryEnvelope, context);

        // Persist an audit record for the retried command execution
        try
        {
            db.CommandAudits.Add(new CommandAuditEntity
            {
                Id = $"retry-{Guid.NewGuid():N}",
                CorrelationId = newCorrelationId,
                AgentId = context.AgentId,
                Source = "retry",
                Command = audit.Command,
                ArgsJson = audit.ArgsJson,
                Status = result.Status.ToString(),
                ErrorMessage = result.Error,
                ErrorCode = result.ErrorCode,
                RoomId = context.RoomId,
                Timestamp = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        catch
        {
            // Non-critical: don't fail the retry if audit persistence fails
        }

        // Wrap the result with retry lineage
        return command with
        {
            Status = result.Status,
            ErrorCode = result.ErrorCode,
            Error = result.Error,
            Result = new Dictionary<string, object?>
            {
                ["retriedCommand"] = audit.Command,
                ["retriedFromAuditId"] = auditId,
                ["retriedFromCorrelationId"] = audit.CorrelationId,
                ["newCorrelationId"] = newCorrelationId,
                ["result"] = result.Result
            }
        };
    }
}
