# 007 — Agent Command System

## Purpose
Defines a unified command pipeline through which agents interact with the platform, codebase, and each other. Every agent action — reading files, moving between rooms, sending messages, managing tasks — flows through a structured envelope with authorization, audit trails, and consistent error handling.

> **Status: Implemented (Phase 1A)** — Command envelope, parser, pipeline, authorization, audit trail, and Phase 1A handlers (LIST_ROOMS, LIST_AGENTS, LIST_TASKS, READ_FILE, SEARCH_CODE) are implemented. Memory commands (REMEMBER, RECALL, LIST_MEMORIES, FORGET) are implemented. Runs in parallel with existing free-text parsing.

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
Agent Response (text) → Command Parser → Authorization → Execution → Audit Event → Response
```

Each stage:
1. **Command Parser**: Extracts structured commands from agent text. Commands use `COMMAND_NAME: args` syntax in agent responses. Parser returns remaining text + extracted commands.
2. **Authorization**: Checks agent permissions against command + args. Returns `denied` if unauthorized.
3. **Execution**: Dispatches to the appropriate handler. Read commands are side-effect-free. Write commands are idempotent where possible.
4. **Audit Event**: Every command execution is recorded as an `ActivityEvent` with the full envelope.
5. **Response**: Result is injected into the agent's next context turn.

### Permission Model

Per-agent permission boundaries:

| Agent | Role | Allowed | Denied |
|-------|------|---------|--------|
| Aristotle | Planner | All task/room management, read all, `RECALL_AGENT`, `CLOSE_ROOM`, `CREATE_ROOM`, `REOPEN_ROOM`, `ADD_TASK_COMMENT`, allowlisted `SHELL` operations | Arbitrary code execution |
| Archimedes | Architect | Read all, spec commands, `ADD_TASK_COMMENT` | Code execution |
| Hephaestus | SoftwareEngineer | File read/write, build, test, git, `ADD_TASK_COMMENT` | Spec write, task approve |
| Prometheus | SoftwareEngineer | Same as Hephaestus | Same |
| Socrates | Reviewer | Read all, approve/reject tasks, `ADD_TASK_COMMENT`, allowlisted `SHELL` operations | File write, arbitrary code execution |
| Thucydides | TechnicalWriter | Spec read/write, file read, `ADD_TASK_COMMENT` | Code execution, task approve |

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

#### Phase 1B: Structured State Management

| Command | Args | Returns | Side Effects | Implementation |
|---------|------|---------|-------------|----------------|
| `ASK_HUMAN` | — | — | — | **DEPRECATED** — replaced by `DM` command. Use `DM: Recipient: @Human` instead. |
| `LINK_TASK_TO_SPEC` | `taskId`, `specSection` | Confirmation + link record | Creates bidirectional link |
| `SHOW_UNLINKED_CHANGES` | `since?` | Tasks/commits without spec links | Audit event |
| `APPROVE_TASK` | `taskId`, `findings?` | Confirmation | Updates task status, records reviewer | `ApproveTaskHandler.cs` — validates InReview/AwaitingValidation state, sets Approved, records reviewer, increments ReviewRounds, posts findings as review message |
| `REQUEST_CHANGES` | `taskId`, `findings` | Confirmation | Updates task status, creates feedback | `RequestChangesHandler.cs` — validates InReview/AwaitingValidation state, sets ChangesRequested, records reviewer, increments ReviewRounds, posts findings as review message |
| `SHOW_REVIEW_QUEUE` | — | Tasks awaiting review | Audit event | `ShowReviewQueueHandler.cs` — queries tasks with InReview or AwaitingValidation status, returns summary list |
| `CLAIM_TASK` | `taskId` | Confirmation | Assigns agent, prevents duplicate work | `ClaimTaskHandler.cs` — validates no other claimant, assigns calling agent, auto-activates Queued tasks |
| `RELEASE_TASK` | `taskId` | Confirmation | Unassigns agent | `ReleaseTaskHandler.cs` — validates calling agent is current assignee, clears assignment |
| `UPDATE_TASK` | `taskId`, `status?`, `blocker?`, `note?` | Confirmation | Updates task state | `UpdateTaskHandler.cs` — validates allowed statuses (Active/Blocked/AwaitingValidation/InReview/Queued), handles blocker→Blocked shorthand, posts notes to task room |
| `SET_PLAN` | `content` | Confirmation + target room ID | Writes markdown plan content for the caller's current room or breakout room | `SetPlanHandler.cs` — validates non-empty content and active room context, then calls `WorkspaceRuntime.SetPlanAsync` |
| `ADD_TASK_COMMENT` | `taskId`, `type?` (Comment\|Finding\|Evidence\|Blocker, default: Comment), `content` | Comment ID and confirmation | Validates task exists, caller is assignee/reviewer/planner. Creates `TaskCommentEntity`, posts activity event | |
| `RECALL_AGENT` | `agentId` (name or ID) | Agent info and room transition details | Validates caller has Planner role, target agent is in Working state in a breakout room. Closes breakout room, moves agent to Idle in parent room. Posts recall notices to both breakout and parent rooms | |
| `CLOSE_ROOM` | `roomId` | Room ID, room name, archived status | Validates caller has Planner role, target room exists, target is not the workspace's main collaboration room, and the room has no active participants. Sets `RoomEntity.Status = Archived`, updates `UpdatedAt`, and publishes a `RoomClosed` activity event. Repeating the command against an already archived room is a no-op success. | `CloseRoomHandler.cs` + `WorkspaceRuntime.CloseRoomAsync()` |
| `MERGE_TASK` | `taskId` | Task ID, title, branch, `mergeCommitSha` | Validates caller is Reviewer or Planner, task status is Approved, task has BranchName. Sets task to Merging status, squash-merges task branch to develop, runs `git add -A` to stage the full squash result, then commits using conventional-commit subject `{prefix}{task.Title}` where `Feature -> feat: `, `Bug -> fix: `, `Chore -> chore: `, and `Spike -> docs: `. On success: updates task to Completed, records merge commit SHA on TaskEntity, and returns it in the result. On conflict: aborts the merge, restores task status to Approved, and returns an error. Authorization enforced at handler level. | `MergeTaskHandler.cs` lines 25-31 — Planner/Reviewer role guard |

#### Phase 1C: Verification — IMPLEMENTED

| Command | Args | Returns | Side Effects | Implementation |
|---------|------|---------|-------------|----------------|
| `RUN_BUILD` | — | Build output, exit code | Runs `dotnet build` | `RunBuildHandler.cs` — 10min safety timeout, output truncated to 3KB |
| `RUN_TESTS` | `scope?` (`all`, `frontend`, `backend`, `file:path`) | Test output, exit code | Runs test suite | `RunTestsHandler.cs` — routes frontend to `npm test`, backend to `dotnet test` |
| `SHOW_DIFF` | `branch?` | Git diff output | Audit event | `ShowDiffHandler.cs` — `git diff --stat -p`, optional branch comparison |
| `GIT_LOG` | `file?`, `since?`, `count?` | Commit history (sha + message) | Audit event | `GitLogHandler.cs` — `git log --oneline`, max 50 entries |

#### Phase 1D: Communication — IMPLEMENTED

| Command | Args | Returns | Side Effects | Implementation |
|---------|------|---------|-------------|----------------|
| `DM` | `recipient` (agentId, agent name, or `@Human`), `message` | Delivery confirmation | Stores DM with RecipientId, posts system notification in recipient's room, triggers immediate agent round or Discord notification | `DmHandler.cs` — routes `@Human` to notification bridge (Discord), agent recipients to DB storage + orchestrator wake-up. Case-insensitive name/ID matching. Self-DM prevented. |
| `ROOM_HISTORY` | `roomId`, `count?` | Recent messages from specified room | Audit event (no movement) | `RoomHistoryHandler.cs` — reads room snapshot, returns last N messages (max 50) |

#### Phase 1E: Navigation — IMPLEMENTED

| Command | Args | Returns | Side Effects | Implementation |
|---------|------|---------|-------------|----------------|
| `MOVE_TO_ROOM` | `roomId` | Confirmation + room name | Updates agent location | `MoveToRoomHandler.cs` — validates room exists, calls MoveAgentAsync |

#### Phase 1G: Room Lifecycle — IMPLEMENTED

| Command | Args | Returns | Side Effects | Implementation |
|---------|------|---------|-------------|----------------|
| `CREATE_ROOM` | `name`, `description?` | Room ID, name, status | Creates a persistent collaboration room as a work context. Generates a slug-based ID. Posts a system welcome message with description. Publishes `RoomCreated` activity event. Planner or Human role required. | `CreateRoomHandler.cs` + `WorkspaceRuntime.CreateRoomAsync()` |
| `REOPEN_ROOM` | `roomId` | Room ID, name, reopened status | Validates room is archived. Sets status to Idle. Publishes `RoomStatusChanged` activity event. Planner or Human role required. Exception-safe via try/catch for TOCTOU protection. | `ReopenRoomHandler.cs` + `WorkspaceRuntime.ReopenRoomAsync()` |
| `INVITE_TO_ROOM` | `agentId` (name or ID), `roomId` | Agent ID/name, room ID/name, confirmation | Moves a specified agent to a specified room. Validates room exists and is not archived. Validates agent exists and is not Working in a breakout (use RECALL_AGENT first). No-op success if agent already in room. Posts system status message in target room. Planner or Human role required. | `InviteToRoomHandler.cs` + `WorkspaceRuntime.MoveAgentAsync()` |

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

### Tier 2 — Full Autonomy

#### Room Management
`RETURN_TO_MAIN`, ~~`INVITE_TO_ROOM`~~ *(implemented — see Phase 1A table)*, ~~`CREATE_ROOM`~~ *(implemented)*, `RESTORE_ROOM`, `ROOM_TOPIC`

#### Communication
`MENTION_TASK_OWNER`, `BROADCAST_TO_ROOM`

#### Task Management
`TASK_STATUS`, `SHOW_DEPENDENCIES`, `MARK_BLOCKED`, `SHOW_TASK_HISTORY`, `REQUEST_REVIEW`, `SHOW_DECISIONS`

#### Code & Spec
`OPEN_SPEC`, `SEARCH_SPEC`, `OPEN_COMPONENT`, `FIND_REFERENCES`

#### Backend Execution
`RUN_FRONTEND_BUILD`, `RUN_TYPECHECK`, `RUN_SERVER`, `CALL_ENDPOINT`, `TAIL_LOGS`, `SHOW_CONFIG`

#### Data & Operations
`QUERY_DB`, `RUN_MIGRATIONS`, `SHOW_MIGRATION_STATUS`, `HEALTHCHECK`, `SHOW_ACTIVE_CONNECTIONS`

#### Audit & Debug
`SHOW_AUDIT_EVENTS`, `SHOW_LAST_ERROR`, `TRACE_REQUEST`, `LIST_FEATURE_FLAGS`, `RETRY_FAILED_JOB`

### Tier 3 — Quality of Life

#### Spec Verification
`VERIFY_SPEC_SECTION`, `COMPARE_SPEC_TO_CODE`, `DETECT_ORPHANED_SECTIONS`

#### Frontend/UX
`PREVIEW_UI`, `CAPTURE_SCREENSHOT`, `COMPARE_SCREENSHOTS`, `SHOW_ROUTES`

#### Context
`HANDOFF_SUMMARY`, `WHOAMI`, `PLATFORM_STATUS`

## Agent Memory System

### Purpose
Persistent per-agent key-value store for learned knowledge across sessions.

### Implementation Status
**IMPLEMENTED** — All memory commands are live as of Phase 1A (commit `63b596c`, 2026-03-28). Memories persisted in `agent_memories` table, injected into agent prompts as `=== YOUR MEMORIES ===` section.

### Commands

| Command | Args | Returns | Implementation |
|---------|------|---------|----------------|
| `REMEMBER` | `category`, `key`, `value` | Confirmation | `RememberHandler.cs` — upsert with validation against 14 allowed categories |
| `RECALL` | `category?`, `key?`, `query?` | Matching memories | `RecallHandler.cs` — LIKE-based search on key/value, optional category filter |
| `LIST_MEMORIES` | `category?` | All memories (optionally filtered) | `ListMemoriesHandler.cs` — query all or by category |
| `FORGET` | `key` | Confirmation | `ForgetHandler.cs` — soft delete by key |

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
    DateTime? UpdatedAt
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
**IMPLEMENTED** — Human-triggered command execution via `POST /api/commands/execute` and async polling via `GET /api/commands/{correlationId}` (commit `TBD`, 2026-04-02).

### Architecture

#### Separation from Agent Pipeline
- **Agent commands**: Flow through `CommandPipeline` → text parser → `CommandAuthorizer` → handlers
- **Human commands**: HTTP request → `CommandController` → handlers directly (bypass parser/authorizer)

**Rationale**: Agent pipeline is designed for text-based agent responses. Human commands are already JSON. Controller-level allowlist is simpler than extending `CommandAuthorizer` with user identity concepts.

#### Command Allowlist (Week 1)

| Tier | Commands | Risk | Human Access |
|------|----------|------|--------------|
| **Read-only** | `READ_FILE`, `SEARCH_CODE`, `LIST_ROOMS`, `LIST_AGENTS`, `LIST_TASKS`, `SHOW_DIFF`, `GIT_LOG`, `SHOW_REVIEW_QUEUE`, `ROOM_HISTORY` | None | ✅ Allowed |
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

All other handlers (26 of 28) require no modifications. They accept `CommandContext` and return `CommandEnvelope` identically for human and agent invocation.

**Evidence**: `src/AgentAcademy.Server/Commands/Handlers/RunBuildHandler.cs` (lines 13, 40, 71), `src/AgentAcademy.Server/Commands/Handlers/RunTestsHandler.cs` (lines 13, 40, 71)

### Known Limitations

1. **No SignalR streaming**: Human commands do not stream live output. `RUN_BUILD` and `RUN_TESTS` require polling to see results. Acceptable for Week 1, but UX will feel sluggish for long-running commands.

2. **No command metadata endpoint**: Frontend must hardcode which commands are synchronous vs asynchronous, and what arguments each command accepts. Future enhancement: `GET /api/commands/metadata` to return schema.

3. **No cancellation**: Once an async command starts, there is no API to cancel it. Build/test runs execute to completion even if user navigates away.

4. **Audit query performance**: Polling endpoint queries `command_audits` by `(CorrelationId, AgentId="human", Source="human-ui")`. Index exists, but polling every N seconds from multiple frontend tabs could generate query load. Consider adding an in-memory cache for pending commands.

## Private Communication (DM)

### Command
`DM: Recipient: @Human/AgentName, Message: <text>` — Available to ALL agents. **IMPLEMENTED**.

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
- **Runtime methods**: `SendDirectMessageAsync`, `GetDirectMessagesForAgentAsync` (defaults `unreadOnly=true`, filtering to recipient-only unacknowledged DMs), `AcknowledgeDirectMessagesAsync` (takes explicit message IDs to prevent races), `GetDmThreadsForHumanAsync`, `GetDmThreadMessagesAsync` in `WorkspaceRuntime.cs`
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
- **Confirmation**: Planned for destructive actions such as `CLOSE_ROOM`; current implementation relies on role gating plus hard validation (for example, refusing to archive the main collaboration room)
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
- `AgentOrchestrator.cs` — After receiving agent response, run through command parser before posting to room
- `WorkspaceRuntime.cs` — Expose read methods needed by LIST_* commands

**New entities:**
- `CommandAuditEntity` — Audit trail for every command execution
- `AgentMemoryEntity` — Per-agent persistent memory (Phase 1, Week 1 Day 5)

### Phase 2 — State Management (Tier 1B)
- Spec-task linking (new junction table)
- Review workflow commands (APPROVE/REQUEST_CHANGES)
- Task claiming (optimistic locking to prevent duplicate work)

### Phase 3 — Verification + Communication (Tier 1C + 1D)
- Build/test execution (sandboxed, with timeouts)
- DM system — **IMPLEMENTED**: extends MessageEntity with RecipientId, adds DirectMessage kind, DmHandler, orchestrator integration, frontend DM panel
- Room history read (existing data, new access pattern)

### Phase 4 — Navigation (Tier 1E)
- Agent self-navigation between rooms
- Room management commands

### Post-MVP
- Tier 2 & 3 rollout
- RBAC middleware extraction
- Spec verification tooling

## Frontend Surfaces

**Status**: NOT IMPLEMENTED. Phase 1A shipped backend-only. Command execution is invisible to users — results are posted as system messages in agent conversation history.

### Command Palette (Primary) — PLANNED
Agent commands and their results will be visible in a searchable palette UI.

**Required for**: Discovery (knowing what commands exist), inspection (seeing what agents did), debugging (understanding command failures).

### Task Panel (Dedicated) — PLANNED
Review queue, spec links, task claims — purpose-built UI for structured state management.

**Required for**: Task-specific commands (APPROVE_TASK, REQUEST_CHANGES, CLAIM_TASK).

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

- **Command discovery**: How do agents learn what commands are available? Added to agent startup prompts as of commit `6117b4e` (2026-03-28). No `HELP` command yet — agents must reference startup prompt or remember syntax.
- **Error recovery**: The spec describes idempotent mutations but doesn't define retry semantics (exponential backoff? max retries? circuit breaker?). Structured error codes (`errorCode` field) now enable agents to make programmatic retry/skip decisions based on error category.
- **Rate limiting**: Per-agent sliding-window rate limiter (30 commands per 60 seconds). Implemented in `CommandRateLimiter` (`src/AgentAcademy.Server/Commands/CommandRateLimiter.cs`), integrated into `CommandPipeline` after authorization. Returns `RATE_LIMIT` error code with retry-after hint. Human UI commands (via `CommandController`) are not rate-limited (already behind cookie auth). Limits are hardcoded — no runtime configuration yet.
- **Frontend surfaces**: Phase 1A shipped backend-only. Command execution is invisible to users. Results are posted as system messages in agent conversation history. Command palette, task panel enhancements, and navigation affordances are planned but not implemented.
- **Tier 2 room commands**: `RETURN_TO_MAIN` remains planned. `CLOSE_ROOM`, `CREATE_ROOM`, `REOPEN_ROOM`, and `INVITE_TO_ROOM` are implemented with planner/human authorization. `LIST_ROOMS` supports optional `status=` filter with validation.

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
1. Agent uses `DM: Recipient: @Human, Message: <text>` command
2. `DmHandler` stores the DM, then delegates to `NotificationManager.SendAgentQuestionAsync`
3. `DiscordNotificationProvider` lazily creates category → channel → thread
4. Message posted as embed in the thread
5. Persistent `MessageReceived` handler routes human replies back to the agent's room via `WorkspaceRuntime.PostHumanMessageAsync`
6. Agent sees reply as a human message in its next orchestration round
7. Human can also reply via the frontend DM panel (`DmPanel.tsx`)

### Implementation Status
**IMPLEMENTED** — `DM` command replaces `ASK_HUMAN`. Handler (`DmHandler.cs`), Discord bridge, reply routing, and frontend DM panel are implemented. All agents have `DM` permission.

**Evidence**: `src/AgentAcademy.Server/Commands/Handlers/DmHandler.cs`, `src/AgentAcademy.Server/Notifications/DiscordNotificationProvider.cs`, `src/AgentAcademy.Server/Controllers/DmController.cs`, `src/agent-academy-client/src/DmPanel.tsx`

## Revision History

| Date | Change | Task | Commit |
|------|--------|------|--------|
| 2026-03-28 | Initial spec from agent team feature request v3 | agent-command-system | — |
| 2026-03-28 | Implemented Phase 1A: envelope, parser, pipeline, authorization, audit, read handlers (LIST_ROOMS, LIST_AGENTS, LIST_TASKS, READ_FILE, SEARCH_CODE), memory handlers (REMEMBER, RECALL, LIST_MEMORIES, FORGET) | command-system-phase1 | `63b596c` |
| 2026-03-28 | Added command reference to agent startup prompts | command-discoverability | `6117b4e` |
| 2026-03-28 | Reconciled frontend surface contradiction: Phase 1A shipped backend-only, no UI surfaces implemented. Documented 9 live commands with implementation evidence. Updated Known Gaps to reflect backend-only state. | spec-007-reconciliation | (this change) |
| 2026-03-29 | Implemented ASK_HUMAN command: Discord agent-to-human question bridge with category-per-workspace, channel-per-agent, thread-per-question architecture. Persistent reply routing via WorkspaceRuntime. | ask-human-command | (this change) |
| 2026-03-30 | Implemented DM command (Phase 1D), replacing ASK_HUMAN. Agent-to-agent and agent-to-human private messaging. MessageEntity.RecipientId + DirectMessage kind. Orchestrator HandleDirectMessage with targeted rounds. System notification in recipient's room. Frontend Telegram-style DM panel. DM API endpoints. 18 tests. | dm-command | (this change) |
| 2026-03-30 | Implemented Phase 1C (RUN_BUILD, RUN_TESTS, SHOW_DIFF, GIT_LOG), ROOM_HISTORY (1D), MOVE_TO_ROOM (1E). All agent timeouts removed — no per-turn LLM timeout, no breakout round cap, no fix round cap. Breakout rooms are open-ended (agents work until WORK REPORT: COMPLETE). DMs delivered to agents in breakout rooms. Task workspace scoping fix. | commands-and-breakout-redesign | (this change) |
| 2026-04-02 | Added Human Command Execution API: `POST /api/commands/execute` and `GET /api/commands/{correlationId}` for Week 1 allowlist (11 commands: all read-only + RUN_BUILD/RUN_TESTS). CommandController bypasses agent pipeline, uses controller-level allowlist + cookie auth. Async commands return 202 + polling. Added CommandAuditEntity.Source field. Build/test handlers serialized via SemaphoreSlim. | implement-frontend-command-execution-api | (this change) |
| 2026-04-04 | Implemented planner-only `CLOSE_ROOM`. Non-main rooms can now be archived when empty; the runtime sets `RoomEntity.Status = Archived` and emits a `RoomClosed` activity event. Updated permission docs and reconciled the confirmation guardrail to match shipped behavior. | close-room-command | (this change) |
| 2026-04-04 | Verified MERGE_TASK Planner/Reviewer role authorization enforcement. Handler code (lines 25-31) guards against unauthorized access. Updated spec table to include implementation reference and clarified design principle scope for agent-initiated commands. | merge-task-authorization-enforcement | `52419d8` |
| 2026-04-04 | Added `ErrorCode` column to `CommandAuditEntity`. Async command polling and audit history now return structured error codes. All 4 audit write paths and the read path updated. Migration `20260404083032_AddCommandAuditErrorCode`. | errorcode-audit-persistence | `5fd74b3` |
| 2026-04-04 | Per-agent command rate limiting (30 commands/60s sliding window). `CommandRateLimiter` integrated into `CommandPipeline` after authorization. `RATE_LIMIT` error code added. 6 new tests. | command-rate-limiting | `df07581` |
| 2026-04-04 | Implemented `INVITE_TO_ROOM` (Phase 1G). Planners/humans can move agents to rooms. Validates room exists/not archived, agent exists/not in breakout. No-op if already in room. System message posted. Added to human command allowlist. 12 new tests. | invite-to-room | (this change) |
