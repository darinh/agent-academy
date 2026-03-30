# 011 — State Recovery and Supervised Restart

## Purpose

Documents the supervised restart and state recovery system that enables Agent Academy to gracefully restart without losing active work. This includes the wrapper script exit code contract, server instance tracking, startup crash detection, and client reconnection protocol.

## Current Behavior

**Status: Planned** (Implementation pending)

The state recovery system consists of four components working together:

### 1. Wrapper Script Exit Code Contract

A wrapper script supervises the .NET application and interprets exit codes to determine restart behavior:

| Exit Code | Meaning | Action |
|-----------|---------|--------|
| `0` | Clean shutdown | Exit wrapper, no restart |
| `75` | Restart requested | Restart .NET process immediately |
| Other (1+) | Crash/error | Exit wrapper, report error |

**File**: `src/AgentAcademy.Server/wrapper.sh` (planned)

The wrapper script:
- Launches the .NET application (`dotnet AgentAcademy.Server.dll`)
- Captures the exit code
- If exit code = 75: restarts the process with the same arguments
- If exit code = 0 or other: exits with the same code
- Logs all restart events for debugging

### 2. Server Instance Tracking

The `ServerInstanceEntity` records each server lifecycle event in the database.

**Schema** (`server_instances` table, planned):
```sql
CREATE TABLE server_instances (
    id TEXT PRIMARY KEY,              -- GUID generated at startup
    started_at DATETIME NOT NULL,     -- Process start timestamp (UTC)
    shutdown_at DATETIME NULL,        -- Clean shutdown timestamp (UTC)
    exit_code INTEGER NULL,           -- Process exit code
    crash_detected BOOLEAN DEFAULT 0, -- TRUE if previous instance crashed
    version TEXT NOT NULL             -- Application version (from AssemblyInfo)
);
```

**Invariants**:
1. Exactly one instance has `shutdown_at = NULL` at any time (the current running instance)
2. `crash_detected = TRUE` only when a previous instance has `shutdown_at = NULL`
3. `exit_code` is only set when `shutdown_at` is not null (process has exited)
4. `started_at` must be UTC
5. `version` matches the assembly version of the running executable

**Entity Definition** (`src/AgentAcademy.Server/Data/Entities/ServerInstanceEntity.cs`, planned):
```csharp
public class ServerInstanceEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ShutdownAt { get; set; }
    public int? ExitCode { get; set; }
    public bool CrashDetected { get; set; }
    public string Version { get; set; } = "";
}
```

### 3. Startup and Shutdown Hooks

The `WorkspaceRuntime` service manages instance lifecycle events.

**On Startup** (`InitializeAsync`, planned modifications):
1. Query for the most recent `ServerInstanceEntity` where `ShutdownAt = NULL`
2. If found:
   - Set `CrashDetected = true` on the current startup instance
   - Update the previous instance: `ShutdownAt = NOW(), ExitCode = -1`
3. Create new `ServerInstanceEntity` for this startup with `CrashDetected` flag
4. Proceed with existing initialization (default room, agents, welcome message)

**On Shutdown** (`IHostApplicationLifetime.ApplicationStopping`, planned):
1. Locate the current instance (where `ShutdownAt = NULL`)
2. Set `ShutdownAt = DateTime.UtcNow`
3. Set `ExitCode = {pending from environment}`

**File**: `src/AgentAcademy.Server/Services/WorkspaceRuntime.cs` (modifications planned)

### 4. Client Reconnect Protocol

When the server restarts, connected clients must detect the restart and refresh their state.

**Health Endpoint** (`GET /api/health/instance`, planned):
```json
{
  "instanceId": "abc-123-def",
  "startedAt": "2026-03-30T16:00:00Z",
  "version": "1.2.3",
  "crashDetected": false
}
```

**Frontend Reconnect Logic** (planned):
- Store `instanceId` from initial health check
- On SignalR reconnect: call `/api/health/instance`
- If `instanceId` changed: display "Server restarted" banner, refresh room list and active room
- If `instanceId` unchanged: resume normally

**File**: `src/agent-academy-client/src/services/healthCheck.ts` (planned)

**Client UX States**:

| State | Trigger | UI Behavior |
|-------|---------|-------------|
| **Reconnecting** | SignalR connection lost (network/server restart) | Display "Reconnecting..." status indicator, disable message input, show loading spinner |
| **Instance Mismatch** | `instanceId` changed after reconnect | Display "Server restarted — refreshing..." banner, fetch fresh room list, reload active room state, clear stale cached data |
| **Success** | `instanceId` matches or reconnect completes | Remove status indicators, re-enable input, resume normal operation |

The reconnect flow uses SignalR's `onreconnecting` and `onreconnected` callbacks to detect connection state changes. On reconnect, the client immediately fetches `/api/health/instance` to compare `instanceId` values.

### 5. Restart Command

The `RESTART_SERVER` agent command triggers a supervised restart.

**Command Format**:
```
RESTART_SERVER:
  reason: <why the restart is needed>
```

**Behavior** (planned):
1. Handler calls `Environment.Exit(75)` after logging the restart reason
2. Wrapper script detects exit code 75
3. Wrapper restarts the .NET process
4. New process detects no crash (clean shutdown with code 75)
5. Clients reconnect and see "Server restarted" notification

**File**: `src/AgentAcademy.Server/Commands/Handlers/RestartServerHandler.cs` (planned)

### 6. Task Comments System

Agents can annotate tasks with structured comments during breakout work using the `ADD_TASK_COMMENT` command.

**Comment Types** (`TaskCommentType` enum, `src/AgentAcademy.Shared/Models/Enums.cs`):

| Type | Purpose | Usage |
|------|---------|-------|
| **Comment** | General notes, status updates, questions | Default type for agent observations or progress notes |
| **Finding** | Issues discovered during work (bugs, tech debt, risks) | Reviewer agents log concerns that don't block completion |
| **Evidence** | Verification proof (test results, build output, diff summaries) | Agents provide artifacts showing acceptance criteria were met |
| **Blocker** | Critical issues preventing task completion | Agent signals inability to proceed, triggers escalation |

**Rendering Expectations** (planned):

- **Comment**: Plain text, gray icon, conversational tone
- **Finding**: Warning icon (⚠️), yellow/amber color, highlighted in task detail view
- **Evidence**: Checkmark icon (✓), green color, rendered near completion status
- **Blocker**: Stop icon (🛑), red color, urgent badge, triggers notification to planner

**API Endpoint**: `POST /api/tasks/{id}/comments` (implemented, `src/AgentAcademy.Server/Controllers/TasksController.cs`)

**Access Control**: Only the task assignee, reviewer, or planner can add comments (enforced in `AddTaskCommentHandler`).

**Storage**: `TaskCommentEntity` in `task_comments` table (FK to `task_items.Id`, fields: `Type`, `Content`, `AuthorId`, `CreatedAt`).

### 7. Breakout Room Termination Paths

Breakout rooms end through one of four paths:

| Path | Trigger | Orchestrator Behavior |
|------|---------|----------------------|
| **Completion** | Agent produces `WORK REPORT: Status: COMPLETE` | Parse report, update task status to Done, post system message, transition agent to Idle in parent room, close breakout room |
| **Recall** | Planner issues `RECALL_AGENT` command | Immediately archive breakout room (set `RoomStatus.Archived`), move agent to Idle in parent room, post recall notice, skip completion/review flow |
| **Cancel** | Human or planner explicitly cancels task | Archive breakout room, update task status to Cancelled, move agent to Idle |
| **Stuck Detection** | Room status becomes non-Active during loop | Exit breakout loop early if `RoomStatus != Active` detected on round boundary, skip completion flow (logged as "recalled or archived") |

**Implementation**: `AgentOrchestrator.RunBreakoutLoopAsync` checks `breakoutRoom.Status != RoomStatus.Active` before each round and after receiving reviewer feedback (`src/AgentAcademy.Server/Services/AgentOrchestrator.cs`).

**Work Report Parsing**: The orchestrator extracts `Status`, `Files`, and `Evidence` fields from the `WORK REPORT:` block using regex. If status is "COMPLETE", the task is marked Done and evidence is stored.

**No Round Caps**: Breakout loops are open-ended — agents continue until producing a completion report, being recalled, or the room being archived. There is no `MaxBreakoutRounds` or `MaxFixRounds` limit.

## Interfaces & Contracts

### Exit Code Contract
```
0   → Clean shutdown (user-initiated or completion)
75  → Supervised restart (config reload, upgrade, maintenance)
>0  → Crash (unhandled exception, fatal error)
```

### Health Endpoint
```
GET /api/health/instance
Response 200:
{
  "instanceId": string,      // GUID
  "startedAt": string,       // ISO 8601 UTC
  "version": string,         // semver
  "crashDetected": boolean   // true if previous instance crashed
}
```

### ServerInstanceEntity Schema
```csharp
public class ServerInstanceEntity
{
    [Key] public string Id { get; set; }
    [Required] public DateTime StartedAt { get; set; }
    public DateTime? ShutdownAt { get; set; }
    public int? ExitCode { get; set; }
    public bool CrashDetected { get; set; }
    [Required] public string Version { get; set; }
}
```

### Restart Command
```
COMMAND: RESTART_SERVER
Arguments:
  - reason: string (required) — human-readable restart justification
Result: Server exits with code 75, wrapper restarts process
```

## Invariants

1. **Single active instance**: Only one `ServerInstanceEntity` with `ShutdownAt = NULL` exists at any time
2. **Crash detection**: `CrashDetected = true` if and only if a previous instance has `ShutdownAt = NULL` at startup
3. **Exit code semantics**: Code 75 means "restart", code 0 means "stop", other codes mean "error"
4. **Wrapper obligation**: Wrapper MUST restart on exit code 75, MUST NOT restart on any other code
5. **Client sync**: Frontend `instanceId` must match backend health endpoint after reconnection
6. **Timestamp consistency**: All `StartedAt` and `ShutdownAt` values are UTC
7. **Version tracking**: `Version` field matches the running assembly version

## Known Gaps

- Wrapper script implementation (shell script wrapper not yet created)
- `ServerInstanceEntity` not yet added to `AgentAcademyDbContext`
- Startup crash detection logic not yet implemented in `WorkspaceRuntime.InitializeAsync`
- Shutdown hook not yet registered with `IHostApplicationLifetime`
- `/api/health/instance` endpoint not yet implemented
- Frontend health check and reconnect logic not yet implemented
- `RESTART_SERVER` command handler not yet implemented
- No mechanism to prevent restart loops (crash → restart → crash → restart)
- No restart history UI (list of past restarts with timestamps and reasons)
- No maximum restart count enforcement

## Revision History

- **2026-03-30**: Initial specification — wrapper exit code contract, `ServerInstanceEntity` schema, startup/shutdown hooks, client reconnect protocol, `RESTART_SERVER` command design
