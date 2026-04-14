# 014 — Database Schema

## Purpose

Consolidated entity relationship reference for Agent Academy's SQLite database. Documents all 31 entities, their properties, relationships, indexes, and constraints as defined by EF Core configurations. For behavioral contracts, see the domain-specific specs referenced in each section.

## Current Behavior

> **Status: Implemented** — 31 entities, 31 DbSets, 16 foreign key relationships, 45+ indexes, 7 unique constraints. Database: SQLite via EF Core 8. Schema managed by code-first migrations.

## Database Provider

- **Engine**: SQLite
- **ORM**: Entity Framework Core 8
- **Schema management**: Code-first migrations (31 migrations as of 2026-04-14)
- **DbContext**: `AgentAcademyDbContext` — applies configurations from `IEntityTypeConfiguration<T>` classes via assembly scanning
- **FTS**: SQLite FTS5 virtual tables for full-text search (agent memories, messages/tasks)

---

## Entity Relationship Diagram

```
┌─────────────────┐       ┌──────────────────┐       ┌──────────────────────┐
│  WorkspaceEntity │◄──────│AgentWorkspaceEntity│     │ SprintScheduleEntity  │
│  PK: Path        │ 1   * │PK: {WorkspacePath, │     │ PK: Id                │
│                  │       │     AgentId}       │     │ UK: WorkspacePath     │
└─────────────────┘        └──────────────────┘      └───────────────────────┘

┌─────────────────┐                           ┌──────────────────────┐
│  SprintEntity    │◄─────────────────────────│  SprintArtifactEntity │
│  PK: Id          │ 1                      * │  PK: Id               │
│  UK: {Workspace, │                          │  UK: {Sprint,Stage,   │
│       Number}    │                          │       Type}           │
│  self-ref:       │                          └──────────────────────┘
│  OverflowFrom    │
└──┬───┬───┬───────┘
   │   │   │
   │   │   │  ┌──────────────────────────┐
   │   │   └──│ ConversationSessionEntity │
   │   │      │ PK: Id                    │
   │   │      │ FK: SprintId (SetNull)    │
   │   │      └──────────────────────────┘
   │   │
   │   │  ┌──────────────┐
   │   └──│  PlanEntity   │
   │      │  PK: RoomId   │
   │      │  FK: SprintId │
   │      └──────────────┘
   │
   │  ┌───────────────┐    ┌───────────────────┐
   └──│  TaskEntity    │◄───│  TaskCommentEntity │
      │  PK: Id        │ 1 *│  PK: Id            │
      │  FK: RoomId    │    └───────────────────┘
      │  FK: SprintId  │    ┌───────────────────┐
      │                │◄───│ TaskEvidenceEntity  │
      │                │ 1 *│  PK: Id            │
      │                │    └───────────────────┘
      │                │    ┌───────────────────┐
      │                │◄───│ SpecTaskLinkEntity  │
      │                │ 1 *│  PK: Id            │
      │                │    │  UK: {Task,Section} │
      │                │    └───────────────────┘
      │                │    ┌────────────────────────┐
      │                │◄──▶│  TaskDependencyEntity   │
      │                │    │  PK: {TaskId,           │
      └────────┬───────┘    │       DependsOnTaskId}  │
               │            └────────────────────────┘
               │
      ┌────────▼───────┐    ┌──────────────────┐
      │   RoomEntity    │◄───│  MessageEntity    │
      │   PK: Id        │ 1 *│  PK: Id           │
      │                │    └──────────────────┘
      │                │    ┌──────────────────────┐
      │                │◄───│ ActivityEventEntity   │
      │                │ 1 *│  PK: Id              │
      │                │    └──────────────────────┘
      │                │    ┌──────────────────┐     ┌────────────────────┐
      │                │◄───│BreakoutRoomEntity│◄────│BreakoutMessageEntity│
      │                │ 1 *│  PK: Id          │ 1  *│  PK: Id             │
      └────────────────┘    └──────────────────┘     └────────────────────┘
```

**Standalone entities** (no FK relationships):
`AgentLocationEntity`, `AgentErrorEntity`, `AgentMemoryEntity`, `CommandAuditEntity`, `LlmUsageEntity`, `NotificationConfigEntity`, `NotificationDeliveryEntity`, `InstructionTemplateEntity`, `ServerInstanceEntity`, `SystemSettingEntity`, `TaskItemEntity`, `SprintScheduleEntity`

**Learning digest cluster**:
```
┌───────────────────────┐     ┌─────────────────────────────┐
│ LearningDigestEntity  │◄────│ LearningDigestSourceEntity   │
│ PK: Id                │ 1  *│ PK: {DigestId,               │
└───────────────────────┘     │      RetrospectiveCommentId} │
                              │ FK: RetrospectiveCommentId ──▶ TaskCommentEntity
                              │ UK: RetrospectiveCommentId   │
                              └─────────────────────────────┘
```

---

## Entities by Domain

### 1. Workspace

#### WorkspaceEntity

> Table: `workspaces` — See [005 — Domain Services](../005-workspace-runtime/spec.md)

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Path | string | **PK** | Absolute filesystem path |
| ProjectName | string? | | Detected project name |
| IsActive | bool | | Whether this is the active workspace |
| LastAccessedAt | DateTime? | | Last access timestamp |
| CreatedAt | DateTime | | Creation timestamp |
| RepositoryUrl | string? | | Git remote URL |
| DefaultBranch | string? | | Git default branch |
| HostProvider | string? | | Git host (e.g., "github") |

**Navigation**: `List<AgentWorkspaceEntity> AgentWorktrees`

#### AgentWorkspaceEntity

> Table: `agent_workspaces`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| WorkspacePath | string | **PK** (composite) | FK → `workspaces.Path` (Cascade) |
| AgentId | string | **PK** (composite) | Agent identifier |
| WorktreePath | string? | | Filesystem path to agent's git worktree |
| CurrentBranch | string? | | Agent's current branch |
| CreatedAt | DateTime | | Creation timestamp |
| LastAccessedAt | DateTime? | | Last access timestamp |

**Indexes**: `idx_agent_workspaces_agent` (AgentId)

---

### 2. Rooms & Messages

#### RoomEntity

> Table: `rooms` — See [001 — Domain Model](../001-domain-model/spec.md)

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | string | **PK** | Room identifier |
| Name | string | | Display name |
| Topic | string? | | Room topic/description |
| Status | string | | `Active`, `Archived` |
| CurrentPhase | string | | Conversation phase |
| WorkspacePath | string? | | Owning workspace path |
| CreatedAt | DateTime | | Creation timestamp |
| UpdatedAt | DateTime | | Last update timestamp |

**Navigation**: `List<MessageEntity> Messages`, `List<TaskEntity> Tasks`, `List<BreakoutRoomEntity> BreakoutRooms`, `List<ActivityEventEntity> ActivityEvents`
**Indexes**: `idx_rooms_workspace` (WorkspacePath)

#### MessageEntity

> Table: `messages`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | string | **PK** | Message identifier |
| RoomId | string | FK → `rooms.Id` (Cascade) | Owning room |
| SenderId | string | | Agent or user ID |
| SenderName | string | | Display name |
| SenderRole | string? | | Agent role |
| SenderKind | string | | `Agent`, `User`, `System` |
| Kind | string | | Message type |
| Content | string | | Message body |
| SentAt | DateTime | | Send timestamp |
| RecipientId | string? | | DM recipient |
| CorrelationId | string? | | Correlation tracking |
| ReplyToMessageId | string? | | Reply threading |
| SessionId | string? | | Conversation session |
| AcknowledgedAt | DateTime? | | DM acknowledgement |

**Indexes**: `idx_messages_room` (RoomId), `idx_messages_sentAt` (SentAt), `idx_messages_recipient_sentAt` (RecipientId, SentAt)

#### PlanEntity

> Table: `plans`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| RoomId | string | **PK** | One plan per room |
| Content | string | | Plan markdown |
| UpdatedAt | DateTime | | Last update |
| SprintId | string? | FK → `sprints.Id` (SetNull) | Associated sprint |

---

### 3. Breakout Rooms

#### BreakoutRoomEntity

> Table: `breakout_rooms`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | string | **PK** | Breakout room identifier |
| Name | string | | Display name |
| ParentRoomId | string | FK → `rooms.Id` (Cascade) | Parent room |
| AssignedAgentId | string | | Agent working in breakout |
| Status | string | | `Active`, `Closed` |
| CloseReason | string? | | `Completed`, `Failed`, `ManualClose`, `Timeout` |
| TaskId | string? | | Linked task |
| CreatedAt | DateTime | | Creation timestamp |
| UpdatedAt | DateTime | | Last update |

**Navigation**: `List<BreakoutMessageEntity> Messages`
**Indexes**: `idx_breakout_rooms_parent` (ParentRoomId), `idx_breakout_rooms_task` (TaskId)

#### BreakoutMessageEntity

> Table: `breakout_messages`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | string | **PK** | Message identifier |
| BreakoutRoomId | string | FK → `breakout_rooms.Id` (Cascade) | Owning breakout |
| SenderId | string | | Sender ID |
| SenderName | string | | Display name |
| SenderRole | string? | | Agent role |
| SenderKind | string | | `Agent`, `User`, `System` |
| Kind | string | | Message type |
| Content | string | | Message body |
| SentAt | DateTime | | Send timestamp |
| SessionId | string? | | Conversation session |

---

### 4. Tasks

#### TaskEntity

> Table: `tasks` — See [010 — Task Management](../010-task-management/spec.md)

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | string | **PK** | Task identifier |
| Title | string | | Task title |
| Description | string | | Full description |
| SuccessCriteria | string | | Acceptance criteria |
| Status | string | | `Pending`, `InProgress`, `InReview`, `Completed`, `Cancelled` |
| Type | string | | `Feature`, `Bug`, `Refactor`, etc. |
| CurrentPhase | string | | Workflow phase |
| CurrentPlan | string | | Agent's implementation plan |
| ValidationStatus | string | | Validation outcome |
| ValidationSummary | string | | Validation details |
| ImplementationStatus | string | | Implementation outcome |
| ImplementationSummary | string | | Implementation details |
| PreferredRoles | string | | Comma-separated role preferences |
| RoomId | string? | FK → `rooms.Id` (SetNull) | Owning room |
| WorkspacePath | string? | | Workspace scope |
| CreatedAt | DateTime | | Creation timestamp |
| UpdatedAt | DateTime | | Last update |
| Size | string? | | `Small`, `Medium`, `Large` |
| StartedAt | DateTime? | | Work start time |
| CompletedAt | DateTime? | | Completion time |
| AssignedAgentId | string? | | Assigned agent |
| AssignedAgentName | string? | | Agent display name |
| UsedFleet | bool | | Whether fleet mode was used |
| FleetModels | string | | Models used in fleet |
| BranchName | string? | | Git branch |
| PullRequestUrl | string? | | PR URL |
| PullRequestNumber | int? | | PR number |
| PullRequestStatus | string? | | PR status |
| ReviewerAgentId | string? | | Code review agent |
| ReviewRounds | int | | Number of review rounds |
| TestsCreated | string | | Tests created description |
| CommitCount | int | | Commits made |
| MergeCommitSha | string? | | Merge commit SHA |
| SprintId | string? | FK → `sprints.Id` (SetNull) | Associated sprint |

**Navigation**: `RoomEntity? Room`, `SprintEntity? Sprint`, `ICollection<TaskDependencyEntity> Dependencies`, `ICollection<TaskDependencyEntity> Dependents`
**Indexes**: `idx_tasks_room`, `idx_tasks_agent`, `idx_tasks_status`, `idx_tasks_sprint`, `idx_tasks_workspace`, `idx_tasks_created`, `idx_tasks_completed`

#### TaskDependencyEntity

> Table: `task_dependencies`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| TaskId | string | **PK** (composite), FK → `tasks.Id` (Cascade) | Dependent task |
| DependsOnTaskId | string | **PK** (composite), FK → `tasks.Id` (Cascade) | Prerequisite task |
| CreatedAt | DateTime | | Creation timestamp |

**Indexes**: `idx_task_deps_depends_on` (DependsOnTaskId)

#### TaskCommentEntity

> Table: `task_comments`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | string | **PK** | Comment identifier |
| TaskId | string | FK → `tasks.Id` (Cascade) | Owning task |
| AgentId | string | | Commenting agent |
| AgentName | string | | Agent display name |
| CommentType | string | | `Retrospective`, `Review`, `Note`, etc. |
| Content | string | | Comment body |
| CreatedAt | DateTime | | Creation timestamp |

**Indexes**: `idx_task_comments_task` (TaskId), `idx_task_comments_agent` (AgentId)

#### TaskEvidenceEntity

> Table: `task_evidence`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | string | **PK** | Evidence identifier |
| TaskId | string | FK → `tasks.Id` (Cascade) | Owning task |
| Phase | string | | `baseline`, `after`, `review` |
| CheckName | string | | Check type name |
| Tool | string | | Tool used for check |
| Command | string? | | Command executed |
| ExitCode | int? | | Process exit code |
| OutputSnippet | string? | | Truncated output |
| Passed | bool | | Whether check passed |
| AgentId | string | | Agent that ran the check |
| AgentName | string | | Agent display name |
| CreatedAt | DateTime | | Creation timestamp |

**Indexes**: `idx_task_evidence_task` (TaskId), `idx_task_evidence_task_phase` (TaskId, Phase)

#### TaskItemEntity

> Table: `task_items`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | string | **PK** | Item identifier |
| Title | string | | Item title |
| Description | string | | Item description |
| Status | string | | Item status |
| AssignedTo | string | | Assigned agent |
| RoomId | string | | Room context |
| BreakoutRoomId | string? | | Breakout context |
| Evidence | string? | | Completion evidence |
| Feedback | string? | | Review feedback |
| CreatedAt | DateTime | | Creation timestamp |
| UpdatedAt | DateTime | | Last update |

**Indexes**: `idx_task_items_agent` (AssignedTo), `idx_task_items_room` (RoomId)

#### SpecTaskLinkEntity

> Table: `spec_task_links` — See [009 — Spec Management](../009-spec-management/spec.md)

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | string | **PK** | Link identifier |
| TaskId | string | FK → `tasks.Id` (Cascade) | Linked task |
| SpecSectionId | string | | Spec section (e.g., "003") |
| LinkType | string | | Link type |
| LinkedByAgentId | string | | Agent that created the link |
| LinkedByAgentName | string | | Agent display name |
| Note | string? | | Descriptive note |
| CreatedAt | DateTime | | Creation timestamp |

**Indexes**: `idx_spec_task_links_task`, `idx_spec_task_links_spec`, **UK**: `idx_spec_task_links_unique` (TaskId, SpecSectionId)

---

### 5. Sprints

#### SprintEntity

> Table: `sprints` — See [013 — Sprint System](../013-sprint-system/spec.md)

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | string | **PK** | Sprint identifier |
| Number | int | | Sequential sprint number per workspace |
| WorkspacePath | string | | Owning workspace |
| Status | string | | `Active`, `Completed`, `Cancelled` |
| CurrentStage | string | | Current lifecycle stage |
| OverflowFromSprintId | string? | FK → `sprints.Id` (SetNull) | Previous sprint (carry-forward) |
| AwaitingSignOff | bool | | Human approval pending |
| PendingStage | string? | | Stage awaiting sign-off |
| SignOffRequestedAt | DateTime? | | When sign-off was requested |
| CreatedAt | DateTime | | Creation timestamp |
| CompletedAt | DateTime? | | Completion timestamp |

**Indexes**: `idx_sprints_workspace_status`, **UK**: `idx_sprints_one_active_per_workspace` (WorkspacePath, filtered: Status='Active'), **UK**: `idx_sprints_workspace_number_unique` (WorkspacePath, Number)

#### SprintArtifactEntity

> Table: `sprint_artifacts`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | int | **PK** (auto-increment) | Artifact identifier |
| SprintId | string | FK → `sprints.Id` (Cascade) | Owning sprint |
| Stage | string | | Sprint stage |
| Type | string | | `RequirementsDocument`, `SprintPlan`, etc. |
| Content | string | | Artifact content |
| CreatedByAgentId | string? | | Author agent |
| CreatedAt | DateTime | | Creation timestamp |
| UpdatedAt | DateTime? | | Last update |

**Indexes**: `idx_sprint_artifacts_sprint`, `idx_sprint_artifacts_sprint_stage`, `idx_sprint_artifacts_sprint_type`, **UK**: `idx_sprint_artifacts_sprint_stage_type_unique` (SprintId, Stage, Type)

#### SprintScheduleEntity

> Table: `sprint_schedules`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | string | **PK** | Schedule identifier |
| WorkspacePath | string | | Owning workspace |
| CronExpression | string | | Cron schedule |
| TimeZoneId | string | | IANA timezone |
| Enabled | bool | | Whether schedule is active |
| NextRunAtUtc | DateTime? | | Next scheduled run |
| LastTriggeredAt | DateTime? | | Last trigger time |
| LastEvaluatedAt | DateTime? | | Last evaluation time |
| LastOutcome | string? | | Last trigger outcome |
| CreatedAt | DateTime | | Creation timestamp |
| UpdatedAt | DateTime | | Last update |

**Indexes**: **UK**: `idx_sprint_schedules_workspace_unique` (WorkspacePath), `idx_sprint_schedules_enabled_next_run` (Enabled, NextRunAtUtc)

---

### 6. Agents

#### AgentLocationEntity

> Table: `agent_locations` — See [003 — Agent System](../003-agent-system/spec.md)

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| AgentId | string | **PK** | Agent identifier |
| RoomId | string | | Current room |
| State | string | | `Idle`, `Active`, `InBreakout` |
| BreakoutRoomId | string? | | Current breakout room |
| UpdatedAt | DateTime | | Last update |

**Indexes**: `idx_agent_locations_room` (RoomId)

#### AgentConfigEntity

> Table: `agent_configs`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| AgentId | string | **PK** | Agent identifier |
| StartupPromptOverride | string? | | Custom startup prompt |
| ModelOverride | string? | | Custom model |
| CustomInstructions | string? | | Additional instructions |
| InstructionTemplateId | string? | FK → `instruction_templates.Id` (SetNull) | Template reference |
| MaxRequestsPerHour | int? | | Rate limit |
| MaxTokensPerHour | long? | | Token limit |
| MaxCostPerHour | decimal? | | Cost limit (TEXT column type) |
| UpdatedAt | DateTime | | Last update |

#### AgentMemoryEntity

> Table: `agent_memories` — See [008 — Agent Memory](../008-agent-memory/spec.md)

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| AgentId | string | **PK** (composite) | Owning agent |
| Key | string | **PK** (composite) | Memory key |
| Category | string | | `knowledge`, `reflection`, `preference`, etc. |
| Value | string | | Memory content |
| CreatedAt | DateTime | | Creation timestamp |
| UpdatedAt | DateTime? | | Last update |
| LastAccessedAt | DateTime? | | Last access (for decay) |
| ExpiresAt | DateTime? | | TTL expiry |

**Indexes**: `idx_agent_memories_agent`, `idx_agent_memories_category`, `idx_agent_memories_expires`

#### AgentErrorEntity

> Table: `agent_errors`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | string | **PK** | Error identifier |
| AgentId | string | | Agent that errored |
| RoomId | string? | | Room context |
| ErrorType | string | | Error classification |
| Message | string | | Error message |
| Recoverable | bool | | Whether error is recoverable |
| Retried | bool | | Whether retry was attempted |
| RetryAttempt | int? | | Retry attempt number |
| OccurredAt | DateTime | | Error timestamp |

**Indexes**: `idx_agent_errors_agent`, `idx_agent_errors_room`, `idx_agent_errors_time`, `idx_agent_errors_type`

---

### 7. Sessions

#### ConversationSessionEntity

> Table: `conversation_sessions`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | string | **PK** | Session identifier |
| RoomId | string | | Owning room |
| WorkspacePath | string? | | Workspace scope |
| RoomType | string | | `Main`, `Breakout` |
| SequenceNumber | int | | Session number within room |
| Status | string | | `Active`, `Archived` |
| Summary | string? | | Session summary |
| MessageCount | int | | Messages in session |
| CreatedAt | DateTime | | Creation timestamp |
| ArchivedAt | DateTime? | | Archive timestamp |
| SprintId | string? | FK → `sprints.Id` (SetNull) | Associated sprint |
| SprintStage | string? | | Sprint stage at session time |

**Indexes**: `idx_conversation_sessions_room_status` (RoomId, Status), `idx_conversation_sessions_workspace`, `idx_conversation_sessions_sprint`

---

### 8. Infrastructure

#### ActivityEventEntity

> Table: `activity_events`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | string | **PK** | Event identifier |
| Type | string | | Event type |
| Severity | string | | `Info`, `Warning`, `Error` |
| RoomId | string? | FK → `rooms.Id` (SetNull) | Room context |
| ActorId | string? | | Agent or user that caused event |
| TaskId | string? | | Related task |
| Message | string | | Event description |
| CorrelationId | string? | | Correlation tracking |
| OccurredAt | DateTime | | Event timestamp |
| MetadataJson | string? | | JSON metadata blob |

**Indexes**: `idx_activity_room`, `idx_activity_time`

#### CommandAuditEntity

> Table: `command_audits` — See [007 — Agent Commands](../007-agent-commands/spec.md)

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | string | **PK** | Audit entry identifier |
| CorrelationId | string | | Command correlation |
| AgentId | string | | Executing agent |
| Source | string? | | Command source |
| Command | string | | Command name |
| ArgsJson | string | | Arguments as JSON |
| Status | string | | `Success`, `Failed`, `Denied` |
| ResultJson | string? | | Result as JSON |
| ErrorMessage | string? | | Error details |
| ErrorCode | string? | | Error classification |
| RoomId | string? | | Room context |
| Timestamp | DateTime | | Execution timestamp |

**Indexes**: `idx_cmd_audits_agent`, `idx_cmd_audits_source`, `idx_cmd_audits_time`, `idx_cmd_audits_correlation`

#### LlmUsageEntity

> Table: `llm_usage`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | string | **PK** | Usage record identifier |
| AgentId | string | | Agent that made the call |
| RoomId | string? | | Room context |
| Model | string? | | LLM model used |
| InputTokens | long | | Input token count |
| OutputTokens | long | | Output token count |
| CacheReadTokens | long | | Cache read tokens |
| CacheWriteTokens | long | | Cache write tokens |
| Cost | double? | | Estimated cost |
| DurationMs | int? | | Call duration |
| ApiCallId | string? | | API call identifier |
| Initiator | string? | | What triggered the call |
| ReasoningEffort | string? | | Reasoning effort level |
| RecordedAt | DateTime | | Recording timestamp |

**Indexes**: `idx_llm_usage_agent`, `idx_llm_usage_room`, `idx_llm_usage_time`, `idx_llm_usage_agent_time` (AgentId, RecordedAt)

#### ServerInstanceEntity

> Table: `server_instances` — See [011 — State Recovery](../011-state-recovery/spec.md)

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | string | **PK**, `[Key]` | Instance identifier |
| StartedAt | DateTime | `[Required]` | Startup timestamp |
| ShutdownAt | DateTime? | | Shutdown timestamp |
| ExitCode | int? | | Process exit code |
| CrashDetected | bool | | Whether crash was detected |
| Version | string | `[Required]` | Application version |

#### SystemSettingEntity

> Table: `system_settings`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Key | string | **PK** | Setting key |
| Value | string | | Setting value |
| UpdatedAt | DateTime | | Last update |

#### InstructionTemplateEntity

> Table: `instruction_templates`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | string | **PK** | Template identifier |
| Name | string | | Display name |
| Description | string? | | Template description |
| Content | string | | Template content |
| CreatedAt | DateTime | | Creation timestamp |
| UpdatedAt | DateTime | | Last update |

**Indexes**: **UK**: `idx_instruction_templates_name` (Name)

---

### 9. Notifications

#### NotificationConfigEntity

> Table: `notification_configs` — See [004 — Notification System](../004-notification-system/spec.md)

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | int | **PK** (auto-increment) | Config entry identifier |
| ProviderId | string | | Notification provider |
| Key | string | | Config key |
| Value | string | | Config value |
| UpdatedAt | DateTime | | Last update |

**Indexes**: **UK**: `idx_notification_configs_provider_key` (ProviderId, Key)

#### NotificationDeliveryEntity

> Table: `notification_deliveries`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | int | **PK** (auto-increment) | Delivery identifier |
| Channel | string | | Notification channel |
| Title | string? | | Notification title |
| Body | string? | | Notification body |
| RoomId | string? | | Room context |
| AgentId | string? | | Agent context |
| ProviderId | string | | Provider that delivered |
| Status | string | | `Sent`, `Failed` |
| Error | string? | | Error details |
| AttemptedAt | DateTime | | Delivery attempt timestamp |

**Indexes**: `idx_notification_deliveries_time`, `idx_notification_deliveries_provider`, `idx_notification_deliveries_channel`, `idx_notification_deliveries_room`

---

### 10. Learning & Digests

#### LearningDigestEntity

> Table: `learning_digests`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| Id | int | **PK** (auto-increment) | Digest identifier |
| CreatedAt | DateTime | | Creation timestamp |
| Summary | string | | Digest summary |
| MemoriesCreated | int | | Number of memories generated |
| RetrospectivesProcessed | int | | Number of retros processed |
| Status | string | | Processing status |

**Navigation**: `List<LearningDigestSourceEntity> Sources`

#### LearningDigestSourceEntity

> Table: `learning_digest_sources`

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| DigestId | int | **PK** (composite), FK → `learning_digests.Id` (Cascade) | Owning digest |
| RetrospectiveCommentId | string | **PK** (composite), FK → `task_comments.Id` (Cascade) | Source retrospective |

**Indexes**: **UK**: `idx_digest_sources_retro_unique` (RetrospectiveCommentId)

---

## Full-Text Search (FTS5)

Two FTS5 virtual tables are created via migrations (not EF entity configurations):

| Virtual Table | Source | Indexed Columns | Purpose |
|---------------|--------|-----------------|---------|
| `agent_memories_fts` | `agent_memories` | Key, Value, Category | Memory search in `MemoryController.Browse` |
| `search_fts` | `messages`, `tasks` | Content/Title/Description | Global search in `SearchController.Search` |

FTS tables are maintained by triggers that sync inserts/updates/deletes from their source tables.

---

## Index Summary

| Table | Index Count | Notable Indexes |
|-------|-------------|-----------------|
| tasks | 7 | status, agent, sprint, workspace, created, completed |
| agent_memories | 3 | agent, category, expires |
| agent_errors | 4 | agent, room, time, type |
| llm_usage | 4 | agent, room, time, composite agent+time |
| command_audits | 4 | agent, source, time, correlation |
| notification_deliveries | 4 | time, provider, channel, room |
| sprint_artifacts | 4 | sprint, sprint+stage, sprint+type, unique sprint+stage+type |
| sprints | 3 | workspace+status, unique active-per-workspace, unique workspace+number |
| messages | 3 | room, sentAt, recipient+sentAt |
| conversation_sessions | 3 | room+status, workspace, sprint |
| All others | 0–2 each | See entity sections above |

## Unique Constraints

| Table | Constraint | Columns |
|-------|-----------|---------|
| sprints | `idx_sprints_one_active_per_workspace` | WorkspacePath (filtered: Status='Active') |
| sprints | `idx_sprints_workspace_number_unique` | WorkspacePath, Number |
| sprint_artifacts | `idx_sprint_artifacts_sprint_stage_type_unique` | SprintId, Stage, Type |
| sprint_schedules | `idx_sprint_schedules_workspace_unique` | WorkspacePath |
| spec_task_links | `idx_spec_task_links_unique` | TaskId, SpecSectionId |
| notification_configs | `idx_notification_configs_provider_key` | ProviderId, Key |
| instruction_templates | `idx_instruction_templates_name` | Name |
| learning_digest_sources | `idx_digest_sources_retro_unique` | RetrospectiveCommentId |

## Foreign Key Relationships

| From | Column | To | On Delete |
|------|--------|----|-----------|
| agent_workspaces | WorkspacePath | workspaces.Path | Cascade |
| messages | RoomId | rooms.Id | Cascade |
| breakout_rooms | ParentRoomId | rooms.Id | Cascade |
| breakout_messages | BreakoutRoomId | breakout_rooms.Id | Cascade |
| activity_events | RoomId | rooms.Id | SetNull |
| tasks | RoomId | rooms.Id | SetNull |
| tasks | SprintId | sprints.Id | SetNull |
| task_dependencies | TaskId | tasks.Id | Cascade |
| task_dependencies | DependsOnTaskId | tasks.Id | Cascade |
| task_comments | TaskId | tasks.Id | Cascade |
| task_evidence | TaskId | tasks.Id | Cascade |
| spec_task_links | TaskId | tasks.Id | Cascade |
| plans | SprintId | sprints.Id | SetNull |
| conversation_sessions | SprintId | sprints.Id | SetNull |
| sprints | OverflowFromSprintId | sprints.Id | SetNull |
| sprint_artifacts | SprintId | sprints.Id | Cascade |
| agent_configs | InstructionTemplateId | instruction_templates.Id | SetNull |
| learning_digests → sources | DigestId | learning_digests.Id | Cascade |
| learning_digest_sources | RetrospectiveCommentId | task_comments.Id | Cascade |

## Migration History

31 migrations from initial schema (2026-03-27) through sprint schedules (2026-04-14). Key milestones:

| Migration | Date | Description |
|-----------|------|-------------|
| InitialCreate | 2026-03-27 | Core entities: rooms, messages, tasks, agents, plans |
| AddTaskExtensionFields | 2026-03-28 | Branch workflow, review, fleet fields on tasks |
| AddWorkspaces | 2026-03-28 | Workspace entity and active workspace |
| AddCommandAuditsAndAgentMemories | 2026-03-28 | Agent memory and command audit tracking |
| AddNotificationConfig | 2026-03-28 | Notification provider configuration |
| AddDirectMessageSupport | 2026-03-30 | DM fields on messages (RecipientId, AcknowledgedAt) |
| AddServerInstances | 2026-03-31 | Server instance tracking for crash detection |
| AddConversationSessions | 2026-03-31 | Conversation session management |
| AddNotificationDeliveries | 2026-04-04 | Notification delivery history |
| AddMemoryFts5Search | 2026-04-04 | FTS5 full-text search on agent memories |
| AddMemoryDecayTtl | 2026-04-04 | Memory expiry and decay |
| AddLlmUsageTracking | 2026-04-04 | LLM usage recording |
| AddAgentErrorTracking | 2026-04-04 | Agent error tracking |
| AddSpecTaskLinking | 2026-04-05 | Spec-to-task link tracking |
| AddTaskEvidence | 2026-04-07 | Task verification evidence |
| AddSprintWorkflow | 2026-04-08 | Sprint lifecycle |
| AddSprintForeignKeys | 2026-04-08 | Sprint FKs on tasks, sessions, plans |
| AddSprintSignOffGates | 2026-04-08 | Human sign-off workflow |
| AddAgentQuotas | 2026-04-11 | Agent quota fields on config |
| AddSearchFts5 | 2026-04-12 | Global FTS5 search |
| AddTaskDependencies | 2026-04-13 | Task dependency graph |
| AddLearningDigests | 2026-04-13 | Learning digest and source entities |
| AddSprintSchedules | 2026-04-14 | Sprint auto-scheduling |

## Known Gaps

1. **No cascade from Workspace to Room** — Rooms reference `WorkspacePath` as a plain string, not a FK. Workspace deletion does not cascade to rooms.
2. **No FK from AgentLocation to Room** — `AgentLocationEntity.RoomId` is indexed but not a foreign key.
3. **TaskItemEntity is orphaned** — No FK relationship to `TaskEntity` or `RoomEntity`; uses plain string columns.
4. **DM threading is implicit** — Direct messages use `RecipientId` + `SenderKind` filtering on `MessageEntity`, not a separate DM entity.
5. **Soft delete not used** — Rooms use `Status = 'Archived'`; tasks use `Status = 'Cancelled'`. No global soft-delete pattern.

## Revision History

| Date | Change |
|------|--------|
| 2026-04-14 | Initial schema catalog — 31 entities, 31 migrations |
