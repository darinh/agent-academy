# 005 — Domain Services Layer

## Purpose

Documents the domain services that manage core workspace state — rooms, agents, messages, tasks, breakout rooms, plans, and activity events. Controllers, command handlers, and the orchestrator inject these services directly.

## Current Behavior

**Status: Implemented**

> **History**: This spec originally documented `WorkspaceRuntime`, a scoped facade that delegated to domain services. As of 2026-04-12, the facade has been deleted — all consumers now inject the focused domain services directly. The behavioral documentation below remains accurate; only the indirection layer was removed.

### Service Architecture

Controllers and command handlers inject focused domain services directly via constructor injection. There is no facade or intermediary layer.

**Service interface contracts** (`Services/Contracts/`): All seven task services plus room, room lifecycle, room snapshot, workspace-room, message, breakout, agent location, and crash recovery services expose interface contracts. Consumers inject the interface (e.g., `ITaskQueryService`, `IRoomService`, `IRoomSnapshotBuilder`, `IWorkspaceRoomService`, `ICrashRecoveryService`), not the concrete class. DI registration uses the forwarding pattern — each concrete type is registered alongside its interface so both resolve to the same scoped instance. See [Service Registration](#service-registration) below.

| Service | Interface | Responsibility | Registration | Source |
|---------|-----------|---------------|--------------|--------|
| `InitializationService` | — | Startup room/agent seeding, server instance tracking | Scoped | `Services/InitializationService.cs` |
| `CrashRecoveryService` | `ICrashRecoveryService` | Crash detection, breakout/agent/task recovery | Scoped + forwarded | `Services/CrashRecoveryService.cs` |
| `RoomService` | `IRoomService` | Room CRUD, phase transitions, workspace scoping | Scoped + forwarded | `Services/RoomService.cs` |
| `RoomSnapshotBuilder` | `IRoomSnapshotBuilder` | Builds read-model snapshots of rooms (messages, task, participants) | Scoped + forwarded | `Services/RoomSnapshotBuilder.cs` |
| `WorkspaceRoomService` | `IWorkspaceRoomService` | Workspace–room relationships, default room creation, startup resolution | Scoped + forwarded | `Services/WorkspaceRoomService.cs` |
| `RoomLifecycleService` | `IRoomLifecycleService` | Room close/reopen/auto-archive/cleanup, agent evacuation | Scoped + forwarded | `Services/RoomLifecycleService.cs` |
| `MessageService` | `IMessageService` | Room/DM/breakout messaging, message trimming | Scoped + forwarded | `Services/MessageService.cs` |
| `TaskQueryService` | `ITaskQueryService` | Task queries, assignment, status updates, evidence/spec-link reads | Scoped + forwarded | `Services/TaskQueryService.cs` |
| `TaskLifecycleService` | `ITaskLifecycleService` | Task creation staging, claim/release/approve/reject | Scoped + forwarded | `Services/TaskLifecycleService.cs` |
| `TaskEvidenceService` | `ITaskEvidenceService` | Evidence recording and gate checks | Scoped + forwarded | `Services/TaskEvidenceService.cs` |
| `TaskDependencyService` | `ITaskDependencyService` | Task dependency DAG: add/remove/query/cycle detection | Scoped + forwarded | `Services/TaskDependencyService.cs` |
| `TaskItemService` | `ITaskItemService` | Task item CRUD | Scoped + forwarded | `Services/TaskItemService.cs` |
| `TaskOrchestrationService` | `ITaskOrchestrationService` | Task creation/completion/rejection coordinating rooms, agents, and lifecycle | Scoped + forwarded | `Services/TaskOrchestrationService.cs` |
| `TaskAnalyticsService` | `ITaskAnalyticsService` | Task cycle effectiveness metrics | Scoped + forwarded | `Services/TaskAnalyticsService.cs` |
| `BreakoutRoomService` | `IBreakoutRoomService` | Breakout room lifecycle, task association, stuck reopening | Scoped + forwarded | `Services/BreakoutRoomService.cs` |
| `AgentLocationService` | `IAgentLocationService` | Agent location tracking and movement | Scoped + forwarded | `Services/AgentLocationService.cs` |
| `PlanService` | — | Plan CRUD with room/breakout validation | Scoped | `Services/PlanService.cs` |
| `TaskSnapshotFactory` | — (static) | Pure DTO mapping: `TaskEntity` → `TaskSnapshot`, comments, evidence, spec links | Static class | `Services/TaskSnapshotFactory.cs` |
| `ActivityPublisher` | — | Event creation, EF persistence, broadcast via `ActivityBroadcaster` | Scoped | `Services/ActivityPublisher.cs` |
| `ActivityBroadcaster` | — | In-memory event buffer (last 100) and subscriber notification | Singleton | `Services/ActivityBroadcaster.cs` |

The workspace overview aggregation (rooms, locations, breakouts, activity) is inlined in `SystemController.GetOverview()` (`Controllers/SystemController.cs`).

### Initialization

> **Source**: `src/AgentAcademy.Server/Services/InitializationService.cs`

On startup, `Program.cs` calls `InitializationService.InitializeAsync()` which:
1. Creates the default room (`main` / "Main Collaboration Room") if it doesn't exist
2. Adds a system welcome message
3. Publishes `RoomCreated` and `AgentLoaded` events for each agent
4. Initializes `AgentLocation` records for all configured agents (default: idle in main room)

Initialization is idempotent — calling it multiple times has no effect.

### Agent Catalog

Agents are loaded from `Config/agents.json` via `AgentCatalogLoader`. The catalog is registered as a singleton `AgentCatalogOptions` in DI. Agents are sorted by name (case-insensitive) at load time.

**File**: `src/AgentAcademy.Server/Config/agents.json`
**Loader**: `src/AgentAcademy.Server/Config/AgentCatalogLoader.cs`

Configured agents (v1 port):
| Id | Name | Role |
|----|------|------|
| planner-1 | Aristotle | Planner |
| architect-1 | Archimedes | Architect |
| software-engineer-1 | Hephaestus | SoftwareEngineer |
| software-engineer-2 | Athena | SoftwareEngineer |
| reviewer-1 | Socrates | Reviewer |
| tech-writer-1 | Thucydides | TechnicalWriter |

### Room Management

> **Source**: `src/AgentAcademy.Server/Services/RoomService.cs`, `src/AgentAcademy.Server/Services/RoomSnapshotBuilder.cs`, `src/AgentAcademy.Server/Services/WorkspaceRoomService.cs`

- `RoomService.GetRoomsAsync(includeArchived)` → rooms for the active workspace, default room first then alphabetically. Archived rooms are excluded by default; pass `includeArchived: true` to include them.
- `RoomService.GetRoomAsync(roomId)` → single room snapshot or null (delegates to `RoomSnapshotBuilder`)
- `RoomService.RenameRoomAsync(roomId, newName)` → renames a room, publishes `RoomRenamed` activity event, cascades to Discord channel name via `OnRoomRenamedAsync`
- `WorkspaceRoomService.EnsureDefaultRoomForWorkspaceAsync(workspacePath)` → creates a workspace-specific default room (named from `_catalog.DefaultRoomName`), moves all agents there. Excludes the catalog default room when checking for existing workspace rooms. Auto-corrects stale room names.
- `WorkspaceRoomService.ResolveStartupMainRoomIdAsync(activeWorkspace)` → resolves the main room ID for a workspace on startup
- `RoomService.GetProjectNameForRoomAsync(roomId)` → resolves `roomId → WorkspacePath → ProjectName` (falls back to directory basename)
- `RoomLifecycleService.CleanupStaleRoomsAsync()` → scans for non-main rooms where all tasks are terminal (Completed/Cancelled), evacuates agents to default room, archives the rooms. Returns count of rooms cleaned up.
- `RoomLifecycleService.CloseRoomAsync(roomId)` → archives a non-main room (guards against main room, non-empty rooms)
- `RoomLifecycleService.ReopenRoomAsync(roomId)` → restores an archived room to Idle status
- `RoomLifecycleService.TryAutoArchiveRoomAsync(roomId)` → auto-archives when all tasks are terminal
- `RoomLifecycleService.IsMainCollaborationRoomAsync(roomId)` → returns true for the main collaboration room (lifecycle guard)

**Room rename API**: `PUT /api/rooms/{roomId}/name` with `{ "name": "..." }` body. Returns updated `RoomSnapshot`. Frontend: double-click room name in sidebar to edit inline.

**Project-scoped rooms**: Rooms are associated with a workspace via `WorkspacePath` (nullable FK to `workspaces.Path`). `GetRoomsAsync()` filters by the active workspace. Rooms without a workspace assignment are only visible when no workspace is active. Each workspace gets its own default room (ID: `{project-slug}-main`), with separate conversation history.

**Legacy room retirement**: `EnsureDefaultRoomForWorkspaceAsync` calls `RetireLegacyDefaultRoomAsync` to clear `WorkspacePath` on the catalog default room if it was backfilled into a workspace by the `AddWorkspacePathToRooms` migration.

**Stale room cleanup**: When all tasks in a room reach terminal state (Completed or Cancelled), the room is automatically archived and agents are evacuated to the workspace default room. Manual cleanup is available via `CleanupStaleRoomsAsync()`, the `CLEANUP_ROOMS` command, or `POST /api/rooms/cleanup`. `GetRoomsAsync()` excludes archived rooms by default; pass `includeArchived: true` to include them. Room rejection via `RejectTaskAsync` automatically reopens an auto-archived room.

**Room cleanup API**: `POST /api/rooms/cleanup` triggers `CleanupStaleRoomsAsync`. Returns `{ "archivedCount": N }`. `GET /api/rooms?includeArchived=true` includes archived rooms.

Each `RoomSnapshot` includes:
- Participants (built from `AgentLocationEntity` records — agents whose current location matches the room, filtered by the room's current phase roster via `SprintPreambles.IsRoleAllowedInStage`, with preferred-role flag from the active task). Agents configured for the room but outside the current phase's roster are excluded from the snapshot; they remain in `AgentLocations` unchanged.
- Recent messages (last 200 from DB)
- Active task (most recent active task for the room)

**Phase-scoped room membership** (design decision, 2026-04-17): Room membership is phase-scoped at the **presentation layer**, not the data layer. `AgentLocations` records the agent's *assigned* room and persists across phase transitions; `RoomSnapshotBuilder.BuildParticipants` applies the phase-roster filter (keyed on `room.CurrentPhase`) when building snapshots, and `ConversationRoundRunner` applies the roster filter (keyed on the active sprint stage) to turn selection. `RoomService.TransitionPhaseAsync` therefore does **not** mutate `AgentLocations` — updating `room.CurrentPhase` causes subsequent snapshots to reflect the new roster automatically. `SprintStageService.AdvanceStageAsync` and `ApproveAdvanceAsync` mirror the new sprint stage onto every room in the same workspace (see spec 013 → "Room phase sync"), so agent-driven stage advancement automatically re-rosters snapshots across the workspace. See `SprintPreambles.IsRoleAllowedInStage` for the per-stage role map.

### Task Management

> **Source**: `src/AgentAcademy.Server/Services/TaskOrchestrationService.cs`, `src/AgentAcademy.Server/Services/TaskQueryService.cs`

- `TaskOrchestrationService.CreateTaskAsync(TaskAssignmentRequest)` → creates task, optionally creates new room
- `TaskQueryService.GetTasksAsync()` → tasks for the active workspace, filtered by `WorkspacePath`
- `TaskQueryService.GetTaskAsync(taskId)` → single task or null

Task creation (in `TaskOrchestrationService`):
- If `RoomId` is provided and room exists: updates existing room to Active/Planning
- If `RoomId` is null: creates new room with normalized title as ID, stamped with active workspace's `WorkspacePath`
- Task entity is stamped with `WorkspacePath` from the room
- Adds system messages (TaskAssignment + Coordination)
- Publishes TaskCreated and PhaseChanged events
- Seeds `TaskSnapshot.CurrentPlan` from `TaskAssignmentRequest.CurrentPlan` when provided; otherwise uses the default planning checklist markdown
- **Auto-join**: When a new room is created, all agents with `AutoJoinDefaultRoom = true` are moved into the room via `MoveAgentAsync`. Agents currently in `Working` state are skipped to avoid disrupting in-flight breakout work. Failures are caught and logged per-agent (best-effort) so task creation always succeeds.

**Task query scoping**: `GetTasksAsync()` filters directly on `TaskEntity.WorkspacePath`. For pre-migration rows where `WorkspacePath` is null, a fallback checks the task's room to determine workspace membership.

### Message Management

> **Source**: `src/AgentAcademy.Server/Services/MessageService.cs`

- `MessageService.PostMessageAsync(PostMessageRequest)` → posts agent message
- `MessageService.PostHumanMessageAsync(roomId, content)` → posts human message

Validation: room must exist, sender must be in catalog (for agent messages).

**Message trimming**: When message count exceeds 200, oldest messages are deleted from the database. This matches v1 behavior.

**Session-aware message loading**: `RoomSnapshotBuilder.BuildRoomSnapshotAsync` loads only messages from the active conversation session (plus legacy untagged messages). Messages are tagged with `SessionId` when posted. See spec 003 → Conversation Session Management for epoch lifecycle details.

**Message tagging**: `PostMessageAsync`, `PostHumanMessageAsync`, and `PostBreakoutMessageAsync` call `ConversationSessionService.GetOrCreateActiveSessionAsync` to tag each message with the active session ID and increment the session's message count.

### Phase Management

> **Source**: `src/AgentAcademy.Server/Services/RoomService.cs`

- `RoomService.TransitionPhaseAsync(roomId, targetPhase, reason?)` → updates room and active task phase

Behavior:
- No-op if already in target phase
- Sets room status to `Completed` if target phase is `FinalSynthesis`
- Adds a Coordination system message documenting the transition
- Updates active task's `CurrentPhase` if one exists

No phase state machine — any phase can transition to any other phase.

**Two drivers for `room.CurrentPhase`**:
1. **Human-driven per-room override** via `POST /api/rooms/{id}/phase` → `RoomService.TransitionPhaseAsync`. Runs the phase prerequisite validator, creates a coordination message, and updates the active task's phase. Used for ad-hoc adjustments to a single room.
2. **Sprint-driven workspace mirror** via `SprintStageService.AdvanceStageAsync` / `ApproveAdvanceAsync`. When an agent (or human) advances the sprint stage, every room in `sprint.WorkspacePath` whose phase differs is updated silently to match the new stage. Emits a `PhaseChanged` activity event with `source: "sprint-sync"` per room. Bypasses the phase prerequisite validator (the sprint is the authoritative driver). See spec 013 → "Room phase sync" for the full contract.

### Agent Location

> **Source**: `src/AgentAcademy.Server/Services/AgentLocationService.cs`

- `AgentLocationService.GetAgentLocationsAsync()` → all agent locations
- `AgentLocationService.GetAgentLocationAsync(agentId)` → single location
- `AgentLocationService.MoveAgentAsync(agentId, roomId, state, breakoutRoomId?)` → updates location

Agent must be in catalog. Room existence is not validated (matches v1).

### Breakout Rooms

> **Source**: `src/AgentAcademy.Server/Services/BreakoutRoomService.cs`

- `BreakoutRoomService.CreateBreakoutRoomAsync(parentRoomId, agentId, name)` → creates breakout, moves agent to Working
- `BreakoutRoomService.CloseBreakoutRoomAsync(breakoutId)` → moves agent to Idle and archives the breakout (`Status = Archived`); messages are retained for audit
- `BreakoutRoomService.GetBreakoutRoomsAsync(parentRoomId)` → list for parent room

### Plan Management

> **Source**: `src/AgentAcademy.Server/Services/PlanService.cs`

- `PlanService.GetPlanAsync(roomId)` → `PlanContent` or null
- `PlanService.SetPlanAsync(roomId, content)` → create or update (upsert)
- `PlanService.DeletePlanAsync(roomId)` → returns true if deleted

Plan records are keyed by the active room identifier and may target either a main collaboration room or a breakout room. `SetPlanAsync` validates that the target ID belongs to an existing room or breakout room before writing.

### Activity Publishing

Activity publishing uses a two-layer architecture:

**`ActivityBroadcaster`** (singleton, `src/AgentAcademy.Server/Services/ActivityBroadcaster.cs`):
- In-memory ring buffer of last 100 events
- Subscriber list for real-time notification
- Thread-safe: subscribers invoked outside the lock to prevent deadlocks
- `Broadcast(evt)` — buffers event and notifies subscribers
- `GetRecentActivity()` → last 100 events
- `Subscribe(callback)` → returns unsubscribe `Action`

**`ActivityPublisher`** (scoped, `src/AgentAcademy.Server/Services/ActivityPublisher.cs`):
- Creates `ActivityEvent` records, adds them to the EF change tracker, and calls `ActivityBroadcaster.Broadcast`
- Caller owns `SaveChangesAsync` (event is persisted when the caller's unit-of-work commits)
- `Publish(type, roomId, actorId, taskId, message, ...)` → creates event, adds entity, broadcasts
- `PublishThinkingAsync(agent, roomId)` → convenience method that publishes + saves immediately
- `PublishFinishedAsync(agent, roomId)` → convenience method that publishes + saves immediately
- `GetRecentActivity()` → delegates to `ActivityBroadcaster`
- `Subscribe(callback)` → delegates to `ActivityBroadcaster`

Callers that need to publish events (e.g., `TaskOrchestrationService`, `ConversationRoundRunner`, `DirectMessageRouter`) resolve `ActivityPublisher` from their scoped DI container.

### Crash Recovery

> **Source**: `src/AgentAcademy.Server/Services/CrashRecoveryService.cs`

On startup, `AgentOrchestrator.HandleStartupRecoveryAsync` checks `CrashRecoveryService.CurrentCrashDetected` (set by `RecordServerInstanceAsync` when the previous instance had no clean shutdown). If a crash is detected, `CrashRecoveryService.RecoverFromCrashAsync(mainRoomId)` runs the following recovery steps in order:

1. **Close all active breakout rooms** — queries for non-terminal breakout rooms and calls `CloseBreakoutRoomAsync` with `BreakoutRoomCloseReason.ClosedByRecovery`
2. **Reset stuck agents** — finds agents in `Working` state whose `BreakoutRoomId` is null or doesn't match an active breakout, moves them to `Idle` via `MoveAgentAsync`
3. **Reset orphaned tasks** — finds tasks with in-progress status (`Active`, `InReview`, `ChangesRequested`, `Approved`, `Merging`, `AwaitingValidation`) whose assignee agent is no longer in an active breakout, clears their `AssignedAgentId` and `AssignedAgentName`
4. **Post recovery notification** — if any recovery actions occurred, posts a system message to the main room with counts (e.g., "Closed 2 breakout room(s), reset 1 stuck agent(s), and reset 1 stuck task(s)"). Uses `CurrentInstanceId` as a correlation ID to prevent duplicate notifications on multiple startup calls.

**Return type**: `CrashRecoveryResult(ClosedBreakoutRooms, ResetWorkingAgents, ResetTasks)` — a sealed record with the counts from each recovery step.

**Idempotency**: Recovery is safe to call multiple times. The correlation-based dedup prevents duplicate system messages. Agents and tasks that are already in a clean state are not affected.

**Note**: Recovery does not re-enqueue pending human messages. That is handled separately by `AgentOrchestrator.ReconstructQueueAsync` which calls `GetRoomsWithPendingHumanMessagesAsync`.

### Workspace Isolation — Git Worktrees

Each agent gets its own filesystem checkout via `git worktree` to enable concurrent development without conflicts.

#### WorktreeService (`Services/WorktreeService.cs`)

Singleton service managing git worktrees for agent-level workspace isolation.

**Core API:**
- `CreateWorktreeAsync(branch)` — creates a linked worktree at `{repoRoot}/.worktrees/{safeName}-{hash}`, where `safeName` is the sanitized branch name and `hash` is an 8-char hex of the raw branch name. The hash makes colliding sanitized names (e.g. `feat/x` vs `feat_x`) resolve to distinct directories.
- `RemoveWorktreeAsync(branch)` — removes the worktree and prunes git metadata
- `GetWorktreePath(branch)` — returns the filesystem path for a branch's worktree, or null
- `GetActiveWorktrees()` — returns all tracked worktree entries
- `ListGitWorktreesAsync()` — parses `git worktree list --porcelain` for ground truth
- `CleanupAllWorktreesAsync()` — available for shutdown/recovery cleanup. **Not currently wired** into the startup or shutdown pipeline (`WebApplicationExtensions.ConfigureShutdownHook()`) — treat as a manual/test-only entry point.
- `SyncWithGitAsync()` — reconciles internal tracking with actual git worktree state. **Wired into startup** via `InitializationService.InitializeAsync` (called at the end of init; failures are logged and swallowed so a sync error does not block other startup work). Callers may also invoke it explicitly when reconciliation is required outside startup.

**Agent worktree management:**
- `EnsureAgentWorktreeAsync(workspacePath, projectName, agentId, branch)` — creates or reuses an agent-specific worktree (helper for callers that need idempotent provisioning; `TaskAssignmentHandler` currently uses `CreateWorktreeAsync` with a task-specific branch instead).
- `GetAgentWorktreePath(projectName, agentId, workspacePath?)` — resolves an agent's worktree path. Paths live under `~/projects/{safeName}-worktrees[-{pathHash}]/{safeAgent}` (outside the repo, not under `.worktrees/`). The optional `workspacePath` adds an 8-char hash suffix so multiple checkouts of the same project name don't collide.
- `RemoveAgentWorktreeAsync(workspacePath, projectName, agentId)` — removes an agent's worktree

**Key types:**
- `WorktreeInfo(Branch, Path, CreatedAt)` — tracked worktree metadata
- `GitWorktreeEntry(Path, Head, Branch, Bare)` — parsed git worktree state

**Invariants:**
- Branch worktrees live under `{repoRoot}/.worktrees/` (gitignored); agent worktrees live under `~/projects/{project}-worktrees[-{hash}]/` (outside the repo)
- Each agent gets at most one worktree per project (per workspace-path hash)
- Worktree cleanup is idempotent and handles already-removed paths gracefully

#### Service Integration

`BreakoutLifecycleService` and `TaskAssignmentHandler` use `WorktreeService` to provide each agent with an isolated working directory:
- When an agent starts a task, `TaskAssignmentHandler` calls `CreateWorktreeAsync(taskBranch)` to provision a worktree for the task's branch (with fallback to the shared checkout if worktree creation fails)
- The agent's `CommandContext.WorkingDirectory` is set to the worktree path
- All git and file operations (build, test, diff, commit) execute within the worktree
- On task completion/cancellation, the worktree is disposed by the task-terminal owner: `TaskOrchestrationService.CompleteTaskAsync` (on merge/complete) and `CancelTaskHandler` (on cancel, before the branch delete so `git branch -D` succeeds). The worktree persists through breakout close/reopen cycles (e.g., task rejection → breakout reopen) — `BreakoutLifecycleService` disposes only the per-breakout Copilot subprocess, not the filesystem worktree.

#### Database Fields

- `TaskEntity.WorkspacePath` — associates tasks with a project directory
- `ConversationSessionEntity.WorkspacePath` — associates sessions with a project directory

#### Worktree Status REST API

`WorktreeController` (`Controllers/WorktreeController.cs`) exposes worktree status for operator visibility into agent work-in-progress.

**Endpoint**: `GET /api/worktrees` → `List<WorktreeStatusSnapshot>`

**Behavior**:
1. Retrieves all active worktrees from `WorktreeService.GetActiveWorktrees()`
2. Batch-queries tasks by branch name to link worktrees to task/agent metadata (prefers the most recent non-completed task per branch)
3. Collects git status for each worktree in parallel (bounded to 4 concurrent `git` operations via `SemaphoreSlim`)
4. For each worktree, runs `git status --porcelain=v1` (dirty files, capped at 10 preview), `git diff --shortstat HEAD --` (aggregate diff counts), and `git log -1 --format=%H%x00%s%x00%an%x00%aI` (last commit metadata). Each git command is wrapped in its own try/catch — individual command failures are tolerated and produce partial data (e.g., diff stats may be zero while dirty files are populated)
5. Returns an empty list if no active worktrees exist

**Response type** (`WorktreeStatusSnapshot` in `AgentAcademy.Shared.Models`):
```csharp
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
```

**Error handling**: Two levels of degradation:
- **Worktree-level failure** (exception escapes to the controller's `BuildSnapshotAsync`): `WorktreeGitStatus.Unavailable(errorMessage)` is returned with `StatusAvailable = false`. This covers missing directories and unrecoverable errors.
- **Git command-level failure** (inside `GetWorktreeGitStatusAsync`): Individual git commands (`status`, `diff`, `log`) are each wrapped in try/catch. If one fails, the others still populate their fields and `StatusAvailable` remains `true` with partial data (e.g., dirty files present but diff stats zeroed). The request never fails due to a single worktree's git issues.

### REST API Endpoints

#### Summary

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/rooms/{roomId}/plan` | Get the current plan for a room |
| `PUT` | `/api/rooms/{roomId}/plan` | Create or update the plan |
| `DELETE` | `/api/rooms/{roomId}/plan` | Delete the plan |
| `GET` | `/api/rooms/{roomId}/artifacts` | Artifacts produced in a room |
| `GET` | `/api/rooms/{roomId}/usage/records` | Individual LLM call records for a room |
| `GET` | `/api/rooms/{roomId}/evaluations` | Artifact evaluations for a room |
| `POST` | `/api/rooms/{roomId}/phase` | Transition room to a new phase |
| `POST` | `/api/rooms/{roomId}/compact` | Reset agent Copilot sessions for a room |

#### Room Plans (PlanController)

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/rooms/{roomId}/plan` | Get the current plan for a room |
| `PUT` | `/api/rooms/{roomId}/plan` | Create or update the plan |
| `DELETE` | `/api/rooms/{roomId}/plan` | Delete the plan |

**GET `/api/rooms/{roomId}/plan`** — returns `PlanContent` or `404` if no plan exists.

**PUT `/api/rooms/{roomId}/plan`** request:
```json
{
  "content": "## Plan\n\n1. Design the API..."
}
```
Response:
```json
{
  "status": "saved",
  "roomId": "room-abc"
}
```

**DELETE `/api/rooms/{roomId}/plan`** response:
```json
{
  "status": "deleted",
  "roomId": "room-abc"
}
```

**Implementation**: `PlanController.cs`, delegates to `PlanService`.

#### Room Artifacts (RoomController)

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/rooms/{roomId}/artifacts` | Artifacts produced in a room |

**GET `/api/rooms/{roomId}/artifacts`** — returns `ArtifactRecord[]`.

**Implementation**: `RoomController.cs`.

#### Room Usage Records (RoomController)

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/rooms/{roomId}/usage/records` | Individual LLM call records for a room |

**GET `/api/rooms/{roomId}/usage/records`** — returns `List<LlmUsageRecord>`.

Query parameters:
- `agentId?` — filter records to a single agent
- `limit?` — maximum number of records (default `50`)

> **Note**: Room-level aggregated usage (`/api/rooms/{roomId}/usage`) and per-agent breakdown (`/api/rooms/{roomId}/usage/agents`) are already documented in spec 003. This endpoint provides the raw per-call records.

**Implementation**: `RoomController.cs`.

#### Room Evaluations (RoomController)

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/rooms/{roomId}/evaluations` | Artifact evaluations for a room |

**GET `/api/rooms/{roomId}/evaluations`** response:
```json
{
  "artifacts": [ /* EvaluationResult[] */ ],
  "aggregateScore": 85.5
}
```

**Implementation**: `RoomController.cs`.

#### Room Phase Transition (CollaborationController)

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/rooms/{roomId}/phase` | Transition room to a new phase |

**POST `/api/rooms/{roomId}/phase`** request:
```json
{
  "targetPhase": "implementation",
  "reason": "Design review complete, moving to implementation"
}
```
Response: `RoomSnapshot` with updated phase.

No phase state machine — any phase can transition to any other.

**Implementation**: `CollaborationController.cs`, delegates to `RoomService.TransitionPhaseAsync`.

#### Room Session Compaction (CollaborationController)

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/rooms/{roomId}/compact` | Reset agent Copilot sessions for a room |

**POST `/api/rooms/{roomId}/compact`** response:
```json
{
  "compactedSessions": 4,
  "totalAgents": 5,
  "note": "1 agent had no active session"
}
```

Forces all agents in the room to start fresh conversation sessions on their next turn.

**Implementation**: `CollaborationController.cs`.

## Interfaces & Contracts

### Service Registration

> **Source**: `src/AgentAcademy.Server/Services/ServiceRegistrationExtensions.cs`

Domain services are registered via `services.AddDomainServices()` (called from `Program.cs`). Task services use the **forwarding pattern** — the concrete class is registered first, then the interface resolves to the same instance:

```csharp
// Task services: concrete + forwarded interface
services.AddScoped<TaskQueryService>();
services.AddScoped<ITaskQueryService>(sp => sp.GetRequiredService<TaskQueryService>());
// ... same pattern for all 7 task service interfaces + room, message, breakout, and agent location services
```

All consumers (controllers, command handlers, other services) inject the interface. The concrete registration remains so that test DI setups that register the concrete type directly continue to work.

Full registration (abridged — see `ServiceRegistrationExtensions.cs` for current list):
```csharp
// Singletons
builder.Services.AddAgentCatalog();                      // AgentCatalogOptions
builder.Services.AddSingleton<ActivityBroadcaster>();     // event buffer
builder.Services.AddSingleton<WorktreeService>();         // worktree manager

// Scoped domain services (via AddDomainServices extension)
services.AddScoped<ActivityPublisher>();

// Task services — concrete + interface forwarding
services.AddScoped<TaskQueryService>();
services.AddScoped<ITaskQueryService>(sp => sp.GetRequiredService<TaskQueryService>());
services.AddScoped<TaskLifecycleService>();
services.AddScoped<ITaskLifecycleService>(sp => sp.GetRequiredService<TaskLifecycleService>());
services.AddScoped<TaskEvidenceService>();
services.AddScoped<ITaskEvidenceService>(sp => sp.GetRequiredService<TaskEvidenceService>());
services.AddScoped<TaskDependencyService>();
services.AddScoped<ITaskDependencyService>(sp => sp.GetRequiredService<TaskDependencyService>());
services.AddScoped<TaskItemService>();
services.AddScoped<ITaskItemService>(sp => sp.GetRequiredService<TaskItemService>());
services.AddScoped<TaskOrchestrationService>();
services.AddScoped<ITaskOrchestrationService>(sp => sp.GetRequiredService<TaskOrchestrationService>());
services.AddScoped<TaskAnalyticsService>();
services.AddScoped<ITaskAnalyticsService>(sp => sp.GetRequiredService<TaskAnalyticsService>());

// Other domain services — concrete + interface forwarding where extracted
services.AddScoped<MessageService>();
services.AddScoped<IMessageService>(sp => sp.GetRequiredService<MessageService>());
services.AddScoped<AgentLocationService>();
services.AddScoped<IAgentLocationService>(sp => sp.GetRequiredService<AgentLocationService>());
services.AddScoped<PlanService>();
services.AddScoped<CrashRecoveryService>();
services.AddScoped<ICrashRecoveryService>(sp => sp.GetRequiredService<CrashRecoveryService>());
services.AddScoped<InitializationService>();
services.AddScoped<BreakoutRoomService>();
services.AddScoped<IBreakoutRoomService>(sp => sp.GetRequiredService<BreakoutRoomService>());
services.AddScoped<RoomService>();
services.AddScoped<IRoomService>(sp => sp.GetRequiredService<RoomService>());
services.AddScoped<RoomSnapshotBuilder>();
services.AddScoped<IRoomSnapshotBuilder>(sp => sp.GetRequiredService<RoomSnapshotBuilder>());
services.AddScoped<WorkspaceRoomService>();
services.AddScoped<IWorkspaceRoomService>(sp => sp.GetRequiredService<WorkspaceRoomService>());
services.AddScoped<RoomLifecycleService>();
services.AddScoped<IRoomLifecycleService>(sp => sp.GetRequiredService<RoomLifecycleService>());
services.AddScoped<AgentConfigService>();
services.AddScoped<SystemSettingsService>();
services.AddScoped<ConversationSessionService>();
services.AddScoped<SprintService>();
services.AddScoped<SearchService>();
services.AddScoped<RoomArtifactTracker>();
services.AddScoped<ArtifactEvaluatorService>();
```

### Key Types
- `PostMessageRequest(RoomId, SenderId, Content, Kind?, CorrelationId?, Hint?)`
- `PhaseTransitionRequest(RoomId, TargetPhase, Reason?)`
- `WorktreeInfo(Branch, Path, CreatedAt)`
- All shared model types from `AgentAcademy.Shared.Models`

Each domain service depends on `AgentAcademyDbContext` (scoped) and the specific services it needs. There is no central facade — services are composed directly by their consumers.

## Invariants

1. Default room always exists after initialization
2. Every configured agent has an `AgentLocation` record after initialization
3. Message count per room never exceeds 200 (trimmed on each post)
4. Phase transitions always produce a Coordination message
5. Breakout room creation always moves agent to Working state
6. Breakout room closure always moves agent back to Idle
7. Activity events are both persisted to DB and buffered in-memory
8. Agent catalog is sorted by name (case-insensitive) at load time
9. Task room creation auto-joins all `AutoJoinDefaultRoom` agents (except those in Working state)
10. Rooms created while a workspace is active are stamped with that workspace's path
11. `GetRoomsAsync()` only returns rooms belonging to the active workspace
12. Plan writes reject unknown room identifiers instead of silently creating orphaned plan rows

## Known Gaps

- ~~No real-time push to external clients~~ — **Resolved**: SignalR hub exists (`/hubs/activity`) and `ActivityHubBroadcaster` forwards events to connected clients. SSE alternative also implemented (`GET /api/activity/stream`). Frontend connects via either transport.
- ~~No agent knowledge persistence (v1 had file-based knowledge storage)~~ — **resolved**: Agent memory system (spec 008) provides persistent key/value storage with categories, FTS5 search, shared cross-agent memories, import/export, and TTL-based decay. Replaces v1's file-based approach with a structured, queryable system.
- ~~No task item management (v1 had `createTaskItem`, `updateTaskStatus`, etc.)~~ — **resolved**: `CREATE_TASK_ITEM`, `UPDATE_TASK_ITEM`, and `LIST_TASK_ITEMS` commands expose task item management. Agents can create, update, and query task items with role-gating, entity validation, and agent catalog resolution.
- ~~Activity event in-memory buffer is per-instance, not shared across scoped instances~~ — **Accepted**: Single-server deployment makes this moot. Would need redesign alongside session persistence (#3) for multi-instance.
- ~~Legacy rooms (created before project-scoping) have `WorkspacePath = null` — they won't appear when a workspace is active~~ — **Resolved**: `GetRoomsAsync` now includes null-workspace rooms. `RetireLegacyDefaultRoomAsync` archives retired rooms to prevent reappearing.

## Revision History

- **2026-04-18**: Spec sync — `WorktreeService.SyncWithGitAsync()` is now wired into `InitializationService.InitializeAsync` (no longer "not currently wired"). Reflects audit fix #105 (post-restart worktree tracking reconciliation). | Anvil
- **2026-04-15**: Extracted `ICrashRecoveryService` interface contract. Updated service architecture text, service table, and DI registration snippet to show scoped + forwarded registration for `CrashRecoveryService`.
- **2026-04-15**: Extracted `IRoomSnapshotBuilder` interface contract. Updated service architecture text, service table, and DI registration snippet to show scoped + forwarded registration for `RoomSnapshotBuilder`.
- **2026-04-15**: Extracted `IWorkspaceRoomService` interface contract. Updated service architecture text, service table, and DI registration snippet to show scoped + forwarded registration for `WorkspaceRoomService`.
- **2026-04-15**: Extracted `IAgentLocationService` interface contract. Updated service table and DI registration for `IRoomService`, `IMessageService`, `IBreakoutRoomService`, `IAgentLocationService` — all now show forwarding pattern.
- **2026-04-15**: Documented task service interface contracts (`Services/Contracts/`). Service table now shows Interface column; DI registration updated to forwarding pattern. Added `TaskSnapshotFactory`, `TaskDependencyService`, `TaskAnalyticsService`, `RoomArtifactTracker`, `ArtifactEvaluatorService`.
- **2026-04-13**: Spec sync — documented `RoomSnapshotBuilder` (room snapshot assembly) and `WorkspaceRoomService` (workspace–room relationships) extracted from `RoomService`. Updated service table, source references, and DI registration. RoomService retains CRUD and phase transitions; snapshot building and workspace orchestration now in dedicated services.
- **2026-04-12**: `WorkspaceRuntime` facade deleted — spec rewritten as "Domain Services Layer". All controllers and command handlers now inject focused services directly. Behavioral documentation preserved; source references updated to actual service files. `GetOverviewAsync` inlined in `SystemController`. No behavioral changes to rooms, messages, tasks, agents, breakouts, plans, or activity publishing.
- **2026-04-11**: Spec reconciliation — updated to reflect full facade decomposition. Added `TaskOrchestrationService` (scoped) to services table; CreateTaskAsync, CompleteTaskAsync, RejectTaskAsync, PostTaskNoteAsync now delegate to it. Fixed `ActivityPublisher` registration from Singleton to Scoped. Separated `ActivityBroadcaster` (singleton in-memory buffer) from `ActivityPublisher` (scoped EF persistence + broadcast). Removed dead methods. Updated Dependencies and Service Registration.
- **2026-04-11**: Documented service extraction architecture — WorkspaceRuntime refactored from monolithic 1800+ line class to a thin facade (839 lines) delegating to 10 extracted services. Public API unchanged. Orchestration logic (CreateTask, CompleteTask, RejectTask, GetOverview) retained in WorkspaceRuntime; all other methods are one-liner delegations. Dead code removed after extraction.
- **2026-04-10**: Workspace isolation — documented `WorktreeService` for agent-level git worktree isolation. Covers worktree creation/removal, agent-specific worktrees, orchestrator integration, and database fields. Synced during stabilization.
- **2026-04-08**: Project scoping phase 1 — added `WorkspacePath` to `TaskEntity` and `ConversationSessionEntity`. Tasks and conversation sessions now have direct project association. `GetTasksAsync()` filters by `WorkspacePath` directly (with room fallback for pre-migration rows). `GetAllSessionsAsync` and `GetSessionStatsAsync` accept optional workspace filter. Migration includes data backfill from rooms table. API: `GET /api/sessions` and `GET /api/sessions/stats` accept `?workspace=` query parameter.
- **2026-04-07**: Documented `RecoverFromCrashAsync` crash recovery behavior — closes active breakouts, resets stuck agents to Idle, unassigns orphaned in-progress tasks, posts correlation-deduped recovery notification. Called by `AgentOrchestrator.HandleStartupRecoveryAsync` on crash detection.
- **2026-04-04**: Task item commands — `CREATE_TASK_ITEM`, `UPDATE_TASK_ITEM`, `LIST_TASK_ITEMS` commands added. Resolves the task item management known gap. Added `GetTaskItemAsync` and `GetTaskItemsAsync` to WorkspaceRuntime. `UpdateTaskItemStatusAsync` now throws on missing items (was silent no-op). Agent catalog validation on assignee, room existence validation on create.
- **2026-04-04**: Marked "No agent knowledge persistence" as resolved — memory system (spec 008) provides persistent storage with categories, FTS5, shared memories, import/export, and TTL decay. Clarified task item gap: internal methods exist but no agent commands expose them.
- **2026-04-04**: Stale room cleanup — auto-archive rooms when all tasks are terminal, `GetRoomsAsync` excludes archived by default, `CleanupStaleRoomsAsync` for bulk cleanup, `CLEANUP_ROOMS` command, `POST /api/rooms/cleanup` API, room reopening on task rejection
- **2026-03-29**: Project-scoped rooms — `WorkspacePath` FK on `RoomEntity`, `GetRoomsAsync` filters by active workspace, `EnsureDefaultRoomForWorkspaceAsync` creates per-project default room with agents, `CreateTaskAsync` stamps new rooms with active workspace path
- **2026-03-29**: Default room ordering — `GetRoomsAsync` now sorts the configured default room first, then remaining rooms alphabetically by name
- **Initial implementation**: Ported from v1 TypeScript `WorkspaceRuntime.ts` to C# with EF Core persistence
