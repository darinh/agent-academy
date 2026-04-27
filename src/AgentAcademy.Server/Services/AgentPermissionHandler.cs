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

            // Subtype-specific diagnostic payload. The base PermissionRequest
            // only exposes Kind; the actionable detail (what command, what file,
            // what tool-call ID) lives on the concrete subtypes. Without this
            // we can't distinguish "model invoked an SDK builtin we don't gate"
            // from "session-lifecycle hook before tool registration" — both
            // surface as bare "Kind=shell" denials. See: PermissionRequestShell,
            // PermissionRequestWrite, PermissionRequestUrl, etc. in SDK 0.2.2.
            //
            // Compute the detail string lazily — only if the corresponding log
            // level is enabled. DescribeRequest does several string allocations
            // (truncation, replace) that are wasted when the sink is filtered
            // (e.g., LogDebug disabled in Production).
            if (isNewKind)
            {
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    var detail = DescribeRequest(request);
                    logger.LogWarning(
                        "Denied permission request: Kind={Kind}, Session={SessionId}, Detail={Detail} (first denial of this Kind in session, total denials={TotalCount})",
                        request.Kind, sessionId, detail, totalCount);
                }
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    var detail = DescribeRequest(request);
                    logger.LogDebug(
                        "Denied permission request: Kind={Kind}, Session={SessionId}, Detail={Detail} (denial #{KindCount} of this Kind, total denials={TotalCount})",
                        request.Kind, sessionId, detail, kindCount, totalCount);
                }
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

    /// <summary>
    /// Builds a compact, structured one-line description of a
    /// <see cref="PermissionRequest"/> for diagnostic logging. Pattern-matches
    /// on the concrete SDK subtype so callers see the actionable fields
    /// (command text, file name, URL, tool-call ID, intention) rather than
    /// only the opaque <c>Kind</c>.
    ///
    /// Truncates long fields (FullCommandText, Intention, NewFileContents) to
    /// keep log lines structured-log-friendly and bounded.
    ///
    /// Applies a best-effort redaction pass over free-text fields to strip
    /// common token shapes (GitHub PATs, bearer tokens, "key=" / "token=" /
    /// "password=" assignments). This is defence in depth — the agent's
    /// request was denied so nothing actually executed, but the log still
    /// lands in <c>/tmp/aa-dev.log</c> which is world-readable on the dev
    /// host. Redaction is pattern-based, not exhaustive — do not rely on it
    /// to scrub arbitrary secrets the model decides to embed.
    /// </summary>
    internal static string DescribeRequest(PermissionRequest request)
    {
        // Cap free-text fields. The agent-stated Intention can be paragraphs;
        // FullCommandText can be a multi-line heredoc. We only need enough to
        // recognise the operation in a tail of the log.
        const int maxField = 240;
        static string Trunc(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "(empty)";
            var redacted = Redact(s);
            var oneLine = redacted.Replace('\n', ' ').Replace('\r', ' ');
            return oneLine.Length <= maxField ? oneLine : oneLine.Substring(0, maxField) + "…";
        }

        return request switch
        {
            PermissionRequestShell s =>
                $"shell tool_call_id={s.ToolCallId ?? "(null)"} cmd=\"{Trunc(s.FullCommandText)}\" intention=\"{Trunc(s.Intention)}\" has_write_redirect={s.HasWriteFileRedirection} cmd_count={s.Commands?.Length ?? 0}",
            PermissionRequestWrite w =>
                $"write tool_call_id={w.ToolCallId ?? "(null)"} file=\"{w.FileName ?? "(null)"}\" intention=\"{Trunc(w.Intention)}\" has_diff={!string.IsNullOrEmpty(w.Diff)}",
            PermissionRequestRead r =>
                $"read tool_call_id={r.ToolCallId ?? "(null)"} path=\"{r.Path ?? "(null)"}\" intention=\"{Trunc(r.Intention)}\"",
            PermissionRequestUrl u =>
                $"url tool_call_id={u.ToolCallId ?? "(null)"} url=\"{RedactUrl(u.Url)}\" intention=\"{Trunc(u.Intention)}\"",
            PermissionRequestMcp m =>
                $"mcp tool_call_id={m.ToolCallId ?? "(null)"} server=\"{m.ServerName ?? "(null)"}\" tool=\"{m.ToolName ?? "(null)"}\" read_only={m.ReadOnly}",
            PermissionRequestMemory mem =>
                $"memory tool_call_id={mem.ToolCallId ?? "(null)"} subject=\"{Trunc(mem.Subject)}\" fact=\"{Trunc(mem.Fact)}\"",
            PermissionRequestCustomTool ct =>
                $"custom-tool tool_call_id={ct.ToolCallId ?? "(null)"} tool=\"{ct.ToolName ?? "(null)"}\" desc=\"{Trunc(ct.ToolDescription)}\"",
            PermissionRequestHook h =>
                $"hook tool_call_id={h.ToolCallId ?? "(null)"} tool=\"{h.ToolName ?? "(null)"}\" hook_msg=\"{Trunc(h.HookMessage)}\"",
            _ => $"(unrecognized subtype {request.GetType().Name})",
        };
    }

    // Redacts common secret shapes so denial logs (which include the rejected
    // command/intention/URL) don't accidentally leak credentials the agent
    // assembled. The patterns are intentionally narrow — high-precision over
    // high-recall. Anything matched is replaced with "[REDACTED]".
    //
    // Patterns covered:
    //   - GitHub tokens: gh[opsu]_<base62 ≥36>
    //   - HTTP bearer tokens: "Authorization: Bearer <token>" / "Bearer <token>"
    //   - "key=", "token=", "secret=", "password=", "api_key=" assignments
    //   - JSON-encoded secret fields: "token":"...", "password":"...", etc.
    //     (matters because tool arguments and results are commonly JSON blobs)
    //   - generic AWS-style 40-char base64 keys when prefixed by typical assignments
    //
    // Patterns NOT covered (knowingly): arbitrary high-entropy strings, JWTs
    // mid-payload, anything novel the model invents. Treat the redactor as
    // defence-in-depth, not a guarantee.
    private static readonly System.Text.RegularExpressions.Regex SecretPatterns = new(
        @"(?ix)
            gh[opsu]_[A-Za-z0-9]{36,}                                              # GitHub PAT/OAuth/server/user tokens
            | authorization\s*[:=]\s*\S+(?:\s+\S+)?                                # Authorization headers (Bearer + token)
            | bearer\s+[A-Za-z0-9._\-]{16,}                                        # bare Bearer tokens
            | (?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|client[_-]?secret)\s*[:=]\s*[^\s""'&]{4,}
            | ""(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|client[_-]?secret)""\s*:\s*""[^""]{4,}""   # JSON-encoded secret fields
        ",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return SecretPatterns.Replace(input, "[REDACTED]");
    }

    // URLs warrant their own redactor: keep scheme+host (the diagnostic value
    // is "what host did the model try to call?") but strip path and query
    // (which often carry tokens like ?access_token=…). Falls back to the raw
    // value when parsing fails so we still see something.
    internal static string RedactUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "(null)";
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return $"{u.Scheme}://{u.Host}{(u.IsDefaultPort ? "" : ":" + u.Port)}/[…]";
        }
        return Redact(url);
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
