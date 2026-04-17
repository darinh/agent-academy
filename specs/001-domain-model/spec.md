# 001 — Domain Model

## Purpose
Defines the core domain types used throughout Agent Academy. These types live in `AgentAcademy.Shared.Models` and are referenced by the server, tests, and (via API responses) the frontend.

Types are ported from the v1 TypeScript definitions (`local-agent-host/shared/src/types.ts`) with additions for notifications, commands, agent memory, direct messaging, and observability.

## Current Behavior

> **Status: Implemented** — All types below are compiled C# records/enums in `src/AgentAcademy.Shared/Models/`.

### File Organization

| File | Types | Status |
|------|-------|--------|
| `Models/Enums.cs` | All enumerations (except `NotificationType`) | ✅ Implemented |
| `Models/Agents.cs` | AgentGitIdentity, ResourceQuota, AgentDefinition, AgentPresence, AgentLocation, AgentCatalogOptions, IAgentCatalog | ✅ Implemented |
| `Models/Rooms.cs` | RoomSnapshot, BreakoutRoom, ChatEnvelope, DeliveryHint, RoomMessagesResponse, ConversationSessionSnapshot, SessionListResponse, SessionStats, PhaseGate, PhasePrerequisiteStatus | ✅ Implemented |
| `Models/Tasks.cs` | TaskSnapshot, TaskItem, TaskAssignmentRequest, TaskAssignmentResult, TaskComment, SpecTaskLink, TaskEvidence, GateCheckResult, TaskDependencyInfo, TaskDependencySummary, BulkUpdateStatusRequest, BulkAssignRequest, BulkOperationResult, BulkOperationError | ✅ Implemented |
| `Models/Activity.cs` | ActivityEvent, WorkspaceOverview | ✅ Implemented |
| `Models/Evaluation.cs` | EvaluationResult, ArtifactRecord, MetricsEntry, MetricsSummary | ✅ Implemented |
| `Models/System.cs` | HealthResult, CopilotStatusValues, AuthUserInfo, AuthStatusResult, HealthCheckResponse, ModelInfo, PermissionPolicy, DependencyStatus, UsageSummary, ErrorRecord, ErrorSummary, ErrorCountByType, ErrorCountByAgent, AgentUsageSummary, LlmUsageRecord, AgentUsageWindow, AgentContextUsage, QuotaStatus, PlanContent, InstanceHealthResult, WorktreeStatusSnapshot | ✅ Implemented |
| `Models/Projects.cs` | ProjectScanResult, WorkspaceMeta | ✅ Implemented |
| `Models/Notifications.cs` | NotificationType (enum), NotificationMessage, InputRequest, UserResponse, ProviderConfigSchema, ConfigField, AgentQuestion | ✅ Implemented |
| `Models/AgentMemory.cs` | AgentMemory | ✅ Implemented |
| `Models/Commands.cs` | CommandEnvelope, CommandErrorCode, CommandParseResult, ParsedCommand, CommandPermissionSet | ✅ Implemented |
| `Models/DirectMessages.cs` | DmThreadSummary, DmMessage | ✅ Implemented |
| `Models/HumanCommands.cs` | HumanCommandFieldMetadata, HumanCommandMetadata | ✅ Implemented |
| `Models/Requests.cs` | PostMessageRequest, PhaseTransitionRequest | ✅ Implemented |

### Enumerations

All enums use `[JsonConverter(typeof(JsonStringEnumConverter))]` for JSON string serialization. Most live in `Models/Enums.cs`; `NotificationType` is defined in `Models/Notifications.cs`.

```csharp
public enum CollaborationPhase { Intake, Planning, Discussion, Validation, Implementation, FinalSynthesis }
public enum AgentAvailability { Ready, Preferred, Active, Busy, Offline }
public enum DeliveryPriority { Low, Normal, High, Urgent }
public enum MessageKind { System, TaskAssignment, Coordination, Plan, Status, Review, Validation, Decision, Question, Response, SpecChangeProposal, DirectMessage }
public enum MessageSenderKind { System, Agent, User }
public enum TaskStatus { Queued, Active, Blocked, AwaitingValidation, InReview, ChangesRequested, Approved, Merging, Completed, Cancelled }
public enum TaskType { Feature, Bug, Chore, Spike }
public enum TaskSize { XS, S, M, L, XL }
public enum PullRequestStatus { Open, ReviewRequested, ChangesRequested, Approved, Merged, Closed }
public enum TaskCommentType { Comment, Finding, Evidence, Blocker, Retrospective }
public enum TaskPriority { Critical = 0, High = 1, Medium = 2, Low = 3 }  // lower int = higher urgency
public enum WorkstreamStatus { NotStarted, Ready, InProgress, Blocked, Completed }
public enum RoomStatus { Idle, Active, AttentionRequired, Completed, Archived }
public enum ActivityEventType { AgentLoaded, AgentThinking, AgentFinished, RoomCreated, RoomClosed, TaskCreated, PhaseChanged, MessagePosted, MessageSent, PresenceUpdated, RoomStatusChanged, ArtifactEvaluated, QualityGateChecked, IterationRetried, CheckpointCreated, AgentErrorOccurred, AgentWarningOccurred, SubagentStarted, SubagentCompleted, SubagentFailed, AgentPlanChanged, AgentSnapshotRewound, ToolIntercepted, CommandExecuted, CommandDenied, CommandFailed, TaskClaimed, TaskReleased, TaskApproved, TaskRejected, TaskChangesRequested, TaskStatusUpdated, TaskCommentAdded, TaskPrStatusChanged, AgentRecalled, RoomRenamed, DirectMessageSent, SpecTaskLinked }
public enum ActivitySeverity { Info, Warning, Error }
public enum AgentState { InRoom, Working, Presenting, Idle }
public enum TaskItemStatus { Pending, Active, Done, Rejected }
public enum CommandStatus { Success, Error, Denied }
public enum SpecLinkType { Implements, Modifies, Fixes, References }
public enum EvidencePhase { Baseline, After, Review }
// In Models/Notifications.cs:
public enum NotificationType { AgentThinking, NeedsInput, TaskComplete, TaskFailed, SpecReview, Error }
```

> **Source**: `src/AgentAcademy.Shared/Models/Enums.cs` (and `Notifications.cs` for `NotificationType`)

### Agent Types

```csharp
public record AgentGitIdentity(string AuthorName, string AuthorEmail);
public record ResourceQuota(int? MaxRequestsPerHour, long? MaxTokensPerHour, decimal? MaxCostPerHour);
public record AgentDefinition(string Id, string Name, string Role, string Summary, string StartupPrompt, string? Model, List<string> CapabilityTags, List<string> EnabledTools, bool AutoJoinDefaultRoom, AgentGitIdentity? GitIdentity = null, CommandPermissionSet? Permissions = null, ResourceQuota? Quota = null);
public record AgentPresence(string AgentId, string Name, string Role, AgentAvailability Availability, bool IsPreferred, DateTime LastActivityAt, List<string> ActiveCapabilities);
public record AgentLocation(string AgentId, string RoomId, AgentState State, string? BreakoutRoomId, DateTime UpdatedAt);
public record AgentCatalogOptions(string DefaultRoomId, string DefaultRoomName, List<AgentDefinition> Agents) : IAgentCatalog;

public interface IAgentCatalog
{
    string DefaultRoomId { get; }
    string DefaultRoomName { get; }
    IReadOnlyList<AgentDefinition> Agents { get; }
}
```

> **Source**: `src/AgentAcademy.Shared/Models/Agents.cs`
>
> **Note**: `ResourceQuota` values are nullable — null means unlimited. Token/cost quotas are best-effort (checked pre-call against recent DB records; concurrent calls may slightly overshoot). `IAgentCatalog` is the read-only view consumers inject; the underlying data may swap at runtime when the catalog is hot-reloaded.

### Room Types

```csharp
public record RoomSnapshot(string Id, string Name, string? Topic, RoomStatus Status, CollaborationPhase CurrentPhase, TaskSnapshot? ActiveTask, List<AgentPresence> Participants, List<ChatEnvelope> RecentMessages, DateTime CreatedAt, DateTime UpdatedAt, PhasePrerequisiteStatus? PhaseGates = null);
public record BreakoutRoom(string Id, string Name, string ParentRoomId, string AssignedAgentId, List<TaskItem> Tasks, RoomStatus Status, List<ChatEnvelope> RecentMessages, DateTime CreatedAt, DateTime UpdatedAt);
public record ChatEnvelope(string Id, string RoomId, string SenderId, string SenderName, string? SenderRole, MessageSenderKind SenderKind, MessageKind Kind, string Content, DateTime SentAt, string? CorrelationId = null, string? ReplyToMessageId = null, DeliveryHint? Hint = null);
public record DeliveryHint(string? TargetRole, string? TargetAgentId, DeliveryPriority Priority, bool ReplyRequested);
public record RoomMessagesResponse(List<ChatEnvelope> Messages, bool HasMore);
public record ConversationSessionSnapshot(string Id, string RoomId, string RoomType, int SequenceNumber, string Status, string? Summary, int MessageCount, DateTime CreatedAt, DateTime? ArchivedAt, string? WorkspacePath = null);
public record SessionListResponse(List<ConversationSessionSnapshot> Sessions, int TotalCount);
public record SessionStats(int TotalSessions, int ActiveSessions, int ArchivedSessions, int TotalMessages);
public record PhaseGate(bool Allowed, string? Reason = null);
public record PhasePrerequisiteStatus(Dictionary<string, PhaseGate> Gates);
```

> **Source**: `src/AgentAcademy.Shared/Models/Rooms.cs`
>
> **Note**: `RoomSnapshot.PhaseGates` is populated on read so the UI can disable transition buttons without a separate API call. The `Gates` dictionary is keyed by `CollaborationPhase` string value.

### Task Types

```csharp
public record TaskSnapshot(
    string Id,
    string Title,
    string Description,
    string SuccessCriteria,
    TaskStatus Status,
    TaskType Type,
    CollaborationPhase CurrentPhase,
    string CurrentPlan,
    WorkstreamStatus ValidationStatus,
    string ValidationSummary,
    WorkstreamStatus ImplementationStatus,
    string ImplementationSummary,
    List<string> PreferredRoles,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    TaskSize? Size = null,
    DateTime? StartedAt = null,
    DateTime? CompletedAt = null,
    string? AssignedAgentId = null,
    string? AssignedAgentName = null,
    bool UsedFleet = false,
    List<string>? FleetModels = null,
    string? BranchName = null,
    string? PullRequestUrl = null,
    int? PullRequestNumber = null,
    PullRequestStatus? PullRequestStatus = null,
    string? ReviewerAgentId = null,
    int ReviewRounds = 0,
    List<string>? TestsCreated = null,
    int CommitCount = 0,
    string? MergeCommitSha = null,
    int CommentCount = 0,
    string? WorkspacePath = null,
    string? SprintId = null,
    List<string>? DependsOnTaskIds = null,
    List<string>? BlockingTaskIds = null,
    TaskPriority Priority = TaskPriority.Medium
);
public record TaskItem(string Id, string Title, string Description, TaskItemStatus Status, string AssignedTo, string RoomId, string? BreakoutRoomId, string? Evidence, string? Feedback, DateTime CreatedAt, DateTime UpdatedAt);
public record TaskAssignmentRequest(string Title, string Description, string SuccessCriteria, string? RoomId, List<string> PreferredRoles, TaskType Type = TaskType.Feature, string? CorrelationId = null, string? CurrentPlan = null, TaskPriority Priority = TaskPriority.Medium);
public record TaskAssignmentResult(string CorrelationId, RoomSnapshot Room, TaskSnapshot Task, ActivityEvent Activity);
public record TaskComment(string Id, string TaskId, string AgentId, string AgentName, TaskCommentType CommentType, string Content, DateTime CreatedAt);
public record SpecTaskLink(string Id, string TaskId, string SpecSectionId, SpecLinkType LinkType, string LinkedByAgentId, string LinkedByAgentName, string? Note, DateTime CreatedAt);

// Evidence ledger — structured verification checks recorded against a task.
public record TaskEvidence(string Id, string TaskId, EvidencePhase Phase, string CheckName, string Tool, string? Command, int? ExitCode, string? OutputSnippet, bool Passed, string AgentId, string AgentName, DateTime CreatedAt);
public record GateCheckResult(string TaskId, string CurrentPhase, string TargetPhase, bool Met, int RequiredChecks, int PassedChecks, List<string> MissingChecks, List<TaskEvidence> Evidence);

// Dependency graph views for a task.
public record TaskDependencyInfo(string TaskId, List<TaskDependencySummary> DependsOn, List<TaskDependencySummary> DependedOnBy);
public record TaskDependencySummary(string TaskId, string Title, TaskStatus Status, bool IsSatisfied);

// Bulk operation payloads. BulkUpdateStatusRequest restricts `Status` to safe values (Queued, Active, Blocked, AwaitingValidation, InReview).
public record BulkUpdateStatusRequest(List<string> TaskIds, TaskStatus Status);
public record BulkAssignRequest(List<string> TaskIds, string AgentId, string? AgentName = null);
public record BulkOperationResult(int Requested, int Succeeded, int Failed, List<TaskSnapshot> Updated, List<BulkOperationError> Errors);
public record BulkOperationError(string TaskId, string Code, string Error);
```

> **Source**: `src/AgentAcademy.Shared/Models/Tasks.cs`

### Activity Types

```csharp
public record ActivityEvent(string Id, ActivityEventType Type, ActivitySeverity Severity, string? RoomId, string? ActorId, string? TaskId, string Message, string? CorrelationId, DateTime OccurredAt);
public record WorkspaceOverview(List<AgentDefinition> ConfiguredAgents, List<RoomSnapshot> Rooms, List<ActivityEvent> RecentActivity, List<AgentLocation> AgentLocations, List<BreakoutRoom> BreakoutRooms, DateTime GeneratedAt);
```

### Evaluation Types

```csharp
public record EvaluationResult(string FilePath, double Score, bool Exists, bool NonEmpty, bool SyntaxValid, bool Complete, List<string> Issues);
public record ArtifactRecord(string AgentId, string RoomId, string FilePath, string Operation, DateTime Timestamp);
public record MetricsEntry(DateTime Timestamp, string Type, int Round, string Phase, string Agent, Dictionary<string, JsonElement> Data);
public record MetricsSummary(int TotalRounds, int TotalArtifacts, int PhaseTransitions, double AverageScore, List<MetricsEntry> Entries);
```

### System Types

```csharp
public record HealthResult(string Status, string Uptime, DateTime Timestamp, string Message = "Agent Academy backend is healthy.");
public record HealthCheckResponse(string Status, List<DependencyStatus> Dependencies, double Uptime, DateTime Timestamp);
public record ModelInfo(string Id, string Name);
public record PermissionPolicy(bool AllowFileAccess, bool AllowMcpServers, bool AllowShellExecution, bool AllowUrlFetch, List<string> AllowedToolCategories);
public record DependencyStatus(string Name, string Status, string? Detail = null);
public record UsageSummary(long TotalInputTokens, long TotalOutputTokens, double TotalCost, int RequestCount, List<string> Models);
public record ErrorRecord(string AgentId, string RoomId, string ErrorType, string Message, bool Recoverable, DateTime Timestamp);
public record ErrorSummary(int TotalErrors, int RecoverableErrors, int UnrecoverableErrors, List<ErrorCountByType> ByType, List<ErrorCountByAgent> ByAgent);
public record ErrorCountByType(string ErrorType, int Count);
public record ErrorCountByAgent(string AgentId, int Count);
public record AgentUsageSummary(string AgentId, long TotalInputTokens, long TotalOutputTokens, double TotalCost, int RequestCount);
public record LlmUsageRecord(string Id, string AgentId, string? RoomId, string? Model, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, double? Cost, int? DurationMs, string? ReasoningEffort, DateTime RecordedAt);

// Quota enforcement — aggregated usage window, current context usage, and allow/deny result.
public record AgentUsageWindow(int RequestCount, long TotalTokens, decimal TotalCost);
public record AgentContextUsage(string AgentId, string? RoomId, string? Model, long CurrentTokens, long MaxTokens, double Percentage, DateTime UpdatedAt);
public record QuotaStatus(string AgentId, bool IsAllowed, string? DeniedReason, int? RetryAfterSeconds, ResourceQuota? ConfiguredQuota, AgentUsageWindow? CurrentUsage);

public record PlanContent(string Content);

// Auth status types (for /api/auth/status endpoint)
public static class CopilotStatusValues
{
    public const string Operational = "operational";    // Auth + SDK ready
    public const string Degraded = "degraded";          // Auth OK, SDK unavailable
    public const string Unavailable = "unavailable";    // Auth not configured or failed
}

public record AuthUserInfo(string Login, string? Name, string? AvatarUrl);
public record AuthStatusResult(bool AuthEnabled, bool Authenticated, string CopilotStatus, AuthUserInfo? User = null);

// Instance health for client reconnect protocol
public record InstanceHealthResult(string InstanceId, DateTime StartedAt, string Version, bool CrashDetected, bool ExecutorOperational, bool AuthFailed, string CircuitBreakerState = "Closed");

// Git worktree status, enriched with linked task and agent info.
public record WorktreeStatusSnapshot(
    string Branch, string RelativePath, DateTimeOffset CreatedAt,
    bool StatusAvailable, string? Error,
    int TotalDirtyFiles, List<string> DirtyFilesPreview,
    int FilesChanged, int Insertions, int Deletions,
    string? LastCommitSha, string? LastCommitMessage, string? LastCommitAuthor, DateTimeOffset? LastCommitDate,
    string? TaskId, string? TaskTitle, string? TaskStatus,
    string? AgentId, string? AgentName);
```

> **Source**: `src/AgentAcademy.Shared/Models/System.cs`

### Project Types

```csharp
public record ProjectScanResult(string Path, string? ProjectName, List<string> TechStack, bool HasSpecs, bool HasReadme, bool IsGitRepo, string? GitBranch, List<string> DetectedFiles, string? RepositoryUrl = null, string? DefaultBranch = null, string? HostProvider = null);
public record WorkspaceMeta(string Path, string? ProjectName, DateTime? LastAccessedAt = null, string? RepositoryUrl = null, string? DefaultBranch = null, string? HostProvider = null);
```

> **Source**: `src/AgentAcademy.Shared/Models/Projects.cs`
>
> **Note**: `RepositoryUrl`, `DefaultBranch`, and `HostProvider` are populated when a project is scanned from a git repo. `HostProvider` is a short identifier (e.g., `github`, `gitlab`) derived from the remote URL.

### Notification Types

```csharp
public enum NotificationType { AgentThinking, NeedsInput, TaskComplete, TaskFailed, SpecReview, Error }
public record NotificationMessage(NotificationType Type, string Title, string Body, string? RoomId = null, string? AgentName = null, Dictionary<string, string>? Actions = null);
public record InputRequest(string Prompt, string? RoomId = null, string? AgentName = null, List<string>? Choices = null, bool AllowFreeform = true);
public record UserResponse(string Content, string? SelectedChoice = null, string ProviderId = "");
public record ProviderConfigSchema(string ProviderId, string DisplayName, string Description, List<ConfigField> Fields);
public record ConfigField(string Key, string Label, string Type, bool Required, string? Description = null, string? Placeholder = null);
public record AgentQuestion(string AgentId, string AgentName, string RoomId, string RoomName, string Question);
```

### Agent Memory Types

```csharp
public record AgentMemory(string AgentId, string Category, string Key, string Value, DateTime CreatedAt, DateTime? UpdatedAt, DateTime? LastAccessedAt = null, DateTime? ExpiresAt = null);
```

> **Source**: `src/AgentAcademy.Shared/Models/AgentMemory.cs`

### Command Types

```csharp
public record CommandEnvelope(string Command, Dictionary<string, object?> Args, CommandStatus Status, Dictionary<string, object?>? Result, string? Error, string CorrelationId, DateTime Timestamp, string ExecutedBy)
{
    public string? ErrorCode { get; init; }
    public int RetryCount { get; init; }  // automatic retry attempts before this result; 0 = first attempt
}

public static class CommandErrorCode
{
    public const string Validation = "VALIDATION";
    public const string NotFound = "NOT_FOUND";
    public const string Permission = "PERMISSION";
    public const string Conflict = "CONFLICT";
    public const string Timeout = "TIMEOUT";
    public const string Execution = "EXECUTION";
    public const string Internal = "INTERNAL";
    public const string RateLimit = "RATE_LIMIT";
    public const string ConfirmationRequired = "CONFIRMATION_REQUIRED";  // destructive command requires confirm=true
    // IsRetryable() returns true for RateLimit, Timeout, Internal
    // Infer(string message) maps exception messages to error codes heuristically
}

public record CommandParseResult(List<ParsedCommand> Commands, string RemainingText);
public record ParsedCommand(string Command, Dictionary<string, string> Args);
public record CommandPermissionSet(List<string> Allowed, List<string> Denied);
```

> **Source**: `src/AgentAcademy.Shared/Models/Commands.cs`

### Direct Message Types

```csharp
public record DmThreadSummary(string AgentId, string AgentName, string AgentRole, string LastMessage, DateTime LastMessageAt, int MessageCount);
public record DmMessage(string Id, string SenderId, string SenderName, string Content, DateTime SentAt, bool IsFromHuman);
```

> **Source**: `src/AgentAcademy.Shared/Models/DirectMessages.cs`

### Human Command Types

```csharp
public sealed record HumanCommandFieldMetadata(string Name, string Label, string Kind, string Description, string? Placeholder = null, bool Required = false, string? DefaultValue = null);
public sealed record HumanCommandMetadata(string Command, string Title, string Category, string Description, string Detail, bool IsAsync, IReadOnlyList<HumanCommandFieldMetadata> Fields);
```

> **Source**: `src/AgentAcademy.Shared/Models/HumanCommands.cs`

### Request Types

```csharp
public record PostMessageRequest(string RoomId, string SenderId, string Content, MessageKind Kind = MessageKind.Response, string? CorrelationId = null, DeliveryHint? Hint = null);
public record PhaseTransitionRequest(string RoomId, CollaborationPhase TargetPhase, string? Reason = null);
```

> **Source**: `src/AgentAcademy.Shared/Models/Requests.cs`

## Interfaces & Contracts

All types live in `AgentAcademy.Shared.Models` namespace. They are:
- C# records (immutable by default)
- Serializable to/from JSON via System.Text.Json
- Enums serialize as strings via `[JsonConverter(typeof(JsonStringEnumConverter))]`
- Used in API responses and SignalR messages

## Invariants

- All domain types are immutable records
- All IDs are non-empty strings
- Collection properties are never null (empty list if no items)
- Timestamps use `DateTime` (UTC by convention)
- Optional fields use nullable types (`string?`, `T?`)

### API Request Validation

All API request types use `System.ComponentModel.DataAnnotations` attributes, enforced automatically by the `[ApiController]` pipeline. Invalid requests receive a `400 Bad Request` with `ProblemDetails` response.

**Validation rules by request type:**

| Request | Field | Constraints |
|---------|-------|-------------|
| `PostMessageRequest` | `RoomId`, `SenderId` | Required, max 100 chars |
| `PostMessageRequest` | `Content` | Required, 1–50,000 chars |
| `PostMessageRequest` | `Kind` | Valid `MessageKind` enum |
| `PhaseTransitionRequest` | `TargetPhase` | Valid `CollaborationPhase` enum |
| `TaskAssignmentRequest` | `Title` | Required, max 200 chars |
| `TaskAssignmentRequest` | `Description` | Required, 1–10,000 chars |
| `TaskAssignmentRequest` | `SuccessCriteria` | Required, 1–5,000 chars |
| `TaskAssignmentRequest` | `Type` | Valid `TaskType` enum |
| `HumanMessageRequest` | `Content` | Required, 1–50,000 chars |
| `SendDmRequest` | `Message` | Required, 1–50,000 chars |
| `CreateCustomAgentRequest` | `Name` | Required, max 100 chars |
| `CreateCustomAgentRequest` | `Prompt` | Required, 1–100,000 chars |
| `CreateRoomRequest` | `Name` | Required, max 200 chars |
| `CreateRoomRequest` | `Description` | Optional, max 1,000 chars |
| `InstructionTemplateRequest` | `Name` | Required, max 200 chars |
| `InstructionTemplateRequest` | `Content` | Required, 1–100,000 chars |
| `ExecuteCommandRequest` | `Command` | Required, 1–10,000 chars |
| `UpdateQuotaRequest` | `MaxRequestsPerHour` | Optional, 1–100,000 |
| `UpdateQuotaRequest` | `MaxTokensPerHour` | Optional, 1–100,000,000 |
| `UpdateQuotaRequest` | `MaxCostPerHour` | Optional, 0.01–10,000 |
| `CompleteTaskRequest` | `CommitCount` | 0–100,000 |
| `UpdateTaskPrRequest` | `Url` | Required, valid URL, max 2,000 chars |
| `UpdateTaskPrRequest` | `Number` | Required, ≥ 1 |
| `ScanRequest`, `SwitchWorkspaceRequest` | `Path` | Required, max 1,000 chars |
| `UpdateTaskStatusRequest` | `Status` | Valid `TaskStatus` enum |
| `UpdateTaskPrRequest` | `Status` | Valid `PullRequestStatus` enum |
| `UpdateLocationRequest` | `State` | Valid `AgentState` enum |
| `MemoryImportRequest` | `Memories` | Max 500 entries |
| `MemoryImportEntry` | `AgentId`, `Category`, `Key` | Required, max 100–200 chars |
| `MemoryImportEntry` | `Value` | Required, max 500 chars |
| `MemoryImportEntry` | `TtlHours` | Optional, 1–87,600 |

## Data Persistence

> **Status: Implemented** — EF Core with SQLite, entity classes in `src/AgentAcademy.Server/Data/Entities/`.

### Architecture

The shared domain types (`AgentAcademy.Shared.Models`) are immutable C# records designed for API serialization. EF Core requires mutable classes with parameterless constructors, so **separate entity classes** live in `AgentAcademy.Server.Data.Entities`. The mapping between API DTOs and persistence entities is handled by the service layer.

### Entity Classes

| Entity | Table | Primary Key | Domain Model Equivalent |
|--------|-------|-------------|------------------------|
| `RoomEntity` | `rooms` | `Id` (string) | `RoomSnapshot` |
| `MessageEntity` | `messages` | `Id` (string) | `ChatEnvelope` |
| `TaskEntity` | `tasks` | `Id` (string) | `TaskSnapshot` |
| `TaskItemEntity` | `task_items` | `Id` (string) | `TaskItem` |
| `TaskCommentEntity` | `task_comments` | `Id` (string) | `TaskComment` |
| `AgentLocationEntity` | `agent_locations` | `AgentId` (string) | `AgentLocation` |
| `BreakoutRoomEntity` | `breakout_rooms` | `Id` (string) | `BreakoutRoom` |
| `BreakoutMessageEntity` | `breakout_messages` | `Id` (string) | (breakout-scoped `ChatEnvelope`) |
| `PlanEntity` | `plans` | `RoomId` (string) | `PlanContent` |
| `ActivityEventEntity` | `activity_events` | `Id` (string) | `ActivityEvent` |
| `WorkspaceEntity` | `workspaces` | `Path` (string) | `WorkspaceMeta` |
| `CommandAuditEntity` | `command_audits` | `Id` (string) | `CommandEnvelope` |
| `AgentMemoryEntity` | `agent_memories` | `{AgentId, Key}` (composite) | `AgentMemory` |
| `NotificationConfigEntity` | `notification_configs` | `Id` (int, auto) | (key-value for provider config) |
| `NotificationDeliveryEntity` | `notification_deliveries` | `Id` (int, auto) | (delivery audit trail) |
| `AgentConfigEntity` | `agent_configs` | `AgentId` (string) | — (per-agent overrides) |
| `InstructionTemplateEntity` | `instruction_templates` | `Id` (string) | — (reusable prompt templates) |
| `ServerInstanceEntity` | `server_instances` | `Id` (string) | `InstanceHealthResult` |
| `ConversationSessionEntity` | `conversation_sessions` | `Id` (string) | `ConversationSessionSnapshot` |
| `SystemSettingEntity` | `system_settings` | `Key` (string) | — (key-value system config) |
| `LlmUsageEntity` | `llm_usage` | `Id` (string) | `LlmUsageRecord` |
| `AgentErrorEntity` | `agent_errors` | `Id` (string) | `ErrorRecord` |
| `SpecTaskLinkEntity` | `spec_task_links` | `Id` (string) | `SpecTaskLink` |

### Key Differences from API DTOs

- Entities store **enums as strings** (not typed enums) for SQLite compatibility and v1 schema alignment
- `TaskEntity.PreferredRoles`, `.FleetModels`, `.TestsCreated` are JSON strings (`"[]"`) — not `List<string>`
- `PlanEntity` uses `RoomId` as its primary key (one plan per room); no explicit EF FK relationship is configured (implicit 1:1 via shared key)
- `AgentLocationEntity` uses `AgentId` as its primary key (one location per agent)
- `AgentMemoryEntity` uses composite key `{AgentId, Key}` (one value per agent per key)
- `SystemSettingEntity` uses `Key` as its primary key (key-value store)
- `WorkspaceEntity` uses `Path` as its primary key
- Navigation properties exist on entities for EF Core relationships (e.g., `RoomEntity.Messages`)
- `DeliveryHint` (an owned type on `ChatEnvelope`) is **not persisted** — it was not in the v1 schema
- `AgentConfigEntity` has a navigation to `InstructionTemplateEntity` for prompt customization
- `AgentErrorEntity` extends `ErrorRecord` with `Retried` and `RetryAttempt` fields
- `LlmUsageEntity` includes `CacheReadTokens`, `CacheWriteTokens`, `ApiCallId`, `Initiator` not present in `LlmUsageRecord`

### Project Scoping

Entities are associated with projects (workspaces) via `WorkspacePath`. The association pattern varies by entity:

| Entity | Scoping | Notes |
|--------|---------|-------|
| `WorkspaceEntity` | **Is** the project | PK = `Path` |
| `RoomEntity` | Direct `WorkspacePath` | Nullable — legacy rooms may be unscoped |
| `TaskEntity` | Direct `WorkspacePath` | Stamped from active workspace on creation. Fallback: room lookup for pre-migration rows |
| `ConversationSessionEntity` | Direct `WorkspacePath` | Stamped from room's workspace on creation. Inherited during rotation |
| `SprintEntity` | Direct `WorkspacePath` | Required (non-nullable) |
| `PlanEntity` | Via Room (PK = `RoomId`) | 1:1 with room — inherits workspace implicitly |
| `MessageEntity` | Via Room (`RoomId`) | Always in a room |
| `ActivityEventEntity` | Via Room (`RoomId`) | Nullable — some events are roomless |
| Child entities | Via parent | TaskComment → Task, SprintArtifact → Sprint, etc. |

**Query pattern**: When filtering by workspace, prefer the direct `WorkspacePath` column where available. For entities without direct workspace association, join through their parent room.

### Relationships

- `Room → Messages` (one-to-many, cascade delete)
- `Room → Tasks` (one-to-many, set null on delete)
- `Room → BreakoutRooms` (one-to-many, cascade delete)
- `Room → ActivityEvents` (one-to-many, set null on delete)
- `BreakoutRoom → BreakoutMessages` (one-to-many, cascade delete)
- `Task → TaskComments` (one-to-many, cascade delete)
- `Task → SpecTaskLinks` (one-to-many, cascade delete)
- `AgentConfig → InstructionTemplate` (many-to-one, set null on delete)

### Entity-Layer Enums

These enums live in the entity layer (`Data/Entities/`), not in `AgentAcademy.Shared.Models`. They are used by server-side logic only.

```csharp
// In BreakoutRoomEntity.cs
public enum BreakoutRoomCloseReason { Completed, Recalled, Cancelled, StuckDetected, ClosedByRecovery, Failed }
```

> **Note**: `BreakoutRoomEntity.CloseReason` stores this as a nullable string. The enum is used in service-layer method signatures (e.g., `CloseBreakoutRoomAsync`).

### Indexes

| Index | Table | Column(s) | Notes |
|-------|-------|-----------|-------|
| `idx_rooms_workspace` | `rooms` | `WorkspacePath` | |
| `idx_messages_room` | `messages` | `RoomId` | |
| `idx_messages_sentAt` | `messages` | `SentAt` | |
| `idx_messages_recipient_sentAt` | `messages` | `RecipientId, SentAt` | Composite |
| `idx_tasks_room` | `tasks` | `RoomId` | |
| `idx_tasks_agent` | `tasks` | `AssignedAgentId` | |
| `idx_tasks_status` | `tasks` | `Status` | |
| `idx_task_items_agent` | `task_items` | `AssignedTo` | |
| `idx_task_items_room` | `task_items` | `RoomId` | |
| `idx_task_comments_task` | `task_comments` | `TaskId` | |
| `idx_task_comments_agent` | `task_comments` | `AgentId` | |
| `idx_breakout_rooms_parent` | `breakout_rooms` | `ParentRoomId` | |
| `idx_breakout_rooms_task` | `breakout_rooms` | `TaskId` | |
| `idx_activity_room` | `activity_events` | `RoomId` | |
| `idx_activity_time` | `activity_events` | `OccurredAt` | |
| `idx_cmd_audits_agent` | `command_audits` | `AgentId` | |
| `idx_cmd_audits_source` | `command_audits` | `Source` | |
| `idx_cmd_audits_time` | `command_audits` | `Timestamp` | |
| `idx_cmd_audits_correlation` | `command_audits` | `CorrelationId` | |
| `idx_agent_memories_agent` | `agent_memories` | `AgentId` | |
| `idx_agent_memories_category` | `agent_memories` | `Category` | |
| `idx_agent_memories_expires` | `agent_memories` | `ExpiresAt` | |
| `idx_notification_configs_provider_key` | `notification_configs` | `ProviderId, Key` | Unique |
| `idx_instruction_templates_name` | `instruction_templates` | `Name` | Unique |
| `idx_conversation_sessions_room_status` | `conversation_sessions` | `RoomId, Status` | Composite |
| `idx_conversation_sessions_workspace` | `conversation_sessions` | `WorkspacePath` | Project scoping |
| `idx_tasks_workspace` | `tasks` | `WorkspacePath` | Project scoping |
| `idx_notification_deliveries_time` | `notification_deliveries` | `AttemptedAt` | |
| `idx_notification_deliveries_provider` | `notification_deliveries` | `ProviderId` | |
| `idx_notification_deliveries_channel` | `notification_deliveries` | `Channel` | |
| `idx_notification_deliveries_room` | `notification_deliveries` | `RoomId` | |
| `idx_llm_usage_agent` | `llm_usage` | `AgentId` | |
| `idx_llm_usage_room` | `llm_usage` | `RoomId` | |
| `idx_llm_usage_time` | `llm_usage` | `RecordedAt` | |
| `idx_agent_errors_agent` | `agent_errors` | `AgentId` | |
| `idx_agent_errors_room` | `agent_errors` | `RoomId` | |
| `idx_agent_errors_time` | `agent_errors` | `OccurredAt` | |
| `idx_agent_errors_type` | `agent_errors` | `ErrorType` | |
| `idx_spec_task_links_task` | `spec_task_links` | `TaskId` | |
| `idx_spec_task_links_spec` | `spec_task_links` | `SpecSectionId` | |
| `idx_spec_task_links_unique` | `spec_task_links` | `TaskId, SpecSectionId` | Unique |

### Configuration

Connection string in `appsettings.json`:
```json
"ConnectionStrings": {
    "DefaultConnection": "Data Source=agent-academy.db"
}
```

Auto-migration runs on startup via `db.Database.Migrate()`.

## Known Gaps

- ~~No persistence mapping (EF Core entity configuration) defined yet~~ ✅ Resolved
- ~~No validation attributes or FluentValidation rules specified~~ ✅ Resolved — DataAnnotations (`[Required]`, `[StringLength]`, `[MinLength]`, `[Range]`, `[Url]`) added to all API request records. Enforced automatically by `[ApiController]` pipeline. See `RequestValidationTests.cs` for coverage.
- ~~`INotificationProvider` interface not yet defined~~ ✅ Resolved (notification providers implemented)
- ~~Event sourcing vs. CRUD approach not decided~~ → CRUD via EF Core
- ~~`MetricsEntry.Data` uses `Dictionary<string, object>` — may need a more specific type~~ ✅ Resolved — Changed to `Dictionary<string, JsonElement>` for JSON serialization safety
- ~~No DTO ↔ Entity mapping layer yet~~ → Service layer handles mapping inline

## Revision History

| Date | Change | Task |
|------|--------|------|
| Initial | Created domain model spec with planned types | scaffold-solution |
| 2025-07-25 | Ported all types from v1 TypeScript to C# records; added notification types; marked Implemented | domain-models |
| 2025-07-27 | Added EF Core persistence layer: entity classes, DbContext, SQLite migration, indexes | ef-core-db |
| 2026-04-06 | Full entity inventory: added 14 missing entities, 5 missing model files, 2 missing enums, updated all existing types to match code. Fixed NotificationType location (Notifications.cs, not Enums.cs). Added 30+ missing indexes. | spec-001-entity-inventory |
| 2026-04-08 | Added WorkspacePath to TaskSnapshot and ConversationSessionSnapshot. Added project-scoping pattern section. Added idx_tasks_workspace and idx_conversation_sessions_workspace indexes. | project-scoping |
| 2026-04-11 | Added DataAnnotations validation to all API request types. 31 validation tests. Closed validation known gap. | add-request-validation |
| 2026-04-14 | Extracted 31 inline entity configurations from `OnModelCreating` into 10 `IEntityTypeConfiguration<T>` files in `Data/Configurations/`. DbContext reduced from 623 to 54 lines. Uses `ApplyConfigurationsFromAssembly`. | dbcontext-refactor |
| 2026-04-17 | Documented shipped records/fields missing from spec 001: `ResourceQuota`, `IAgentCatalog`, `PhaseGate`, `PhasePrerequisiteStatus`, `RoomSnapshot.PhaseGates`, `AgentDefinition.Quota`, `TaskEvidence`, `GateCheckResult`, `TaskDependencyInfo/Summary`, bulk task ops, `AgentUsageWindow`, `AgentContextUsage`, `QuotaStatus`, `WorktreeStatusSnapshot`, `HealthResult.Message`, `ProjectScanResult`/`WorkspaceMeta` git metadata fields, `CommandEnvelope.RetryCount`, `CommandErrorCode.ConfirmationRequired`, `TaskPriority` + `EvidencePhase` enums, `TaskCommentType.Retrospective`. | spec-001-missing-records |
