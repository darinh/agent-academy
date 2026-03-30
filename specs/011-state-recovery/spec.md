# 011 — State Recovery and Supervised Restart

## Purpose

Documents the supervised restart and state recovery system that enables Agent Academy to gracefully restart without losing active work. This includes the wrapper script exit code contract, server instance tracking, startup crash detection, and client reconnection protocol.

## Current Behavior

**Status: Planned** (Implementation pending)

The state recovery system consists of six components working together:

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
- If `instanceId` changed: display "Server restarted" banner, refresh room list, active room, task detail, task comments, and direct-message state
- If `instanceId` unchanged: resume normally without clearing current room selection
- If the health check fails during reconnect, keep the UI in reconnecting state and retry instead of assuming a clean resume

**File**: `src/agent-academy-client/src/services/healthCheck.ts` (planned)

#### Client UX States (planned)

| State | Trigger | Expected UX |
|-------|---------|-------------|
| `reconnecting` | SignalR disconnects after a previously healthy connection | Keep current room/task visible, show a reconnect banner or status indicator, and disable mutating actions that require a live server connection |
| `instance-mismatch` | Reconnect health check returns a different `instanceId` | Show "Server restarted" notice, clear stale transient UI state (thinking indicators, pending breakout presence), then refetch authoritative server state |
| `resume-success` | Reconnect health check returns the same `instanceId` | Dismiss reconnect UI and continue without a full workspace reset |
| `crash-recovered` | Health endpoint returns `crashDetected = true` on startup or reconnect | Surface that the previous instance ended unexpectedly and prompt the user to verify in-flight work |
| `refresh-failed` | Post-restart state reload fails | Keep the banner visible, preserve last known read-only data, and offer retry/manual refresh rather than presenting partial fresh state as authoritative |

### 5. Task Comment Recovery Surface

Task comments are part of the state that must survive reconnect and restart flows.

The reconnect refresh must rehydrate task comments from `GET /api/tasks/{id}/comments` rather than trusting in-memory client state. Per `specs/010-task-management/spec.md`, comments are ordered by `CreatedAt` ascending and use these types:

| Type | Purpose | Rendering expectation after reconnect |
|------|---------|---------------------------------------|
| `Comment` | General note or update | Neutral treatment with author, timestamp, and full content |
| `Finding` | Review observation or code issue | Distinct finding/review styling so unresolved issues remain visually obvious |
| `Evidence` | Verification proof (tests, logs, screenshots) | Evidence styling that keeps verification artifacts easy to scan |
| `Blocker` | Blocking issue that prevents progress | High-severity styling so blockers remain prominent after recovery |

Rendering invariants for the client:
- comments remain ordered oldest-to-newest after refresh
- type badges or labels remain visible after reconnect so semantic meaning is not lost
- author and timestamp metadata are preserved for auditability
- filtering by comment type, when task-detail filtering exists, must operate on the server-refreshed dataset rather than stale cached state

### 6. Breakout Termination and Recovery Paths

Open-ended breakout loops need explicit termination semantics so a restart or reconnect does not leave the system in an ambiguous state.

| Termination Path | Trigger | Required Behavior |
|------------------|---------|-------------------|
| `complete` | Agent posts `WORK REPORT:` with status `COMPLETE` | Close breakout, return the agent to the parent-room flow, persist the final work report, and treat the breakout as successfully finished |
| `recall` | Planner/operator recalls the working agent | Close breakout, move the agent to `Idle` in the parent room, and post recall notices so the termination is visible to the team |
| `cancel` | Planner/operator explicitly aborts the breakout | Record a cancellation reason, stop further breakout work, close the breakout, and leave the linked task in a non-success terminal state |
| `stuck-detected` | No meaningful progress signal is observed for a configured threshold, or repeated executor failures prevent forward motion | Mark the breakout as stuck, surface a visible warning in the parent room/UI, and require explicit human/planner follow-up (recall, cancel, or retry) instead of silently discarding the work |

Recovery expectations:
- reconnect should never invent a successful breakout completion that was not persisted server-side
- stale client-side `Working` indicators must be cleared when the authoritative server state shows recall, cancel, or stuck status
- if the termination reason cannot be confirmed after restart, the system should present the breakout as needing operator review rather than assuming success

### 7. Restart Command

The `RESTART_SERVER` agent command triggers a supervised restart.

**Command Format**:
```
RESTART_SERVER:
  reason: <why the restart is needed>
```

**Behavior** (planned):
1. Handler records the restart reason in logs or instance history
2. Handler calls `Environment.Exit(75)`
3. Wrapper script detects exit code 75
4. Wrapper restarts the .NET process
5. The new process records a new server instance
6. Clients reconnect and refresh against `/api/health/instance`

**File**: `src/AgentAcademy.Server/Commands/Handlers/RestartServerHandler.cs` (planned)

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
8. **Recovery over guesswork**: After reconnect, client state must be refreshed from authoritative server endpoints before transient UI state is trusted
9. **Visible termination**: Breakout termination reason must be observable as complete, recall, cancel, or stuck-detected

## Known Gaps

- Wrapper script implementation (shell script wrapper not yet created)
- `ServerInstanceEntity` not yet added to `AgentAcademyDbContext`
- Startup crash detection logic not yet implemented in `WorkspaceRuntime.InitializeAsync`
- Shutdown hook not yet registered with `IHostApplicationLifetime`
- `/api/health/instance` endpoint not yet implemented
- Frontend health check and reconnect logic not yet implemented
- `RESTART_SERVER` command handler not yet implemented
- Breakout cancellation and stuck-detection controls are not yet implemented
- No persisted termination-reason field currently exists for breakout lifecycle outcomes
- No mechanism to prevent restart loops (crash → restart → crash → restart)
- No restart history UI (list of past restarts with timestamps and reasons)
- No maximum restart count enforcement

## Revision History

- **2026-03-30**: Initial specification — wrapper exit code contract, `ServerInstanceEntity` schema, startup/shutdown hooks, client reconnect protocol, `RESTART_SERVER` command design
- **2026-03-30**: Expanded planned reconnect UX states, task-comment recovery expectations, and breakout termination paths | spec-doc-gap-fix
