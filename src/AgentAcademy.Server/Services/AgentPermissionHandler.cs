using GitHub.Copilot.SDK;
using AgentAcademy.Server.Services.AgentWatchdog;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Custom permission handler for the Copilot SDK that approves the
/// permission kinds implied by the agent's registered tools, plus the
/// always-safe envelope kinds (tool/custom-tool/read). Denies everything
/// else (e.g., "url" when no network tool is registered).
///
/// IMPORTANT: the safe-kinds set is derived from <c>registeredToolNames</c>
/// at handler creation time. If a tool is registered (e.g., <c>write_file</c>)
/// but its underlying permission kind (e.g., "write") is denied, the SDK
/// silently retries until the per-turn budget is exhausted and the agent
/// stops responding entirely (see incident: Sprint #2 self-drive hang on
/// 2026-04-25). This is why the handler MUST consent to operations implied
/// by every tool it gates — see <c>ToolImpliedKinds</c>.
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
    // Permission kinds that are ALWAYS safe regardless of which tools the
    // agent has — these are tool-invocation envelopes (the SDK fires them
    // when calling any registered AIFunction), read operations, and SDK
    // lifecycle hooks.
    //
    // Per inspection of GitHub.Copilot.SDK 0.2.2, the complete set of
    // PermissionRequest discriminators the SDK can fire is:
    //   custom-tool, hook, mcp, memory, read, shell, tool, url, write.
    //
    // - "tool" / "custom-tool" / "read": tool-invocation envelopes — fired
    //   for every registered AIFunction call. Denying these stops the agent.
    // - "hook": SDK session-lifecycle hooks. Fires during session priming,
    //   before any user-initiated tool runs. Denying it surfaces as an
    //   "unexpected user permission response" on the very first turn —
    //   observed during P1.9 supervised acceptance run, 2026-04-26.
    //
    // The remaining four kinds (mcp, memory, shell, url, write) are NOT
    // in this set by default — they're either denied outright (mcp, memory,
    // url) or gated by tool registration via <see cref="ToolImpliedKinds"/>
    // (shell, write).
    private static readonly HashSet<string> AlwaysSafeKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "custom-tool",
        "hook",
        "read",
        "tool",
    };

    // Maps registered tool name → underlying permission kind(s) the SDK
    // emits when that tool actually executes. Keyed case-sensitively to
    // match how tools are registered (lowercase, snake_case in this
    // codebase). Values are matched case-insensitively against
    // <see cref="PermissionRequest.Kind"/>.
    //
    // Rationale: if we explicitly registered `write_file`, then the agent
    // is authorised to write — denying the underlying "write" permission
    // request causes the SDK to either silently retry until the per-turn
    // budget is exhausted (observed: agent stops responding entirely) or
    // returns an unhelpful failure. The handler MUST consent to the
    // operations implied by the tool surface it gates, otherwise the
    // tool registry and the permission policy disagree and the agent hangs.
    //
    // Add new entries here when introducing tools that need shell/write/url
    // permissions. Tools that only need "tool"/"custom-tool"/"read" do not
    // need an entry — those are in <see cref="AlwaysSafeKinds"/>.
    private static readonly IReadOnlyDictionary<string, string[]> ToolImpliedKinds =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // write_file persists files inside the workspace.
            ["write_file"] = ["write"],
            // commit_changes shells out to git for staging + committing,
            // and may write working-tree files (e.g., index updates).
            ["commit_changes"] = ["shell", "write"],
        };

    /// <summary>
    /// Creates a <see cref="PermissionRequestHandler"/> that approves
    /// permissions implied by the agent's registered tools, plus the
    /// always-safe kinds (tool envelopes, reads, SDK lifecycle hooks).
    /// Denies everything else.
    ///
    /// Denial logging is deduplicated by <see cref="PermissionRequest.Kind"/>:
    /// the FIRST time a given Kind is denied in this session it logs at
    /// Warning (so a previously-unseen Kind — typically introduced by a new
    /// SDK version — is always visible regardless of denial volume); every
    /// subsequent denial of the same Kind logs at Debug to avoid spam.
    /// This makes the diagnostic property structurally repro-free: a future
    /// SDK can add a new kind and we will see it on the very first denial
    /// even at default log level.
    /// </summary>
    /// <param name="registeredToolNames">
    /// The set of tool names registered for the session. If empty, all
    /// permissions are approved (no tools = nothing to gate). When
    /// non-empty, the safe-kinds set is extended with the union of kinds
    /// implied by the registered tools (see <see cref="ToolImpliedKinds"/>).
    /// </param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="livenessTracker">
    /// Watchdog liveness tracker. The handler attributes approve/deny events
    /// to the in-flight turn by SDK <c>SessionId</c> (resolved via the
    /// tracker's session→turn map maintained by <c>CopilotSdkSender</c>).
    /// Approvals call <see cref="IAgentLivenessTracker.NoteProgressBySessionId"/>
    /// — they count as forward progress. Denials call
    /// <see cref="IAgentLivenessTracker.IncrementDenialBySessionId"/> — they
    /// bump the per-turn denial counter without resetting the stall timer,
    /// so a denial storm trips the watchdog's denial-count trigger.
    /// </param>
    public static PermissionRequestHandler Create(
        IReadOnlySet<string> registeredToolNames,
        ILogger logger,
        IAgentLivenessTracker livenessTracker)
    {
        // Per-handler state. Captured by the returned closure, so it is
        // scoped to exactly one Copilot session and GCs with that session.
        var state = new DenialState();
        var tracker = livenessTracker;

        // Pre-compute the effective safe-kinds set for this session by
        // unioning the always-safe kinds with the kinds implied by every
        // registered tool. Computing once at handler creation (not per
        // request) keeps the hot path allocation-free.
        var effectiveSafeKinds = new HashSet<string>(AlwaysSafeKinds, StringComparer.OrdinalIgnoreCase);
        foreach (var toolName in registeredToolNames)
        {
            if (ToolImpliedKinds.TryGetValue(toolName, out var implied))
            {
                foreach (var kind in implied)
                    effectiveSafeKinds.Add(kind);
            }
        }

        return (PermissionRequest request, PermissionInvocation invocation) =>
        {
            // When no tools are registered, approve everything
            // (same as ApproveAll — nothing to gate).
            if (registeredToolNames.Count == 0)
            {
                tracker.NoteProgressBySessionId(invocation.SessionId, "perm:approve:" + request.Kind);
                return Task.FromResult(new PermissionRequestResult
                {
                    Kind = PermissionRequestResultKind.Approved
                });
            }

            // Approve safe kinds + kinds implied by the agent's registered
            // tools. Anything else (e.g., "url" with no network tool) is denied.
            if (effectiveSafeKinds.Contains(request.Kind))
            {
                tracker.NoteProgressBySessionId(invocation.SessionId, "perm:approve:" + request.Kind);
                logger.LogDebug(
                    "Approved permission: Kind={Kind}, Session={SessionId}",
                    request.Kind, invocation.SessionId);

                return Task.FromResult(new PermissionRequestResult
                {
                    Kind = PermissionRequestResultKind.Approved
                });
            }

            var sessionId = invocation.SessionId ?? "unknown";
            var totalCount = Interlocked.Increment(ref state.Count);

            // Bump the per-turn denial counter on the tracker. The watchdog
            // uses this for the denial-storm stall trigger. If no turn is
            // linked (e.g., session priming, before LinkSession), this is
            // a no-op — by design.
            tracker.IncrementDenialBySessionId(invocation.SessionId, request.Kind);

            // Dedup by Kind: log NEW kinds at Warning (always visible),
            // repeats at Debug (suppress spam). Single dictionary keyed by
            // Kind tracks both presence and count; access is serialised by
            // a lock — contention is naturally low (one permission request
            // in flight per session turn).
            var key = request.Kind ?? string.Empty;
            bool isNewKind;
            int kindCount;
            lock (state.KindCounts)
            {
                if (state.KindCounts.TryGetValue(key, out var existing))
                {
                    isNewKind = false;
                    kindCount = existing + 1;
                    state.KindCounts[key] = kindCount;
                }
                else
                {
                    isNewKind = true;
                    kindCount = 1;
                    state.KindCounts[key] = 1;
                }
            }

            if (isNewKind)
            {
                logger.LogWarning(
                    "Denied permission request: Kind={Kind}, Session={SessionId} (first denial of this Kind in session, total denials={TotalCount})",
                    request.Kind, sessionId, totalCount);
            }
            else
            {
                logger.LogDebug(
                    "Denied permission request: Kind={Kind}, Session={SessionId} (denial #{KindCount} of this Kind, total denials={TotalCount})",
                    request.Kind, sessionId, kindCount, totalCount);
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
        // Tracks denial counts per distinct Kind for dedup-based logging.
        // Access is gated by lock(KindCounts) — kept simple over a concurrent
        // collection because contention here is naturally serialised by the
        // SDK's permission-request cadence (one in flight per session turn).
        public readonly Dictionary<string, int> KindCounts =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
