using GitHub.Copilot.SDK;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Builds compact, structured one-line diagnostic descriptions of the SDK's
/// tool-execution lifecycle events (<c>tool.execution_start</c>,
/// <c>tool.execution_progress</c>, <c>tool.execution_complete</c>) and
/// session warnings.
///
/// These events are observability-only — they do NOT change orchestration
/// behaviour. They exist to disambiguate sessions that stall mid-turn:
/// <see cref="AgentPermissionHandler"/> only sees requests the SDK gates
/// through <c>OnPermissionRequest</c>, so when an agent calls an SDK
/// builtin (e.g. <c>apply_patch</c>, <c>str_replace_editor</c>, <c>bash</c>)
/// that runs without a permission round-trip, the existing denial logs are
/// blank and we have no idea what the model is doing. Logging tool start/
/// complete events fills that blind spot.
///
/// Free-text fields (<c>Arguments</c>, <c>ProgressMessage</c>, <c>Result</c>,
/// <c>Error</c>) are bounded and run through <see cref="AgentPermissionHandler.Redact"/>
/// to strip common token shapes — a tool's arguments are server-side
/// untrusted input from the model's perspective and may embed credentials
/// the model assembled. The redaction is best-effort defence in depth, not
/// a guarantee.
/// </summary>
internal static class ToolEventDescriber
{
    private const int MaxFieldLength = 240;

    /// <summary>
    /// Renders a <see cref="ToolExecutionStartEvent"/> as a single
    /// structured log line. Captures tool name, call id, MCP server (if
    /// any), parent tool call (for sub-agent calls), and a redacted +
    /// truncated argument blob.
    /// </summary>
    internal static string DescribeStart(ToolExecutionStartData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var mcpClause = string.IsNullOrEmpty(data.McpServerName)
            ? ""
            : $" mcp_server=\"{data.McpServerName}\" mcp_tool=\"{data.McpToolName ?? "(null)"}\"";
        var parentClause = string.IsNullOrEmpty(data.ParentToolCallId)
            ? ""
            : $" parent_tool_call_id={data.ParentToolCallId}";

        return $"tool_start tool=\"{data.ToolName ?? "(null)"}\" tool_call_id={data.ToolCallId ?? "(null)"} args=\"{Trunc(data.Arguments?.ToString())}\"{mcpClause}{parentClause}";
    }

    /// <summary>
    /// Renders a <see cref="ToolExecutionProgressEvent"/> as a single
    /// structured log line. Useful for long-running MCP tools that emit
    /// progress notifications between start and complete.
    /// </summary>
    internal static string DescribeProgress(ToolExecutionProgressData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return $"tool_progress tool_call_id={data.ToolCallId ?? "(null)"} msg=\"{Trunc(data.ProgressMessage)}\"";
    }

    /// <summary>
    /// Renders a <see cref="ToolExecutionCompleteEvent"/> as a single
    /// structured log line. Includes success flag, model identifier,
    /// elapsed milliseconds (when supplied by the caller — the SDK
    /// payload itself doesn't carry duration), and a truncated +
    /// redacted result/error preview.
    /// </summary>
    internal static string DescribeComplete(ToolExecutionCompleteData data, long? elapsedMs = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        var elapsedClause = elapsedMs is { } ms ? $" elapsed_ms={ms}" : "";
        // ToolExecutionCompleteData.Result is on success; .Error/.ErrorMessage
        // on failure. Probe both via reflection-safe property access — the
        // SDK shapes vary across versions and we'd rather log "(no result)"
        // than crash the diagnostic path.
        var resultPreview = ExtractResultPreview(data);

        return $"tool_complete tool_call_id={data.ToolCallId ?? "(null)"} success={data.Success} model=\"{data.Model ?? "(null)"}\" interaction_id={data.InteractionId ?? "(null)"}{elapsedClause} result=\"{resultPreview}\"";
    }

    /// <summary>
    /// Renders a <see cref="SessionWarningEvent"/> as a single
    /// structured log line. The SDK fires these for situations that
    /// don't error out the session but the host should know about
    /// (e.g. context approaching the buffer threshold, MCP connection
    /// flapping).
    /// </summary>
    internal static string DescribeWarning(SessionWarningData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        // RedactUrl strips path+query (which often carry tokens), keeping
        // scheme+host so we still see what host the warning points at.
        // Trunc handles the URL clause length cap and quote-escaping.
        var urlClause = string.IsNullOrEmpty(data.Url)
            ? ""
            : $" url=\"{Trunc(AgentPermissionHandler.RedactUrl(data.Url))}\"";
        return $"session_warning category=\"{data.WarningType ?? "(unknown)"}\" msg=\"{Trunc(data.Message)}\"{urlClause}";
    }

    private static string ExtractResultPreview(ToolExecutionCompleteData data)
    {
        if (data.Success)
        {
            var resultObj = data.Result;
            if (resultObj is null) return "(empty)";
            // Prefer the LLM-facing Content (already token-trimmed by the
            // SDK); fall back to DetailedContent for the UI-facing payload.
            // Trunc applies its own length cap on top.
            var preview = !string.IsNullOrEmpty(resultObj.Content)
                ? resultObj.Content
                : resultObj.DetailedContent;
            return Trunc(preview);
        }

        var errorObj = data.Error;
        if (errorObj is null) return "(no error detail)";
        var codeClause = string.IsNullOrEmpty(errorObj.Code) ? "" : $"[{errorObj.Code}] ";
        return $"ERROR: {codeClause}{Trunc(errorObj.Message)}";
    }

    /// <summary>
    /// Cap free-text fields at <see cref="MaxFieldLength"/> chars, replace
    /// CR/LF with spaces (so the entry stays a single log line), run the
    /// result through the shared secret redactor, then escape embedded
    /// quotes/backslashes so the surrounding `key="value"` wrapper remains
    /// unambiguous to grep/jq downstream consumers (tool arguments and
    /// results are routinely JSON, which contains literal `"`). Mirrors the
    /// policy in <see cref="AgentPermissionHandler.DescribeRequest"/> with
    /// the added escape pass.
    /// </summary>
    internal static string Trunc(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        var redacted = AgentPermissionHandler.Redact(value);
        var oneLine = redacted.Replace('\n', ' ').Replace('\r', ' ');
        var capped = oneLine.Length <= MaxFieldLength
            ? oneLine
            : oneLine.Substring(0, MaxFieldLength) + "…";
        return EscapeForLogField(capped);
    }

    /// <summary>
    /// Escape embedded backslashes and quotes so a value rendered inside
    /// a `key="value"` clause cannot prematurely close the wrapper or
    /// inject ambiguous separators into the structured log line.
    /// </summary>
    private static string EscapeForLogField(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        // Order matters: backslashes first so we don't double-escape the
        // backslashes we're about to introduce in front of quotes.
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
