# 007 — Agent Command System

## Purpose
Defines a unified command pipeline through which agents interact with the platform, codebase, and each other. Every agent action — reading files, moving between rooms, sending messages, managing tasks — flows through a structured envelope with authorization, audit trails, and consistent error handling.

> **Status: Implemented** — All Tier 1 command phases (1A–1H), Tier 2 Room Management, Task Workflow, Communication, Task Management, Code & Spec, Backend Execution, Data & Operations, and Audit & Debug commands are implemented: envelope, parser, pipeline, authorization, audit trail, rate limiting, structured error codes, and 100 handlers covering read operations, state management, verification, communication, navigation, room lifecycle, memory, task management, dependencies, agent context, code navigation, spec browsing, frontend build/typecheck, endpoint probing, log tailing, config inspection, database queries, migration management, health monitoring, connection tracking, audit event queries, error inspection, request tracing, system settings, command retry, and system commands. Memory commands (REMEMBER, RECALL, LIST_MEMORIES, FORGET, EXPORT_MEMORIES, IMPORT_MEMORIES) are implemented. Tier 2 Task Workflow (Phase 2A: TASK_STATUS, SHOW_TASK_HISTORY, SHOW_DEPENDENCIES, REQUEST_REVIEW, WHOAMI) implemented. Tier 2 Communication (Phase 2B: MENTION_TASK_OWNER, BROADCAST_TO_ROOM) implemented. Tier 2 Task Management (Phase 2C: MARK_BLOCKED, SHOW_DECISIONS) implemented. Tier 2 Code & Spec (Phase 2D: OPEN_SPEC, SEARCH_SPEC, OPEN_COMPONENT, FIND_REFERENCES) implemented. Tier 2 Backend Execution (Phase 2E: RUN_FRONTEND_BUILD, RUN_TYPECHECK, CALL_ENDPOINT, TAIL_LOGS, SHOW_CONFIG) implemented. Tier 2 Data & Operations (Phase 2F: QUERY_DB, RUN_MIGRATIONS, SHOW_MIGRATION_STATUS, HEALTHCHECK, SHOW_ACTIVE_CONNECTIONS) implemented. Tier 2 Audit & Debug (Phase 2G: SHOW_AUDIT_EVENTS, SHOW_LAST_ERROR, TRACE_REQUEST, LIST_SYSTEM_SETTINGS, RETRY_FAILED_JOB) implemented. Tier 3 Spec Verification (Phase 3A: VERIFY_SPEC_SECTION, COMPARE_SPEC_TO_CODE, DETECT_ORPHANED_SECTIONS) implemented. Tier 3 Context (Phase 3B: HANDOFF_SUMMARY, PLATFORM_STATUS) implemented. Runs in parallel with existing free-text parsing. Remaining Tier 3 commands (Frontend/UX) are roadmap items.

## Motivation
Today, agents have no formalized way to interact with the platform. The orchestrator parses free-text blocks like `TASK ASSIGNMENT:` from agent responses. This creates:
- No audit trail for agent actions
- No authorization model (any agent can do anything)
- No structured error handling
- No discoverability (agents must remember syntax from prompts)
- No way for agents to read platform state (rooms, tasks, agent locations)

## Architecture

### Command Envelope

Every command flows through a stable envelope from day 1:

```json
{
  "command": "READ_FILE",
  "args": { "path": "src/Program.cs" },
  "status": "success",
  "result": { "content": "...", "lines": 42 },
  "error": null,
  "errorCode": null,
  "correlationId": "cmd-a1b2c3",
  "timestamp": "2026-03-28T12:00:00Z",
  "executedBy": "software-engineer-1"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `command` | string | Command name (SCREAMING_SNAKE) |
| `args` | object | Command-specific parameters |
| `status` | `"success"` \| `"error"` \| `"denied"` | Outcome |
| `result` | object? | Command-specific return data (null on error) |
| `error` | string? | Human-readable error message |
| `errorCode` | string? | Structured error category for programmatic handling (null on success) |
| `correlationId` | string | Unique ID for audit linkage |
| `timestamp` | DateTime | ISO 8601 execution time |
| `executedBy` | string | Agent ID that issued the command |

### Error Codes

Every failed command includes an `errorCode` string that agents can branch on instead of parsing error messages. Defined in `CommandErrorCode` (`src/AgentAcademy.Shared/Models/Commands.cs`):

| Code | Meaning | Example |
|------|---------|---------|
| `VALIDATION` | Missing or invalid arguments | `"Missing required argument: path"` |
| `NOT_FOUND` | Referenced resource does not exist | `"File not found: foo.cs"`, `"Task 'x' not found"` |
| `PERMISSION` | Agent lacks permission or path traversal denied | `"Only planners can close rooms"` |
| `CONFLICT` | Operation conflicts with current state | `"Task is already Completed"`, `"Room has active participants"` |
| `TIMEOUT` | Operation exceeded its time limit | `"Tests timed out after 5 minutes"` |
| `EXECUTION` | Runtime/process failure (crash, non-zero exit) | `"Build failed with exit code 1"` |
| `INTERNAL` | Unexpected internal error | `"Command execution failed: NullReferenceException"` |
| `RATE_LIMIT` | Agent exceeded command rate limit | `"Rate limit exceeded. Try again in 45s."` |

Error codes are string constants (not an enum) for extensibility. The `error` field retains the human-readable detail; `errorCode` provides the category.

The `FormatResultsForContext` method (used to inject command results into agent conversation history) includes the `ErrorCode` on a separate line when present:

```
[Error] READ_FILE (cmd-a1b2c3)
  ErrorCode: NOT_FOUND
  Error: File not found: nonexistent.cs
```

### Pipeline

```
Agent Response (text) → Command Parser → Authorization → Rate Limit → Execution → Audit → Response
```

Each stage:
1. **Command Parser**: Extracts structured commands from agent text. Commands use `COMMAND_NAME:` syntax with either indented `Key: value` lines or inline `key=value` pairs. Parser returns remaining text + extracted commands.
2. **Authorization**: Checks agent permissions against command + args. Returns `denied` if unauthorized.
3. **Rate Limit**: Sliding-window rate limiter (default: 30 commands per 60 seconds). Returns `RATE_LIMIT` error code if exceeded.
4. **Execution**: Dispatches to the appropriate handler. Read commands are side-effect-free. Write commands are idempotent where possible. Handlers that opt in via `IsRetrySafe` are automatically retried on transient failure (up to 3 attempts with 1s/2s exponential backoff). Only `TIMEOUT` and `INTERNAL` error codes trigger pipeline retry — `RATE_LIMIT` is excluded (policy enforcement, not transient). Non-retry-safe handlers execute exactly once; the agent must re-issue manually on failure.
5. **Audit**: Every command execution is recorded as a `CommandAuditEntity` with the full envelope, including `ErrorCode` and `RetryCount` if applicable. Only the final attempt is audited.
6. **Response**: Result is injected into the agent's next context turn.

### Permission Model

Per-agent permission boundaries are defined in `src/AgentAcademy.Server/Config/agents.json` (the authoritative source) via two layers:

1. **`Permissions.Allowed` / `Permissions.Denied`** — explicit command allowlist/denylist enforced by `CommandAuthorizationService`. `LIST_*` is a wildcard matching every read-only `LIST_*` command.
2. **`EnabledTools`** — SDK tool-group gates for non-command capabilities (file write, code execution, task DB mutations): `code` (file read + search), `code-write` (file write scoped to `src/`), `spec-write` (file write scoped to `specs/` and `docs/`), `task-state`, `task-write`, `memory`, `chat`.

The table below summarises each agent's permission envelope by capability group. For the exact command list, always read `agents.json`.

| Agent | Role | Command Groups (summary) | SDK Tools | Denied Commands |
|-------|------|--------------------------|-----------|-----------------|
| Aristotle | Planner | All read (`LIST_*`, `READ_FILE`, `SEARCH_CODE`, `GIT_LOG`, `SHOW_DIFF`); full task lifecycle (`CLAIM_TASK`, `RELEASE_TASK`, `UPDATE_TASK`, `APPROVE_TASK`, `REQUEST_CHANGES`, `REJECT_TASK`, `CANCEL_TASK`, `MERGE_TASK`, `REBASE_TASK`, `SHOW_REVIEW_QUEUE`); task items + deps + comments; goal cards (`CREATE_GOAL_CARD`, `UPDATE_GOAL_CARD_STATUS`); communication (`DM`, `MENTION_TASK_OWNER`, `BROADCAST_TO_ROOM`); room management (`CREATE_ROOM`, `CLOSE_ROOM`, `REOPEN_ROOM`, `INVITE_TO_ROOM`, `MOVE_TO_ROOM`, `CLEANUP_ROOMS`, `ROOM_HISTORY`, `ROOM_TOPIC`, `RETURN_TO_MAIN`); sprint lifecycle (`START_SPRINT`, `ADVANCE_STAGE`, `COMPLETE_SPRINT`, `SCHEDULE_SPRINT`, `GENERATE_DIGEST`); memory (`REMEMBER`, `RECALL`, `FORGET`, `EXPORT_MEMORIES`, `IMPORT_MEMORIES`); evidence (`RECORD_EVIDENCE`, `QUERY_EVIDENCE`, `CHECK_GATES`, `STORE_ARTIFACT`); build/test (`RUN_BUILD`, `RUN_TESTS`); GitHub (`CREATE_PR`, `POST_PR_REVIEW`, `GET_PR_REVIEWS`, `MERGE_PR`); orchestration (`RECALL_AGENT`, `RESTART_SERVER`, `CLEANUP_WORKTREES`, `LINK_TASK_TO_SPEC`, `SHOW_UNLINKED_CHANGES`, `SET_PLAN`); allowlisted `SHELL` | `chat`, `task-state`, `task-write`, `memory`, `code` | *(none)* |
| Archimedes | Architect | Read all (`LIST_*`, `READ_FILE`, `SEARCH_CODE`, `GIT_LOG`, `SHOW_DIFF`); task items + comments + deps; goal cards (`CREATE_GOAL_CARD`, `UPDATE_GOAL_CARD_STATUS`); communication (`DM`, `MENTION_TASK_OWNER`, `BROADCAST_TO_ROOM`); `SET_PLAN`, `REBASE_TASK`; memory + evidence (`QUERY_EVIDENCE`, `CHECK_GATES`, `STORE_ARTIFACT`); PR read (`GET_PR_REVIEWS`); room navigation (`MOVE_TO_ROOM`, `ROOM_HISTORY`, `ROOM_TOPIC`, `RETURN_TO_MAIN`) | `chat`, `task-state`, `task-write`, `memory`, `code` | `RESTART_SERVER` (implied: no task approval, no code execution, no file write) |
| Hephaestus | SoftwareEngineer | Read all; self-task lifecycle (`CLAIM_TASK`, `RELEASE_TASK`, `UPDATE_TASK`, `REBASE_TASK`); task items + comments + deps; goal cards (`CREATE_GOAL_CARD`, `UPDATE_GOAL_CARD_STATUS`); communication (`DM`, `MENTION_TASK_OWNER`, `BROADCAST_TO_ROOM`); memory; evidence (`RECORD_EVIDENCE`, `QUERY_EVIDENCE`, `CHECK_GATES`, `STORE_ARTIFACT`); build/test (`RUN_BUILD`, `RUN_TESTS`); git (`COMMIT_CHANGES`, `SHOW_DIFF`, `GIT_LOG`); GitHub (`CREATE_PR`, `GET_PR_REVIEWS`); room navigation (`MOVE_TO_ROOM`, `ROOM_HISTORY`, `ROOM_TOPIC`, `RETURN_TO_MAIN`); `SET_PLAN` | `chat`, `task-state`, `task-write`, `memory`, `code`, `code-write` | `APPROVE_TASK`, `REQUEST_CHANGES`, `RESTART_SERVER` |
| Athena | SoftwareEngineer | Same as Hephaestus | Same as Hephaestus | Same as Hephaestus |
| Socrates | Reviewer | Read all; review authority (`APPROVE_TASK`, `REQUEST_CHANGES`, `REJECT_TASK`, `SHOW_REVIEW_QUEUE`, `CANCEL_TASK`, `MERGE_TASK`, `REBASE_TASK`); task items + comments; goal cards (`CREATE_GOAL_CARD`, `UPDATE_GOAL_CARD_STATUS`); communication (`DM`, `MENTION_TASK_OWNER`, `BROADCAST_TO_ROOM`); evidence (`RECORD_EVIDENCE`, `QUERY_EVIDENCE`, `CHECK_GATES`, `STORE_ARTIFACT`); build/test (`RUN_BUILD`, `RUN_TESTS`); PRs (`CREATE_PR`, `POST_PR_REVIEW`, `GET_PR_REVIEWS`, `MERGE_PR`); allowlisted `SHELL`; memory; room navigation (`MOVE_TO_ROOM`, `ROOM_HISTORY`, `ROOM_TOPIC`, `RETURN_TO_MAIN`); `SET_PLAN` | `chat`, `task-state`, `task-write`, `memory`, `code` (no `code-write` — reviewer reads but doesn't author code) | `RESTART_SERVER` |
| Thucydides | TechnicalWriter | Read all (`LIST_*`, `READ_FILE`, `SEARCH_CODE`, `GIT_LOG`, `SHOW_DIFF`); task items + comments; goal cards (`CREATE_GOAL_CARD`, `UPDATE_GOAL_CARD_STATUS`); communication (`DM`, `MENTION_TASK_OWNER`, `BROADCAST_TO_ROOM`); memory; evidence (`QUERY_EVIDENCE`, `CHECK_GATES`, `STORE_ARTIFACT`); `SET_PLAN`, `REBASE_TASK`; room navigation (`MOVE_TO_ROOM`, `ROOM_HISTORY`, `ROOM_TOPIC`, `RETURN_TO_MAIN`) | `chat`, `task-state`, `task-write`, `memory`, `code`, `spec-write` (file write scoped to `specs/` and `docs/` only; see "Spec write capability" note below) | `APPROVE_TASK`, `REQUEST_CHANGES`, `RESTART_SERVER` |

> **Spec write capability**: Thucydides owns the spec (see spec 009 §Spec Ownership) and holds the `spec-write` SDK tool group in `agents.json`. The `spec-write` group exposes the same `write_file` and `commit_changes` tools as `code-write` but the wrapper restricts writes to the `specs/` and `docs/` directories only — attempts to write outside those roots are rejected with an error, and Thucydides is never granted `code-write`. This preserves the engineer/writer split: engineers own `src/`, Thucydides owns `specs/` and `docs/`, and neither can modify the other's domain via SDK tools. Implementation: `CodeWriteToolWrapper` parameterised over a list of `allowedRoots` (`src` for code-write; `specs`, `docs` for spec-write) with `SpecWriteProtectedPaths` empty — no spec or docs file is structurally protected because Thucydides is expected to maintain the entire spec/docs corpus.

> **Phase 2A commands**: All agents receive `TASK_STATUS`, `SHOW_TASK_HISTORY`, `SHOW_DEPENDENCIES`, and `WHOAMI` (read-only). `REQUEST_REVIEW` is granted to `SoftwareEngineer` and `Planner` roles only (agents that can be task assignees or manage task lifecycle). The authoritative source remains `agents.json`.

> **Phase 2B commands**: All agents receive `MENTION_TASK_OWNER` and `BROADCAST_TO_ROOM`. These are non-destructive communication commands — `MENTION_TASK_OWNER` sends a DM, `BROADCAST_TO_ROOM` posts a room message. The authoritative source remains `agents.json`.

**Escalation rules:**
- Tighten standards → Socrates review only
- Relax standards → Human approval required
- Self-modification → Socrates + Human approval
- Socrates cannot modify own review standards

### Task Creation Gating
- Only agents with the `Planner` role can create tasks via TASK ASSIGNMENT blocks
- Exception: any agent can create a task with `Type: Bug`
- Non-planner agents attempting to create non-Bug tasks will have their assignment converted to a proposal message

## Command Reference

### Tier 1 — Critical Path

#### Phase 1A: Formalized Read Operations — IMPLEMENTED
These formalize existing capabilities with audit trails and structured output.

| Command | Args | Returns | Side Effects | Implementation |
|---------|------|---------|-------------|----------------|
| `READ_FILE` | `path`, `startLine?`, `endLine?` | File content, line count (files); entry listing (directories). Auto-truncates at 12,000 chars with `truncated=true` and continuation hint. | Audit event | `ReadFileHandler.cs` — validates path, reads lines, lists directories, protects against traversal, truncates large output |
| `SEARCH_CODE` | `query`, `path?`, `glob?`, `ignoreCase?` | Matching lines with file/line refs. Caps at 50 results with `truncated=true` hint. | Audit event | `SearchCodeHandler.cs` — `git grep`-based search; respects .gitignore, skips binary files, supports case-insensitive mode |
| `LIST_ROOMS` | `status?` | All rooms: id, name, status, phase, participant count, message count, active task. Optional `status` filter (e.g., `status=Active`, `status=Archived`) with case-insensitive `RoomStatus` enum validation. | Audit event | `ListRoomsHandler.cs` — queries all rooms with preloaded agent locations; filters by `RoomStatus` when provided |
| `LIST_AGENTS` | — | All agents: id, name, role, location (room/workspace), state, active task item | Audit event | `ListAgentsHandler.cs` — queries agent catalog + locations + presence |
| `LIST_TASKS` | `status?`, `assignee?` | Tasks with status, assignee, dependencies, acceptance criteria, spec links, review state | Audit event | `ListTasksHandler.cs` — queries tasks with optional filters |

**Evidence**: `src/AgentAcademy.Server/Commands/Handlers/{ReadFile,SearchCode,ListRooms,ListAgents,ListTasks}Handler.cs` — committed in `63b596c` (2026-03-28).

#### Phase 1B: Structured State Management — IMPLEMENTED

| Command | Args | Returns | Side Effects | Implementation |
|---------|------|---------|-------------|----------------|
| `ASK_HUMAN` | — | — | — | **DEPRECATED** — replaced by `DM` command. Use `DM: Recipient: @Human` instead. |
| `LINK_TASK_TO_SPEC` | `taskId`, `specSection` | Confirmation + link record | Creates bidirectional link |
| `SHOW_UNLINKED_CHANGES` | `since?` | Tasks/commits without spec links | Audit event |
| `APPROVE_TASK` | `taskId`, `findings?` | Confirmation | Updates task status, records reviewer | `ApproveTaskHandler.cs` — validates InReview/AwaitingValidation state, sets Approved, records reviewer, increments ReviewRounds, posts findings as review message |
| `REQUEST_CHANGES` | `taskId`, `findings` | Confirmation | Updates task status, creates feedback | `RequestChangesHandler.cs` — validates InReview/AwaitingValidation state, sets ChangesRequested, records reviewer, increments ReviewRounds, posts findings as review message |
| `REJECT_TASK` | `taskId`, `reason` | Confirmation, optional `revertCommitSha` | Reverts Approved/Completed → ChangesRequested, reverts merge if completed, reopens breakout room | `RejectTaskHandler.cs` — Planner/Reviewer/Human role gate, reverts merge commit on develop for completed tasks, reopens archived breakout, posts rejection reason |
| `SHOW_REVIEW_QUEUE` | — | Tasks awaiting review | Audit event | `ShowReviewQueueHandler.cs` — queries tasks with InReview or AwaitingValidation status, returns summary list |
| `CLAIM_TASK` | `taskId` | Confirmation | Assigns agent, prevents duplicate work | `ClaimTaskHandler.cs` — validates no other claimant, assigns calling agent, auto-activates Queued tasks |
| `RELEASE_TASK` | `taskId` | Confirmation | Unassigns agent | `ReleaseTaskHandler.cs` — validates calling agent is current assignee, clears assignment |
| `UPDATE_TASK` | `taskId`, `status?`, `blocker?`, `note?` | Confirmation | Updates task state | `UpdateTaskHandler.cs` — validates allowed statuses (Active/Blocked/AwaitingValidation/InReview/Queued), handles blocker→Blocked shorthand, posts notes to task room |
| `SET_PLAN` | `content` | Confirmation + target room ID | Writes markdown plan content for the caller's current room or breakout room | `SetPlanHandler.cs` — validates non-empty content and active room context, then calls `PlanService.SetPlanAsync` |
| `ADD_TASK_COMMENT` | `taskId`, `type?` (Comment\|Finding\|Evidence\|Blocker, default: Comment), `content` | Comment ID and confirmation | Validates task exists, caller is assignee/reviewer/planner. Creates `TaskCommentEntity`, posts activity event | |
| `CREATE_TASK_ITEM` | `title`, `description?`, `assignedTo?` (ID or name, default: caller), `roomId?` (default: current room), `breakoutRoomId?` | Task item ID, title, status, assignedTo, roomId | Validates agent exists in catalog, room exists. Creates `TaskItemEntity` with Pending status. | `CreateTaskItemHandler.cs` + `TaskItemService.CreateTaskItemAsync()` |
| `UPDATE_TASK_ITEM` | `taskItemId`, `status` (Pending\|Active\|Done\|Rejected), `evidence?` | Task item ID, title, status, evidence | Validates item exists, caller is assignee/Planner/Reviewer/Human. Updates status and optional evidence. | `UpdateTaskItemHandler.cs` + `TaskItemService.UpdateTaskItemStatusAsync()` |
| `LIST_TASK_ITEMS` | `roomId?`, `status?` (Pending\|Active\|Done\|Rejected) | List of task items with count and applied filters | Scoped to active workspace when no room filter. Any agent can list. | `ListTaskItemsHandler.cs` + `TaskItemService.GetTaskItemsAsync()` |
| `RECALL_AGENT` | `agentId` (name or ID) | Agent info and room transition details | Validates caller has Planner role, target agent is in Working state in a breakout room. Closes breakout room, moves agent to Idle in parent room. Posts recall notices to both breakout and parent rooms | |
| `CLOSE_ROOM` | `roomId` | Room ID, room name, archived status | Validates caller has Planner or Human role, target room exists, target is not the workspace's main collaboration room, and the room has no active participants. Sets `RoomEntity.Status = Archived`, updates `UpdatedAt`, and publishes a `RoomClosed` activity event. Repeating the command against an already archived room is a no-op success. | `CloseRoomHandler.cs` + `RoomService.CloseRoomAsync()` |
| `MERGE_TASK` | `taskId` | Task ID, title, branch, `mergeCommitSha` | Validates caller is Reviewer or Planner, task status is Approved, task has BranchName. Sets task to Merging status, squash-merges task branch to develop, runs `git add -A` to stage the full squash result, then commits using conventional-commit subject `{prefix}{task.Title}` where `Feature -> feat: `, `Bug -> fix: `, `Chore -> chore: `, and `Spike -> docs: `. On success: updates task to Completed, records merge commit SHA on TaskEntity, and returns it in the result. On conflict: aborts the merge, restores task status to Approved, and returns an error. Authorization enforced at handler level. | `MergeTaskHandler.cs` lines 25-31 — Planner/Reviewer role guard |
| `CREATE_GOAL_CARD` | `task_description`, `intent`, `divergence`, `steelman`, `strawman`, `verdict` (Proceed\|ProceedWithCaveat\|Challenge), `fresh_eyes_1`, `fresh_eyes_2`, `fresh_eyes_3`, `task_id?` | `goalCardId`, `verdict`, `status`, `message` | Creates an immutable goal card — the structured intent artifact an agent produces before starting significant work. Validates TaskId FK if provided. Challenge verdict auto-sets status to Challenged and publishes `GoalCardChallenged` activity event; other verdicts set Active and publish `GoalCardCreated`. All content fields are required with length validation. Any agent can create. | `CreateGoalCardHandler.cs` + `GoalCardService.CreateAsync()` |
| `UPDATE_GOAL_CARD_STATUS` | `goal_card_id`, `status` (Active\|Completed\|Challenged\|Abandoned) | `goalCardId`, `status`, `message` | Transitions a goal card through its lifecycle state machine. Valid transitions: Active → Completed\|Challenged\|Abandoned; Challenged → Active\|Abandoned. Completed and Abandoned are terminal. Challenged status publishes `GoalCardChallenged` activity event. Any agent can update. | `UpdateGoalCardStatusHandler.cs` + `GoalCardService.UpdateStatusAsync()` |

#### Phase 1C: Verification — IMPLEMENTED

| Command | Args | Returns | Side Effects | Implementation |
|---------|------|---------|-------------|----------------|
| `RUN_BUILD` | — | Build output, exit code | Runs `dotnet build` | `RunBuildHandler.cs` — 10min safety timeout, output truncated to 3KB |
| `RUN_TESTS` | `scope?` (`all`, `frontend`, `backend`, `file:path`) | Test output, exit code | Runs test suite | `RunTestsHandler.cs` — routes frontend to `npm test`, backend to `dotnet test` |
| `SHOW_DIFF` | `branch?` | Git diff output | Audit event | `ShowDiffHandler.cs` — `git diff --stat -p`, optional branch comparison |
| `GIT_LOG` | `file?`, `since?`, `count?` | Commit history (sha + message) | Audit event | `GitLogHandler.cs` — `git log --oneline`, max 50 entries |
| `RECORD_EVIDENCE` | `taskId`, `checkName`, `passed` (true/false), `phase?` (Baseline/After/Review, default: After), `tool?` (default: manual), `command?`, `exitCode?`, `output?` (max 500 chars) | Evidence ID, task ID, phase, check name, passed status, confirmation message | Records a `TaskEvidenceEntity` in the `task_evidence` table. Handler-level authorization: caller must be assignee, reviewer, planner, or human. | `RecordEvidenceHandler.cs` + `TaskEvidenceService.RecordEvidenceAsync()` |
| `QUERY_EVIDENCE` | `taskId`, `phase?` (Baseline/After/Review, default: all) | Task ID, phase filter, total/passed/failed counts, evidence list (id, phase, checkName, tool, command, exitCode, output, passed, agentName, createdAt) | Audit event (read-only) | `QueryEvidenceHandler.cs` + `TaskQueryService.GetTaskEvidenceAsync()` |
| `CHECK_GATES` | `taskId` | Task ID, currentPhase, targetPhase, met (bool), requiredChecks, passedChecks, missingChecks list, evidence summary | Evaluates gate requirements and publishes `GateChecked` activity event. Read-only — does not transition task status. | `CheckGatesHandler.cs` + `TaskEvidenceService.CheckGatesAsync()` |

**Evidence**: `src/AgentAcademy.Server/Commands/Handlers/{RecordEvidence,QueryEvidence,CheckGates}Handler.cs` — committed in `42d4124` (2026-04-07).

#### Phase 1D: Communication — IMPLEMENTED

| Command | Args | Returns | Side Effects | Implementation |
|---------|------|---------|-------------|----------------|
| `DM` | `recipient` (agentId, agent name, or `@Human`), `message` | Delivery confirmation | Stores DM with RecipientId, posts system notification in recipient's room, triggers immediate agent round or Discord notification | `DmHandler.cs` — routes `@Human` to notification bridge (Discord), agent recipients to DB storage + orchestrator wake-up. Case-insensitive name/ID matching. Self-DM prevented. |
| `ROOM_HISTORY` | `roomId`, `count?` | Recent messages from specified room | Audit event (no movement) | `RoomHistoryHandler.cs` — reads room snapshot, returns last N messages (max 50) |

#### Phase 1E: Navigation — IMPLEMENTED

| Command | Args | Returns | Side Effects | Implementation |
|---------|------|---------|-------------|----------------|
| `MOVE_TO_ROOM` | `roomId` | Confirmation + room name | Updates agent location | `MoveToRoomHandler.cs` — validates room exists, calls MoveAgentAsync |
| `RETURN_TO_MAIN` | — | Confirmation + main room name | Moves calling agent to default room (Idle) | `ReturnToMainHandler.cs` — syntactic sugar for MOVE_TO_ROOM with DefaultRoomId. No-op if already in main room. Any role. |

#### Phase 1G: Room Lifecycle — IMPLEMENTED

| Command | Args | Returns | Side Effects | Implementation |
|---------|------|---------|-------------|----------------|
| `CREATE_ROOM` | `name`, `description?` | Room ID, name, status | Creates a persistent collaboration room as a work context. Generates a slug-based ID. Posts a system welcome message with description. Publishes `RoomCreated` activity event. Planner or Human role required. | `CreateRoomHandler.cs` + `RoomService.CreateRoomAsync()` |
| `REOPEN_ROOM` | `roomId` | Room ID, name, reopened status | Validates room is archived. Sets status to Idle. Publishes `RoomStatusChanged` activity event. Planner or Human role required. Exception-safe via try/catch for TOCTOU protection. | `ReopenRoomHandler.cs` + `RoomService.ReopenRoomAsync()` |
| `INVITE_TO_ROOM` | `agentId` (name or ID), `roomId` | Agent ID/name, room ID/name, confirmation | Moves a specified agent to a specified room. Validates room exists and is not archived. Validates agent exists and is not Working in a breakout (use RECALL_AGENT first). No-op success if agent already in room. Posts system status message in target room. Planner or Human role required. | `InviteToRoomHandler.cs` + `AgentLocationService.MoveAgentAsync()` |
| `ROOM_TOPIC` | `roomId`, `topic` | Room ID, name, topic | Sets or clears the topic description for a room. Empty/whitespace topic clears it. Cannot set topic on archived rooms. Publishes `RoomStatusChanged` activity event. Any agent can set topic. | `RoomTopicHandler.cs` + `RoomService.SetRoomTopicAsync()` |

#### Phase 1F: System — IMPLEMENTED

| Command | Args | Returns | Side Effects | Implementation |
|---------|------|---------|-------------|----------------|
| `RESTART_SERVER` | `reason` | Exit code (75), confirmation | Posts system message, sets exit code 75, triggers graceful shutdown via `IHostApplicationLifetime.StopApplication()`. Wrapper script detects code 75 and restarts immediately. | `RestartServerHandler.cs` — Planner-only authorization (role-gated + CommandAuthorizer). Schedules shutdown on background thread for response propagation. |
| `SHELL` | `operation`, plus operation-specific args:<br>`git-checkout` → `branch`<br>`git-stash-pop` → `branch`<br>`git-commit` → `message`<br>`restart-server` → `reason`<br>`dotnet-build` / `dotnet-test` → no extra args | Structured result including `operation`, `exitCode`, `success`, and operation-specific fields like `branch`, `commitSha`, `reason`, or `output` | Executes one allowlisted operational command only; no arbitrary bash or shell composition | `ShellCommandHandler.cs` — Planner/Reviewer-only authorization (role-gated in both `CommandAuthorizer` and handler). Validates the operation name and allowed args, routes git actions through `GitService`, runs dotnet operations via direct `ProcessStartInfo.ArgumentList`, and returns explicit errors for unsupported operations, invalid args, missing stashes, or non-zero exits. |

**`SHELL` operation allowlist**
- `git-checkout` — runs `git checkout <branch>` after validating the branch name
- `git-stash-pop` — restores the newest `auto-stash:{branch}:<timestamp>` entry for the specified branch
- `git-commit` — runs `git commit -m <message>` against already staged changes and returns the resulting commit SHA
- `restart-server` — same restart semantics as `RESTART_SERVER`, but exposed through the allowlisted `SHELL` dispatcher
- `dotnet-build` — runs `dotnet build --nologo -v q`
- `dotnet-test` — runs `dotnet test --nologo -v q`

#### Phase 1H: Worktree Management — IMPLEMENTED

| Command | Args | Returns | Side Effects | Implementation |
|---------|------|---------|-------------|----------------|
| `LIST_WORKTREES` | `status?` | All active worktrees: branch, relative path, created time, linked task info (id, title, status, agent). Optional `status` filter matches on task status. | Audit event | `ListWorktreesHandler.cs` — queries WorktreeService + DB task enrichment; read-only, retry-safe |
| `CLEANUP_WORKTREES` | `includeOrphans?`, `confirm` | Count and list of removed branches | Removes worktrees linked to completed/cancelled tasks. With `includeOrphans=true`, also removes worktrees with no linked task. | `CleanupWorktreesHandler.cs` — Planner/Human-only; destructive (requires `confirm=true`) |
| `LIST_AGENT_STATS` | `hoursBack?`, `agentId?` | Per-agent task effectiveness: completion rate, cycle time, review rounds, first-pass approval rate, rework rate, commits per task. Overview totals included. | Audit event | `ListAgentStatsHandler.cs` — delegates to `TaskAnalyticsService`; read-only, retry-safe. Agent filter matches by ID or name (case-insensitive). |

**Evidence**: `src/AgentAcademy.Server/Commands/Handlers/{ListWorktrees,CleanupWorktrees,ListAgentStats}Handler.cs` — 17 tests in `WorktreeCommandHandlerTests.cs`, 16 tests in `ListAgentStatsHandlerTests.cs`.

#### Phase 2A: Task Workflow & Context — IMPLEMENTED

| Command | Args | Returns | Side Effects | Implementation |
|---------|------|---------|-------------|----------------|
| `TASK_STATUS` | `taskId` | Deep task detail: full snapshot, dependency graph (upstream/downstream with satisfaction), evidence summary (total/passed/failed by phase), spec links, timeline (created/started/completed) | Audit event | `TaskStatusHandler.cs` — read-only, retry-safe; aggregates from `ITaskQueryService`, `ITaskDependencyService` |
| `SHOW_TASK_HISTORY` | `taskId`, `count?` (default 20, max 50) | Interleaved chronological list of comments and evidence records, newest first | Audit event | `ShowTaskHistoryHandler.cs` — read-only, retry-safe; merges comments + evidence by timestamp |
| `SHOW_DEPENDENCIES` | `taskId` | Dependency graph: upstream tasks (what this depends on), downstream tasks (what depends on this), satisfaction status, blocked flag | Audit event | `ShowDependenciesHandler.cs` — read-only, retry-safe; delegates to `ITaskDependencyService.GetDependencyInfoAsync` |
| `REQUEST_REVIEW` | `taskId`, `summary?` | Updated task status, previous status, review round count | Transitions task to InReview, posts notification to room, optionally posts summary as task note | `RequestReviewHandler.cs` — validates caller is assignee/Planner/Human; accepts Active/AwaitingValidation/ChangesRequested source states |
| `WHOAMI` | — | Agent identity: id, name, role, current room, breakout room, working directory, enabled tools, capability tags, allowed/denied commands | Audit event | `WhoAmIHandler.cs` — read-only, retry-safe; uses `CommandContext` + `IAgentCatalog` lookup |

**Evidence**: `src/AgentAcademy.Server/Commands/Handlers/{TaskStatus,ShowTaskHistory,ShowDependencies,RequestReview,WhoAmI}Handler.cs` — 28 tests in `Tier2TaskCommandTests.cs`.

#### Phase 2B: Communication — IMPLEMENTED

| Command | Args | Returns | Side Effects | Implementation |
|---------|------|---------|-------------|----------------|
| `MENTION_TASK_OWNER` | `taskId`, `message` | Task id/title, recipient name/id, delivery confirmation | Sends a DM from the caller to the task's assigned agent with task context prefix. Wakes recipient via `HandleDirectMessage`. | `MentionTaskOwnerHandler.cs` — validates task exists, has assignee, assignee in catalog, not self-mention; falls back to `roomId = "main"` if null |
| `BROADCAST_TO_ROOM` | `roomId`, `message` | Room id/name, participant count, delivery confirmation | Posts a message to the target room as the calling agent (even if not a room member). Message prefixed with `[Broadcast from AgentName / Role]`. | `BroadcastToRoomHandler.cs` — validates room exists and is not Archived; uses `PostMessageAsync` for agent-attributed authorship |

**Permission model**: Both commands granted to all agents in `agents.json`. `MENTION_TASK_OWNER` requires the target task to have an assigned agent in the active catalog. `BROADCAST_TO_ROOM` allows cross-room posting (sender need not be in the target room).

**Evidence**: `src/AgentAcademy.Server/Commands/Handlers/{MentionTaskOwner,BroadcastToRoom}Handler.cs` — 15 tests in `Tier2CommunicationCommandTests.cs`.

### Tier 2 — Full Autonomy

#### Room Management
`RETURN_TO_MAIN` *(implemented — see Phase 1E table)*, ~~`INVITE_TO_ROOM`~~ *(implemented — see Phase 1G table)*, ~~`CREATE_ROOM`~~ *(implemented)*, ~~`RESTORE_ROOM`~~ *(consolidated into `REOPEN_ROOM`)*, ~~`ROOM_TOPIC`~~ *(implemented — see Phase 1G table)*

#### Communication
~~`MENTION_TASK_OWNER`~~ *(implemented — see Phase 2B table)*, ~~`BROADCAST_TO_ROOM`~~ *(implemented — see Phase 2B table)*

#### Task Management
~~`TASK_STATUS`~~ *(implemented — see Phase 2A table)*, ~~`SHOW_DEPENDENCIES`~~ *(implemented)*, ~~`MARK_BLOCKED`~~ *(implemented — see Phase 2C table)*, ~~`SHOW_TASK_HISTORY`~~ *(implemented)*, ~~`REQUEST_REVIEW`~~ *(implemented)*, ~~`SHOW_DECISIONS`~~ *(implemented — see Phase 2C table)*

##### Phase 2C — Task Management Commands

| Command | Args | Returns | Side Effects | Implementation |
|---------|------|---------|-------------|----------------|
| `MARK_BLOCKED` | `taskId`, `reason` | Task id/title, previous status, new status, reason | Transitions task to `Blocked` status. Records a `Blocker`-typed comment with the reason. Posts system status notification to the room. Rejects terminal (`Completed`/`Cancelled`), already-`Blocked`, and merge-workflow (`Approved`/`Merging`) states. | `MarkBlockedHandler.cs` — comment/notification are non-critical (swallowed on failure) |
| `SHOW_DECISIONS` | `taskId`, `count?` (default 20, max 50) | Task id/title, decision list (id, agent, content, timestamp), counts | Read-only. Filters task comments to `Decision` type, ordered newest-first. | `ShowDecisionsHandler.cs` — retry-safe |

**Permission model**: Both commands granted to all agents in `agents.json`. `MARK_BLOCKED` is not retry-safe (mutating). `SHOW_DECISIONS` is retry-safe (read-only).

**New enum value**: `TaskCommentType.Decision` added to support tagging comments as decisions. Agents record decisions via `ADD_TASK_COMMENT taskId=X type=Decision content="..."` and surface them with `SHOW_DECISIONS`.

**Evidence**: `src/AgentAcademy.Server/Commands/Handlers/{MarkBlocked,ShowDecisions}Handler.cs` — 23 tests in `Tier2TaskManagementCommandTests.cs`.

#### Code & Spec
~~`OPEN_SPEC`~~, ~~`SEARCH_SPEC`~~, ~~`OPEN_COMPONENT`~~, ~~`FIND_REFERENCES`~~ *(all implemented — Phase 2D)*

| Command | Args | Returns | Notes |
|---------|------|---------|-------|
| `OPEN_SPEC` | `id` (section number or directory name, e.g. "007" or "007-agent-commands"), `startLine?`, `endLine?` | Section content with metadata (heading, summary, path, totalLines) | Resolves numeric prefixes via `ISpecManager.GetSpecSectionsAsync()`. Truncates at 12K chars. Retry-safe. |
| `SEARCH_SPEC` | `query`, `ignoreCase?` | Line-level matches scoped to `specs/` | Uses `git grep` on spec files. Max 50 results. Retry-safe. |
| `OPEN_COMPONENT` | `name` (component/class name, e.g. "CommandParser"), `startLine?`, `endLine?` | File content with path | Uses `git ls-files` to find tracked files in `src/` matching name. Returns partial-match suggestions on no exact match. Supports `.cs`, `.tsx`, `.ts`, `.jsx`, `.js`. Retry-safe. |
| `FIND_REFERENCES` | `symbol`, `path?` (subdirectory scope, defaults to `src/`), `ignoreCase?`, `wholeWord?` (default true) | References grouped by file with line numbers | Fixed-string search (`-F`) — not regex. Whole-word matching by default. Results grouped by file. Max 50 matches. Retry-safe. |

**Evidence**: `src/AgentAcademy.Server/Commands/Handlers/{OpenSpec,SearchSpec,OpenComponent,FindReferences}Handler.cs` — 30 tests in `Tier2CodeSpecCommandTests.cs`.

#### GitHub Integration
~~`CREATE_PR`~~, ~~`POST_PR_REVIEW`~~, ~~`GET_PR_REVIEWS`~~, ~~`MERGE_PR`~~ *(all implemented — see spec 010 §5 GitHub Integration for argument schemas, permission gates, and preconditions)*

#### Backend Execution
~~`RUN_FRONTEND_BUILD`~~, ~~`RUN_TYPECHECK`~~, ~~`CALL_ENDPOINT`~~, ~~`TAIL_LOGS`~~, ~~`SHOW_CONFIG`~~ *(all implemented — see Phase 2E table below)*

`RUN_SERVER` — dropped as redundant with existing `RESTART_SERVER`.

| Command | Args | Returns | Notes |
|---------|------|---------|-------|
| `RUN_FRONTEND_BUILD` | *(none)* | `exitCode`, `output`, `success` | Runs `npm run build` in `src/agent-academy-client`. Serialized via `FrontendLock`. 10-min timeout. Concurrent stdout/stderr capture with process-tree kill on timeout. Async for human UI. |
| `RUN_TYPECHECK` | *(none)* | `exitCode`, `output`, `success` | Runs `npx tsc --noEmit` in client dir. Shares `FrontendLock` with `RUN_FRONTEND_BUILD` to prevent concurrent TypeScript operations. 5-min timeout. Async for human UI. |
| `CALL_ENDPOINT` | `path` (required, must start with `/`) | `statusCode`, `contentType`, `body`, `method` | GET-only v1. Restricted to Planner/Reviewer roles (enforced in handler). Denied paths: `/api/auth`, `/api/commands`. Path validation rejects `//`, `\`. Resolves port from `IServerAddressesFeature`, rebuilds URL as `http://127.0.0.1:{port}`. 30s timeout. |
| `TAIL_LOGS` | `lines?` (default 100, max 500), `filter?` (substring) | `count`, `entries[]` (timestamp, level, category, message, exception) | Reads from `InMemoryLogStore` ring buffer (500 capacity). Filter matches message, category, or exception. Retry-safe. |
| `SHOW_CONFIG` | `section?` (must be in allowlist) | `sections{}`, `allowedSections[]` | Allowlisted sections: Logging, Cors, AllowedHosts, Copilot. Sensitive keys (secret, password, key, token, credential, connectionstring, certificate, passphrase, signing) masked as `***`. Retry-safe. |

**Infrastructure**: `InMemoryLogStore` (singleton ring buffer, 500 entries) + `InMemoryLogProvider` (ILoggerProvider) registered in `Program.cs`. Captures Information+ level logs.

**Evidence**: `src/AgentAcademy.Server/Commands/Handlers/{RunFrontendBuild,RunTypecheck,CallEndpoint,TailLogs,ShowConfig}Handler.cs`, `src/AgentAcademy.Server/Services/InMemoryLogSink.cs` — 31 tests in `Tier2BackendExecutionCommandTests.cs`.

#### Data & Operations
~~`QUERY_DB`~~, ~~`RUN_MIGRATIONS`~~, ~~`SHOW_MIGRATION_STATUS`~~, ~~`HEALTHCHECK`~~, ~~`SHOW_ACTIVE_CONNECTIONS`~~ *(all implemented — Phase 2F)*

##### Phase 2F — Data & Operations Commands

| Command | Args | Returns | Notes |
|---------|------|---------|-------|
| `QUERY_DB` | `query` (required), `limit?` (default 100, max 1000) | `columns`, `rows[]`, `rowCount`, `hasMore`, `truncated`, `limit` | **Human-only** (in-handler role gate). Opens a separate read-only SQLite connection via `SqliteConnectionStringBuilder(Mode=ReadOnly)`. Rejects non-SELECT statements (regex), PRAGMA writes, multiple statements (`;`), and denied tables (AgentMemories, NotificationConfigs, SystemSettings, AgentConfigs, InstructionTemplates). 10s timeout. Retry-safe. |
| `SHOW_MIGRATION_STATUS` | *(none)* | `appliedCount`, `pendingCount`, `applied[]`, `pending[]`, `isUpToDate` | All agents. Uses `Database.GetAppliedMigrationsAsync()` / `GetPendingMigrationsAsync()` on a scoped DbContext. Retry-safe. |
| `RUN_MIGRATIONS` | *(none, confirm=true required)* | `message`, `applied[]` | **Human-only** (in-handler role gate). **Destructive** with confirmation gate. Uses `Database.MigrateAsync()` on a scoped DbContext with process-wide `SemaphoreSlim` to prevent concurrent migrations. Pending check occurs inside the lock to avoid TOCTOU race. Async for human UI. |
| `HEALTHCHECK` | *(none)* | `status` (healthy/degraded), `checks{}`, `timestamp` | All agents. Checks: database connectivity, pending migration count, server uptime, active rooms/tasks, registered agent count, memory usage (working set + GC), active SignalR connections. Retry-safe. |
| `SHOW_ACTIVE_CONNECTIONS` | *(none)* | `count`, `instance`, `connections[]` (truncated connectionId, connectedAt, duration) | **Planner/Reviewer/Human** (in-handler role gate). Reads from `SignalRConnectionTracker` singleton. Connection IDs truncated to 8 chars. Current-instance only (not distributed). Retry-safe. |

**Infrastructure**: `SignalRConnectionTracker` (singleton, `ConcurrentDictionary`) registered in `Program.cs`. `ActivityHub.OnConnectedAsync`/`OnDisconnectedAsync` call the tracker. Connection data is in-memory, current-instance only — a distributed backplane would require a shared store.

**Evidence**: `src/AgentAcademy.Server/Commands/Handlers/{QueryDb,ShowMigrationStatus,RunMigrations,Healthcheck,ShowActiveConnections}Handler.cs`, `src/AgentAcademy.Server/Services/SignalRConnectionTracker.cs` — 35 tests in `Tier2DataOperationsCommandTests.cs`.

#### Worktree Management
~~`LIST_WORKTREES`~~ *(implemented)*, ~~`CLEANUP_WORKTREES`~~ *(implemented)*

#### Audit & Debug — IMPLEMENTED
~~`SHOW_AUDIT_EVENTS`~~ *(implemented — see Phase 2G table)*, ~~`SHOW_LAST_ERROR`~~ *(implemented — see Phase 2G table)*, ~~`TRACE_REQUEST`~~ *(implemented — see Phase 2G table)*, ~~`LIST_FEATURE_FLAGS`~~ *(renamed to `LIST_SYSTEM_SETTINGS`, implemented — see Phase 2G table)*, ~~`RETRY_FAILED_JOB`~~ *(implemented — see Phase 2G table)*

##### Phase 2G — Audit & Debug Commands

| Command | Description | Roles | Retry-Safe | Args |
|---------|-------------|-------|------------|------|
| `SHOW_AUDIT_EVENTS` | Query `activity_events` with filters (type, severity, actor, room, since, count). Returns events sorted newest-first. Default 20, max 100. | All agents | Yes | `type`, `severity`, `actorId`, `roomId`, `since` (ISO 8601), `count` |
| `SHOW_LAST_ERROR` | Merges errors from `activity_events` (Severity=Error, AgentErrorOccurred, CommandFailed, SubagentFailed) and `command_audits` (Status=Error) into a chronological timeline. Default 5, max 25. | All agents | Yes | `count`, `agentId` |
| `TRACE_REQUEST` | Traces events by `correlationId` across both `activity_events` and `command_audits`. Returns unified chronological timeline. | All agents | Yes | `correlationId` (required) |
| `LIST_SYSTEM_SETTINGS` | Returns all runtime system settings with current values and defaults via `ISystemSettingsService.GetAllWithDefaultsAsync()`. Renamed from `LIST_FEATURE_FLAGS` (spec roadmap name) — system settings is more accurate. | All agents | Yes | *(none)* |
| `RETRY_FAILED_JOB` | Re-executes a previously failed command from the audit trail. Only `IsRetrySafe` commands can be retried. Hard role gate (Planner/Human). Generates new correlationId for lineage tracking. Checks current agent permissions against target command. | Planner, Human | No | `auditId` (required) |

**Evidence**: `src/AgentAcademy.Server/Commands/Handlers/{ShowAuditEvents,ShowLastError,TraceRequest,ListSystemSettings,RetryFailedJob}Handler.cs` — 30 tests in `Tier2AuditDebugCommandTests.cs`.

**Database**: Migration `AddAuditEventIndexes` adds `idx_activity_correlation` (on `CorrelationId`) and `idx_activity_severity_time` (composite on `Severity`, `OccurredAt`) to `activity_events` table for query performance.

### Tier 3 — Quality of Life

#### Phase 3A — Spec Verification (Implemented)

| Command | Args | Returns | Implementation |
|---------|------|---------|----------------|
| `VERIFY_SPEC_SECTION` | `id` (section number or dir name) | Verified/broken path counts, broken path list, CLEAN/DRIFT_DETECTED status | `VerifySpecSectionHandler.cs` — extracts file path references from spec markdown, validates each against the filesystem |
| `COMPARE_SPEC_TO_CODE` | `id` (section number or dir name) | Claims list (file paths, handler classes, command names) with verified/broken status, accuracy percentage, declared spec status | `CompareSpecToCodeHandler.cs` — cross-references spec claims against codebase |
| `DETECT_ORPHANED_SECTIONS` | `id?` (optional filter) | Per-section orphan reports, total orphaned count, CLEAN/ORPHANS_DETECTED status | `DetectOrphanedSectionsHandler.cs` — scans all (or filtered) spec sections for broken file references |

**Evidence**: `src/AgentAcademy.Server/Commands/Handlers/VerifySpecSectionHandler.cs`, `src/AgentAcademy.Server/Commands/Handlers/CompareSpecToCodeHandler.cs`, `src/AgentAcademy.Server/Commands/Handlers/DetectOrphanedSectionsHandler.cs`, `src/AgentAcademy.Server/Commands/Handlers/SpecReferenceExtractor.cs`

Shared utility `SpecReferenceExtractor` handles markdown file path extraction (labeled paths, backtick paths, parenthetical paths), handler class name extraction, and path traversal-safe filesystem validation.

#### Frontend/UX
`PREVIEW_UI`, `CAPTURE_SCREENSHOT`, `COMPARE_SCREENSHOTS`, `SHOW_ROUTES`

#### Context — IMPLEMENTED

| Command | Args | Returns | Implementation |
|---------|------|---------|----------------|
| `HANDOFF_SUMMARY` | *(none)* | Agent identity, current location (room/breakout/state/workdir), assigned tasks with status/branch/phase, review queue filtered to calling agent, last 10 non-expired memories ordered by UpdatedAt, summary line | `HandoffSummaryHandler.cs` — read-only, retry-safe; uses `IAgentLocationService` (with `CommandContext` fallback), `ITaskQueryService`, `AgentAcademyDbContext` (`AsNoTracking` — does not mutate memory access metadata). Per-section try/catch for partial results. |
| `PLATFORM_STATUS` | *(none)* | Server health (uptime, version, instanceId, crashDetected, memory), executor status (operational, authFailed, circuitBreaker), agent locations, room counts by status, task counts by status, active sprint info, SignalR connection count, overall healthy/degraded status | `PlatformStatusHandler.cs` — read-only, retry-safe; aggregates `IAgentExecutor`, `IAgentCatalog`, `IAgentLocationService`, `IRoomService`, `ISprintService`, `AgentAcademyDbContext`, `SignalRConnectionTracker`. Per-section try/catch with `overallHealthy` flag degraded on any failure. |

~~`WHOAMI`~~ *(implemented — see Phase 2A table)*

**Evidence**: `src/AgentAcademy.Server/Commands/Handlers/HandoffSummaryHandler.cs`, `src/AgentAcademy.Server/Commands/Handlers/PlatformStatusHandler.cs`

## Agent Memory System

### Purpose
Persistent per-agent key-value store for learned knowledge across sessions.

### Implementation Status
**IMPLEMENTED** — All memory commands are live as of Phase 1A (commit `63b596c`, 2026-03-28). Memories persisted in `agent_memories` table, injected into agent prompts as `=== YOUR MEMORIES ===` section.

### Commands

| Command | Args | Returns | Implementation |
|---------|------|---------|----------------|
| `REMEMBER` | `category`, `key`, `value`, `ttl?` (hours) | Confirmation + expiresAt | `RememberHandler.cs` — upsert with validation against allowed categories |
| `RECALL` | `category?`, `key?`, `query?`, `include_expired?` | Matching memories (with stale flag) | `RecallHandler.cs` — FTS5-based search with BM25 ranking, LIKE fallback |
| `LIST_MEMORIES` | `category?`, `include_expired?` | All memories (filtered, with stale flag) | `ListMemoriesHandler.cs` — query all or by category |
| `FORGET` | `key` | Confirmation | `ForgetHandler.cs` — hard delete by key |
| `EXPORT_MEMORIES` | `include_expired?` | Full memory dump (JSON) | `ExportMemoriesHandler.cs` — all memories with metadata |
| `IMPORT_MEMORIES` | JSON payload (up to 500 entries) | Import summary | `ImportMemoriesHandler.cs` — validates categories, 500-char limit, upsert semantics |

**Evidence**: `src/AgentAcademy.Server/Commands/Handlers/{Remember,Recall,ListMemories,Forget}Handler.cs`

### Categories by Role

| Category | Used By | Example |
|----------|---------|---------|
| `decision` | Planner | "We chose SQLite over Postgres for simplicity" |
| `lesson` | All | "EF Core requires explicit Include() for navigation" |
| `pattern` | Architect, Engineer | "Services use constructor DI, never static" |
| `preference` | All | "User prefers conventional commits" |
| `invariant` | Reviewer | "All public APIs return ProblemDetails on error" |
| `risk` | Reviewer, Planner | "CopilotExecutor is single-user token model" |
| `gotcha` | Engineer | "Griffel doesn't support ::after with borders" |
| `incident` | All | "Deleted wrong room in session 0a4af9ed" |
| `constraint` | All | "No force-push, no branch delete" |
| `finding` | Reviewer | "BuildParticipants had N+1 query" |
| `spec-drift` | TechnicalWriter | "StubExecutor spec was aspirational, not factual" |
| `mapping` | Architect | "MessageEntity → ChatEnvelope via BuildChatEnvelope()" |
| `verification` | Reviewer | "Always check double-close on soft-deleted entities" |
| `gap-pattern` | All | "Frontend types often lag behind server models" |

### Interfaces

```csharp
public record AgentMemory(
    string AgentId,
    string Category,
    string Key,
    string Value,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    DateTime? LastAccessedAt = null,
    DateTime? ExpiresAt = null
);
```

**Persistence**: New `AgentMemoryEntity` table, keyed by `(AgentId, Key)`. Loaded into agent context at prompt-build time. Queryable via `RECALL`.

## Agent Self-Modification

### Model
Proposal-based with external approval. No agent can unilaterally modify itself or others.

### Command

```
PROPOSE_AGENT_UPDATE:
  Agent: <target-agent>
  Section: <startup-prompt|capabilities|tools|model>
  Change: <description of change>
  Rationale: <why this improves the agent>
```

### Approval Rules

| Change Type | Required Approvers |
|-------------|-------------------|
| Self-change | Socrates + Human |
| Change to another agent | Target agent + Socrates |
| Tighten standards | Socrates only |
| Relax standards | Human required |
| Modify Socrates' review standards | Human only |

### Audit
- Full diff of before/after
- Rationale recorded
- Rollback capability via `REVERT_AGENT_UPDATE: <change-id>`

## Human Command Execution API

### Purpose
Expose a subset of platform commands to authenticated human users via HTTP REST API. Enables the frontend "Commands tab" to execute read operations, builds, and tests without requiring an agent intermediary.

### Implementation Status
**IMPLEMENTED** — Human-triggered command execution via `POST /api/commands/execute` and async polling via `GET /api/commands/{correlationId}` (commit `0f32f17`, 2026-04-02).

### Architecture

#### Separation from Agent Pipeline
- **Agent commands**: Flow through `CommandPipeline` → text parser → `CommandAuthorizer` → handlers
- **Human commands**: HTTP request → `CommandController` → handlers directly (bypass parser/authorizer)

**Rationale**: Agent pipeline is designed for text-based agent responses. Human commands are already JSON. Controller-level allowlist is simpler than extending `CommandAuthorizer` with user identity concepts.

#### Command Allowlist (Week 1)

| Tier | Commands | Risk | Human Access |
|------|----------|------|--------------|
| **Read-only** | `READ_FILE`, `SEARCH_CODE`, `LIST_ROOMS`, `LIST_AGENTS`, `LIST_TASKS`, `LIST_WORKTREES`, `LIST_AGENT_STATS`, `SHOW_DIFF`, `GIT_LOG`, `SHOW_REVIEW_QUEUE`, `ROOM_HISTORY` | None | ✅ Allowed |
| **Side effects** | `RUN_BUILD`, `RUN_TESTS` | Compute cost, long-running | ✅ Allowed |
| **Room management** | `CREATE_ROOM`, `REOPEN_ROOM`, `CLOSE_ROOM`, `INVITE_TO_ROOM` | State mutation | ✅ Allowed |
| **Dangerous** | `SHELL`, `RESTART_SERVER`, `MERGE_TASK`, `RECALL_AGENT` | State mutation, git ops | ❌ Denied |
| **Agent-only** | `REMEMBER`, `RECALL`, `FORGET`, `DM`, `MOVE_TO_ROOM`, `CLAIM_TASK`, `SET_PLAN` | Identity mismatch | ❌ Denied |

**Week 1 scope: 11 commands.** All read-only + build/test.

### API Contract

#### Execute Command
```http
POST /api/commands/execute
Authorization: Cookie (authenticated session)
Content-Type: application/json

{
  "command": "READ_FILE",
  "args": {
    "path": "src/Program.cs",
    "startLine": 10,
    "endLine": 50
  }
}
```

**Response (synchronous commands)**:
```json
{
  "command": "READ_FILE",
  "status": "completed",
  "result": {
    "content": "...",
    "lines": 40
  },
  "error": null,
  "correlationId": "cmd-a1b2c3d4",
  "timestamp": "2026-04-02T01:00:00Z",
  "executedBy": "human"
}
```

**Response (async commands: RUN_BUILD, RUN_TESTS)**:
```json
{
  "command": "RUN_BUILD",
  "status": "pending",
  "result": null,
  "error": null,
  "correlationId": "cmd-e5f6g7h8",
  "timestamp": "2026-04-02T01:00:00Z",
  "executedBy": "human"
}
```
*Status: `202 Accepted`. Command executes in background.*

#### Poll Command Status
```http
GET /api/commands/{correlationId}
Authorization: Cookie (authenticated session)
```

**Response**:
```json
{
  "command": "RUN_BUILD",
  "status": "completed",
  "result": {
    "exitCode": 0,
    "output": "Build succeeded.",
    "success": true
  },
  "error": null,
  "correlationId": "cmd-e5f6g7h8",
  "timestamp": "2026-04-02T01:00:15Z",
  "executedBy": "human"
}
```

**Status values**: `"pending"`, `"completed"`, `"failed"`, `"denied"`

### Error Responses

| Status | Condition | Response Body |
|--------|-----------|---------------|
| `401 Unauthorized` | User not authenticated | `{ "code": "not_authenticated", "message": "..." }` |
| `403 Forbidden` | Command not in allowlist | `{ "code": "command_denied", "message": "..." }` |
| `400 Bad Request` | Missing/invalid payload | `{ "code": "invalid_command_request", "message": "..." }` |
| `404 Not Found` | CorrelationId not found in polling | `{ "code": "command_not_found", "message": "..." }` |

### Implementation Details

#### CommandController.cs
**File**: `src/AgentAcademy.Server/Controllers/CommandController.cs`

**Handler resolution**: Injects `IEnumerable<ICommandHandler>` and builds a dictionary keyed by `CommandName`, same pattern as `CommandPipeline` (line 46).

**Context creation**: Creates a synthetic `CommandContext` with:
- `AgentId`: `"human"`
- `AgentName`: `"Human"`
- `AgentRole`: `"Human"`
- `RoomId`: `null` (human commands have no room context)
- Uses a fresh service scope per request via `IServiceScopeFactory`

**Allowlist enforcement**: Static `HashSet<string>` (lines 25-38) checked before handler lookup. Non-allowlisted commands return `403 Forbidden`.

**Argument normalization**: Converts `Dictionary<string, JsonElement>` to `Dictionary<string, object?>` (lines 303-326). Only scalar JSON values allowed (string, number, boolean, null). Arrays/objects rejected to prevent handlers from misinterpreting complex types as strings.

**Async command handling**:
- Commands in `AsyncCommands` set (`RUN_BUILD`, `RUN_TESTS`) return `202 Accepted` immediately
- Creates a "Pending" audit row with the correlationId
- Fires handler on background thread via `Task.Run`
- Handler writes final result to same audit row when complete
- Polling endpoint reads from `CommandAuditEntity` by correlationId

**Concurrency control**: `RunBuildHandler` and `RunTestsHandler` use static `SemaphoreSlim(1,1)` to serialize execution. Prevents human and agent builds from conflicting.

#### Audit Trail
**Field**: `CommandAuditEntity.Source` (nullable string, line 13 of `CommandAuditEntity.cs`)

- Agent-invoked commands: `Source = null` (or could be set to `"agent"`)
- Human-invoked commands: `Source = "human-ui"`

**Field**: `CommandAuditEntity.ErrorCode` (nullable string)

- Persists the structured error category (`VALIDATION`, `NOT_FOUND`, `PERMISSION`, `CONFLICT`, `TIMEOUT`, `EXECUTION`, `INTERNAL`, `RATE_LIMIT`) from `CommandEnvelope.ErrorCode`
- Null on success or for pre-existing audit rows (column is nullable)
- Written by all audit paths: `CommandPipeline.AuditAsync` (agent), `CommandController.CreateAuditEntity` (human sync), `CommandController.UpdateAuditAsync` (human async)
- Read back by `CommandController.ToResponse(CommandAuditEntity)` for the polling endpoint

All human commands audit with:
- `AgentId = "human"`
- `Source = "human-ui"`
- `RoomId = null`

**Database migrations**:
- `20260402012749_AddCommandAuditSource.cs` adds `Source` column + index for efficient polling queries.
- `20260404083032_AddCommandAuditErrorCode.cs` adds `ErrorCode` column (nullable TEXT).

#### SystemController.cs
**Lines modified**: Advertises new command endpoints in system metadata.

### Handler Modifications
**Concurrency control added to build/test handlers.** Two handlers were modified to prevent concurrent execution:

- **RunBuildHandler.cs**: Added static `SemaphoreSlim BuildLock = new(1, 1)` (line 13). `WaitAsync` at line 40, `Release()` in finally block at line 71. Prevents overlapping builds from human UI and agent commands.
- **RunTestsHandler.cs**: Added static `SemaphoreSlim TestLock = new(1, 1)` (line 13). Same pattern as build handler. Prevents overlapping test runs.

All other handlers require no modifications. They accept `CommandContext` and return `CommandEnvelope` identically for human and agent invocation.

**Evidence**: `src/AgentAcademy.Server/Commands/Handlers/RunBuildHandler.cs` (lines 13, 40, 71), `src/AgentAcademy.Server/Commands/Handlers/RunTestsHandler.cs` (lines 13, 40, 71)

### Known Limitations

1. **No SignalR streaming**: Human commands do not stream live output. `RUN_BUILD` and `RUN_TESTS` require polling to see results. Acceptable for Week 1, but UX will feel sluggish for long-running commands.

2. ~~**No command metadata endpoint**~~: **RESOLVED** — `GET /api/commands/metadata` returns the full command catalog (`HumanCommandMetadata[]`) including title, category, description, detail, isAsync flag, and field schemas. Frontend fetches dynamically on mount with fallback to hardcoded catalog. Server-side `HumanCommandRegistry` is the single source of truth.

3. **No cancellation**: Once an async command starts, there is no API to cancel it. Build/test runs execute to completion even if user navigates away.

4. **Audit query performance**: Polling endpoint queries `command_audits` by `(CorrelationId, AgentId="human", Source="human-ui")`. Index exists, but polling every N seconds from multiple frontend tabs could generate query load. Consider adding an in-memory cache for pending commands.

## Private Communication (DM)

### Command
`DM` — Available to ALL agents. **IMPLEMENTED**.

Syntax (indented multi-line):
```
DM:
  Recipient: @Human
  Message: I need clarification on the database schema
```

Syntax (inline key=value):
```
DM: recipient=@Human message=I need clarification on the database schema
```

### Use Cases
- Escalation of blocking issues
- Architectural risk alerts
- UX ambiguity that needs human judgment
- Security concerns
- Spec disputes requiring human tiebreaker
- Review independence (Socrates flagging concerns privately)
- Platform feedback and feature requests
- Agent-to-agent private coordination

### Principles
- Default communication is open (in rooms)
- DMs are the exception, not the norm
- Audit metadata is logged (who, when, that a DM occurred) but content is not visible to other agents
- Human can see all DMs (agents are not told this)
- All DM recipients respond promptly (agents are triggered immediately, humans notified via Discord)

### Implementation
- **Handler**: `src/AgentAcademy.Server/Commands/Handlers/DmHandler.cs`
- **Data model**: `MessageEntity.RecipientId` (nullable) — null = room message, non-null = DM. `MessageKind.DirectMessage`. `MessageEntity.AcknowledgedAt` (nullable) — set when the recipient has seen the DM in a prompt.
- **Runtime methods**: `SendDirectMessageAsync`, `GetDirectMessagesForAgentAsync` (defaults `unreadOnly=true`, filtering to recipient-only unacknowledged DMs), `AcknowledgeDirectMessagesAsync` (takes explicit message IDs to prevent races), `GetDmThreadsForHumanAsync`, `GetDmThreadMessagesAsync` in `MessageService.cs`
- **Orchestrator**: `HandleDirectMessage(agentId)` triggers targeted agent round via extended `QueueItem(RoomId, TargetAgentId?)` queue. After building a prompt that includes DMs, all included message IDs are acknowledged so they don't repeat on the next round.
- **Context injection**: DMs injected as `=== DIRECT MESSAGES ===` section in agent prompts. Only unacknowledged DMs are shown to prevent duplication across rounds.
- **Breakout forwarding**: When a DM is sent to an agent in a breakout room, all unread DMs are posted as individual breakout messages and then acknowledged.
- **System notification**: "📩 {sender} sent a direct message to {recipient}." posted in recipient's room (audit metadata, no content).
- **Frontend**: Telegram-style DM panel (`DmPanel.tsx`) with conversation list + chat view. "Messages" tab in tab bar.
- **API**: `DmController.cs` — `GET /api/dm/threads`, `GET /api/dm/threads/{agentId}`, `POST /api/dm/threads/{agentId}`
- **Replaces ASK_HUMAN**: The `DM: Recipient: @Human` flow uses the same Discord notification bridge as the former `ASK_HUMAN` command.

## Safety & Operational Constraints

### Guardrails
- **Dry-run mode**: Side-effecting commands support `dryRun: true` returning what would happen
- **Confirmation**: Destructive commands require explicit `confirm=true` in args before execution. Without the flag, the pipeline returns `Denied` status with `CONFIRMATION_REQUIRED` error code and a structured response containing the warning, command name, and retry hint. Destructive handlers self-declare via `ICommandHandler.IsDestructive` (default false). Confirmation check runs after authorization but before rate limiting (unconfirmed commands don't consume rate-limit budget). Applies to both agent pipeline and human/consultant API. Destructive commands: `CLOSE_ROOM`, `CLEANUP_ROOMS`, `CLEANUP_WORKTREES`, `REJECT_TASK`, `CANCEL_TASK`, `RESTART_SERVER`, `FORGET`, `MERGE_TASK`. All agent `StartupPrompt` entries in `agents.json` document this flow with per-agent destructive command lists and the two-step `confirm=true` workflow.
- **Secret redaction**: All command output is scanned for secrets/tokens before logging
- **Idempotent mutations**: Write commands produce the same result when called twice with the same args

### Forbidden Actions
- No `git push --force`
- No branch deletion
- No production data mutation
- No credential access outside approved config paths
- No execution of arbitrary shell commands (only approved build/test commands)

## Implementation Plan

### Phase 1 — Foundation (Tier 1A + Pipeline)
**New files:**
- `src/AgentAcademy.Server/Commands/CommandEnvelope.cs` — Envelope types
- `src/AgentAcademy.Server/Commands/CommandParser.cs` — Extract commands from agent text
- `src/AgentAcademy.Server/Commands/CommandPipeline.cs` — Auth → Execute → Audit pipeline
- `src/AgentAcademy.Server/Commands/Handlers/` — One handler per command group

**Modified files:**
- `AgentTurnRunner.cs` — After receiving agent response, run through command parser before posting to room
- Domain service files — Expose read methods needed by LIST_* commands

**New entities:**
- `CommandAuditEntity` — Audit trail for every command execution
- `AgentMemoryEntity` — Per-agent persistent memory (Phase 1, Week 1 Day 5)

### Phase 2 — State Management (Tier 1B)
- Spec-task linking (new junction table) — **IMPLEMENTED**: `SpecTaskLinkEntity` junction table, `LINK_TASK_TO_SPEC` and `SHOW_UNLINKED_CHANGES` commands, `SpecManager.LoadSpecContextForTaskAsync` for filtered spec loading, REST endpoints
- Review workflow commands (APPROVE/REQUEST_CHANGES) — **IMPLEMENTED**
- Task claiming (optimistic locking to prevent duplicate work) — **IMPLEMENTED**: `CLAIM_TASK` / `RELEASE_TASK` commands

### Phase 3 — Verification + Communication (Tier 1C + 1D) — IMPLEMENTED
- Build/test execution — **IMPLEMENTED**: `RUN_BUILD`, `RUN_TESTS`, `SHOW_DIFF`, `GIT_LOG` handlers with workspace-scoped process execution
- DM system — **IMPLEMENTED**: extends MessageEntity with RecipientId, adds DirectMessage kind, DmHandler, orchestrator integration, frontend DM panel
- Room history read — **IMPLEMENTED**: `ROOM_HISTORY` handler returns paginated message history for any room
- Evidence ledger — **IMPLEMENTED**: `RECORD_EVIDENCE`, `QUERY_EVIDENCE`, `CHECK_GATES` for structured verification tracking

### Phase 4 — Navigation + Room Management (Tier 1E + 1G) — IMPLEMENTED
- Agent self-navigation — **IMPLEMENTED**: `MOVE_TO_ROOM`, `RETURN_TO_MAIN` handlers for agent room transitions
- Room management — **IMPLEMENTED**: `CREATE_ROOM`, `ARCHIVE_ROOM`, `REOPEN_ROOM`, `INVITE_TO_ROOM`, `ROOM_TOPIC` handlers for full room lifecycle

### Post-MVP
- Tier 2 & 3 rollout
- RBAC middleware extraction
- Spec verification tooling

## Frontend Surfaces

**Status**: IMPLEMENTED. Command Palette (`Cmd+K`), Commands tab, audit log panel, and task panel all provide frontend access to the command system.

### Command Palette (Primary) — IMPLEMENTED
Keyboard-driven command search and execution overlay, opened with `Cmd+K` / `Ctrl+K`.

**Features**:
- Text search across command title, name, description, and category
- Commands grouped by category (workspace, code, git, operations)
- Keyboard navigation: ↑/↓ to move, Enter to select, Esc to close/back
- Detail view with field inputs for command arguments
- Execute with `Cmd+Enter` / `Ctrl+Enter`, inline result display
- Async command polling for long-running operations (RUN_BUILD, RUN_TESTS)
- Dynamic catalog from `GET /api/commands/metadata` with hardcoded fallback

**Required for**: Discovery (knowing what commands exist), inspection (seeing what agents did), debugging (understanding command failures).

### Task Panel (Dedicated) — IMPLEMENTED
Purpose-built UI for structured task state management, integrated into the existing `TaskListPanel`.

**Features**:
- Review queue filter (tasks in InReview, AwaitingValidation, Approved, ChangesRequested)
- Spec links section in task detail: fetched from `GET /api/tasks/{taskId}/specs`, displays section ID, link type (Implements/Modifies/Fixes/References), linked-by agent, and optional note
- Evidence ledger section: on-demand load via `QUERY_EVIDENCE` command, displays verification checks in a table with phase, check name, pass/fail, tool, and agent
- Gate status check: on-demand via `CHECK_GATES` command for Active/AwaitingValidation/InReview tasks, shows met/unmet status with missing check names
- Agent assignment for Queued tasks: agent picker using `PUT /api/tasks/{taskId}/assign`
- Detail caching: spec links, evidence, gate results, and comments cached by task ID + updatedAt timestamp to avoid refetching on collapse/expand
- Review actions: Approve, Request Changes, Reject, Merge (unchanged from prior implementation)

**Evidence**: `src/agent-academy-client/src/TaskListPanel.tsx`, `src/agent-academy-client/src/api.ts` (types: `SpecTaskLink`, `TaskEvidence`, `GateCheckResult`; function: `getTaskSpecLinks`)

**Required for**: Task-specific commands (APPROVE_TASK, REQUEST_CHANGES, CLAIM_TASK via agent assignment).

### Room Sidebar (Navigation) — EXISTS (enhanced planned)
Agent workspaces and room navigation affordances exist. Command feedback integration planned.

**Required for**: Navigation commands (MOVE_TO_ROOM, RETURN_TO_MAIN).

### Design Principle (Not Yet Applied)
Minimal surfaces should ship with the commands they support — not as a separate phase. When backend commands are ready, corresponding UI affordances should be added in the same PR. Phase 1A violated this principle by shipping backend-only due to time constraints.

**Note**: Authorization and task-state commands (MERGE_TASK, APPROVE_TASK, etc.) shipped with backend enforcement before dedicated UI surfaces (task panel). This is acceptable when agent-initiated commands don't require human UI — the "ship together" principle applies to human-facing features, not agent-only workflows.

## Invariants

- Every command execution produces an audit event (no silent operations)
- Read commands never produce side effects beyond audit logging
- Authorization check precedes execution for every command
- Rate limiting check follows authorization — agents exceeding 30 commands per 60 seconds are rejected with `RATE_LIMIT` error code
- Command envelope shape is stable from day 1 — new commands extend `args`/`result`, never change the envelope
- Agent permissions are configured, not hardcoded — stored in agent catalog
- Memory system is per-agent isolated — agents cannot read each other's memories
- Self-modification requires external approval — no agent can unilaterally change its own configuration

## Known Gaps

- ~~**Command discovery**~~: **Resolved** — `LIST_COMMANDS` handler returns all available commands with descriptions and per-agent authorization status. Agents also receive commands in their startup prompts.
- ~~**Error recovery**~~: **Resolved** — CopilotExecutor has exponential backoff retries (transient: 2s/4s/8s, 3 attempts; quota: 5s/15s/30s, 3 attempts) and a global circuit breaker (trips after 5 consecutive failures, 60s cooldown before probing). Structured error codes (`errorCode` field) enable agents to make programmatic retry/skip decisions. Pipeline-level retry: `CommandPipeline.ExecuteWithRetryAsync` automatically retries commands that opt in via `ICommandHandler.IsRetrySafe` (up to 3 attempts with 1s/2s exponential backoff). Only `TIMEOUT` and `INTERNAL` error codes trigger retry — `RATE_LIMIT` is excluded (policy, not transient). 19 read-only and idempotent handlers are marked retry-safe. Non-safe handlers (CREATE_*, DM, COMMIT_*, RUN_BUILD, RUN_TESTS, SHELL, RECALL_AGENT, ROOM_TOPIC, destructive commands) execute exactly once; agents must re-issue manually. `CommandEnvelope.RetryCount` reports attempt count to agents. 9 tests.
- ~~**Rate limiting**~~: **Resolved** — Per-agent sliding-window rate limiter. Defaults: 30 commands per 60 seconds. Implemented in `CommandRateLimiter`, integrated into `CommandPipeline` after authorization. Returns `RATE_LIMIT` error code with retry-after hint. Human UI commands (via `CommandController`) are not rate-limited. Limits are runtime-configurable via `PUT /api/settings` with keys `commands.rateLimitMaxCommands` and `commands.rateLimitWindowSeconds`. Changes take effect immediately (no restart needed). Persisted in `system_settings` table and loaded on startup.
- ~~**Frontend surfaces**~~: **Resolved** — Commands tab with dynamic catalog from `GET /api/commands/metadata`. Command palette (Cmd+K) with search, keyboard navigation, and inline execution. Command audit log panel on Dashboard. Task panel enhanced with spec links, evidence ledger, gate status, and agent assignment UI.
- ~~**Tier 2 room commands**~~: **Resolved** — All room lifecycle commands are implemented (`CLOSE_ROOM`, `CREATE_ROOM`, `REOPEN_ROOM`, `INVITE_TO_ROOM`, `RETURN_TO_MAIN`, `ROOM_TOPIC`). `RESTORE_ROOM` was consolidated into `REOPEN_ROOM` (same functionality). `LIST_ROOMS` supports optional `status=` filter with validation. Room commands are now exposed in the command metadata endpoint.

## Discord Agent Question Bridge

### Purpose
Enables any agent to send a direct message to the human via Discord and receive a routed reply. Designed to unblock agents that need human input (clarification, decisions, credentials). Now accessed via the `DM` command (replaces the deprecated `ASK_HUMAN`).

### Architecture
```
Discord Server
├── #agent-academy              (main notification channel — unchanged)
├── 📁 {workspace-name}         (category per workspace/room)
│   ├── #{agent-name}           (channel per agent)
│   │   └── Thread: "{question}"  (thread per question)
```

### Flow
1. Agent uses `DM:` command with `Recipient: @Human` and `Message: <text>` args
2. `DmHandler` stores the DM, then delegates to `NotificationManager.SendAgentQuestionAsync`
3. `DiscordNotificationProvider` lazily creates category → channel → thread
4. Message posted as embed in the thread
5. Persistent `MessageReceived` handler routes human replies back to the agent's room via `MessageService.PostHumanMessageAsync`
6. Agent sees reply as a human message in its next orchestration round
7. Human can also reply via the frontend DM panel (`DmPanel.tsx`)

### Implementation Status
**IMPLEMENTED** — `DM` command replaces `ASK_HUMAN`. Handler (`DmHandler.cs`), Discord bridge, reply routing, and frontend DM panel are implemented. All agents have `DM` permission.

**Evidence**: `src/AgentAcademy.Server/Commands/Handlers/DmHandler.cs`, `src/AgentAcademy.Server/Notifications/DiscordNotificationProvider.cs`, `src/AgentAcademy.Server/Controllers/DmController.cs`, `src/agent-academy-client/src/DmPanel.tsx`

## Revision History

| Date | Change | Task | Commit |
|------|--------|------|--------|
| 2026-04-05 | Command audit log: `GET /api/commands/audit` (paginated, filterable) and `GET /api/commands/audit/stats` (aggregates by status/agent/command). AuditLogPanel on Dashboard. 15 backend + 19 frontend tests. | command-audit-log | — |
| 2026-03-28 | Initial spec from agent team feature request v3 | agent-command-system | — |
| 2026-03-28 | Implemented Phase 1A: envelope, parser, pipeline, authorization, audit, read handlers (LIST_ROOMS, LIST_AGENTS, LIST_TASKS, READ_FILE, SEARCH_CODE), memory handlers (REMEMBER, RECALL, LIST_MEMORIES, FORGET) | command-system-phase1 | `63b596c` |
| 2026-03-28 | Added command reference to agent startup prompts | command-discoverability | `6117b4e` |
| 2026-03-28 | Reconciled frontend surface contradiction: Phase 1A shipped backend-only, no UI surfaces implemented. Documented 9 live commands with implementation evidence. Updated Known Gaps to reflect backend-only state. | spec-007-reconciliation | (this change) |
| 2026-03-29 | Implemented ASK_HUMAN command: Discord agent-to-human question bridge with category-per-workspace, channel-per-agent, thread-per-question architecture. Persistent reply routing via MessageService. | ask-human-command | (this change) |
| 2026-03-30 | Implemented DM command (Phase 1D), replacing ASK_HUMAN. Agent-to-agent and agent-to-human private messaging. MessageEntity.RecipientId + DirectMessage kind. Orchestrator HandleDirectMessage with targeted rounds. System notification in recipient's room. Frontend Telegram-style DM panel. DM API endpoints. 18 tests. | dm-command | (this change) |
| 2026-03-30 | Implemented Phase 1C (RUN_BUILD, RUN_TESTS, SHOW_DIFF, GIT_LOG), ROOM_HISTORY (1D), MOVE_TO_ROOM (1E). All agent timeouts removed — no per-turn LLM timeout, no breakout round cap, no fix round cap. Breakout rooms are open-ended (agents work until WORK REPORT: COMPLETE). DMs delivered to agents in breakout rooms. Task workspace scoping fix. | commands-and-breakout-redesign | (this change) |
| 2026-04-02 | Added Human Command Execution API: `POST /api/commands/execute` and `GET /api/commands/{correlationId}` for Week 1 allowlist (11 commands: all read-only + RUN_BUILD/RUN_TESTS). CommandController bypasses agent pipeline, uses controller-level allowlist + cookie auth. Async commands return 202 + polling. Added CommandAuditEntity.Source field. Build/test handlers serialized via SemaphoreSlim. | implement-frontend-command-execution-api | (this change) |
| 2026-04-04 | Implemented planner-only `CLOSE_ROOM`. Non-main rooms can now be archived when empty; the runtime sets `RoomEntity.Status = Archived` and emits a `RoomClosed` activity event. Updated permission docs and reconciled the confirmation guardrail to match shipped behavior. | close-room-command | (this change) |
| 2026-04-04 | Verified MERGE_TASK Planner/Reviewer role authorization enforcement. Handler code (lines 25-31) guards against unauthorized access. Updated spec table to include implementation reference and clarified design principle scope for agent-initiated commands. | merge-task-authorization-enforcement | `52419d8` |
| 2026-04-04 | Added `ErrorCode` column to `CommandAuditEntity`. Async command polling and audit history now return structured error codes. All 4 audit write paths and the read path updated. Migration `20260404083032_AddCommandAuditErrorCode`. | errorcode-audit-persistence | `5fd74b3` |
| 2026-04-04 | Per-agent command rate limiting (30 commands/60s sliding window). `CommandRateLimiter` integrated into `CommandPipeline` after authorization. `RATE_LIMIT` error code added. 6 new tests. | command-rate-limiting | `df07581` |
| 2026-04-04 | Implemented `INVITE_TO_ROOM` (Phase 1G). Planners/humans can move agents to rooms. Validates room exists/not archived, agent exists/not in breakout. No-op if already in room. System message posted. Added to human command allowlist. 12 new tests. | invite-to-room | (this change) |
| 2026-04-04 | Implemented `RETURN_TO_MAIN` (Phase 1E). Any agent can return to the main collaboration room. Syntactic sugar for MOVE_TO_ROOM with DefaultRoomId. 3 new tests. | return-to-main | (this change) |
| 2026-04-04 | Implemented `ROOM_TOPIC` (Phase 1G). Any agent can set/clear a room's topic. DB migration adds `Topic` column. `RESTORE_ROOM` consolidated into `REOPEN_ROOM`. All Tier 2 room commands now implemented. 5 new tests. | room-topic | (this change) |
| 2026-04-04 | `REJECT_TASK` command added (Tier 2 Task Management). Reverts Approved/Completed → ChangesRequested, reverts merge if needed, reopens breakout. Role-gated. `APPROVE_TASK` and `REQUEST_CHANGES` also got role gates. Review round limit (5) enforced. | reject-task | (this change) |
| 2026-04-04 | Rate limit runtime configuration. `CommandRateLimiter.Configure()` method + settings keys (`commands.rateLimitMaxCommands`, `commands.rateLimitWindowSeconds`). Live-reconfigured via `PUT /api/settings`. | rate-limit-config | (this change) |
| 2026-04-04 | Command metadata endpoint: `GET /api/commands/metadata` returns `HumanCommandMetadata[]` from `HumanCommandRegistry`. Filters by allowlist + handler existence. Frontend loads dynamically with hardcoded fallback. Resolves known gap #2. 13 new backend tests, 3 new frontend tests. | command-metadata-endpoint | (this change) |
| 2026-04-07 | Evidence ledger commands: RECORD_EVIDENCE, QUERY_EVIDENCE, CHECK_GATES added to Phase 1C (Verification). Records structured verification checks against tasks with phase (Baseline/After/Review), check names, tool, command, exit code, output. CHECK_GATES evaluates minimum evidence for status transitions. All 6 agents permitted. Human API allowlist updated. Permission model table updated. 23 new tests (1375 total). | evidence-ledger | `42d4124` |
| 2026-04-05 | Spec-task linking (Phase 2): `SpecTaskLinkEntity` junction table, `LINK_TASK_TO_SPEC` and `SHOW_UNLINKED_CHANGES` commands, `SpecManager.LoadSpecContextForTaskAsync` for task-filtered spec loading, REST endpoints `GET /api/tasks/{id}/specs` and `GET /api/specs/{sectionId}/tasks`, cascade delete, unique constraint, `SpecTaskLinked` activity event. 36 new tests. | spec-task-linking | (this change) |
| 2026-04-12 | Pipeline-level command retry: `ExecuteWithRetryAsync` retries retry-safe commands on `TIMEOUT`/`INTERNAL` errors (up to 3 attempts, 1s/2s exponential backoff). `ICommandHandler.IsRetrySafe` opt-in property. 19 read-only/idempotent handlers marked safe. `CommandEnvelope.RetryCount` added. `FormatResultsForContext` includes retry count. `RATE_LIMIT` intentionally excluded from pipeline retry. Error recovery known gap resolved. 9 new tests. | pipeline-retry | (this change) |
| 2026-04-13 | Worktree management commands (Phase 1H): `LIST_WORKTREES` (read-only, retry-safe) returns active worktrees with task/agent enrichment and optional `status` filter. `CLEANUP_WORKTREES` (destructive, Planner/Human-only) removes stale worktrees for completed/cancelled tasks, with `includeOrphans` option. Added to destructive commands list and read-only allowlist. 17 new tests. | worktree-agent-commands | (this change) |
| 2026-04-13 | Agent stats command + reviewer capabilities: `LIST_AGENT_STATS` (read-only, retry-safe) surfaces per-agent task effectiveness metrics (completion rate, cycle time, review rounds, first-pass approval, rework rate) for data-driven task assignments. Filters by `agentId` (name or ID) and `hoursBack`. Added to human/consultant allowlist and HumanCommandRegistry (analytics category). Reviewer agent (Socrates) granted `code` SDK tool group + `READ_FILE`, `SHOW_DIFF`, `SEARCH_CODE`, `GIT_LOG`, `RUN_BUILD`, `RUN_TESTS` command permissions — fixes gap where startup prompt documented commands the reviewer couldn't execute. Engineer prompts updated with worktree awareness. Planner prompt updated with `LIST_AGENT_STATS` documentation. 16 new tests. | agent-stats-command | (this change) |
| 2026-04-17 | §Permission Model table reconciled with `agents.json` (closes #70). Prior table understated grants by 20+ commands per agent (Aristotle missing sprint/PR/build/test/worktree management; Socrates missing PRs/build/test; Hephaestus/Athena missing `COMMIT_CHANGES`/PR commands; Archimedes/Thucydides "spec commands" claim replaced with actual read-only grant). Added SDK tool-group column (`EnabledTools`) and an explicit note that `agents.json` is authoritative. Flagged Thucydides missing `code-write` as a known operational gap (spec writes currently route through humans/engineers). | agents-permissions-70 | (this change) |
| 2026-04-17 | Thucydides granted `spec-write` SDK tool group — closes the known gap where spec authorship had to route through humans/engineers. New tool group exposes the same `write_file` and `commit_changes` tools as `code-write` but the `CodeWriteToolWrapper` is parameterised over `allowedRoot` so spec-write rejects any path outside `specs/`. Registered in `AgentToolRegistry.ContextualGroups` alongside `code-write`, wired to `IAgentToolFunctions.CreateSpecWriteTools`. Thucydides still does not hold `code-write`, preserving the engineer/writer separation. Defence-in-depth: (a) symlink escape detection — any write whose path passes through a symlinked directory under the allowed root is rejected; (b) commit scope enforcement — `commit_changes` refuses to commit if any currently staged file is outside the wrapper's `allowedRoot` or matches a protected-file rule, preventing piggy-back commits of `src/` changes via a spec-write call. 19 new tests (`SpecWriteToolWrapperTests` covering scope rejection, input validation, symlink escape, commit-scope enforcement; registry tests for `spec-write` resolution and tool-name de-dup). | thucydides-specs-write | (this change) |
| 2026-04-18 | Spec sync: `spec-write` scope text reconciled with implementation. Spec previously said "writes restricted to `specs/` only", but `IAgentToolFunctions.CreateSpecWriteTools` configures `allowedRoots: { "specs", "docs" }` and `CodeWriteToolWrapper` accepts a list of allowed roots. Permission Model table, supporting note, and the EnabledTools enumeration now state `specs/` and `docs/` for the `spec-write` group. No code change. | spec-write-docs-scope-sync | (this change) |
| 2026-04-20 | Goal card commands: `CREATE_GOAL_CARD` and `UPDATE_GOAL_CARD_STATUS` added to Phase 1B (Structured State Management). Structured intent artifacts that agents create before starting significant work — captures task vs. intent analysis, steelman/strawman arguments, verdict, and fresh-eyes questions. Immutable content with validated status state machine (Active → Completed/Challenged/Abandoned; Challenged → Active/Abandoned). All 6 agents granted both commands. Permission Model table updated. | spec-goal-cards | `37d2bd4` |
| 2026-04-20 | Tier 2 Communication commands (Phase 2B): `MENTION_TASK_OWNER` sends a DM to the agent assigned to a task with task context prefix, wakes recipient via orchestrator. `BROADCAST_TO_ROOM` posts an agent-attributed message to any room (cross-room, no membership required), rejects Archived rooms. Both commands granted to all 6 agents. Also registered 11 previously missing handlers in `CommandParser.KnownCommands` (Tier 2A + goal cards + list commands). 15 new tests. | tier2-communication-commands | (this change) |
| 2026-04-20 | Tier 2C Task Management commands: `MARK_BLOCKED` transitions task to Blocked with reason and records Blocker comment. `SHOW_DECISIONS` surfaces Decision-typed comments. Both reject terminal/merge-workflow states. All 6 agents granted. 23 new tests. | tier2-task-management-commands | (this change) |
| 2026-04-21 | Tier 2D Code & Spec commands (Phase 2D): `OPEN_SPEC` reads spec sections by ID with numeric prefix resolution via `ISpecManager`. `SEARCH_SPEC` searches spec files via `git grep` scoped to `specs/`. `OPEN_COMPONENT` finds and reads source files by component name via `git ls-files` (avoids build outputs). `FIND_REFERENCES` searches for symbol usages via fixed-string `git grep -F` with whole-word matching, scoped to `src/`. All 4 commands are read-only, retry-safe, granted to all 6 agents. 30 new tests. | tier2-code-spec-commands | (this change) |
| 2026-04-21 | Tier 2E Backend Execution commands (Phase 2E): `RUN_FRONTEND_BUILD`, `RUN_TYPECHECK`, `CALL_ENDPOINT`, `TAIL_LOGS`, `SHOW_CONFIG`. See Phase 2E table. 33 new tests. | tier2-backend-execution | (this change) |
| 2026-04-21 | Tier 2F Data & Operations commands (Phase 2F): `QUERY_DB` (Human-only, read-only SQLite connection with `SqliteConnectionStringBuilder(Mode=ReadOnly)`, table denylist, statement filtering, 10s timeout). `SHOW_MIGRATION_STATUS` (all agents, applied/pending migration IDs). `RUN_MIGRATIONS` (Human-only, destructive with confirmation, process-wide semaphore lock, TOCTOU-safe pending check inside lock). `HEALTHCHECK` (all agents, checks DB/uptime/entities/resources/SignalR). `SHOW_ACTIVE_CONNECTIONS` (Planner/Reviewer/Human, reads `SignalRConnectionTracker` singleton, truncated connection IDs, current-instance only). New infrastructure: `SignalRConnectionTracker` (ConcurrentDictionary singleton) wired into `ActivityHub.OnConnectedAsync`/`OnDisconnectedAsync`. Adversarial review caught 4 issues (hasMore loop bug, leaked DI scope, read-only mode bypass, migration TOCTOU race) — all fixed. 35 new tests. KnownCommands: 85 → 90. | tier2-data-operations | (this change) |
| 2026-04-21 | Tier 2G Audit & Debug commands (Phase 2G): `SHOW_AUDIT_EVENTS` queries `activity_events` with type/severity/actor/room/since/count filters. `SHOW_LAST_ERROR` merges errors from `activity_events` (Severity=Error, error event types) and `command_audits` (Status=Error) chronologically. `TRACE_REQUEST` traces by correlationId across both tables. `LIST_SYSTEM_SETTINGS` returns runtime settings with defaults via `ISystemSettingsService`. `RETRY_FAILED_JOB` re-executes failed retry-safe commands with hard Planner/Human role gate, permission check, and new correlationId lineage. Renamed `LIST_FEATURE_FLAGS` to `LIST_SYSTEM_SETTINGS`. Migration adds `idx_activity_correlation` and `idx_activity_severity_time` indexes. 30 new tests. KnownCommands: 90 → 95. | tier2-audit-debug | (this change) |
| 2026-04-21 | Tier 3B Context commands (Phase 3B): `HANDOFF_SUMMARY` generates structured agent state snapshot for handoff — identity, location (IAgentLocationService with CommandContext fallback), assigned tasks filtered to calling agent, review queue filtered to calling agent, last 10 non-expired memories via AsNoTracking (does not mutate LastAccessedAt), summary line. `PLATFORM_STATUS` returns comprehensive platform status — server health, executor state, agent locations, room/task counts grouped by actual status, active sprint via IRoomService workspace resolution, SignalR connections, overall healthy/degraded flag with per-section try/catch for partial results. Adversarial review caught 2 issues (false-healthy on section failure, room status mislabeling) — both fixed. All 6 agents granted both commands. 27 new tests. KnownCommands: 98 → 100. | tier3b-context-commands | (this change) |
