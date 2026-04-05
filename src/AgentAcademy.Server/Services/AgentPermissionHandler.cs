using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Custom permission handler for the Copilot SDK that approves only
/// tool call permissions and denies dangerous kinds (shell, write, URL).
///
/// When no tools are registered for a session, approves all permissions
/// (same as <see cref="PermissionHandler.ApproveAll"/> — nothing to gate).
/// </summary>
public static class AgentPermissionHandler
{
    // Permission kinds that are safe for our read-only tools.
    // The SDK fires permission requests for various operation types;
    // we deny anything that could execute shell commands, write files,
    // or make network requests.
    private static readonly HashSet<string> SafeKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "custom-tool",
        "read",
        "tool",
    };

    /// <summary>
    /// Creates a <see cref="PermissionRequestHandler"/> that approves
    /// tool-related permissions and denies unrecognized request kinds.
    /// </summary>
    /// <param name="registeredToolNames">
    /// The set of tool names registered for the session. If empty,
    /// all permissions are approved (no tools = nothing to gate).
    /// </param>
    /// <param name="logger">Logger for diagnostics.</param>
    public static PermissionRequestHandler Create(
        IReadOnlySet<string> registeredToolNames,
        ILogger logger)
    {
        return (PermissionRequest request, PermissionInvocation invocation) =>
        {
            // When no tools are registered, approve everything
            // (same as ApproveAll — nothing to gate).
            if (registeredToolNames.Count == 0)
            {
                return Task.FromResult(new PermissionRequestResult
                {
                    Kind = PermissionRequestResultKind.Approved
                });
            }

            // Approve only safe permission kinds (tool calls, reads).
            // Deny shell execution, file writes, network access, etc.
            if (SafeKinds.Contains(request.Kind))
            {
                logger.LogDebug(
                    "Approved permission: Kind={Kind}, Session={SessionId}",
                    request.Kind, invocation.SessionId);

                return Task.FromResult(new PermissionRequestResult
                {
                    Kind = PermissionRequestResultKind.Approved
                });
            }

            logger.LogWarning(
                "Denied permission request: Kind={Kind}, Session={SessionId}",
                request.Kind, invocation.SessionId);

            return Task.FromResult(new PermissionRequestResult
            {
                Kind = PermissionRequestResultKind.DeniedByRules
            });
        };
    }
}
