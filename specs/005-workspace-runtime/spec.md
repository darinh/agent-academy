# 005 — Workspace Runtime

## Purpose

Documents the `WorkspaceRuntime` service — the central state manager for Agent Academy. It orchestrates rooms, agents, messages, tasks, breakout rooms, plans, and activity events.

## Current Behavior

**Status: Implemented**

`WorkspaceRuntime` is a scoped service (`AddScoped<WorkspaceRuntime>()`) that uses EF Core (`AgentAcademyDbContext`) for persistence and an in-memory buffer for recent activity events.

### Initialization

On startup, `Program.cs` calls `InitializeAsync()` which:
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

- `GetRoomsAsync(includeArchived)` → rooms for the active workspace, default room first then alphabetically. Archived rooms are excluded by default; pass `includeArchived: true` to include them.
- `GetRoomAsync(roomId)` → single room snapshot or null
- `RenameRoomAsync(roomId, newName)` → renames a room, publishes `RoomRenamed` activity event, cascades to Discord channel name via `OnRoomRenamedAsync`
- `CreateDefaultRoomAsync()` → creates default room if none exists (legacy, uses global `main` room)
- `EnsureDefaultRoomForWorkspaceAsync(workspacePath)` → creates a workspace-specific default room (named from `_catalog.DefaultRoomName`), moves all agents there. Excludes the catalog default room when checking for existing workspace rooms. Auto-corrects stale room names.
- `GetProjectNameForRoomAsync(roomId)` → resolves `roomId → WorkspacePath → ProjectName` (falls back to directory basename)
- `CleanupStaleRoomsAsync()` → scans for non-main rooms where all tasks are terminal (Completed/Cancelled), evacuates agents to default room, archives the rooms. Returns count of rooms cleaned up.

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

- `CreateTaskAsync(TaskAssignmentRequest)` → creates task, optionally creates new room
- `GetTasksAsync()` → all tasks ordered by creation date descending
- `GetTaskAsync(taskId)` → single task or null

Task creation:
- If `RoomId` is provided and room exists: updates existing room to Active/Planning
- If `RoomId` is null: creates new room with normalized title as ID, stamped with active workspace's `WorkspacePath`
- Adds system messages (TaskAssignment + Coordination)
- Publishes TaskCreated and PhaseChanged events
- Seeds `TaskSnapshot.CurrentPlan` from `TaskAssignmentRequest.CurrentPlan` when provided; otherwise uses the default planning checklist markdown
- **Auto-join**: When a new room is created, all agents with `AutoJoinDefaultRoom = true` are moved into the room via `MoveAgentAsync`. Agents currently in `Working` state are skipped to avoid disrupting in-flight breakout work. Failures are caught and logged per-agent (best-effort) so task creation always succeeds.

### Message Management

- `PostMessageAsync(PostMessageRequest)` → posts agent message
- `PostHumanMessageAsync(roomId, content)` → posts human message

Validation: room must exist, sender must be in catalog (for agent messages).

**Message trimming**: When message count exceeds 200, oldest messages are deleted from the database. This matches v1 behavior.

**Session-aware message loading**: `BuildRoomSnapshotAsync` loads only messages from the active conversation session (plus legacy untagged messages). Messages are tagged with `SessionId` when posted. See spec 003 → Conversation Session Management for epoch lifecycle details.

**Message tagging**: `PostMessageAsync`, `PostHumanMessageAsync`, and `PostBreakoutMessageAsync` call `ConversationSessionService.GetOrCreateActiveSessionAsync` to tag each message with the active session ID and increment the session's message count.

### Phase Management

- `TransitionPhaseAsync(roomId, targetPhase, reason?)` → updates room and active task phase

Behavior:
- No-op if already in target phase
- Sets room status to `Completed` if target phase is `FinalSynthesis`
- Adds a Coordination system message documenting the transition
- Updates active task's `CurrentPhase` if one exists

No phase state machine — any phase can transition to any other phase.

### Agent Location

- `GetAgentLocationsAsync()` → all agent locations
- `GetAgentLocationAsync(agentId)` → single location
- `MoveAgentAsync(agentId, roomId, state, breakoutRoomId?)` → updates location

Agent must be in catalog. Room existence is not validated (matches v1).

### Breakout Rooms

- `CreateBreakoutRoomAsync(parentRoomId, agentId, name)` → creates breakout, moves agent to Working
- `CloseBreakoutRoomAsync(breakoutId)` → moves agent to Idle, deletes breakout + messages
- `GetBreakoutRoomsAsync(parentRoomId)` → list for parent room

### Plan Management

- `GetPlanAsync(roomId)` → `PlanContent` or null
- `SetPlanAsync(roomId, content)` → create or update (upsert)
- `DeletePlanAsync(roomId)` → returns true if deleted

Plan records are keyed by the active room identifier and may target either a main collaboration room or a breakout room. `SetPlanAsync` validates that the target ID belongs to an existing room or breakout room before writing.

### Activity Publishing

- `PublishThinking(agent, roomId)` → AgentThinking event
- `PublishFinished(agent, roomId)` → AgentFinished event
- `GetRecentActivity()` → last 100 events from in-memory buffer
- `StreamActivity(callback)` → subscribe to events, returns unsubscribe action

Internal `Publish()` method:
1. Creates `ActivityEvent` record
2. Persists to `activity_events` table
3. Buffers in-memory (last 100)
4. Notifies all subscribers

## Interfaces & Contracts

### Service Registration (Program.cs)
```csharp
builder.Services.AddAgentCatalog();       // singleton AgentCatalogOptions
builder.Services.AddScoped<WorkspaceRuntime>(); // scoped service
```

### Key Types
- `PostMessageRequest(RoomId, SenderId, Content, Kind?, CorrelationId?, Hint?)`
- `PhaseTransitionRequest(RoomId, TargetPhase, Reason?)`
- All shared model types from `AgentAcademy.Shared.Models`

### Dependencies
- `AgentAcademyDbContext` (scoped)
- `ILogger<WorkspaceRuntime>`
- `AgentCatalogOptions` (singleton)

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

- No real-time push to external clients — SignalR hub exists (`/hubs/activity`) and `ActivityHubBroadcaster` forwards events to connected clients, but activity subscribers are also available in-process
- No agent knowledge persistence (v1 had file-based knowledge storage)
- No task item management (v1 had `createTaskItem`, `updateTaskStatus`, etc.)
- Activity event in-memory buffer is per-instance, not shared across scoped instances
- Legacy rooms (created before project-scoping) have `WorkspacePath = null` — they won't appear when a workspace is active

## Revision History

- **2026-04-04**: Stale room cleanup — auto-archive rooms when all tasks are terminal, `GetRoomsAsync` excludes archived by default, `CleanupStaleRoomsAsync` for bulk cleanup, `CLEANUP_ROOMS` command, `POST /api/rooms/cleanup` API, room reopening on task rejection
- **2026-03-29**: Project-scoped rooms — `WorkspacePath` FK on `RoomEntity`, `GetRoomsAsync` filters by active workspace, `EnsureDefaultRoomForWorkspaceAsync` creates per-project default room with agents, `CreateTaskAsync` stamps new rooms with active workspace path
- **2026-03-29**: Default room ordering — `GetRoomsAsync` now sorts the configured default room first, then remaining rooms alphabetically by name
- **Initial implementation**: Ported from v1 TypeScript `WorkspaceRuntime.ts` to C# with EF Core persistence
