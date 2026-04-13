using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles RESTART_SERVER — triggers a graceful server restart with exit code 75.
/// The wrapper script detects this exit code and restarts the process immediately.
/// Restricted to Planner role agents only.
/// Enforces a server-side restart rate limit to prevent infinite restart loops.
/// </summary>
public sealed class RestartServerHandler : ICommandHandler
{
    /// <summary>Exit code signaling the wrapper script to restart immediately.</summary>
    internal const int RestartExitCode = 75;

    /// <summary>Maximum intentional restarts allowed within <see cref="RestartWindowHours"/>.</summary>
    internal const int MaxRestartsPerWindow = 10;

    /// <summary>Sliding window (in hours) for restart rate limiting.</summary>
    internal const int RestartWindowHours = 1;

    /// <summary>Serializes restart check-and-act to prevent concurrent bypass.</summary>
    private static readonly SemaphoreSlim RestartGate = new(1, 1);

    public string CommandName => "RESTART_SERVER";
    public bool IsDestructive => true;
    public string DestructiveWarning => "RESTART_SERVER will shut down and restart the server process. All in-flight agent rounds will be interrupted.";

    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<RestartServerHandler> _logger;

    public RestartServerHandler(
        IHostApplicationLifetime lifetime,
        ILogger<RestartServerHandler> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!RestartServerCommand.TryParse(command.Args, out var parsed, out var error))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = error
            };
        }

        // Authorization: only Planner role agents can restart the server.
        if (!string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Agent {AgentId} ({Role}) attempted RESTART_SERVER — denied (Planner only)",
                context.AgentId, context.AgentRole);

            return command with
            {
                Status = CommandStatus.Denied,
                ErrorCode = CommandErrorCode.Permission,
                Error = "RESTART_SERVER is restricted to Planner role agents."
            };
        }

        // Serialize check-and-act to prevent concurrent callers from both passing
        // the rate limit check simultaneously.
        await RestartGate.WaitAsync();
        try
        {
            // Rate limit: prevent restart loops by counting recent intentional restarts.
            var db = context.Services.GetRequiredService<AgentAcademyDbContext>();
            var windowStart = DateTime.UtcNow.AddHours(-RestartWindowHours);
            var recentRestartCount = await db.ServerInstances
                .Where(si => si.ExitCode == RestartExitCode
                    && si.ShutdownAt != null
                    && si.ShutdownAt > windowStart)
                .CountAsync();

            if (recentRestartCount >= MaxRestartsPerWindow)
            {
                _logger.LogError(
                    "RESTART_SERVER denied — {Count} intentional restarts in the last {Window}h (limit: {Max}). Possible restart loop.",
                    recentRestartCount, RestartWindowHours, MaxRestartsPerWindow);

                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.RateLimit,
                    Error = $"Restart rate limit exceeded: {recentRestartCount} restarts in the last {RestartWindowHours} hour(s) (max {MaxRestartsPerWindow}). " +
                            "This may indicate a restart loop. Wait for the window to expire or investigate the root cause."
                };
            }

            _logger.LogWarning(
                "Server restart requested by {AgentId}: {Reason} (restart {Count}/{Max} in window)",
                context.AgentId, parsed!.Reason, recentRestartCount + 1, MaxRestartsPerWindow);

            // Post a system message so the restart is visible in chat history.
            try
            {
                var catalog = context.Services.GetRequiredService<IAgentCatalog>();
        var messages = context.Services.GetRequiredService<MessageService>();
                await messages.PostSystemStatusAsync(catalog.DefaultRoomId,
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
                    ["restartsInWindow"] = recentRestartCount + 1,
                    ["maxRestartsPerWindow"] = MaxRestartsPerWindow,
                    ["message"] = "Server restart initiated. The wrapper script will restart the process."
                }
            };
        }
        finally
        {
            RestartGate.Release();
        }
    }
}
