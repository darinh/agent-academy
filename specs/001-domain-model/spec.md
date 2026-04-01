# 001 — Domain Model

## Purpose
Defines the core domain types used throughout Agent Academy. These types live in `AgentAcademy.Shared.Models` and are referenced by the server, tests, and (via API responses) the frontend.

Types are ported from the v1 TypeScript definitions (`local-agent-host/shared/src/types.ts`) with additions for the notification system.

## Current Behavior

> **Status: Implemented** — All types below are compiled C# records/enums in `src/AgentAcademy.Shared/Models/`.

### File Organization

| File | Types | Status |
|------|-------|--------|
| `Models/Enums.cs` | All enumerations | ✅ Implemented |
| `Models/Agents.cs` | AgentDefinition, AgentPresence, AgentLocation, AgentCatalogOptions | ✅ Implemented |
| `Models/Rooms.cs` | RoomSnapshot, BreakoutRoom, ChatEnvelope, DeliveryHint | ✅ Implemented |
| `Models/Tasks.cs` | TaskSnapshot, TaskItem, TaskAssignmentRequest, TaskAssignmentResult | ✅ Implemented |
| `Models/Activity.cs` | ActivityEvent, WorkspaceOverview | ✅ Implemented |
| `Models/Evaluation.cs` | EvaluationResult, ArtifactRecord, MetricsEntry, MetricsSummary | ✅ Implemented |
| `Models/System.cs` | HealthResult, HealthCheckResponse, ModelInfo, PermissionPolicy, DependencyStatus, UsageSummary, ErrorRecord, PlanContent, CopilotStatusValues, AuthUserInfo, AuthStatusResult | ✅ Implemented |
| `Models/Projects.cs` | ProjectScanResult, WorkspaceMeta | ✅ Implemented |
| `Models/Notifications.cs` | NotificationType, NotificationMessage, InputRequest, UserResponse, ProviderConfigSchema, ConfigField | ✅ Implemented |

### Enumerations

All enums use `[JsonConverter(typeof(JsonStringEnumConverter))]` for JSON string serialization.

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
public enum TaskCommentType { Comment, Finding, Evidence, Blocker }
public enum WorkstreamStatus { NotStarted, Ready, InProgress, Blocked, Completed }
public enum RoomStatus { Idle, Active, AttentionRequired, Completed, Archived }
public enum ActivityEventType { AgentLoaded, AgentThinking, AgentFinished, RoomCreated, RoomClosed, TaskCreated, PhaseChanged, MessagePosted, MessageSent, PresenceUpdated, RoomStatusChanged, ArtifactEvaluated, QualityGateChecked, IterationRetried, CheckpointCreated, AgentErrorOccurred, AgentWarningOccurred, SubagentStarted, SubagentCompleted, SubagentFailed, AgentPlanChanged, AgentSnapshotRewound, ToolIntercepted, CommandExecuted, CommandDenied, CommandFailed, TaskClaimed, TaskReleased, TaskApproved, TaskChangesRequested, TaskStatusUpdated, RoomRenamed, DirectMessageSent }
public enum ActivitySeverity { Info, Warning, Error }
public enum AgentState { InRoom, Working, Presenting, Idle }
public enum TaskItemStatus { Pending, Active, Done, Rejected }
public enum NotificationType { AgentThinking, NeedsInput, TaskComplete, TaskFailed, SpecReview, Error }
```

> **Source**: `src/AgentAcademy.Shared/Models/Enums.cs`

### Agent Types

```csharp
public record AgentDefinition(string Id, string Name, string Role, string Summary, string StartupPrompt, string? Model, List<string> CapabilityTags, List<string> EnabledTools, bool AutoJoinDefaultRoom);
public record AgentPresence(string AgentId, string Name, string Role, AgentAvailability Availability, bool IsPreferred, DateTime LastActivityAt, List<string> ActiveCapabilities);
public record AgentLocation(string AgentId, string RoomId, AgentState State, string? BreakoutRoomId, DateTime UpdatedAt);
public record AgentCatalogOptions(string DefaultRoomId, string DefaultRoomName, List<AgentDefinition> Agents);
```

### Room Types

```csharp
public record RoomSnapshot(string Id, string Name, RoomStatus Status, CollaborationPhase CurrentPhase, TaskSnapshot? ActiveTask, List<AgentPresence> Participants, List<ChatEnvelope> RecentMessages, DateTime CreatedAt, DateTime UpdatedAt);
public record BreakoutRoom(string Id, string Name, string ParentRoomId, string AssignedAgentId, List<TaskItem> Tasks, RoomStatus Status, List<ChatEnvelope> RecentMessages, DateTime CreatedAt, DateTime UpdatedAt);
public record ChatEnvelope(string Id, string RoomId, string SenderId, string SenderName, string? SenderRole, MessageSenderKind SenderKind, MessageKind Kind, string Content, DateTime SentAt, string? CorrelationId = null, string? ReplyToMessageId = null, DeliveryHint? Hint = null);
public record DeliveryHint(string? TargetRole, string? TargetAgentId, DeliveryPriority Priority, bool ReplyRequested);
```

> **Source**: `src/AgentAcademy.Shared/Models/Rooms.cs`

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
    int CommentCount = 0
);
public record TaskItem(string Id, string Title, string Description, TaskItemStatus Status, string AssignedTo, string RoomId, string? BreakoutRoomId, string? Evidence, string? Feedback, DateTime CreatedAt, DateTime UpdatedAt);
public record TaskAssignmentRequest(string Title, string Description, string SuccessCriteria, string? RoomId, List<string> PreferredRoles, TaskType Type = TaskType.Feature, string? CorrelationId = null, string? CurrentPlan = null);
public record TaskAssignmentResult(string CorrelationId, RoomSnapshot Room, TaskSnapshot Task, ActivityEvent Activity);
public record TaskComment(string Id, string TaskId, string AgentId, string AgentName, TaskCommentType CommentType, string Content, DateTime CreatedAt);
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
public record MetricsEntry(DateTime Timestamp, string Type, int Round, string Phase, string Agent, Dictionary<string, object> Data);
public record MetricsSummary(int TotalRounds, int TotalArtifacts, int PhaseTransitions, double AverageScore, List<MetricsEntry> Entries);
```

### System Types

```csharp
public record HealthResult(string Status, string Uptime, DateTime Timestamp);
public record HealthCheckResponse(string Status, List<DependencyStatus> Dependencies, double Uptime, DateTime Timestamp);
public record ModelInfo(string Id, string Name);
public record PermissionPolicy(bool AllowFileAccess, bool AllowMcpServers, bool AllowShellExecution, bool AllowUrlFetch, List<string> AllowedToolCategories);
public record DependencyStatus(string Name, string Status, string? Detail = null);
public record UsageSummary(long TotalInputTokens, long TotalOutputTokens, decimal TotalCost, int RequestCount, List<string> Models);
public record ErrorRecord(string AgentId, string RoomId, string ErrorType, string Message, bool Recoverable, DateTime Timestamp);
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
```

### Project Types

```csharp
public record ProjectScanResult(string Path, string? ProjectName, List<string> TechStack, bool HasSpecs, bool HasReadme, bool IsGitRepo, string? GitBranch, List<string> DetectedFiles);
public record WorkspaceMeta(string Path, string? ProjectName, DateTime? LastAccessedAt = null);
```

### Notification Types (NEW)

```csharp
public enum NotificationType { AgentThinking, NeedsInput, TaskComplete, TaskFailed, SpecReview, Error }
public record NotificationMessage(NotificationType Type, string Title, string Body, string? RoomId = null, string? AgentName = null, Dictionary<string, string>? Actions = null);
public record InputRequest(string Prompt, string? RoomId = null, string? AgentName = null, List<string>? Choices = null, bool AllowFreeform = true);
public record UserResponse(string Content, string? SelectedChoice = null, string ProviderId = "");
public record ProviderConfigSchema(string ProviderId, string DisplayName, string Description, List<ConfigField> Fields);
public record ConfigField(string Key, string Label, string Type, bool Required, string? Description = null, string? Placeholder = null);
```

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
| `AgentLocationEntity` | `agent_locations` | `AgentId` (string) | `AgentLocation` |
| `BreakoutRoomEntity` | `breakout_rooms` | `Id` (string) | `BreakoutRoom` |
| `BreakoutMessageEntity` | `breakout_messages` | `Id` (string) | (breakout-scoped `ChatEnvelope`) |
| `PlanEntity` | `plans` | `RoomId` (string) | `PlanContent` |
| `ActivityEventEntity` | `activity_events` | `Id` (string) | `ActivityEvent` |

### Key Differences from API DTOs

- Entities store **enums as strings** (not typed enums) for SQLite compatibility and v1 schema alignment
- `TaskEntity.PreferredRoles` is a JSON string (`"[]"`) — not a `List<string>`
- `PlanEntity` uses `RoomId` as its primary key (one plan per room)
- `AgentLocationEntity` uses `AgentId` as its primary key (one location per agent)
- Navigation properties exist on entities for EF Core relationships (e.g., `RoomEntity.Messages`)
- `DeliveryHint` (an owned type on `ChatEnvelope`) is **not persisted** — it was not in the v1 schema

### Relationships

- `Room → Messages` (one-to-many, cascade delete)
- `Room → Tasks` (one-to-many, set null on delete)
- `Room → BreakoutRooms` (one-to-many, cascade delete)
- `Room → ActivityEvents` (one-to-many, set null on delete)
- `Room → Plan` (one-to-one, cascade delete)
- `BreakoutRoom → BreakoutMessages` (one-to-many, cascade delete)

### Indexes (matching v1 schema)

| Index | Table | Column(s) |
|-------|-------|-----------|
| `idx_messages_room` | `messages` | `RoomId` |
| `idx_messages_sentAt` | `messages` | `SentAt` |
| `idx_tasks_room` | `tasks` | `RoomId` |
| `idx_task_items_agent` | `task_items` | `AssignedTo` |
| `idx_task_items_room` | `task_items` | `RoomId` |
| `idx_breakout_rooms_parent` | `breakout_rooms` | `ParentRoomId` |
| `idx_activity_room` | `activity_events` | `RoomId` |
| `idx_activity_time` | `activity_events` | `OccurredAt` |

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
- No validation attributes or FluentValidation rules specified
- `INotificationProvider` interface not yet defined (will be added with notification service implementation)
- ~~Event sourcing vs. CRUD approach not decided~~ → CRUD via EF Core
- `MetricsEntry.Data` uses `Dictionary<string, object>` — may need a more specific type
- No DTO ↔ Entity mapping layer yet (will be added with service implementations)

## Revision History

| Date | Change | Task |
|------|--------|------|
| Initial | Created domain model spec with planned types | scaffold-solution |
| 2025-07-25 | Ported all types from v1 TypeScript to C# records; added notification types; marked Implemented | domain-models |
| 2025-07-27 | Added EF Core persistence layer: entity classes, DbContext, SQLite migration, indexes | ef-core-db |
