using System.Text.RegularExpressions;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Commands;

/// <summary>
/// Checks agent permissions against the command being executed.
/// Default-deny: only explicitly allowed commands are permitted.
/// Supports wildcard patterns (e.g., "READ_*", "LIST_*", "*").
/// </summary>
public sealed class CommandAuthorizer
{
    private static readonly Dictionary<string, HashSet<string>> RestrictedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SHELL"] = new(StringComparer.OrdinalIgnoreCase) { "Planner", "Reviewer" }
    };

    /// <summary>
    /// Check if the agent is authorized to execute the given command.
    /// Returns null if authorized, or a denied CommandEnvelope if not.
    /// </summary>
    public CommandEnvelope? Authorize(CommandEnvelope command, AgentDefinition agent)
    {
        var permissions = agent.Permissions;

        // No permissions configured = deny all commands
        if (permissions == null)
            return Deny(command, $"Agent '{agent.Name}' has no command permissions configured.");

        // Check explicit deny first (takes priority over allow)
        if (permissions.Denied.Any(p => MatchesPattern(command.Command, p)))
            return Deny(command, $"Command '{command.Command}' is explicitly denied for agent '{agent.Name}'.");

        // Check allow list
        if (permissions.Allowed.Any(p => MatchesPattern(command.Command, p)))
        {
            if (RestrictedRoles.TryGetValue(command.Command, out var allowedRoles)
                && !allowedRoles.Contains(agent.Role))
            {
                return Deny(command,
                    $"Command '{command.Command}' is restricted to roles: {string.Join(", ", allowedRoles)}.");
            }

            return null; // Authorized
        }

        // Default deny
        return Deny(command, $"Agent '{agent.Name}' is not authorized to execute '{command.Command}'.");
    }

    /// <summary>
    /// Match a command name against a permission pattern.
    /// Patterns support:
    ///   - Exact match: "READ_FILE"
    ///   - Prefix wildcard: "READ_*"
    ///   - Full wildcard: "*"
    /// </summary>
    private static bool MatchesPattern(string command, string pattern)
    {
        if (pattern == "*")
            return true;

        if (pattern.EndsWith('*'))
        {
            var prefix = pattern[..^1];
            return command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(command, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static CommandEnvelope Deny(CommandEnvelope command, string reason)
    {
        return command with
        {
            Status = CommandStatus.Denied,
            Error = reason,
            Result = null
        };
    }
}
