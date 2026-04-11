using System.Collections.Concurrent;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Custom permission handler for the Copilot SDK that approves only
/// tool call permissions and denies dangerous kinds (shell, write, URL).
///
/// When no tools are registered for a session, approves all permissions
/// (same as <see cref="PermissionHandler.ApproveAll"/> — nothing to gate).
///
/// Tracks denial counts per session to log escalating warnings and avoid
/// flooding the log with identical denial messages.
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

    // Per-session denial counter to reduce log noise. Key: sessionId.
    private static readonly ConcurrentDictionary<string, int> DenialCounts = new();

    /// <summary>
    /// Creates a <see cref="PermissionRequestHandler"/> that approves
    /// tool-related permissions and denies unrecognized request kinds.
    /// Logs the first 3 denials per session at Warning, then only at Debug.
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

            var sessionId = invocation.SessionId ?? "unknown";
            var count = DenialCounts.AddOrUpdate(sessionId, 1, (_, c) => c + 1);

            if (count <= 3)
            {
                logger.LogWarning(
                    "Denied permission request: Kind={Kind}, Session={SessionId} (denial #{Count})",
                    request.Kind, sessionId, count);
            }
            else if (count == 4)
            {
                logger.LogWarning(
                    "Denied permission request: Kind={Kind}, Session={SessionId} — suppressing further warnings ({Count} total denials)",
                    request.Kind, sessionId, count);
            }
            else
            {
                logger.LogDebug(
                    "Denied permission request: Kind={Kind}, Session={SessionId} (denial #{Count})",
                    request.Kind, sessionId, count);
            }

            return Task.FromResult(new PermissionRequestResult
            {
                Kind = PermissionRequestResultKind.DeniedByRules
            });
        };
    }

    /// <summary>
    /// Clears denial counts for a session that has been disposed.
    /// </summary>
    public static void ClearSession(string sessionId)
    {
        DenialCounts.TryRemove(sessionId, out _);
    }
}
