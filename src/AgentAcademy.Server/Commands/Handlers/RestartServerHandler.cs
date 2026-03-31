using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles RESTART_SERVER — triggers a graceful server restart with exit code 75.
/// The wrapper script detects this exit code and restarts the process immediately.
/// Restricted to Planner role agents only.
/// </summary>
public sealed class RestartServerHandler : ICommandHandler
{
    /// <summary>Exit code signaling the wrapper script to restart immediately.</summary>
    internal const int RestartExitCode = 75;

    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<RestartServerHandler> _logger;

    public RestartServerHandler(
        IHostApplicationLifetime lifetime,
        ILogger<RestartServerHandler> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    public string CommandName => "RESTART_SERVER";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!RestartServerCommand.TryParse(command.Args, out var parsed, out var error))
        {
            return command with
            {
                Status = CommandStatus.Error,
                Error = error
            };
        }

        // Authorization: only Planner role agents can restart the server.
        // CommandAuthorizer handles allow/deny lists from agents.json,
        // but RESTART_SERVER is critical enough to double-check here.
        if (!string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Agent {AgentId} ({Role}) attempted RESTART_SERVER — denied (Planner only)",
                context.AgentId, context.AgentRole);

            return command with
            {
                Status = CommandStatus.Denied,
                Error = "RESTART_SERVER is restricted to Planner role agents."
            };
        }

        _logger.LogWarning(
            "Server restart requested by {AgentId}: {Reason}",
            context.AgentId, parsed!.Reason);

        // Post a system message so the restart is visible in chat history.
        try
        {
            var runtime = context.Services.GetRequiredService<WorkspaceRuntime>();
            await runtime.PostSystemStatusAsync(runtime.DefaultRoomId,
                $"🔄 **Server restarting**: {parsed.Reason} (requested by {context.AgentName})");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post restart notification");
        }

        // Set exit code BEFORE stopping — Environment.Exit(75) races the host.
        Environment.ExitCode = RestartExitCode;

        // Schedule the stop on a background thread so the command response
        // has a chance to propagate before the host shuts down.
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            _lifetime.StopApplication();
        });

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["reason"] = parsed.Reason,
                ["exitCode"] = RestartExitCode,
                ["message"] = "Server restart initiated. The wrapper script will restart the process."
            }
        };
    }
}
