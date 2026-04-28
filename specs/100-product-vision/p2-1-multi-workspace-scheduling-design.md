# P2.1 — Multi-Workspace Orchestrator Scheduling — Design Doc

**Status**: PROPOSED — design only; no code change in this PR. Implementation follows in a separate PR after review.
**Roadmap entry**: P2.1 (`specs/100-product-vision/roadmap.md:147–153`); first item in Phase 2.
**Closes gap**: G5 — Cross-Project Autonomy Missing (`specs/100-product-vision/gap-analysis.md:57–62`).
**Risk**: 🔴 (concurrency, fairness, multi-workspace state isolation; touches the per-round dispatch path AND every service that today implicitly resolves "the active workspace" as global state).
**Author**: anvil (operator: agent-academy), 2026-04-28.
**Phase 2 acceptance test** (from `spec.md` §7 / `roadmap.md:144–145`):
> Two projects exist. The human loads Project A in the UI. A sprint is active in Project B. Without any UI interaction with Project B, the sprint in Project B advances and reaches a checkpoint observable on inspection.

This doc is the design preamble the roadmap entry asks for ("investigation needed"). Read this first; do not start coding the scheduler refactor, the workspace-context plumbing, or the auto-archive predicate change until it is approved or amended.

---

## 1. Problem statement

### 1.1 What the vision says

`spec.md` §7 ("Cross-Project Background Work"):
> The team works on multiple projects even if only one is loaded in the UI at any given time. The orchestrator and scheduler are project-aware and process work for any workspace whose schedule allows it. The UI shows the project the human is currently inspecting; the agents work on whatever has work to do.

### 1.2 What an investigation of the current code shows

The roadmap brief says the orchestrator is "per-workspace" but the scheduler "focuses on whichever workspace is loaded." That framing is **slightly wrong in both directions**, and the real picture is more nuanced. Four concrete subsystems matter:

| Subsystem | Workspace scope today | Cross-workspace today? | File / line |
|---|---|---|---|
| **Sprint schedule cron evaluator** (`SprintSchedulerService.EvaluateSchedulesAsync`) | Iterates `db.SprintSchedules` with no `IsActive` filter — every enabled, due schedule gets a sprint created | ✅ **Already cross-workspace** | `Services/SprintSchedulerService.cs:74–82` |
| **Sprint timeout sweeper** (`SprintTimeoutService.CheckOnceAsync`) | Calls `GetTimedOutSignOffSprintsAsync` and `GetOverdueSprintsAsync` which scan all sprints | ✅ **Already cross-workspace** | `Services/SprintTimeoutService.cs:64–134`; predicates in `SprintService` |
| **Orchestrator queue** (`AgentOrchestrator`) | Single global `Queue<QueueItem>` keyed by `RoomId`. Singleton. Rooms from any workspace can sit in the queue together | ✅ Cross-workspace at the *queue* layer; ⚠️ **serialised globally** (one room runs at a time, regardless of workspace) | `Services/AgentOrchestrator.cs:13–413` |
| **Per-round dispatch path** (`ConversationRoundRunner`, `DirectMessageRouter`, `OrchestratorWakeService`) | Resolves workspace from the **room** via `roomService.GetWorkspacePathForRoomAsync(roomId)` — not from "the active workspace" | ✅ Per-round resolution is correctly room-scoped | `Services/ConversationRoundRunner.cs:119, 154`; `Services/DirectMessageRouter.cs:102`; `Services/OrchestratorWakeService.cs:38–76` |
| **Sprint terminal-stage handler** (`SprintTerminalStageHandler`) | Loads sprint by id; calls `OrchestratorWakeService.WakeWorkspaceRoomsForSprintAsync(sprintId)` which uses the sprint's `WorkspacePath` | ✅ **Already cross-workspace** (PR #192, sprint #17) | `Services/SprintTerminalStageHandler.cs:60–108`; `Services/OrchestratorWakeService.cs:38–76` |

So the **structural bones are already multi-workspace**. The schedulers iterate all workspaces; the dispatch path resolves workspace per room; the terminal-stage driver wakes the right rooms.

The actual breakage is in **shared services that resolve "the active workspace" as a global side-channel** instead of receiving workspace context from their caller. These services are called from the per-round dispatch path. Result: while a sprint in Project B is running, an agent round resolves Project A's tasks/breakouts/main-room rather than Project B's, because the agent looked through the active-workspace lens.

### 1.3 The six concrete bugs that prevent G5 (five in scope; one deferred)

Discovered by tracing every `IsActive`/`GetActiveWorkspacePathAsync()` call from the per-round dispatch surface (round runner, DM router, terminal-stage driver) into shared services. Each is a real cross-workspace contamination, not a hypothetical:

#### Bug 1 — `taskItemService.GetActiveTaskItemsAsync()` filters to the active workspace

`Services/ConversationRoundRunner.cs:187` calls `taskItemService.GetActiveTaskItemsAsync()` to build the planner's task-list context every round. That method (`Services/TaskItemService.cs:84–102`) scopes by `GetActiveWorkspacePathAsync()`, which queries `Workspaces.Where(w => w.IsActive)`. **Effect**: when a round runs in Project B's room, the planner's prompt contains Project A's open tasks (or empty if Project A is null and B is not active). The planner cannot see what work is in flight in its own workspace.

#### Bug 2 — `RoomLifecycleService.IsMainCollaborationRoomAsync` returns false for non-active workspaces

`Services/RoomLifecycleService.cs:78–94`:
```csharp
var activeWorkspace = await GetActiveWorkspacePathAsync();
if (activeWorkspace is null || room.WorkspacePath != activeWorkspace)
    return false;
```
Used by:
- `TryAutoArchiveRoomAsync` (`RoomLifecycleService.cs:154`) — the predicate that decides "this is a main room, do not auto-archive."
- `CleanupStaleRoomsDetailedAsync` (`RoomLifecycleService.cs:214`) — the bulk-cleanup loop.
- `CloseRoomAsync` (`RoomLifecycleService.cs:104`) — guard against closing the main room.
- `CloseRoomHandler` (`Commands/Handlers/CloseRoomHandler.cs:55`).

**Effect**: Project B's main room is, from the perspective of the active workspace (A), not a main room. If a cleanup pass fires while Project A is loaded, **Project B's main collaboration room can be auto-archived**, freezing every running sprint in Project B. There is no test today guarding against this; manual testing has not exercised the two-workspace case.

#### Bug 3 — `TaskOrchestrationService.CreateTaskAsync` stamps tasks with the active workspace

`Services/TaskOrchestrationService.cs:88–104` resolves `IsActive` workspace and writes that as the new room's `WorkspacePath`. The contaminated path is the **agent SDK `create_task` tool**: `TaskWriteToolWrapper.CreateTaskAsync` (`Services/TaskWriteToolWrapper.cs:42–105`) builds a `TaskAssignmentRequest` with `RoomId: null` and calls `taskOrchestration.CreateTaskAsync(request)` (`TaskWriteToolWrapper.cs:82–92`). The wrapper runs inside an agent round, so when an agent in Project B's room invokes `create_task`, control reaches `TaskOrchestrationService.cs:88` which reads `IsActive` and stamps the new room with **Project A's** workspace path, not B's. Subsequent workspace-scoped queries in that room return wrong data, and the task vanishes from Project B's sprint scope.

**Note on what is NOT broken here**: `TaskAssignmentHandler` (the parser for `TASK ASSIGNMENT:` agent text-blocks) already does the right thing — it resolves workspace from the source room (`Services/TaskAssignmentHandler.cs:155`: `var workspacePath = await roomService.GetWorkspacePathForRoomAsync(roomId);`) with an inline comment specifically calling out that `GetActiveWorkspacePathAsync()` would be wrong here. That handler creates tasks via `TaskItemService.CreateTaskItemAsync` + `BreakoutRoomService.EnsureTaskForBreakoutAsync` (`TaskAssignmentHandler.cs:174–180`), passing `roomId` explicitly — no `IsActive` call in that path. The bug is **limited to the SDK tool wrapper path** which lacks roomId in its current API.

#### Bug 4 — `TaskQueryService.GetTasksAsync()` filters by the active workspace, contaminating per-round agent commands

`Services/TaskQueryService.cs:37–47` (the no-arg `GetTasksAsync` overload) calls `GetActiveWorkspacePathAsync()` (`TaskQueryService.cs:596–602`) and scopes tasks by `IsActive`. Two per-round consumers hit this:

- `Commands/Handlers/ListTasksHandler.cs:18–20` — implements the `LIST_TASKS` text-command agents fire to discover work in their workspace.
- `Services/AgentToolFunctions.cs:117–120` — implements the SDK `list_tasks` tool agents call from inside their reasoning loop.

Both run inside `AgentTurnRunner` (`Services/AgentTurnRunner.cs:126–198`). **Effect**: an agent in Project B's room runs `LIST_TASKS` and receives Project A's task list (or empty). This is the bug Sprint #17 main-room operators surfaced as `LIST_TASKS SprintId/Cancelled gap` — the task-discovery surface is workspace-incorrect even before sprint/cancellation filtering. **This must be fixed in P2.1**; without it, an agent in a backgrounded workspace cannot see its own tasks.

#### Bug 5 — `ConversationKickoffService.TryKickoffAsync` accepts an `activeWorkspace` parameter from a global source

`Services/ConversationKickoffService.cs:36–58` posts a workspace-naming kickoff message ("Workspace ready: {activeWorkspace}…"). It expects the caller to pass the right workspace. In production today, the caller (`InitializationService` at startup) passes the active workspace. **Effect**: if the kickoff path were ever invoked for a room whose workspace differs from the loaded one, it would post the wrong workspace name. Low-impact today (single call site; only fires at startup) but a future trap once schedulers/timeouts can also kick off conversations.

#### Bug 6 — Global queue serialisation produces priority inversion across workspaces

`AgentOrchestrator._queue` is a single `Queue<QueueItem>` with `_processing` boolean. `ProcessQueueAsync` (`Services/AgentOrchestrator.cs:141–160`) drains items strictly serially. Cross-workspace consequence: if Project B has a 5-minute agent round in flight, a fresh human message in Project A waits behind it. Per `spec.md` §7 ("the agents work on whatever has work to do"), and the acceptance test ("without any UI interaction with Project B, the sprint in Project B advances"), this serialisation is **acceptable for correctness** — Project B's sprint *will* advance even if Project A is loaded. But it produces a UX regression: a human typing into the loaded workspace is blocked behind background work in another workspace. **For Phase 2, we accept this serialisation** and surface it via P2.2 (UI Indicator). Per-workspace concurrency is an explicit non-goal of P2.1 (see §8).

---

## 2. Design principles (informed by what's already in the codebase)

These are reuse opportunities surfaced by the survey. Most of the design below is "stop using the global active-workspace channel; thread workspace context from the caller." Deliberately little new mechanism is introduced.

1. **The room is the source of truth for workspace context, not `IsActive`.** Every sprint, task, and message has a room; every room (with one legacy exception, the orphaned default room) has a `WorkspacePath`. The dispatch path already resolves workspace from the room (`ConversationRoundRunner.cs:154`). The fix is to **propagate that resolved workspace** into the shared services that today reach for `IsActive` instead.
2. **Don't introduce an `IWorkspaceContext` ambient/AsyncLocal.** Tempting, but: (a) it hides data flow and makes tests fragile; (b) `AgentOrchestrator` is a singleton and its `ProcessQueueAsync` runs continuations on thread-pool threads — AsyncLocal correctness across the queue->dispatch boundary is non-obvious; (c) every service that needs workspace already takes a `roomId` parameter or a `sprint`/`task` entity from which workspace is one DB hop away. Pass workspace explicitly.
3. **Reuse `roomService.GetWorkspacePathForRoomAsync(roomId)` as the single resolution primitive.** Already exists, already used by the dispatch path. Adding new resolution methods invites drift; one method, one source of truth.
4. **Don't broaden the orchestrator queue this PR.** Per §1.3 Bug 6 above and §8 below, per-workspace concurrency is **out of scope** for P2.1. Phase 2 closes by making sprints **advance** across workspaces; whether they advance **in parallel** is a Phase 3 concern. Splitting the queue is a separate, larger design change with new fairness, ordering, and back-pressure questions.
5. **Active workspace remains a UI affordance, not an orchestration input.** `WorkspacesEntity.IsActive` continues to exist and continues to mean "the workspace the UI is showing." It is a presentation hint for controllers and the Discord channel manager. **It must not be consulted by any service whose caller is the per-round dispatch path or any background hosted service.**
6. **Per-step atomicity, not whole-chain atomicity.** Each bug-fix is a tight, surgical change. Each fix lands with tests that prove the cross-workspace case (two workspaces, one loaded, the other has the work). Anvil's evidence cascade applies per fix; the implementation PR contains all five in-scope fixes (Bugs 1–5) but each is independently revertable. Bug 6 (queue concurrency) is out-of-scope per §8.
7. **No new entity columns.** `WorkspaceEntity.IsActive` stays a singleton-bool. We do not introduce a "background-active" flag; "is there work to do" is derived from sprints/queue state, not stored.
8. **Fail-loud on workspace-context absence in the dispatch path.** Today, when `roomService.GetWorkspacePathForRoomAsync(roomId)` returns null (legacy orphaned default room), services silently fall back to active workspace. Post-fix: services on the **per-round dispatch path** (round runner, agent commands, agent SDK tools, terminal-stage driver) accept an explicit `string? workspacePath` argument and treat null as "no workspace context" (which means: don't filter, or skip the workspace-coupled action — log a warning the first time per session). They MUST NOT fall back to `IsActive`.

   **Exception, narrowly scoped**: the `TaskOrchestrationService.CreateTaskAsync` write path (Bug 3 fix in §4.3) keeps an `IsActive` fallback **for the controller / UI entry point only** — when a human creates a task via the API with no source room, the only sensible workspace is "the loaded one." That fallback path is gated behind the explicit `sourceWorkspacePath = null` caller contract, which is set only by `TaskController` and never by `TaskWriteToolWrapper` (the per-round caller). The dispatch-path principle and the controller-fallback rule do not collide because they apply to disjoint caller sets — see §4.3 for the gating.

---

## 3. State model — the workspace's view of an agent round

Every queue item that the orchestrator dispatches has, at the moment of dispatch, a single answer to the question "what workspace does this work belong to?" The matrix below classifies the dispatch surfaces and their workspace resolution rule. Today, only the rows marked ✅ resolve correctly; ❌ rows fall back to global `IsActive`.

| Dispatch surface | Workspace resolved from | Today | Post-P2.1 |
|---|---|---|---|
| Human message in room (`AgentOrchestrator.HandleHumanMessage(roomId)`) | `roomService.GetWorkspacePathForRoomAsync(roomId)` in `ConversationRoundRunner` | ✅ | ✅ (unchanged) |
| Self-drive continuation (`TryEnqueueSystemContinuation(roomId, sprintId)`) | Same as above (resolved per-round) | ✅ | ✅ (unchanged) |
| DM trigger (`HandleDirectMessage(agentId)`) | `DirectMessageRouter.RouteAsync` → `roomService.GetWorkspacePathForRoomAsync(roomId)` | ✅ | ✅ (unchanged) |
| Terminal-stage driver wake (`OrchestratorWakeService.WakeWorkspaceRoomsForSprintAsync`) | `sprint.WorkspacePath` | ✅ | ✅ (unchanged) |
| Planner's "open tasks" context (`taskItemService.GetActiveTaskItemsAsync`) | Today: `IsActive` workspace; **Post-fix**: caller passes resolved per-round workspace | ❌ | ✅ (Bug 1) |
| Auto-archive guard (`IsMainCollaborationRoomAsync`) | Today: room.WorkspacePath compared against `IsActive`; **Post-fix**: derive purely from room state | ❌ | ✅ (Bug 2) |
| Agent SDK `create_task` tool (`TaskWriteToolWrapper.CreateTaskAsync` → `TaskOrchestrationService.CreateTaskAsync` with null roomId) | Today: `IsActive`; **Post-fix**: tool wrapper resolves the agent's current room → workspace and passes it as `sourceWorkspacePath` | ❌ | ✅ (Bug 3) |
| Agent `LIST_TASKS` command + `list_tasks` tool (`ListTasksHandler`, `AgentToolFunctions` → `TaskQueryService.GetTasksAsync()`) | Today: `IsActive`; **Post-fix**: per-round caller resolves room workspace and passes it through a new overload | ❌ | ✅ (Bug 4) |
| Kickoff message workspace name (`ConversationKickoffService.TryKickoffAsync`) | Today: caller; **Post-fix**: caller is required to pass the room's workspace, not the global active one | ❌ (latent) | ✅ (Bug 5) |
| Sprint scheduler (`SprintSchedulerService`) | `schedule.WorkspacePath` per row | ✅ | ✅ (unchanged) |
| Sprint timeout sweeper (`SprintTimeoutService`) | Per-sprint, no workspace coupling | ✅ | ✅ (unchanged) |

Note that no row mentions splitting the singleton orchestrator queue. The queue is global; that is a Phase 3 question (see §8).

---

## 4. Implementation strategy

Five surgical changes (Bugs 1–5). Each fix is independently testable and independently revertable. The implementation PR contains all five with one commit per fix so reviewers can see the per-bug blast radius. Bug 6 (queue serialisation) is documented but not fixed — see §8.

### 4.1 Bug 1 fix — Planner sees the room's tasks, not the global active workspace's tasks

**Add** an overload `GetActiveTaskItemsAsync(string? workspacePath, CancellationToken ct = default)` to `ITaskItemService` and `TaskItemService`. The new overload:
```csharp
public async Task<List<TaskItem>> GetActiveTaskItemsAsync(string? workspacePath, CancellationToken ct = default)
{
    var activeStatuses = new[] { nameof(TaskItemStatus.Pending), nameof(TaskItemStatus.Active) };
    var query = _db.TaskItems.Where(t => activeStatuses.Contains(t.Status));

    if (workspacePath is not null)
    {
        var workspaceRoomIds = await _db.Rooms
            .Where(r => r.WorkspacePath == workspacePath)
            .Select(r => r.Id)
            .ToListAsync(ct);
        query = query.Where(t => workspaceRoomIds.Contains(t.RoomId));
    }
    // workspacePath null → unscoped; round-runner ALWAYS passes a non-null value
    // because it has already resolved roomWorkspacePath at line 154. Null means
    // the caller is a controller / UI lookup that hasn't (and shouldn't) decided
    // a workspace lens.

    var entities = await query.OrderBy(t => t.CreatedAt).ToListAsync(ct);
    return entities.Select(BuildTaskItem).ToList();
}
```
**Mark the no-arg overload `[Obsolete("Pass the resolved workspace path explicitly. Calling this from a per-round path causes cross-workspace contamination — see G5 / P2.1.")]`** but keep it functional (it still does `IsActive` lookup) so we don't break controllers that legitimately want "what's open in the loaded workspace right now" (e.g., a UI endpoint).

**Update** `ConversationRoundRunner.cs:187`:
```csharp
var taskItems = await taskItemService.GetActiveTaskItemsAsync(roomWorkspacePath, cancellationToken);
```
`roomWorkspacePath` is already resolved at `ConversationRoundRunner.cs:154`; we just need to pass it.

**Note**: `TaskItemService.GetTaskItemsAsync(roomId, status)` (`TaskItemService.cs:116–142`) has the same `IsActive` pattern (line 129) but its callers are not on the per-round path — its only caller chain is via UI/controller endpoints that legitimately want the loaded workspace's task list. Leave it alone (the audit pass in §4.6 confirms the verdict). If a future per-round caller appears, add an overload then.

### 4.2 Bug 2 fix — `IsMainCollaborationRoomAsync` becomes workspace-pure

**Replace** `RoomLifecycleService.IsMainCollaborationRoomAsync` body (`RoomLifecycleService.cs:78–94`):

```csharp
public async Task<bool> IsMainCollaborationRoomAsync(string roomId)
{
    var room = await _db.Rooms.FindAsync(roomId);
    if (room is null) return false;

    // Legacy orphan default room — always a main room.
    if (room.Id == _catalog.DefaultRoomId) return true;

    // The room must belong to *some* workspace, and the room's name must
    // match the main-room shape. We do NOT compare against `IsActive`:
    // every workspace's main room is a main room, regardless of which one
    // the UI is currently showing.
    if (string.IsNullOrEmpty(room.WorkspacePath)) return false;

    return room.Name == _catalog.DefaultRoomName
        || room.Name.EndsWith("Main Room", StringComparison.Ordinal)
        || room.Name.EndsWith("Collaboration Room", StringComparison.Ordinal);
}
```

**Test additions** (in `RoomLifecycleServiceTests` — file may need to be created if it doesn't exist; check `tests/AgentAcademy.Server.Tests/`):
- `IsMainCollaborationRoom_ReturnsTrue_ForBackgroundWorkspaceMainRoom`: two workspaces, only A is active, query B's main room → expect `true`. Today this returns `false` (regression test).
- `TryAutoArchive_DoesNotArchive_BackgroundWorkspaceMainRoom`: two workspaces, only A is active, all of B's tasks terminal, run `TryAutoArchiveRoomAsync(B's main room)` → expect room status unchanged.
- `CleanupStaleRooms_SkipsBackgroundWorkspaceMainRoom`: same setup, run `CleanupStaleRoomsDetailedAsync()` → expect B's main room in the `Skips` list with `MainRoom` reason.

### 4.3 Bug 3 fix — Agent SDK `create_task` resolves workspace from the calling agent's room

**Change** `TaskOrchestrationService.CreateTaskAsync` (`Services/TaskOrchestrationService.cs:66`) signature to accept an explicit workspace path:

```csharp
public async Task<TaskAssignmentResult> CreateTaskAsync(
    TaskAssignmentRequest request,
    string? sourceWorkspacePath = null)
```

When `request.RoomId` is empty (new room case, `TaskOrchestrationService.cs:86–105`):
- If `sourceWorkspacePath` is non-null, use it directly as the new room's `WorkspacePath` — **no `IsActive` lookup**.
- If `sourceWorkspacePath` is null, fall back to `IsActive` — preserves controller-driven behaviour where a human-initiated task via the API has no source room. Per §2 principle 8, this fallback is **gated to the controller / UI caller set only**; per-round callers MUST pass a non-null value.

**Update callers**:
- `TaskWriteToolWrapper.CreateTaskAsync` (`Services/TaskWriteToolWrapper.cs:42–105`) — the agent SDK `create_task` tool. The wrapper has `_agentId` and a scoped `IServiceProvider`. Resolve the agent's current room via `agentLocationService.GetAgentLocationAsync(_agentId)`, then resolve workspace via `roomService.GetWorkspacePathForRoomAsync(location.RoomId)`. Pass the resolved value as `sourceWorkspacePath`. **If either resolution returns null, the wrapper returns an error string** (`"Error: cannot resolve workspace context for agent {id}; create_task requires the agent to be in a workspace-scoped room"`) — does NOT fall back to `IsActive`. This honours §2 principle 8 for the per-round path.
- `TaskController` (the human/API path) — pass null (controller correctly operates in the active-workspace context per §2 principle 5). The fallback inside `CreateTaskAsync` handles it.
- `TaskAssignmentHandler` (the `TASK ASSIGNMENT:` parsed-text path) — **does NOT call `TaskOrchestrationService.CreateTaskAsync`** in current code (`TaskAssignmentHandler.cs:174–180` uses `TaskItemService.CreateTaskItemAsync` + `BreakoutRoomService.EnsureTaskForBreakoutAsync` directly, and already resolves workspace from the source room at `TaskAssignmentHandler.cs:155`). No change required — left unbroken.

**Test addition** (in `TaskOrchestrationServiceTests` or `TaskWriteToolWrapperTests`):
- `AgentSdkCreateTask_StampsNewRoomWithAgentRoomWorkspace_NotActiveWorkspace`: two workspaces, only A is active, agent is in B's main room, agent invokes `create_task` SDK tool → new room's `WorkspacePath` should be B's, not A's. Today this fails (regression test).
- `AgentSdkCreateTask_FailsCleanly_WhenAgentRoomHasNoWorkspace`: agent in legacy orphan room invokes `create_task` → returns clear error string, does NOT silently use `IsActive`.

### 4.4 Bug 4 fix — `LIST_TASKS` / `list_tasks` see the room's workspace, not the global active workspace's

**Add** an overload `GetTasksAsync(string? workspacePath, CancellationToken ct = default)` to `ITaskQueryService` and `TaskQueryService` mirroring the §4.1 pattern:

```csharp
public async Task<List<TaskSnapshot>> GetTasksAsync(string? workspacePath, CancellationToken ct = default)
{
    // … same shape: filter by workspace-scoped roomIds when non-null,
    // unscoped when null. No IsActive lookup in the new overload.
}
```
**Mark the no-arg overload `[Obsolete(...)]`** with the same message as §4.1.

**Update both per-round callers**:
- `Commands/Handlers/ListTasksHandler.cs:18–20` — `ListTasksHandler` runs inside `AgentTurnRunner` and is invoked with the executing room's id. Resolve `workspacePath = await roomService.GetWorkspacePathForRoomAsync(roomId)` from the handler's command-context `RoomId`, pass through.
- `Services/AgentToolFunctions.cs:117–120` — the SDK `list_tasks` tool. Same resolution pattern as Bug 3's `TaskWriteToolWrapper` fix: read `_agentId`'s current room, resolve workspace, pass it. On null, return an error string explaining no workspace context (do NOT fall back to `IsActive`).

**Test addition** (in `ListTasksHandlerTests` or new `AgentToolFunctionsTests`):
- `ListTasks_ReturnsBackgroundWorkspaceTasks_WhenInvokedFromBackgroundRoom`: two workspaces, only A is active, agent invokes `LIST_TASKS` from B's room → result contains B's tasks, not A's.
- `ListTasks_FailsCleanly_WhenInvokedFromOrphanRoom`: same as Bug 3's defensive test.

### 4.5 Bug 5 fix — Kickoff message uses the room's workspace, not the active one (cleanup)

`ConversationKickoffService.TryKickoffAsync` already accepts an `activeWorkspace` parameter (`ConversationKickoffService.cs:37`). The fix is naming + caller discipline, not a code change in the service itself:

- **Rename the parameter** to `workspacePath` (drop "active" — it's misleading).
- **Audit the single caller** (`InitializationService` at startup) — pass `workspacePath` resolved from the room, not from `IsActive`. At startup with one workspace this is identical, so no behavioural change today, but the contract becomes correct for any future caller (e.g., a scheduler-fired kickoff).

This is a small change but it removes a latent bug that would surface if multi-workspace kickoff were added later. **Marked optional in §6 if the implementation PR runs long.**

### 4.6 Bug 6 — explicitly out of scope for P2.1 (see §8)

Documented but not fixed. Per-workspace concurrency / queue partitioning is a separate design.

### 4.7 Audit pass — every other `IsActive` consumer

The five fixes above (Bugs 1–5) are the ones that block the acceptance test on a real two-workspace run. But the implementation PR should include a one-time audit pass over the remaining `IsActive` / `GetActiveWorkspacePathAsync` consumers and either:
- Confirm they are controller-only / UI-only (no fix needed; they correctly want "the loaded workspace").
- Or surface them as additional bugs in a follow-up roadmap entry.

Audit list (from grep of `src/AgentAcademy.Server/Services/*.cs`):

| Service | Line | Audit verdict |
|---|---|---|
| `BreakoutRoomService.GetAgentSessionsAsync` | `BreakoutRoomService.cs:205` | UI-only ("agent session history" panel). Safe to keep `IsActive`. |
| `WorkspaceService.GetActiveWorkspaceAsync` | `WorkspaceService.cs:28` | UI/controller. Safe. |
| `TaskItemService.GetActiveTaskItemsAsync` (no-arg) | `TaskItemService.cs:84` | **Bug 1** — fixed by overload. |
| `TaskItemService.GetTaskItemsAsync` (no-roomId branch) | `TaskItemService.cs:129` | UI-only consumer (admin task list). Mark `[Obsolete]` defensively but no per-round caller exists today. Audit deliverable: confirm caller list at implementation time. |
| `TaskQueryService.GetTasksAsync` (no-arg) → `GetActiveWorkspacePathAsync` | `TaskQueryService.cs:37–47, 596–602` | **Bug 4** — fixed by overload. Per-round callers (`ListTasksHandler`, `AgentToolFunctions`) updated. |
| `RoomLifecycleService.GetActiveWorkspacePathAsync` | `RoomLifecycleService.cs:421` | Used by `IsMainCollaborationRoomAsync` ONLY (`RoomLifecycleService.cs:87`) — that's the Bug 2 site, fixed. Verified NOT used by `EvacuateRoomAsync` (which derives default room from `room.WorkspacePath` directly at `RoomLifecycleService.cs:432–434`). After Bug 2 fix, `GetActiveWorkspacePathAsync` becomes dead code in this file and should be deleted. |
| `RoomService.GetActiveWorkspacePathAsync` | `RoomService.cs:497` | Public API surface; controllers depend on it. Keep. |
| `RoomService.GetActiveProjectNameAsync` | `RoomService.cs:254` | Used by Discord channel manager as a fallback (`DiscordChannelManager.cs:294`). UI-shaped. Safe. |
| `InitializationService.GetActiveWorkspacePathAsync` | `InitializationService.cs:148` | Startup-only. Safe (one workspace at boot). |
| `TaskOrchestrationService` inline | `TaskOrchestrationService.cs:90` | **Bug 3** — fixed (controller-only fallback retained per §2 principle 8 exception). |
| `BreakoutRoomService` inline | `BreakoutRoomService.cs:445` | UI-only (above). Safe. |
| `WorkspaceController` | `WorkspaceController.cs:57, 85` | Controller. Safe. |
| `SearchController` | `SearchController.cs:52` | Controller. Safe. |
| `SprintController` (multiple) | `SprintController.cs:74, 98, 176, 467, 634` | Controllers. Safe (human creates sprints in the loaded workspace). |
| `DiscordChannelManager` | `DiscordChannelManager.cs:294, 411` | Fallback path; safe. |

Audit cost: ~30 minutes of grep-and-trace. Audit deliverable: this table, refreshed by the implementation PR with an "✅ confirmed safe" / "❌ fix required" verdict per row.

---

## 5. Test strategy — proving the acceptance test passes

The phase 2 acceptance test cannot be written as a single end-to-end integration test (it needs two real workspaces, a UI session, and time). It IS, however, decomposable into **unit-level cross-workspace regression tests**, plus **one tier-3 smoke verification** that runs the four fixes against a seeded two-workspace database.

### 5.1 Unit-level regression tests (one per bug)

Each fix lands with a paired test that fails on the pre-fix code and passes on the post-fix code:

| Bug | Test name | Setup | Assertion |
|---|---|---|---|
| 1 | `Planner_TaskContext_ResolvedFromRoomWorkspace_NotActive` | Workspace A active; sprint+open tasks in B; run a round in B's main room | `taskItemService.GetActiveTaskItemsAsync(workspacePath)` is invoked with B's path; planner's `taskItems` argument contains B's tasks, not A's. **Test target**: invoke `ConversationRoundRunner.RunRoundsAsync` with `IAgentExecutor` substituted (mirrors `SprintTerminalStageHandlerTests` pattern); inspect the `taskItems` collection passed to `IAgentTurnRunner.RunAgentTurnAsync` via a substituted runner that captures arguments. |
| 2a | `IsMainCollaborationRoom_ReturnsTrue_ForNonActiveWorkspaceMainRoom` | Workspace A active; query B's main room | `true` |
| 2b | `TryAutoArchive_PreservesNonActiveWorkspaceMainRoom` | Workspace A active; B's main room with all tasks terminal | Room remains `Idle` (not `Archived`) |
| 2c | `CleanupStaleRooms_SkipsNonActiveWorkspaceMainRooms` | Workspace A active; B's main room candidate | `Skips` contains B's main room with reason `MainRoom` |
| 3a | `AgentSdkCreateTask_StampsNewRoomWithAgentRoomWorkspace_NotActive` | Workspace A active; agent (location = B's main room) invokes `TaskWriteToolWrapper.CreateTaskAsync` | New room's `WorkspacePath == B` |
| 3b | `AgentSdkCreateTask_FailsCleanly_WhenAgentRoomHasNoWorkspace` | Agent in legacy orphan room invokes `create_task` | Returns error string mentioning workspace context; **does not** silently use `IsActive` |
| 4a | `ListTasks_ReturnsBackgroundWorkspaceTasks_WhenInvokedFromBackgroundRoom` | Workspace A active; B has tasks; agent invokes `LIST_TASKS` from B's room | Returned task list contains B's tasks, not A's |
| 4b | `ListTasksTool_ResolvesWorkspaceFromAgentRoom` | Same setup; agent invokes the SDK `list_tasks` tool | Same assertion as 4a, via `AgentToolFunctions` path |
| 5 | `Kickoff_UsesPassedWorkspacePath_NotActiveWorkspace` | Workspace A active; pass workspacePath="B" to kickoff | Posted message contains B's project name |

All nine are **fast, in-memory EF tests** following the pattern in `SprintSchedulerServiceTests` and `SprintTerminalStageHandlerTests`. **Caveat surfaced by the rubber-duck pass**: existing test base classes typically seed one workspace; cross-workspace tests need a small helper (`SeedSecondWorkspace(path, isActive: false)`) added once and reused. The implementation PR adds this helper alongside the first test that needs it.

### 5.2 Tier-3 smoke verification — the real two-workspace scenario

A one-shot integration-shaped test that directly mirrors the §10 acceptance test:

```
[Arrange]
  Workspace A: created, IsActive=true, has main room A-main, no sprint
  Workspace B: created, IsActive=false, has main room B-main, sprint #N at Implementation
              with one task "T1" (Status=Active)
  (Both workspaces are real EF rows; both rooms are real RoomEntity rows.)

[Act]
  Simulate: agent in B-main posts a message that completes T1
  → MessageService writes the agent message
  → Status update flows through TaskService.CompleteAsync
  → ConversationRoundRunner runs a follow-up round in B-main
  → Round completes
  → SprintTerminalStageHandler.AdvanceIfReadyAsync(sprint #N) runs
  → Classifier sees ImplementationInProgress → ReadyForSelfEval
  → TryStartSelfEvalAsync flips SelfEvaluationInFlight=true
  → OrchestratorWakeService.WakeWorkspaceRoomsForSprintAsync wakes B's rooms (NOT A's)

[Assert]
  Sprint #N row: SelfEvaluationInFlight == true, SelfEvalStartedAt != null
  Orchestrator queue: contains B-main, NOT A-main
  Workspace A: still IsActive=true (never touched)
  Workspace B: IsActive=false (never touched)
  No exception thrown
```

This test sits in `tests/AgentAcademy.Server.Tests/` as `MultiWorkspaceSchedulingTests.SprintAdvancesInBackgroundWorkspace`. Naming mirrors the acceptance test phrasing so the link is obvious.

### 5.3 What the test strategy does NOT cover

- **The Bug 6 priority-inversion behaviour** (long round in B blocking A) — explicit non-goal; tested only via P2.2 follow-up.
- **Real Copilot-SDK execution in two workspaces** — out of scope; we mock `IAgentExecutor` for these tests, which is the same pattern Sprint #17 used for `SprintTerminalStageHandlerTests`.
- **UI behaviour** — covered by P2.2.

---

## 6. Out-of-band considerations

### 6.1 Operational readiness

- **Observability**: Each fix should log workspace context where it resolves it. Today, `OrchestratorWakeService` logs `sprintId` only — adding `workspacePath` to those log lines (and to `ConversationRoundRunner`'s round-start log) makes cross-workspace bugs detectable in the wild without hitting the database.
- **Degradation**: When `roomService.GetWorkspacePathForRoomAsync(roomId)` returns null for a legacy orphan room, services on the per-round path log a warning and skip the workspace-coupled action rather than fall back to `IsActive`. (Today's behaviour silently selects active workspace; post-fix should be explicit.) The controller fallback (Bug 3 §4.3) is the only place null falls through to `IsActive`, and that path is reached only by human-initiated controller calls where "loaded workspace" is the right answer.
- **Secrets**: No new external integrations; nothing to plumb.

### 6.2 Migration

No DB migration. No breaking config changes. No client-side change.

The `[Obsolete]` markers on the no-arg overloads will produce build warnings on any future callsite that adds them — that's the correct UX. Don't suppress them globally; let them surface.

### 6.3 Implementation-PR scope cap

If the implementation PR runs long (e.g., the audit pass surfaces 3+ additional bugs), defer Bug 5 (kickoff cleanup) to a follow-up PR. Bugs 1, 2, 3, 4 together are sufficient for the §10 acceptance test to pass on a two-workspace setup. Bug 5 is a latent-trap fix, not a bug-the-acceptance-test-catches.

---

## 7. Risks and counter-arguments

### 7.1 "Why not just split the orchestrator queue per workspace?"

Considered and rejected for this PR (see §1.3 Bug 6, §2 principle 4, §8). The acceptance test does not require parallel cross-workspace dispatch — it requires that **a backgrounded sprint advances**. Serialised dispatch satisfies that as long as the dispatched round resolves the right workspace (which is what fixes 1–5 ensure). Splitting the queue is real concurrency work — fairness, ordering, back-pressure, watchdog tuning — and belongs in its own design doc once we have data on whether the priority-inversion is actually painful in practice.

### 7.2 "What about the `IsActive` field on `WorkspaceEntity` — should we just remove it?"

No. It encodes a real product concept ("which workspace is the human looking at") that the UI, controllers, and Discord channel manager legitimately need. The fix is to **stop calling it from per-round / background paths**, not to delete it.

### 7.3 "What if two workspaces both have an active sprint at the same time?"

Already supported at the `SprintEntity` level — the unique index is `(WorkspacePath, Status='Active')`, not global. So workspace A and workspace B can each have one active sprint independently. This design changes nothing about that constraint; it just lets the dispatch path treat the two sprints' rounds correctly.

### 7.4 "Could a third workspace's sprint starve under serial dispatch?"

Theoretically yes — three workspaces, two with constant chatter, third's continuation perpetually re-enqueued behind the others. In practice: every workspace's `SystemContinuation` items are deduped per-room (`AgentOrchestrator.cs:343–357`) and rounds are bounded by stage caps. A starved third workspace's sprint would eventually trip `SelfDriveDecisionService`'s round-cap, get blocked, and fire `SprintBlocked → NeedsInput`. So the **degradation mode is observable, not silent**. P2.2 (UI indicator) makes it visible to the human. We do not need preventive fairness logic in P2.1.

### 7.5 "What about the SignalR hub broadcasting to all clients?"

Out of scope for P2.1. The hub already broadcasts every event globally; the UI filters by current workspace. That's a UI concern (P2.2 territory).

### 7.6 "What if the agent's resolved per-round workspace path is null (orphan room)?"

Per §2 principle 8, services on the per-round path treat null as "no workspace context" — they skip the workspace-coupled action and log a warning. They MUST NOT silently fall back to `IsActive`. The few legitimate orphans (the legacy default room, only) get logged as a warning the first time per session. This protects against silent contamination at the cost of a few false-negative actions on legacy orphan rooms; that's the right trade because orphan rooms are a transient artefact of pre-`WorkspaceRoomService.TryAdoptLegacyRoomAsync` data.

### 7.7 "Does the human switching the loaded workspace mid-round cause issues?"

Mid-round workspace switches are safe by construction. The round captures `roomWorkspacePath` at `ConversationRoundRunner.cs:154` (per-round scope, per-DbContext, immutable for the round's lifetime). A human's `POST /api/workspaces/activate` call writes a different `IsActive` row but does **not** mutate any in-flight round's captured workspace value. Post-fix, no shared service consults `IsActive` from the dispatch path, so the live round continues with its captured workspace. Only the **next** round (or controller call) sees the new active workspace — and that's the correct semantics.

---

## 8. Explicit non-goals

These are real concerns this design does **NOT** address. Each is acceptable to defer:

- **Per-workspace concurrent dispatch** (Bug 6). Punt to a Phase 3 design once we have empirical evidence that serialised dispatch hurts. Today there is no such data.
- **UI indication of background activity.** That's P2.2 — separate roadmap item.
- **Fair-share scheduling between workspaces.** No data to design against. Reactive (caps + blocked notifications) is sufficient.
- **Cross-workspace task migration / re-parenting.** No product use case.
- **Removing `IsActive` from `WorkspaceEntity`.** It's a UI affordance, kept for that purpose.
- **Auditing every controller call to `IsActive`.** Controllers are UI-coupled by design; that's the correct lens for them.

---

## 9. Verification ledger (implementation PR)

When the implementation PR opens, it should produce an Anvil evidence bundle showing:

- **Baseline (pre-fix)**: each of the nine unit tests in §5.1 fails on `develop` (proves they catch the regression).
- **After (post-fix)**: same tests pass; full `dotnet build` and `dotnet test` clean; the §5.2 smoke test passes.
- **Adversarial review**: 3 reviewers (gpt-5.3-codex + gemini-3-pro-preview + claude-opus-4.6) — required because P2.1 is 🔴.

This design PR (no code) requires only:
- The doc parses as Markdown.
- Cross-references resolve (file paths + line numbers exist on `develop` HEAD as of 2026-04-28).
- Roadmap row updated to mark P2.1 as `in_progress`.

---

## 10. Roadmap update

When this design PR merges, mark P2.1 as `in_progress` in `roadmap.md` Status Tracking with a one-line note pointing here:
> P2.1 → in_progress. Design landed (`specs/100-product-vision/p2-1-multi-workspace-scheduling-design.md`). Six bugs identified; five (Bugs 1–5) scoped for the implementation PR; Bug 6 (queue concurrency) deferred to Phase 3. Implementation PR follows.

When the implementation PR merges, mark P2.1 as `done` with a note linking the PR and confirming the §5.2 smoke test passes.

---

## Spec Change Proposal

- **Sections affected**: `specs/100-product-vision/spec.md` §7 (Cross-Project Background Work) — no text change required; the section already describes the target behaviour. `specs/100-product-vision/gap-analysis.md` G5 — update with the five-bug breakdown from §1.3 of this doc, and flip the verification checklist item (`gap-analysis.md:112`) to confirm investigation complete.
- **Change type**: NEW_CAPABILITY (closes G5; first item in Phase 2).
- **Proposed changes** (when implementation lands):
  - `gap-analysis.md` G5: change status from `(🟡 Important)` to `[RESOLVED YYYY-MM-DD]` with the implementation PR reference.
  - `roadmap.md` P2.1: status `pending` → `done` with one-line outcome.
  - `roadmap.md` Status Tracking table: P2.1 row updated.
- **Verification**: §10 acceptance test (the one in §1 of this doc) executed against the post-fix build. Two real workspaces seeded, one loaded, the other has an active sprint with a task that completes; sprint advances to self-eval without any UI interaction with the background workspace.
