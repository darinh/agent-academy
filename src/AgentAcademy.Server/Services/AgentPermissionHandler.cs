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
/// The denial counter is scoped per <see cref="Create"/> call (per Copilot
/// session). The closure holds a <see cref="DenialState"/> instance that is
/// garbage-collected together with the session config — no static state
/// accumulates across session churn.
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
    /// Logs the first 3 denials at Warning, the 4th as a suppression
    /// notice, and subsequent denials at Debug.
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
        // Per-handler state. Captured by the returned closure, so it is
        // scoped to exactly one Copilot session and GCs with that session.
        var state = new DenialState();

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
            var count = Interlocked.Increment(ref state.Count);

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
    /// No-op retained for backward compatibility. Denial counts are now
    /// closure-local and GC'd with the session; callers no longer need to
    /// clear state manually.
    /// </summary>
    [Obsolete("Denial counts are now closure-local and GC'd with the session; this call is a no-op.")]
    public static void ClearSession(string sessionId)
    {
        // Intentional no-op.
    }

    private sealed class DenialState
    {
        public int Count;
    }
}
