# 005 — Domain Services Layer

## Purpose

Documents the domain services that manage core workspace state — rooms, agents, messages, tasks, breakout rooms, plans, and activity events. Controllers, command handlers, and the orchestrator inject these services directly.

## Current Behavior

**Status: Implemented**

> **History**: This spec originally documented `WorkspaceRuntime`, a scoped facade that delegated to domain services. As of 2026-04-12, the facade has been deleted — all consumers now inject the focused domain services directly. The behavioral documentation below remains accurate; only the indirection layer was removed.

### Service Architecture

Controllers and command handlers inject focused domain services directly via constructor injection. There is no facade or intermediary layer.

| Service | Responsibility | Registration | Source |
|---------|---------------|--------------|--------|
| `InitializationService` | Startup room/agent seeding, server instance tracking | Scoped | `Services/InitializationService.cs` |
| `CrashRecoveryService` | Crash detection, breakout/agent/task recovery | Scoped | `Services/CrashRecoveryService.cs` |
| `RoomService` | Room CRUD, phase transitions, workspace scoping | Scoped | `Services/RoomService.cs` |
| `RoomSnapshotBuilder` | Builds read-model snapshots of rooms (messages, task, participants) | Scoped | `Services/RoomSnapshotBuilder.cs` |
| `WorkspaceRoomService` | Workspace–room relationships, default room creation, startup resolution | Scoped | `Services/WorkspaceRoomService.cs` |
| `RoomLifecycleService` | Room close/reopen/auto-archive/cleanup, agent evacuation | Scoped | `Services/RoomLifecycleService.cs` |
| `MessageService` | Room/DM/breakout messaging, message trimming | Scoped | `Services/MessageService.cs` |
| `TaskQueryService` | Task queries, assignment, status updates, evidence/spec-link reads | Scoped | `Services/TaskQueryService.cs` |
| `TaskLifecycleService` | Task creation staging, claim/release/approve/reject | Scoped | `Services/TaskLifecycleService.cs` |
| `TaskEvidenceService` | Evidence recording and gate checks | Scoped | `Services/TaskEvidenceService.cs` |
| `BreakoutRoomService` | Breakout room lifecycle, task association, stuck reopening | Scoped | `Services/BreakoutRoomService.cs` |
| `TaskItemService` | Task item CRUD | Scoped | `Services/TaskItemService.cs` |
| `AgentLocationService` | Agent location tracking and movement | Scoped | `Services/AgentLocationService.cs` |
| `PlanService` | Plan CRUD with room/breakout validation | Scoped | `Services/PlanService.cs` |
| `TaskOrchestrationService` | Task creation/completion/rejection coordinating rooms, agents, and lifecycle | Scoped | `Services/TaskOrchestrationService.cs` |
| `ActivityPublisher` | Event creation, EF persistence, broadcast via `ActivityBroadcaster` | Scoped | `Services/ActivityPublisher.cs` |
| `ActivityBroadcaster` | In-memory event buffer (last 100) and subscriber notification | Singleton | `Services/ActivityBroadcaster.cs` |

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
- Participants (built from `AgentLocationEntity` records — agents whose current location matches the room, with preferred-role flag from the active task)
- Recent messages (last 200 from DB)
- Active task (most recent active task for the room)

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

### Agent Location

> **Source**: `src/AgentAcademy.Server/Services/AgentLocationService.cs`

- `AgentLocationService.GetAgentLocationsAsync()` → all agent locations
- `AgentLocationService.GetAgentLocationAsync(agentId)` → single location
- `AgentLocationService.MoveAgentAsync(agentId, roomId, state, breakoutRoomId?)` → updates location

Agent must be in catalog. Room existence is not validated (matches v1).

### Breakout Rooms

> **Source**: `src/AgentAcademy.Server/Services/BreakoutRoomService.cs`

- `BreakoutRoomService.CreateBreakoutRoomAsync(parentRoomId, agentId, name)` → creates breakout, moves agent to Working
- `BreakoutRoomService.CloseBreakoutRoomAsync(breakoutId)` → moves agent to Idle, deletes breakout + messages
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

Callers that need to publish events (e.g., `TaskOrchestrationService`, `AgentOrchestrator`) inject `ActivityPublisher` directly.

### Crash Recovery

> **Source**: `src/AgentAcademy.Server/Services/CrashRecoveryService.cs`

On startup, `AgentOrchestrator.HandleStartupRecoveryAsync` checks `CrashRecoveryService.CurrentCrashDetected` (set by `RecordServerInstanceAsync` when the previous instance had no clean shutdown). If a crash is detected, `CrashRecoveryService.RecoverFromCrashAsync(mainRoomId)` runs the following recovery steps in order:

1. **Close all active breakout rooms** — queries for non-terminal breakout rooms and calls `CloseBreakoutRoomAsync` with `BreakoutRoomCloseReason.ClosedByRecovery`
2. **Reset stuck agents** — finds agents in `Working` state whose `BreakoutRoomId` is null or doesn't match an active breakout, moves them to `Idle` via `MoveAgentAsync`
3. **Reset orphaned tasks** — finds tasks with in-progress status (`Active`, `AwaitingValidation`, `InReview`) whose assignee agent is no longer in an active breakout, clears their `AssignedAgentId` and `AssignedAgentName`
4. **Post recovery notification** — if any recovery actions occurred, posts a system message to the main room with counts (e.g., "Closed 2 breakout room(s), reset 1 stuck agent(s), and reset 1 stuck task(s)"). Uses `CurrentInstanceId` as a correlation ID to prevent duplicate notifications on multiple startup calls.

**Return type**: `CrashRecoveryResult(ClosedBreakoutRooms, ResetWorkingAgents, ResetTasks)` — a sealed record with the counts from each recovery step.

**Idempotency**: Recovery is safe to call multiple times. The correlation-based dedup prevents duplicate system messages. Agents and tasks that are already in a clean state are not affected.

**Note**: Recovery does not re-enqueue pending human messages. That is handled separately by `AgentOrchestrator.ReconstructQueueAsync` which calls `GetRoomsWithPendingHumanMessagesAsync`.

### Workspace Isolation — Git Worktrees

Each agent gets its own filesystem checkout via `git worktree` to enable concurrent development without conflicts.

#### WorktreeService (`Services/WorktreeService.cs`)

Singleton service managing git worktrees for agent-level workspace isolation.

**Core API:**
- `CreateWorktreeAsync(branch)` — creates a linked worktree at `{repoRoot}/.worktrees/{branch}`
- `RemoveWorktreeAsync(branch)` — removes the worktree and prunes git metadata
- `GetWorktreePath(branch)` — returns the filesystem path for a branch's worktree, or null
- `GetActiveWorktrees()` — returns all tracked worktree entries
- `ListGitWorktreesAsync()` — parses `git worktree list --porcelain` for ground truth
- `CleanupAllWorktreesAsync()` — removes all managed worktrees (used on shutdown/recovery)
- `SyncWithGitAsync()` — reconciles internal tracking with actual git worktree state

**Agent worktree management:**
- `EnsureAgentWorktreeAsync(workspacePath, projectName, agentId, branch)` — creates or reuses an agent-specific worktree. Naming convention: `.worktrees/{projectName}-{agentId}`
- `GetAgentWorktreePath(projectName, agentId, workspacePath?)` — resolves an agent's worktree path
- `RemoveAgentWorktreeAsync(workspacePath, projectName, agentId)` — removes an agent's worktree

**Key types:**
- `WorktreeInfo(Branch, Path, CreatedAt)` — tracked worktree metadata
- `GitWorktreeEntry(Path, Head, Branch, Bare)` — parsed git worktree state

**Invariants:**
- Worktrees are stored under `{repoRoot}/.worktrees/` (gitignored)
- Each agent gets at most one worktree per project
- `SyncWithGitAsync()` is called on startup to reconcile stale state
- Worktree cleanup is idempotent and handles already-removed paths gracefully

#### Orchestrator Integration

The `AgentOrchestrator` uses `WorktreeService` to provide each agent with an isolated working directory:
- When an agent starts a task, `EnsureAgentWorktreeAsync()` provisions a worktree
- The agent's `CommandContext.WorkingDirectory` is set to the worktree path
- All git and file operations (build, test, diff, commit) execute within the worktree
- On task completion/cancellation, the worktree persists for merge operations

#### Database Fields

- `TaskEntity.WorkspacePath` — associates tasks with a project directory
- `ConversationSessionEntity.WorkspacePath` — associates sessions with a project directory

## Interfaces & Contracts

### Service Registration (Program.cs)
```csharp
// Singletons
builder.Services.AddAgentCatalog();                      // AgentCatalogOptions
builder.Services.AddSingleton<ActivityBroadcaster>();     // event buffer
builder.Services.AddSingleton<WorktreeService>();         // worktree manager

// Scoped domain services
builder.Services.AddScoped<ActivityPublisher>();
builder.Services.AddScoped<TaskQueryService>();
builder.Services.AddScoped<TaskLifecycleService>();
builder.Services.AddScoped<TaskEvidenceService>();
builder.Services.AddScoped<MessageService>();
builder.Services.AddScoped<AgentLocationService>();
builder.Services.AddScoped<PlanService>();
builder.Services.AddScoped<CrashRecoveryService>();
builder.Services.AddScoped<InitializationService>();
builder.Services.AddScoped<BreakoutRoomService>();
builder.Services.AddScoped<TaskItemService>();
builder.Services.AddScoped<RoomService>();
builder.Services.AddScoped<RoomSnapshotBuilder>();
builder.Services.AddScoped<WorkspaceRoomService>();
builder.Services.AddScoped<RoomLifecycleService>();
builder.Services.AddScoped<TaskOrchestrationService>();
builder.Services.AddScoped<AgentConfigService>();
builder.Services.AddScoped<SystemSettingsService>();
builder.Services.AddScoped<ConversationSessionService>();
builder.Services.AddScoped<SprintService>();
builder.Services.AddScoped<SearchService>();
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
