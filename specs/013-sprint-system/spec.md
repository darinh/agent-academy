# 013 — Sprint System

## Purpose

Defines the sprint lifecycle used to structure multi-agent collaboration into discrete, stage-gated iterations. Sprints give agents a shared frame of reference (intake → plan → discuss → validate → implement → synthesize) and give the human operator control gates at key transitions.

## Current Behavior

> **Status: Implemented** — Full sprint lifecycle operational: creation with overflow carry-forward, six-stage advancement with artifact gates and human sign-off, artifact storage with upsert, completion/cancellation, real-time event broadcasting, orchestrator integration (stage-aware prompts and role roster filtering), REST API, agent commands, and frontend panel.

### Sprint Lifecycle

A sprint is a numbered iteration scoped to a single workspace. Each workspace may have **at most one active sprint** at any time. The lifecycle follows a fixed sequence of six stages:

```
Intake → Planning → Discussion → Validation → Implementation → FinalSynthesis
```

Stages advance forward only — there is no mechanism to revert to a previous stage. If rework is needed, the sprint stays at the current stage while agents revise their artifacts.

### Stages

| Stage | Purpose | Required Artifact | Sign-off | Roster |
|-------|---------|-------------------|----------|--------|
| Intake | Gather requirements, define scope | `RequirementsDocument` | ✅ Human | Planner only |
| Planning | Break work into tasks, assign agents | `SprintPlan` | ✅ Human | Planner, Architect |
| Discussion | Debate trade-offs, challenge assumptions | — | — | Planner, Architect, SoftwareEngineer, TechnicalWriter |
| Validation | Review plan for feasibility, produce validation report | `ValidationReport` | — | All roles |
| Implementation | Execute the plan: tasks, branches, PRs, reviews | — | — | All roles |
| FinalSynthesis | Retrospective: what was delivered, lessons, overflow | `SprintReport` | — | All roles |

**Artifact gates**: Stages with a required artifact cannot be advanced until an artifact of that type has been stored for the current stage.

**Human sign-off**: Intake and Planning stages enter an `AwaitingSignOff` state when agents request advancement. A human must approve (advancing to the next stage) or reject (keeping the sprint at the current stage for revision). Other stages advance immediately.

**Role roster**: The orchestrator filters agents per stage. Agents whose role is not in the roster for the current stage are excluded from conversation rounds. Stages not listed in the roster map (Validation, Implementation, FinalSynthesis) admit all roles.

### Sprint States

```
Active ──→ Completed     (normal completion from FinalSynthesis)
Active ──→ Cancelled     (cancelled at any stage)
Active ──→ AwaitingSignOff ──→ Active  (approve / reject cycle)
```

A completed or cancelled sprint sets `CompletedAt` to the current UTC timestamp.

### Overflow Carry-Forward

When the previous sprint in the workspace has an `OverflowRequirements` artifact stored in the **FinalSynthesis** stage, the next sprint created for the same workspace:

1. Sets `OverflowFromSprintId` to the previous sprint's ID
2. Auto-injects the overflow content as an `OverflowRequirements` artifact in the new sprint's Intake stage
3. The overflow content is appended to the Intake stage preamble in agent prompts

This ensures unfinished work is not silently dropped between sprints.

## Entities

> **Source**: `src/AgentAcademy.Server/Data/Entities/SprintEntity.cs`, `SprintArtifactEntity.cs`

### SprintEntity

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `string` | GUID primary key |
| `Number` | `int` | Sequential sprint number within the workspace (1-based) |
| `WorkspacePath` | `string` | Owning workspace path |
| `Status` | `string` | `Active` / `Completed` / `Cancelled` |
| `CurrentStage` | `string` | Current stage name from the stage sequence |
| `OverflowFromSprintId` | `string?` | FK to previous sprint if overflow exists |
| `AwaitingSignOff` | `bool` | True when waiting for human approval to advance |
| `PendingStage` | `string?` | The stage that will become current if sign-off is approved |
| `SignOffRequestedAt` | `DateTime?` | UTC timestamp when `AwaitingSignOff` became true |
| `CreatedAt` | `DateTime` | UTC creation timestamp |
| `CompletedAt` | `DateTime?` | UTC completion/cancellation timestamp |

**Table**: `sprints`
**Indexes**:
- `idx_sprints_one_active_per_workspace` on `WorkspacePath` — unique, filtered: `Status = 'Active'`
- `idx_sprints_workspace_status` on `(WorkspacePath, Status)`
- `idx_sprints_workspace_number_unique` on `(WorkspacePath, Number)` — unique

**Relationships**:
- Self-referential: `OverflowFromSprint` → `SprintEntity` (FK: `OverflowFromSprintId`, on delete: set null)
- Referenced by: `TaskEntity.Sprint`, `PlanEntity.Sprint`, `SprintArtifactEntity.Sprint`, `ConversationSessionEntity.Sprint`

### SprintArtifactEntity

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | Auto-incremented primary key |
| `SprintId` | `string` | FK to owning sprint |
| `Stage` | `string` | Stage the artifact belongs to |
| `Type` | `string` | Artifact type (see enum below) |
| `Content` | `string` | JSON content of the artifact |
| `CreatedByAgentId` | `string?` | Agent that created the artifact (null for system-injected) |
| `CreatedAt` | `DateTime` | UTC creation timestamp |
| `UpdatedAt` | `DateTime?` | UTC timestamp of last update (upsert) |

**Table**: `sprint_artifacts`
**Indexes**:
- `idx_sprint_artifacts_sprint` on `SprintId`
- `idx_sprint_artifacts_sprint_stage` on `(SprintId, Stage)`
- `idx_sprint_artifacts_sprint_type` on `(SprintId, Type)`
- `idx_sprint_artifacts_sprint_stage_type_unique` on `(SprintId, Stage, Type)` — unique

**Constraint**: One artifact of each type per stage per sprint (enforced by unique index). Storing the same type again for the same stage upserts the content.

## Shared Models

> **Source**: `src/AgentAcademy.Shared/Models/Sprints.cs`

### Enums

```csharp
public enum SprintStage { Intake, Planning, Discussion, Validation, Implementation, FinalSynthesis }
public enum SprintStatus { Active, Completed, Cancelled }
public enum ArtifactType { RequirementsDocument, SprintPlan, ValidationReport, SprintReport, OverflowRequirements }
```

All use `JsonStringEnumConverter` for JSON serialization.

### DTOs

```csharp
public record SprintSnapshot(
    string Id, int Number, SprintStatus Status, SprintStage CurrentStage,
    string? OverflowFromSprintId, bool AwaitingSignOff, SprintStage? PendingStage,
    DateTime? SignOffRequestedAt,
    DateTime CreatedAt, DateTime? CompletedAt);

public record SprintArtifact(
    int Id, string SprintId, SprintStage Stage, ArtifactType Type,
    string Content, string? CreatedByAgentId, DateTime CreatedAt, DateTime? UpdatedAt);
```

### Metrics DTOs

```csharp
public record SprintMetrics(
    string SprintId, int SprintNumber, SprintStatus Status,
    double? DurationSeconds, int StageTransitions, int ArtifactCount,
    int TaskCount, int CompletedTaskCount,
    Dictionary<string, double> TimePerStageSeconds,
    DateTime CreatedAt, DateTime? CompletedAt);

public record SprintMetricsSummary(
    int TotalSprints, int CompletedSprints, int CancelledSprints, int ActiveSprints,
    double? AverageDurationSeconds, double AverageTaskCount, double AverageArtifactCount,
    Dictionary<string, double> AverageTimePerStageSeconds);
```

Time values are in seconds (doubles) for clean JSON serialization. `DurationSeconds` is null for active sprints. `AverageDurationSeconds` is computed from completed sprints only. `TimePerStageSeconds` is derived from `SprintStageAdvanced` activity events — for active sprints, the current stage's elapsed time extends to `DateTime.UtcNow`.

### Typed Artifact Schemas

Artifact content is JSON. The following record types describe the expected shape per artifact type:

```csharp
public record RequirementsDocument(string Title, string Description,
    List<string> InScope, List<string> OutOfScope, List<string> AcceptanceCriteria);

public record SprintPlanDocument(string Summary, List<SprintPlanPhase> Phases,
    List<string>? OverflowRequirements);

public record SprintPlanPhase(string Name, string Description, List<string> Deliverables);

public record ValidationReport(string Verdict, List<string> Findings,
    List<string>? RequiredChanges);

public record SprintReport(string Summary, List<string> Delivered,
    List<string> Learnings, List<string>? OverflowRequirements);
```

**Note**: These types are enforced at the storage layer by `ValidateArtifactContent`. When `StoreArtifactAsync` is called with a known artifact type, the JSON content must deserialize into the corresponding record with all required fields present and non-empty. OverflowRequirements is exempt (free-form). Unknown type strings are rejected.

## Service Layer

> **Source**: `src/AgentAcademy.Server/Services/SprintService.cs` (626 lines)

### SprintService

Registered as scoped. Dependencies: `AgentAcademyDbContext`, `ActivityBroadcaster`, `ILogger<SprintService>`.

#### Constants

| Name | Type | Value |
|------|------|-------|
| `Stages` | `ReadOnlyCollection<string>` | `["Intake", "Planning", "Discussion", "Validation", "Implementation", "FinalSynthesis"]` |
| `RequiredArtifactByStage` | `IReadOnlyDictionary<string, string>` | Intake→RequirementsDocument, Planning→SprintPlan, Validation→ValidationReport, FinalSynthesis→SprintReport |
| `SignOffRequiredStages` | `IReadOnlySet<string>` | `{"Intake", "Planning"}` |

#### Methods

| Method | Returns | Description |
|--------|---------|-------------|
| `CreateSprintAsync(workspacePath)` | `SprintEntity` | Creates next sprint. Throws if active sprint exists. Links overflow from previous sprint. |
| `GetActiveSprintAsync(workspacePath)` | `SprintEntity?` | Returns the active sprint for a workspace. |
| `GetSprintByIdAsync(sprintId)` | `SprintEntity?` | Lookup by ID. |
| `GetSprintsForWorkspaceAsync(workspace, limit, offset)` | `(List<SprintEntity>, int)` | Paginated list, ordered by number descending. |
| `StoreArtifactAsync(sprintId, stage, type, content, agentId?)` | `SprintArtifactEntity` | Upserts artifact. Validates sprint is active and stage is valid. |
| `GetSprintArtifactsAsync(sprintId, stage?)` | `List<SprintArtifactEntity>` | Returns artifacts, optionally filtered by stage. |
| `AdvanceStageAsync(sprintId)` | `SprintEntity` | Advances to next stage. Checks artifact gate and sign-off requirement. |
| `ApproveAdvanceAsync(sprintId)` | `SprintEntity` | Approves pending sign-off. Moves to the pending stage. |
| `RejectAdvanceAsync(sprintId)` | `SprintEntity` | Rejects pending sign-off. Clears AwaitingSignOff without advancing. |
| `CompleteSprintAsync(sprintId, force)` | `SprintEntity` | Completes the sprint. Must be in FinalSynthesis unless force=true. Checks SprintReport artifact. |
| `CancelSprintAsync(sprintId)` | `SprintEntity` | Cancels an active sprint. |
| `GetStageIndex(stage)` | `int` | Returns index of stage in the sequence (-1 if invalid). Static. |
| `GetNextStage(stage)` | `string?` | Returns the stage after the given one, or null if last. Static. |

#### Event Broadcasting

Every state change queues an `ActivityEvent` which is persisted to the `activity_events` table and broadcast via `ActivityBroadcaster` after `SaveChangesAsync`. Events use these types:

| Operation | ActivityEventType | Metadata Keys |
|-----------|-------------------|---------------|
| Create sprint | `SprintStarted` | sprintId, sprintNumber, status, currentStage |
| Store artifact | `SprintArtifactStored` | sprintId, artifactId (if update), stage, artifactType, createdByAgentId, isUpdate |
| Advance stage | `SprintStageAdvanced` | sprintId, action (advanced/signoff_requested/approved/rejected), previousStage, currentStage, pendingStage, awaitingSignOff |
| Complete sprint | `SprintCompleted` | sprintId, status (Completed) |
| Cancel sprint | `SprintCancelled` | sprintId, status (Cancelled) |

## Stage Preambles & Role Roster

> **Source**: `src/AgentAcademy.Server/Services/SprintPreambles.cs` (162 lines)

### SprintPreambles (static class)

Provides stage-specific instruction text injected into agent prompts and role-based roster filtering.

#### Stage Preambles

Each stage has a dedicated instruction block:

- **Intake**: "Gathering requirements. Produce RequirementsDocument. Ask clarifying questions. Don't propose solutions."
- **Planning**: "Using RequirementsDocument, produce SprintPlan with tasks, assignments, dependencies, risks."
- **Discussion**: "Open discussion. Debate trade-offs, challenge assumptions, raise concerns."
- **Validation**: "Validate plan for completeness and feasibility. Produce ValidationReport. Flag blockers."
- **Implementation**: "Execute the plan. Workflow: create tasks → task branches → PRs → review → merge. Follow the SprintPlan."
- **FinalSynthesis**: "Retrospective. Produce SprintReport with outcomes, lessons, overflow items."

#### BuildPreamble

```csharp
public static string BuildPreamble(
    int sprintNumber, string stage,
    IReadOnlyList<(string Stage, string Summary)>? priorStageContext,
    string? overflowContent)
```

Composes the full preamble by concatenating:
1. Sprint number header (`=== SPRINT #N ===`)
2. Stage-specific instruction
3. Overflow content (Intake only, if previous sprint had overflow)
4. Prior stage context summaries (one per completed stage)

#### IsRoleAllowedInStage / FilterByStageRoster

Controls which agents participate in each stage. See the Stages table above for the roster mapping.

## Orchestrator Integration

> **Source**: `src/AgentAcademy.Server/Services/AgentOrchestrator.cs`

The orchestrator integrates sprint context into conversation rounds:

1. **LoadSprintContextAsync**: Loads the active sprint, builds the preamble from stage instructions + prior stage context + overflow. Returns `(Preamble, ActiveStage)`.

2. **Prompt injection**: The sprint preamble is passed to `PromptBuilder.BuildConversationPrompt` and included in every agent's prompt for both main room and DM conversations.

3. **Roster filtering**: Before running conversation rounds, the orchestrator:
   - Excludes the planner if their role isn't in the current stage's roster
   - Filters the agent list through `SprintPreambles.FilterByStageRoster`

4. **Session management**: When `ADVANCE_STAGE` is executed, `ConversationSessionService.CreateSessionForStageAsync` archives the current session and creates a new one tagged with the sprint ID and stage. This provides clean context boundaries per stage.

### Sprint Context in Sessions

> **Source**: `src/AgentAcademy.Server/Services/ConversationSessionService.cs`

- `CreateSessionForStageAsync(roomId, sprintId, stage)`: Archives the current session for the room and creates a fresh one linked to the sprint stage.
- `GetStageContextAsync(sprintId, stage)`: Returns the summary of the archived session for a specific stage.
- `GetSprintContextAsync(sprintId)`: Returns summaries for all completed stages in canonical order, deduplicated to the latest archived session per stage. Used by `LoadSprintContextAsync` to build the prior-stage context portion of the preamble.

## REST API

> **Source**: `src/AgentAcademy.Server/Controllers/SprintController.cs` (341 lines)

All endpoints require authentication (cookie auth or consultant key) when auth is configured. If no auth provider is enabled (e.g., local development without OAuth), the authorization fallback policy is not registered and endpoints are accessible without credentials. This is a system-wide pattern, not sprint-specific.

### Read Endpoints

| Method | Path | Description | Returns |
|--------|------|-------------|---------|
| GET | `/api/sprints` | List sprints for active workspace | `SprintListResponse { Sprints, TotalCount }` |
| GET | `/api/sprints/active` | Active sprint with artifacts | `SprintDetailResponse` or 204 |
| GET | `/api/sprints/{id}` | Sprint by ID with artifacts | `SprintDetailResponse` or 404 |
| GET | `/api/sprints/{id}/artifacts` | Artifacts for a sprint | `List<SprintArtifact>` |
| GET | `/api/sprints/{id}/metrics` | Aggregated metrics for a sprint | `SprintMetrics` or 404 |
| GET | `/api/sprints/metrics/summary` | Workspace-level sprint metrics rollup | `SprintMetricsSummary` |

Query parameters:
- `GET /api/sprints`: `limit` (default 20), `offset` (default 0)
- `GET /api/sprints/{id}/artifacts`: `stage` (optional filter)

### Write Endpoints

| Method | Path | Description | Returns |
|--------|------|-------------|---------|
| POST | `/api/sprints` | Start new sprint | `SprintDetailResponse` or 409 |
| POST | `/api/sprints/{id}/advance` | Advance to next stage | `SprintDetailResponse` or 409 |
| POST | `/api/sprints/{id}/complete` | Complete sprint | `SprintSnapshot` or 409 |
| POST | `/api/sprints/{id}/cancel` | Cancel sprint | `SprintSnapshot` or 409 |
| POST | `/api/sprints/{id}/approve-advance` | Approve pending sign-off | `SprintDetailResponse` or 409 |
| POST | `/api/sprints/{id}/reject-advance` | Reject pending sign-off | `SprintSnapshot` or 409 |

Query parameters:
- `POST /api/sprints/{id}/complete`: `force` (boolean, skip stage/artifact checks)

**Workspace ownership**: Write endpoints validate that the sprint belongs to the active workspace. Returns 404 if the sprint doesn't exist or belongs to a different workspace.

**Response types**:
- `SprintDetailResponse { Sprint, Artifacts, Stages }` — includes the full stage list for UI stage progression rendering
- `SprintListResponse { Sprints, TotalCount }` — paginated
- `SprintSnapshot` — lightweight sprint state

### Error Handling

- `InvalidOperationException` → 409 Conflict with `{ error: "message" }`
- All other exceptions → 500 with generic `ProblemDetails`

## Agent Commands

> **Source**: `src/AgentAcademy.Server/Commands/Handlers/`

### START_SPRINT

**Handler**: `StartSprintHandler`
**Args**: None (uses active workspace)
**Agent permissions**: Planner only (Aristotle). All other roles denied. Human/Consultant access via REST API.
**Behavior**: Creates a new sprint for the active workspace. Fails if an active sprint already exists.
**Result**: `{ sprintId, number, stage, workspacePath, overflowFrom, message }`

### ADVANCE_STAGE

**Handler**: `AdvanceStageHandler`
**Args**: `SprintId` (optional — resolves from active workspace if omitted)
**Agent permissions**: Planner only (Aristotle). Human/Consultant access via REST API.
**Behavior**: Advances the sprint to the next stage. Validates artifact gate. If sign-off required, enters AwaitingSignOff. On success, creates a new conversation session for the new stage (best-effort — failure becomes a warning, not a rollback).
**Result**: `{ sprintId, number, previousStage, currentStage, pendingStage?, awaitingSignOff, warning?, message }`

### STORE_ARTIFACT

**Handler**: `StoreArtifactHandler`
**Args**: `Type` (required), `Content` (required), `SprintId` (optional), `Stage` (optional — defaults to current stage)
**Agent permissions**: All roles (Planner, Architect, SoftwareEngineer, Reviewer, TechnicalWriter).
**Behavior**: Stores or updates a deliverable artifact. Validates sprint is active and stage is valid.
**Result**: `{ sprintId, stage, type, agentId, message }`

### COMPLETE_SPRINT

**Handler**: `CompleteSprintHandler`
**Args**: `SprintId` (optional), `Force` (optional boolean)
**Agent permissions**: Planner only (Aristotle). Human/Consultant access via REST API.
**Behavior**: Marks the sprint as completed. Must be in FinalSynthesis with SprintReport artifact unless force=true.
**Result**: `{ sprintId, number, status, completedAt, message }`

## Frontend

> **Source**: `src/agent-academy-client/src/SprintPanel.tsx` (1148 lines)

### SprintPanel Component

The Sprint tab displays the active sprint with full lifecycle controls:

**Active Sprint View**:
- Stage progression bar showing all 6 stages with current-stage highlight
- Stage metadata (icon, label, description) per stage
- Elapsed time since sprint creation
- Sign-off controls (approve/reject buttons) when `AwaitingSignOff` is true
- Advance and Complete action buttons
- Cancel sprint button
- Artifact viewer with markdown rendering (via `react-markdown` + `remark-gfm`)

**No Active Sprint View**:
- EmptyState component with "Start Sprint" button
- Sprint history list (previous sprints) with status badges

**Sprint History**:
- Paginated list of past sprints
- Click to view detail with artifacts

### API Functions

> **Source**: `src/agent-academy-client/src/api.ts`

| Function | HTTP | Path |
|----------|------|------|
| `getActiveSprint()` | GET | `/api/sprints/active` |
| `getSprints(limit, offset)` | GET | `/api/sprints` |
| `getSprintDetail(id)` | GET | `/api/sprints/{id}` |
| `getSprintArtifacts(id, stage?)` | GET | `/api/sprints/{id}/artifacts` |
| `startSprint()` | POST | `/api/sprints` |
| `advanceSprint(id)` | POST | `/api/sprints/{id}/advance` |
| `completeSprint(id, force?)` | POST | `/api/sprints/{id}/complete` |
| `cancelSprint(id)` | POST | `/api/sprints/{id}/cancel` |
| `approveSprintAdvance(id)` | POST | `/api/sprints/{id}/approve-advance` |
| `rejectSprintAdvance(id)` | POST | `/api/sprints/{id}/reject-advance` |

### Real-Time Updates

The frontend subscribes to `SprintRealtimeEvent` via the activity SSE stream. Event actions:

```typescript
type SprintEventAction = "started" | "stage_advanced" | "artifact_stored" | "completed";
```

When a sprint event arrives, the panel:
1. Increments a `sprintVersion` counter (optimistic UI trigger)
2. Refetches the active sprint detail
3. Updates the UI with the new state

### Task Filtering

Tasks can be filtered by sprint: `getTasks(sprintId?)` passes the sprint ID as a query parameter to `GET /api/tasks?sprintId={id}`. This shows only tasks created during the active sprint.

## Sprint Duration Limits

> **Source**: `src/AgentAcademy.Server/Services/SprintTimeoutService.cs`

A background service prevents sprints from blocking indefinitely. Two timeout types:

### Sign-Off Timeout

When a sprint enters `AwaitingSignOff`, `SignOffRequestedAt` is set on the entity. If no human approves or rejects within the configured timeout (default: 4 hours), the service auto-rejects the sign-off — clearing `AwaitingSignOff` so agents can revise and re-request.

The auto-reject emits a `SprintStageAdvanced` event with `action: "timeout_rejected"` and `reason: "timeout"` metadata.

### Sprint Max Duration Timeout

If an active sprint exceeds the configured max duration (default: 48 hours), the service auto-cancels it. This emits a `SprintCancelled` event with `reason: "timeout"` metadata.

### Configuration

> **Source**: `src/AgentAcademy.Shared/Models/Sprints.cs` (`SprintTimeoutSettings`)

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Master switch for timeout checking |
| `SignOffTimeoutMinutes` | `int` | `240` | Minutes before auto-reject of pending sign-off |
| `MaxSprintDurationHours` | `int` | `48` | Hours before auto-cancel of active sprint |
| `CheckIntervalMinutes` | `int` | `5` | Polling interval for the background service |

Configuration section: `SprintTimeouts` in `appsettings.json`.

### Entity Change

`SprintEntity.SignOffRequestedAt` (`DateTime?`) — set when `AwaitingSignOff` becomes true, cleared on approve/reject/timeout. Persisted in the `sprints` table (migration: `AddSprintSignOffRequestedAt`). Exposed in `SprintSnapshot` DTO.

## Invariants

1. **One active sprint per workspace**: `CreateSprintAsync` enforces this with a read-then-insert check. The index on `(WorkspacePath, Status)` is not unique — concurrent requests could theoretically race and create duplicates. In practice, sprint creation is single-threaded (triggered by one agent or one human via the command pipeline or REST API). The unique index on `(WorkspacePath, Number)` prevents duplicate numbering but not duplicate active status.
2. **Forward-only advancement**: No API exists to move a sprint backward. `AdvanceStageAsync` only increments the stage index.
3. **Artifact gates are mandatory**: Cannot advance from a stage that has a required artifact type until that artifact has been stored.
4. **Sign-off gates are blocking**: Intake and Planning stages enter `AwaitingSignOff` rather than advancing. The sprint cannot advance further until a human approves.
5. **Upsert semantics for artifacts**: Storing the same artifact type for the same stage of the same sprint overwrites the content (unique index: `SprintId + Stage + Type`).
6. **Sprint completion requires FinalSynthesis**: Unless `force=true`, the sprint must be in FinalSynthesis stage with a SprintReport artifact.
7. **Cancellation is always allowed**: An active sprint can be cancelled at any stage without conditions.
8. **Events are post-commit**: Activity events are queued during the operation and broadcast only after `SaveChangesAsync` succeeds. Subscribers never see events for uncommitted state.

## Test Coverage

| Test File | Focus | Count |
|-----------|-------|-------|
| `SprintServiceTests.cs` | Core lifecycle, artifact gates, sign-off, overflow | 48 tests |
| `SprintCommandHandlerTests.cs` | Command handler integration | 17 tests |
| `SprintControllerTests.cs` | REST API endpoints | 19 tests |
| `SprintPreamblesTests.cs` | Preamble construction, role filtering | 19 tests |
| `SprintServiceEventTests.cs` | Activity event emission | 17 tests |
| `SprintMetricsTests.cs` | Per-sprint metrics and workspace-level summary | 15 tests |
| `SprintTimeoutTests.cs` | Sign-off/duration timeout + background service | 19 tests |
| `sprintPanel.test.ts` | Frontend panel rendering and interactions | 53 tests |
| `sprintRealtime.test.ts` | Real-time event handling | 34 tests |

**Total: 241 tests** across backend and frontend.

## Known Gaps

- ~~**No artifact content validation**: The typed record schemas (`RequirementsDocument`, `SprintPlanDocument`, etc.) are not enforced at the storage layer. Agents can store arbitrary JSON that doesn't conform to the expected schema.~~ — **Resolved**: `StoreArtifactAsync` validates artifact content against typed record schemas before storage. RequirementsDocument, SprintPlan, ValidationReport, and SprintReport must deserialize correctly with all required fields present and non-empty. SprintPlanPhase nested records are validated. OverflowRequirements is free-form (no validation). Unknown artifact type strings are rejected. Malformed JSON and missing required fields produce distinct `ArgumentException` messages. 10 tests.
- ~~**No sprint duration limits**: There's no timeout or maximum duration for a sprint. A sprint in `AwaitingSignOff` will block indefinitely until a human responds.~~ — **Resolved**: `SprintTimeoutService` background service checks every 5 minutes (configurable). Sign-off timeout auto-rejects after 4 hours (configurable). Sprint max duration auto-cancels after 48 hours (configurable). `SignOffRequestedAt` field on `SprintEntity` tracks sign-off entry time. Configurable via `SprintTimeoutSettings` (section: `SprintTimeouts`). Events include `reason: "timeout"` metadata. 19 tests.
- ~~**No sprint metrics aggregation**: While individual events are tracked, there's no rollup of sprint-level metrics (total rounds, token cost per sprint, time per stage).~~ — **Resolved**: `GetSprintMetricsAsync` returns per-sprint rollup (duration, stage transitions, artifact/task counts, time per stage). `GetMetricsSummaryAsync` returns workspace-level averages. REST endpoints: `GET /api/sprints/{id}/metrics` and `GET /api/sprints/metrics/summary`. 15 tests.

## Revision History

| Date | Change | Task/Branch |
|------|--------|-------------|
| 2026-04-11 | Initial spec — documenting implemented sprint system | — |
| 2026-04-11 | Fix 3 known gaps: SprintCancelled event type, stage-aware overflow, active sprint unique index | develop |
| 2026-04-11 | Sprint metrics aggregation: per-sprint and workspace-level rollup endpoints with 15 tests | develop |
| 2026-04-11 | Sprint duration limits: sign-off timeout (4h default), max duration (48h default), SprintTimeoutService background service, 19 tests | develop |
| 2026-04-11 | Artifact content validation: typed schema enforcement at storage layer, unknown type rejection, 10 tests | develop |
