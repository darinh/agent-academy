# 011 — State Recovery and Supervised Restart

## Purpose

Documents the supervised restart and state recovery system that enables Agent Academy to gracefully restart without losing active work. This includes the wrapper script exit code contract, server instance tracking, startup crash detection, and client reconnection protocol.

## Current Behavior

**Status: Implemented** (Wrapper script, server instances, startup crash recovery, restart command, health endpoint, auth recovery, and client reconnect UX all implemented.)

The state recovery system consists of eight components working together:

### 1. Wrapper Script Exit Code Contract

A wrapper script supervises the .NET application and interprets exit codes to determine restart behavior:

| Exit Code | Meaning | Action |
|-----------|---------|--------|
| `0` | Clean shutdown | Exit wrapper, no restart |
| `75` | Restart requested | Restart .NET process immediately |
| `1+` | Crash/error | Restart with exponential backoff (2s→4s→8s→16s→32s, max 5 attempts) |

**File**: `src/AgentAcademy.Server/wrapper.sh`

The wrapper script:
- Launches the .NET application (`dotnet AgentAcademy.Server.dll`)
- Captures the exit code
- If exit code = 75: restarts the process immediately, resets crash counter
- If exit code = 0: exits cleanly
- If exit code = 1+: restarts with exponential backoff, up to 5 attempts
- Resets crash counter if process runs for ≥ 60 seconds (configurable via `AA_HEALTH_SEC`)
- Maximum crash restarts configurable via `AA_MAX_CRASH` (default: 5)
- DLL path auto-detected or set via `AA_DLL_PATH`
- Logs all restart events with timestamps

### 2. Server Instance Tracking

The `ServerInstanceEntity` records each server lifecycle event in the database.

**Schema** (`server_instances` table, **implemented**):
```sql
CREATE TABLE server_instances (
    Id TEXT PRIMARY KEY,              -- GUID generated at startup
    StartedAt TEXT NOT NULL,          -- Process start timestamp (UTC)
    ShutdownAt TEXT NULL,             -- Clean shutdown timestamp (UTC)
    ExitCode INTEGER NULL,            -- Process exit code
    CrashDetected INTEGER DEFAULT 0,  -- 1 if previous instance crashed
    Version TEXT NOT NULL              -- Assembly version
);
```

**Invariants**:
1. Exactly one instance has `ShutdownAt = NULL` at any time (the current running instance)
2. `CrashDetected = true` only when a previous instance has `ShutdownAt = NULL`
3. `ExitCode` is only set when `ShutdownAt` is not null (process has exited)
4. `StartedAt` must be UTC
5. `Version` matches the assembly version of the running executable

**Entity Definition** (`src/AgentAcademy.Server/Data/Entities/ServerInstanceEntity.cs`, **implemented**):
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

The `InitializationService` and `CrashRecoveryService` manage instance lifecycle events.

**On Startup** (`InitializeAsync` + startup bootstrap, **implemented**):
1. Query for the most recent `ServerInstanceEntity` where `ShutdownAt = NULL`
2. If found:
   - Set `CrashDetected = true` on the current startup instance
   - Update the previous instance: `ShutdownAt = NOW(), ExitCode = -1`
3. Create new `ServerInstanceEntity` with `CrashDetected` flag and assembly version
4. Set `CrashRecoveryService.CurrentInstanceId` (static property for health endpoint)
5. Proceed with existing initialization (default room, agents, welcome message)
6. Resolve the authoritative main room for the active workspace, if any
7. If `CrashDetected = true`, ask `AgentOrchestrator` to run startup recovery before normal work resumes
8. Startup recovery closes every active breakout via `CloseBreakoutRoomAsync(..., ClosedByRecovery)`, resets any lingering `Working` agents to `Idle`, and posts a main-room system message beginning with `"System recovered from crash"`

**On Shutdown** (`IHostApplicationLifetime.ApplicationStopping`, **implemented** in `Program.cs`):
1. Locate the current instance (by `CurrentInstanceId`)
2. Set `ShutdownAt = DateTime.UtcNow`
3. Set `ExitCode = Environment.ExitCode`

**Files**: `src/AgentAcademy.Server/Services/InitializationService.cs`, `src/AgentAcademy.Server/Services/CrashRecoveryService.cs`, `src/AgentAcademy.Server/Services/AgentOrchestrator.cs`, `src/AgentAcademy.Server/Program.cs`

### 4. Client Reconnect Protocol

When the server restarts, connected clients must detect the restart and refresh their state.

**Health Endpoint** (`GET /api/health/instance`, **implemented**):
```json
{
  "instanceId": "abc-123-def",
  "startedAt": "2026-03-30T16:00:00Z",
  "version": "1.2.3",
  "crashDetected": false,
  "executorOperational": true,
  "authFailed": false,
  "circuitBreakerState": "Closed"
}
```

**Frontend Reconnect Logic** (**implemented**):
- Store `instanceId` from initial health check (`useWorkspace.ts` mount effect)
- On SignalR disconnect: immediately show reconnecting banner globally (above all tabs)
- On SignalR reconnect: call `evaluateReconnect()` which fetches `/api/health/instance`
- If `instanceId` changed: display "Server restarted" syncing banner, clear thinking indicators, refresh workspace data
- If `instanceId` unchanged: dismiss banner and resume normally
- If `crashDetected` is true: display "Crash recovered" banner with extended visibility (8s vs 4s)
- If the health check fails during reconnect: show error banner, preserve last known state

**Files**: `src/agent-academy-client/src/healthCheck.ts` (evaluateReconnect, RECONNECTING_BANNER), `src/agent-academy-client/src/RecoveryBanner.tsx` (4 tones: reconnecting, syncing, crash, error), `src/agent-academy-client/src/useWorkspace.ts` (reconnect orchestration)

#### Client UX States (**implemented**)

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
| `stuck-detected` | Agent produces `MaxConsecutiveIdleRounds` (5) consecutive responses with zero parsed commands, **or** the breakout exceeds `MaxBreakoutRounds` (200) total rounds | Close breakout with `StuckDetected` reason, mark linked task as `Blocked`, move agent to `Idle` in parent room, post 🔴 warning to parent room. **Implemented** in `AgentOrchestrator.RunBreakoutLoopAsync`. |
| `closed-by-recovery` | Server startup detects the previous instance crashed while breakout work was active | Close the breakout during startup recovery, persist `ClosedByRecovery` as the close reason, return the assigned agent to `Idle`, and notify the main room so interrupted work can be re-evaluated |

Recovery expectations:
- reconnect should never invent a successful breakout completion that was not persisted server-side
- stale client-side `Working` indicators must be cleared when the authoritative server state shows recall, cancel, or stuck status
- crash recovery must clear persisted active breakouts and `Working` agent locations before normal orchestration resumes
- if the termination reason cannot be confirmed after restart, the system should present the breakout as needing operator review rather than assuming success

### 7. Restart Command

The `RESTART_SERVER` agent command triggers a supervised restart.

**Command Format**:
```
RESTART_SERVER:
  reason: <why the restart is needed>
```

**Behavior** (**implemented**):
1. Handler validates Planner role authorization (double-checked beyond CommandAuthorizer)
2. Records the restart reason in logs
3. Posts system message to the main room: "🔄 Server restarting: {reason}"
4. Sets `Environment.ExitCode = 75`
5. Schedules `IHostApplicationLifetime.StopApplication()` on a background thread (500ms delay for response propagation)
6. Wrapper script detects exit code 75 and restarts the .NET process
7. The new process records a new server instance and detects if previous shutdown was clean
8. Clients reconnect and refresh against `/api/health/instance`

**File**: `src/AgentAcademy.Server/Commands/Handlers/RestartServerHandler.cs`

### 8. Auth Retry vs Restart Escalation

The `CopilotExecutor` implements a token-based authentication recovery strategy that avoids server restarts for recoverable auth failures.

**Authentication Failure Detection** (**implemented**):

When the Copilot SDK returns an authentication error during agent execution:

1. **Auth Exception Handling** (`CopilotExecutor.RunAsync`):
   - `CopilotAuthException`: Authentication failure (token expired/revoked)
     - Marks `_authFailed = true` via `HandleAuthFailureAsync`
     - Invalidates the current agent session
     - Posts system message: "⚠️ **Copilot SDK authentication failed.** The OAuth token has expired or been revoked. Please re-authenticate at `/api/auth/login` to restore agent functionality."
     - Falls back to `StubExecutor` for this request
     - **Never retried** — no automatic retry loop for auth failures
   
   - `CopilotAuthorizationException`: Token lacks required permissions
     - Invalidates the current agent session
     - Falls back to `StubExecutor`
     - **Never retried** — no automatic retry loop for authorization failures

2. **Auth Recovery Flow** (`CopilotExecutor.EnsureClientAsync`):
    - If `_authFailed` is true, `CopilotClient` remains unavailable
    - When a new token is provided (via `CopilotTokenProvider` after user re-login):
      - `_activeToken` changes, triggering client recreation
      - `_authFailed` is cleared to `false`
      - Posts recovery message: "✅ **Copilot SDK reconnected.** A new token has been provided — agents are coming back online."
    - `CopilotAuthMonitorService` proactively probes `GET /user` every 5 minutes:
      - Before probing: checks if token is expiring soon (within 30 minutes of its 8-hour lifetime). If so, proactively refreshes via `RefreshTokenAsync()` before the token expires.
      - `200` → executor transitions back to operational if it was previously degraded
      - `401` / `403` → attempts token refresh via `ICopilotAuthProbe.RefreshTokenAsync()` before degrading. If refresh succeeds, the system recovers without user intervention. If refresh fails (expired refresh token, revoked app), executor transitions to degraded and posts the re-authentication notice.
      - Network failures, timeouts, and other non-auth responses are logged and ignored so transient outages do not force degraded mode
    - Token refresh exchanges the stored refresh token at `POST https://github.com/login/oauth/access_token` with `grant_type=refresh_token`. GitHub rotates refresh tokens on each use; both the new access token and new refresh token are stored. Refreshed tokens are written back to the auth cookie on the next HTTP request via middleware.
    - `/api/auth/status` exposes `copilotStatus` with three states:
      - `operational` — browser auth cookie and Copilot SDK token are both healthy
      - `degraded` — browser auth cookie is still valid, but the SDK token is missing or `_authFailed = true`
      - `unavailable` — no authenticated browser session is present
    - The auth status response remains fail-closed: `authenticated = false` whenever `copilotStatus != operational`
    - The frontend render contract is driven by `copilotStatus`: `unavailable` routes to `LoginPage`, while `degraded` keeps the workspace shell visible in limited mode with mutating actions paused
    - The frontend polls `/api/auth/status` every 30 seconds and treats `operational -> degraded` with a retained `user` payload as an automatic re-auth trigger
    - Automatic re-authentication reuses the existing `/api/auth/login` OAuth redirect, debounced once per tab and suppressed after explicit logout to avoid redirect loops
    - Health endpoint still exposes `authFailed` for instance-health diagnostics

3. **Retry Policy** (`CopilotExecutor.SendAndCollectWithRetryAsync`):
   - **Auth/authorization failures**: Never retried (thrown immediately)
   - **Quota/rate-limit errors**: Retried up to 3 times with exponential backoff (5s, 15s, 30s)
   - **Transient errors**: Retried up to 3 times with exponential backoff (2s, 4s, 8s)

**Why No Restart Escalation**:

Auth failures are caused by expired/revoked OAuth tokens, not by corrupted SDK state or broken file handles. The correct fix is to refresh the token automatically (via the stored refresh token) or, if the refresh token is also expired, via user re-authentication. The system:

- Exposes `copilotStatus` in `/api/auth/status` so the UI can distinguish normal login from re-authentication, while `authFailed` remains available from the health endpoint for diagnostics
- Continues serving non-agent endpoints (workspace, tasks, rooms) during auth failure
- Automatically recovers when `CopilotTokenProvider` receives a new token (after OAuth callback)
- Starts the browser re-authentication flow automatically when the UI observes a degraded SDK state but still has the browser GitHub session, while keeping the existing workspace visible in limited mode until Copilot recovers
- Uses `StubExecutor` as fallback so orchestration doesn't crash

**Restart is only needed** when:
- Unrecoverable SDK state corruption occurs (not auth failures)
- Configuration changes require process reload
- Planner explicitly requests it via `RESTART_SERVER` command

**Files**: 
- `src/AgentAcademy.Server/Services/CopilotExecutor.cs` (lines 142-157, 278-306, 476-484, 640-679)
- `src/AgentAcademy.Server/Services/CopilotExceptions.cs` (typed exception definitions)

## Interfaces & Contracts

### Exit Code Contract
```
0   → Clean shutdown (user-initiated or completion)
75  → Supervised restart (RESTART_SERVER command)
1+  → Crash (unhandled exception, fatal error) — wrapper restarts with backoff
```

### Health Endpoint
```
GET /api/health/instance
Response 200:
{
  "instanceId": string,        // GUID
  "startedAt": string,         // ISO 8601 UTC
  "version": string,           // semver
  "crashDetected": boolean,    // true if previous instance crashed
  "executorOperational": boolean, // true if Copilot SDK client is active
  "authFailed": boolean        // true if auth failure detected (awaiting re-login)
}
```

Authenticated UI gating contract:
```json
{
  "authEnabled": true,
  "authenticated": false,
  "copilotStatus": "degraded",
  "user": {
    "login": "octocat",
    "name": "Monalisa Octocat",
    "avatarUrl": "https://avatars.githubusercontent.com/u/1"
  }
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
4. **Wrapper obligation**: Wrapper MUST restart on exit code 75, MUST NOT restart on code 0, MUST restart with backoff on code 1+
5. **Client sync**: Frontend `instanceId` must match backend health endpoint after reconnection
6. **Timestamp consistency**: All `StartedAt` and `ShutdownAt` values are UTC
7. **Version tracking**: `Version` field matches the running assembly version
8. **Recovery over guesswork**: After reconnect, client state must be refreshed from authoritative server endpoints before transient UI state is trusted
9. **Visible termination**: Breakout termination reason must be observable as complete, recall, cancel, or stuck-detected
10. **Auth recovery without restart**: Authentication failures trigger user re-authentication flow, not server restart. `/api/auth/status` exposes `copilotStatus` for client gating, `authFailed` remains available for diagnostics, and recovery is cleared automatically on token refresh.

## Known Gaps

- ~~Wrapper script implementation~~ — **Implemented**: `src/AgentAcademy.Server/wrapper.sh`
- ~~`ServerInstanceEntity` not yet added~~ — **Implemented**: `src/AgentAcademy.Server/Data/Entities/ServerInstanceEntity.cs`
- ~~Startup crash detection logic~~ — **Implemented** in `InitializationService.InitializeAsync`
- ~~Shutdown hook not yet registered~~ — **Implemented** in `Program.cs` via `IHostApplicationLifetime.ApplicationStopping`
- ~~`/api/health/instance` endpoint~~ — **Implemented** in `SystemController.cs`
- ~~`RESTART_SERVER` command handler~~ — **Implemented**: `RestartServerHandler.cs`
- ~~Startup crash recovery actions~~ — **Implemented**: crash-detected boot now closes active breakouts with `ClosedByRecovery`, resets lingering `Working` agents to `Idle`, and posts a main-room recovery notification
- ~~Frontend health check and reconnect logic not yet implemented~~ — **Implemented**: `healthCheck.ts` with `evaluateReconnect()`, global `RecoveryBanner` with 4 tones, reconnect/disconnect handling in `useWorkspace.ts`
- ~~Breakout cancellation and stuck-detection controls are not yet implemented~~ — **Implemented**: `AgentOrchestrator.RunBreakoutLoopAsync` tracks consecutive idle rounds (no commands parsed) and enforces an absolute round cap. On detection, closes breakout with `StuckDetected`, marks linked task as `Blocked`, and notifies the parent room.
- Persisted breakout close reasons cover `Completed`, `Recalled`, `ClosedByRecovery`, `StuckDetected`, and `Failed`; `Cancelled` is emitted on branch setup failure
- ~~No mechanism to prevent restart loops (crash → restart → crash → restart)~~ — **Resolved**: `wrapper.sh` implements exponential backoff (2^(n-1) seconds, capped at 32s), configurable crash limit (AA_MAX_CRASH, default 5), and health threshold (AA_HEALTH_SEC, default 60s — resets counter for long-running instances).
- No restart history UI (list of past restarts with timestamps and reasons) — **resolved**: REST API at `GET /api/system/restarts` and `GET /api/system/restarts/stats`; frontend panel `RestartHistoryPanel.tsx` embedded in `DashboardPanel` with stats cards, paginated instance table, crash recovery badges, and graceful error handling (see spec 300 § Server Instance History)
- ~~No maximum restart count enforcement in the server (only in wrapper)~~ — **resolved**: `RestartServerHandler` enforces a server-side rate limit of 10 intentional restarts per hour via `SemaphoreSlim`-guarded check against `ServerInstances` table. Returns `RATE_LIMIT` error when exceeded.

## Revision History

- **2026-04-04**: Restart history UI. `RestartHistoryPanel` component in Dashboard shows 24h stats (crashes, restarts, clean shutdowns, running) and paginated server instance table with shutdown-reason badges and crash-recovery indicators. Uses `Promise.allSettled` for independent endpoint failure, `useRef`-based request sequencing to prevent stale response races, inline error banners on failed refresh, and offset clamping when total shrinks. API types and functions added to `api.ts`. 9 new frontend tests.
- **2026-04-04**: Server-side restart rate limiting and restart history API. `RestartServerHandler` enforces max 10 intentional restarts per hour with `SemaphoreSlim`-guarded check against `ServerInstances` table. New endpoints: `GET /api/system/restarts` (paginated instance history with derived shutdown reason) and `GET /api/system/restarts/stats` (aggregated counts by type with configurable time window, SQL-level aggregation). Adversarial review by GPT-5.3 Codex found 3 issues: race condition (fixed with semaphore), stats window logic (fixed ShutdownAt-based filtering), memory materialization (pushed to SQL). 18 new tests.
- **2026-04-04**: Implemented breakout stuck-detection. `AgentOrchestrator.RunBreakoutLoopAsync` tracks consecutive idle rounds (zero commands parsed) and enforces absolute max-round cap. On detection: closes breakout with `StuckDetected`, marks linked task as `Blocked`, notifies parent room. Thresholds: `MaxConsecutiveIdleRounds=5`, `MaxBreakoutRounds=200`. 3 new tests. Updated Known Gaps.
- **2026-04-01**: Implemented startup crash recovery actions. On crash-detected boot, `AgentOrchestrator` now triggers runtime repair that closes active breakout rooms with persisted `ClosedByRecovery` reason, resets lingering `Working` agents to `Idle`, and posts a main-room recovery notice.
- **2026-04-01**: Added proactive SDK auth expiry detection. `CopilotAuthMonitorService` probes GitHub `/user` every 5 minutes, treats only `401/403` as definitive auth failure, and debounces room/Discord notifications so they fire only on degraded/operational transitions.
- **2026-03-31**: Added Section 8 (Auth Retry vs Restart Escalation) documenting CopilotExecutor's token-based recovery strategy. Authentication failures trigger user re-authentication instead of server restart. Auth/authorization exceptions are never retried; quota/transient errors retry with exponential backoff. Health endpoint `authFailed` flag exposed for diagnostics, and `/api/auth/status` now surfaces `copilotStatus` (`operational` / `degraded` / `unavailable`) for client gating. Added Invariant 10. Updated Invariant 4 to clarify crash-restart behavior (code 1+).
- **2026-03-31**: Implemented wrapper script (crash-restart with backoff), server instance tracking (entity + migration + crash detection), RESTART_SERVER command handler (Planner-only, exit code 75), /api/health/instance endpoint (instanceId, authFailed, executorOperational), IHostApplicationLifetime shutdown hook. Updated exit code table to include crash-restart behavior.
- **2026-03-30**: Initial specification — wrapper exit code contract, `ServerInstanceEntity` schema, startup/shutdown hooks, client reconnect protocol, `RESTART_SERVER` command design
- **2026-03-30**: Expanded planned reconnect UX states, task-comment recovery expectations, and breakout termination paths | spec-doc-gap-fix
