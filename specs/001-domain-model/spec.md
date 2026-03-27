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
| `Models/System.cs` | HealthResult, HealthCheckResponse, ModelInfo, PermissionPolicy, DependencyStatus, UsageSummary, ErrorRecord, PlanContent | ✅ Implemented |
| `Models/Projects.cs` | ProjectScanResult, WorkspaceMeta | ✅ Implemented |
| `Models/Notifications.cs` | NotificationType, NotificationMessage, InputRequest, UserResponse, ProviderConfigSchema, ConfigField | ✅ Implemented |

### Enumerations

All enums use `[JsonConverter(typeof(JsonStringEnumConverter))]` for JSON string serialization.

```csharp
public enum CollaborationPhase { Intake, Planning, Discussion, Validation, Implementation, FinalSynthesis }
public enum AgentAvailability { Ready, Preferred, Active, Busy, Offline }
public enum DeliveryPriority { Low, Normal, High, Urgent }
public enum MessageKind { System, TaskAssignment, Coordination, Plan, Status, Review, Validation, Decision, Question, Response, SpecChangeProposal }
public enum MessageSenderKind { System, Agent, User }
public enum TaskStatus { Queued, Active, Blocked, AwaitingValidation, Completed, Cancelled }
public enum WorkstreamStatus { NotStarted, Ready, InProgress, Blocked, Completed }
public enum RoomStatus { Idle, Active, AttentionRequired, Completed, Archived }
public enum ActivityEventType { AgentLoaded, AgentThinking, AgentFinished, RoomCreated, RoomClosed, TaskCreated, PhaseChanged, MessagePosted, MessageSent, PresenceUpdated, RoomStatusChanged, ArtifactEvaluated, QualityGateChecked, IterationRetried, CheckpointCreated, AgentErrorOccurred, AgentWarningOccurred, SubagentStarted, SubagentCompleted, SubagentFailed, AgentPlanChanged, AgentSnapshotRewound, ToolIntercepted }
public enum ActivitySeverity { Info, Warning, Error }
public enum AgentState { InRoom, Working, Presenting, Idle }
public enum TaskItemStatus { Pending, Active, Done, Rejected }
public enum NotificationType { AgentThinking, NeedsInput, TaskComplete, TaskFailed, SpecReview, Error }
```

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

### Task Types

```csharp
public record TaskSnapshot(string Id, string Title, string Description, string SuccessCriteria, TaskStatus Status, CollaborationPhase CurrentPhase, string CurrentPlan, WorkstreamStatus ValidationStatus, string ValidationSummary, WorkstreamStatus ImplementationStatus, string ImplementationSummary, List<string> PreferredRoles, DateTime CreatedAt, DateTime UpdatedAt);
public record TaskItem(string Id, string Title, string Description, TaskItemStatus Status, string AssignedTo, string RoomId, string? BreakoutRoomId, string? Evidence, string? Feedback, DateTime CreatedAt, DateTime UpdatedAt);
public record TaskAssignmentRequest(string Title, string Description, string SuccessCriteria, string? RoomId, List<string> PreferredRoles, string? CorrelationId = null);
public record TaskAssignmentResult(string CorrelationId, RoomSnapshot Room, TaskSnapshot Task, ActivityEvent Activity);
```

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

## Known Gaps

- No persistence mapping (EF Core entity configuration) defined yet
- No validation attributes or FluentValidation rules specified
- `INotificationProvider` interface not yet defined (will be added with notification service implementation)
- Event sourcing vs. CRUD approach not decided
- `MetricsEntry.Data` uses `Dictionary<string, object>` — may need a more specific type

## Revision History

| Date | Change | Task |
|------|--------|------|
| Initial | Created domain model spec with planned types | scaffold-solution |
| 2025-07-25 | Ported all types from v1 TypeScript to C# records; added notification types; marked Implemented | domain-models |
