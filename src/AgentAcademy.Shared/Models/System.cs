using System.ComponentModel.DataAnnotations;

namespace AgentAcademy.Shared.Models;

/// <summary>
/// Basic health check result returned by the health endpoint.
/// </summary>
public record HealthResult(
    string Status,
    string Uptime,
    DateTime Timestamp,
    string Message = "Agent Academy backend is healthy."
);

/// <summary>
/// Canonical Copilot availability states surfaced to the client auth flow.
/// </summary>
public static class CopilotStatusValues
{
    public const string Operational = "operational";
    public const string Degraded = "degraded";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// Minimal authenticated GitHub user profile returned by auth status checks.
/// </summary>
public record AuthUserInfo(
    string Login,
    string? Name,
    string? AvatarUrl
);

/// <summary>
/// Auth status contract for the frontend login gate.
/// </summary>
public record AuthStatusResult(
    bool AuthEnabled,
    bool Authenticated,
    string CopilotStatus,
    AuthUserInfo? User = null
);

/// <summary>
/// Detailed health check response including dependency statuses.
/// </summary>
public record HealthCheckResponse(
    string Status,
    List<DependencyStatus> Dependencies,
    double Uptime,
    DateTime Timestamp
);

/// <summary>
/// Information about an available LLM model.
/// </summary>
public record ModelInfo(
    string Id,
    string Name
);

/// <summary>
/// Security permissions granted to an agent, controlling tool and resource access.
/// </summary>
public record PermissionPolicy(
    bool AllowFileAccess,
    bool AllowMcpServers,
    bool AllowShellExecution,
    bool AllowUrlFetch,
    List<string> AllowedToolCategories
);

/// <summary>
/// Health status of an individual external dependency (e.g., database, LLM provider).
/// </summary>
public record DependencyStatus(
    string Name,
    string Status,
    string? Detail = null
);

/// <summary>
/// Aggregated token and cost usage for a collaboration session.
/// </summary>
public record UsageSummary(
    long TotalInputTokens,
    long TotalOutputTokens,
    double TotalCost,
    int RequestCount,
    List<string> Models
);

/// <summary>
/// Record of an error encountered by an agent during collaboration.
/// </summary>
public record ErrorRecord(
    string AgentId,
    string RoomId,
    string ErrorType,
    string Message,
    bool Recoverable,
    DateTime Timestamp
);

/// <summary>
/// Aggregated error summary with breakdowns by type and agent.
/// </summary>
public record ErrorSummary(
    int TotalErrors,
    int RecoverableErrors,
    int UnrecoverableErrors,
    List<ErrorCountByType> ByType,
    List<ErrorCountByAgent> ByAgent
);

/// <summary>Error count grouped by error type.</summary>
public record ErrorCountByType(string ErrorType, int Count);

/// <summary>Error count grouped by agent ID.</summary>
public record ErrorCountByAgent(string AgentId, int Count);

/// <summary>
/// Per-agent usage breakdown within a room.
/// </summary>
public record AgentUsageSummary(
    string AgentId,
    long TotalInputTokens,
    long TotalOutputTokens,
    double TotalCost,
    int RequestCount
);

/// <summary>
/// Individual LLM API call usage record.
/// </summary>
public record LlmUsageRecord(
    string Id,
    string AgentId,
    string? RoomId,
    string? Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    double? Cost,
    int? DurationMs,
    string? ReasoningEffort,
    DateTime RecordedAt
);

/// <summary>
/// Aggregated agent usage within a time window, used for quota enforcement.
/// </summary>
public record AgentUsageWindow(
    int RequestCount,
    long TotalTokens,
    decimal TotalCost
);

/// <summary>
/// Result of a quota check — whether the agent is allowed to proceed.
/// </summary>
public record QuotaStatus(
    string AgentId,
    bool IsAllowed,
    string? DeniedReason,
    int? RetryAfterSeconds,
    ResourceQuota? ConfiguredQuota,
    AgentUsageWindow? CurrentUsage
);

/// <summary>
/// Wrapper for plan content text.
/// </summary>
public record PlanContent(
    [Required, MinLength(1), StringLength(100_000)] string Content
);

/// <summary>
/// Instance-level health result for client reconnect protocol.
/// Clients compare <see cref="InstanceId"/> to detect server restarts.
/// </summary>
public record InstanceHealthResult(
    string InstanceId,
    DateTime StartedAt,
    string Version,
    bool CrashDetected,
    bool ExecutorOperational,
    bool AuthFailed,
    string CircuitBreakerState = "Closed"
);

/// <summary>
/// Snapshot of a git worktree's status, enriched with linked task and agent info.
/// </summary>
public record WorktreeStatusSnapshot(
    string Branch,
    string RelativePath,
    DateTimeOffset CreatedAt,
    bool StatusAvailable,
    string? Error,
    int TotalDirtyFiles,
    List<string> DirtyFilesPreview,
    int FilesChanged,
    int Insertions,
    int Deletions,
    string? LastCommitSha,
    string? LastCommitMessage,
    string? LastCommitAuthor,
    DateTimeOffset? LastCommitDate,
    string? TaskId,
    string? TaskTitle,
    string? TaskStatus,
    string? AgentId,
    string? AgentName
);
