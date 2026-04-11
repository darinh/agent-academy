# 003 — Agent Execution System

## Purpose
Defines how Agent Academy sends prompts to LLM providers and receives responses. The execution layer is abstracted behind `IAgentExecutor`, allowing the rest of the system to operate regardless of which backend is available.

## Current Behavior

> **Status: Implemented** — Interface, Copilot SDK executor, and stub fallback are compiled and tested.

### Architecture

```
┌──────────────────────────────────┐
│         Callers                  │
│  (Orchestrator, API endpoints)  │
└──────────────┬───────────────────┘
               │ IAgentExecutor
               ▼
┌──────────────────────────────────┐
│        CopilotExecutor           │
│  • Manages CopilotClient        │
│  • Caches sessions per agent    │
│  • Streams & collects response  │
│  • Falls back to StubExecutor   │
└──────────────┬───────────────────┘
               │ (on failure)
               ▼
┌──────────────────────────────────┐
│         StubExecutor             │
│  • Clear offline error notice   │
│  • No LLM connection needed     │
│  • IsFullyOperational = false   │
└──────────────────────────────────┘
```

### Interface: `IAgentExecutor`

**File**: `src/AgentAcademy.Server/Services/IAgentExecutor.cs`

```csharp
public interface IAgentExecutor
{
    bool IsFullyOperational { get; }
    bool IsAuthFailed { get; }
    CircuitState CircuitBreakerState { get; }
    Task MarkAuthDegradedAsync(CancellationToken ct = default);
    Task MarkAuthOperationalAsync(CancellationToken ct = default);
    Task<string> RunAsync(AgentDefinition agent, string prompt, string? roomId, string? workspacePath = null, CancellationToken ct = default);
    Task InvalidateSessionAsync(string agentId, string? roomId);
    Task InvalidateRoomSessionsAsync(string roomId);
    Task InvalidateAllSessionsAsync();
    Task DisposeAsync();
}
```

| Member | Description |
|--------|-------------|
| `IsFullyOperational` | `true` when backed by a live LLM provider |
| `IsAuthFailed` | `true` when auth failure detected requiring user re-authentication |
| `CircuitBreakerState` | Current circuit breaker state: `Closed` (normal), `Open` (fallback), `HalfOpen` (probing) |
| `MarkAuthDegradedAsync` | Transitions to auth-degraded; emits room notice + notifications on first failure |
| `MarkAuthOperationalAsync` | Transitions back to auth-operational; emits recovery notice on state change |
| `RunAsync` | Sends prompt, returns complete response text. Optional `workspacePath` creates/uses a worktree-scoped CopilotClient. |
| `InvalidateSessionAsync` | Disposes a single cached session |
| `InvalidateRoomSessionsAsync` | Disposes all sessions for a room |
| `InvalidateAllSessionsAsync` | Disposes all sessions across all rooms and agents |
| `DisposeAsync` | Releases all resources |

### Implementation: `CopilotExecutor`

**File**: `src/AgentAcademy.Server/Services/CopilotExecutor.cs`
**NuGet**: `GitHub.Copilot.SDK` v0.2.2

Key behaviors:
- **Lazy client initialization**: `CopilotClient` is created on first use and cached.
- **Token resolution chain**: Resolved in priority order via `ResolveToken()`:
  1. **User OAuth token** — from `CopilotTokenProvider` (captured during GitHub OAuth login)
  2. **Config token** — `Copilot:GitHubToken` from `IConfiguration` (appsettings / user-secrets)
  3. **Environment / CLI** — `null` passed to SDK, which checks `COPILOT_GITHUB_TOKEN`, `GH_TOKEN`, `GITHUB_TOKEN`, or copilot CLI login state
- **CLI path configuration**: `Copilot:CliPath` in `appsettings.json` controls which copilot binary the SDK uses. Must be set to `"copilot"` (system PATH) or an explicit path to an already-authenticated copilot CLI. The SDK's bundled binary (at `runtimes/linux-x64/native/copilot`) ships with no auth state and will fail to connect. The system copilot CLI (e.g., `~/.local/bin/copilot`) uses existing `copilot auth login` credentials.
- **Token-change awareness**: When the resolved token changes (e.g., user logs in after server was using a config token), the old `CopilotClient` is disposed, all sessions are cleared, and a new client is created with the new token. Failure state is reset so the new token gets a fresh attempt. If the executor was in an auth-failed state, a recovery message is posted to the main room.
- **Error classification**: `SessionErrorEvent.Data.ErrorType` is classified into typed exceptions:
  - `authentication` → `CopilotAuthException` (definitive — no retry, triggers auth failure notification)
  - `authorization` → `CopilotAuthorizationException` (no retry — token lacks required permissions)
  - `quota`, `rate_limit` → `CopilotQuotaException` (retried with longer backoff: 5s/15s/30s, max 3 attempts)
  - Other/unknown → `CopilotTransientException` (retried with backoff: 2s/4s/8s, max 3 attempts)
- **Auth failure handling**: On definitive auth failure, the executor sets `IsAuthFailed = true`, posts a re-authentication notice to the main room, and notifies via the notification system. When the user re-authenticates (token changes), the flag is cleared and a recovery message is posted automatically.
- **Proactive auth expiry probe**: `CopilotAuthMonitorService` runs every 5 minutes and issues a lightweight `GET https://api.github.com/user` probe using the current GitHub token source. Only HTTP `401` and `403` are treated as definitive auth degradation; success clears a prior degraded state, while timeouts, transport failures, and other status codes are logged as transient and do not change auth state. Before probing, the monitor checks if the token is expiring soon (within 30 minutes); if so, it proactively refreshes via `ICopilotAuthProbe.RefreshTokenAsync()`. On auth failure, a refresh is attempted before degrading the system.
- **Permission handling**: Sessions use `AgentPermissionHandler.Create()` which approves tool calls for the registered tools. When no tools are registered for an agent, all permissions are approved (same as `PermissionHandler.ApproveAll`). The handler logs all permission requests for diagnostics.
- **Session-per-agent-per-room**: Sessions keyed by `{agentId}:{roomId}`, default room is `"default"`. Worktree-scoped sessions use the key `{worktreePath}:{agentId}:{roomId}`.
- **Per-worktree CopilotClient**: When an agent works in a git worktree (breakout room), a dedicated `CopilotClient` is created with `Cwd` set to the worktree directory, ensuring the CLI's built-in tools operate in the correct filesystem location. Worktree clients are stored in a `ConcurrentDictionary<string, CopilotClient>` keyed by path. Lifecycle: created on first use per worktree path, disposed when the breakout room closes, and all worktree clients are disposed on token rotation. `EnsureWorktreeClientAsync` avoids deadlocks by returning `null` (caller falls back to the default client) rather than calling `EnsureClientAsync` while holding the lock.
- **Streaming aggregation**: Subscribes to `AssistantMessageDeltaEvent` for incremental tokens, uses `AssistantMessageEvent` for the final complete content.
- **Session priming**: Sends `AgentDefinition.StartupPrompt` as the first message to establish agent identity. The startup prompt is NOT repeated in per-round prompts — it lives only in the session priming to avoid redundant context accumulation.
- **Model selection**: Uses `AgentDefinition.Model` in `SessionConfig`, defaults to `"gpt-5"`.
- **TTL cleanup**: Sessions expire after 10 minutes of inactivity; a background timer runs every 2 minutes.
- **Automatic fallback**: If `CopilotClient.StartAsync()` fails or any individual call fails (after retry exhaustion), delegates to `StubExecutor`.
- **Circuit breaker**: Prevents burning through retries when the API is consistently failing. Global (not per-agent) since all agents share the same token. States: Closed (normal flow), Open (immediate fallback, no retries), HalfOpen (one probe allowed after cooldown). Trips after 5 consecutive failures (quota, transient, or unknown — auth errors do NOT trip the circuit). Cooldown: 60 seconds before probing. Auto-resets on token change. State exposed via `GET /api/health/instance` (`CircuitBreakerState` field) and `IAgentExecutor.CircuitBreakerState`. Open-circuit events are recorded in `AgentErrors` with type `circuit_open`.
- **Graceful disposal**: All sessions and the client are disposed on shutdown.

### Token Provider: `CopilotTokenProvider`

**File**: `src/AgentAcademy.Server/Services/CopilotTokenProvider.cs`

Singleton service bridging GitHub OAuth login to `CopilotExecutor`:
- `SetToken(string)` — backward-compatible entry point; captures access token only.
- `SetTokens(accessToken, refreshToken?, expiresIn?, refreshTokenExpiresIn?)` — captures access token, refresh token, and expiry timestamps. Preserves existing refresh token when the new value is null (supports partial updates during token refresh).
- `ClearToken()` — called by `AuthController.Logout` to remove all stored tokens and timestamps.
- `Token` — volatile read; available to the executor even during background orchestration (where `HttpContext` is null).
- `RefreshToken` — the OAuth refresh token for automatic renewal (valid ~6 months).
- `ExpiresAtUtc` — UTC expiry time of the current access token.
- `IsTokenExpiringSoon` — true when the access token is within 30 minutes of expiry.
- `CanRefresh` — true when a non-expired refresh token is available.
- `HasPendingCookieUpdate` / `MarkCookieUpdatePending()` / `ClearCookieUpdatePending()` — flag for middleware to write refreshed tokens back to the auth cookie.
- `TokenSetAt` — UTC timestamp of when the token was last set (nullable, null if never set). Used by the health endpoint for diagnostics.

### Authentication Flow → Copilot SDK Activation

```
┌────────────────┐     ┌───────────────────┐     ┌──────────────────┐
│  User Browser  │────▶│  GitHub OAuth      │────▶│  OnCreatingTicket │
│  GET /login    │     │  (GitHub.com)      │     │  (Program.cs)     │
└────────────────┘     └───────────────────┘     └────────┬─────────┘
                                                          │ SetTokens()
                                                          ▼
                                                 ┌──────────────────┐
                                                 │ CopilotToken     │
                                                 │ Provider          │
                                                 │ (singleton)       │
                                                 └────────┬─────────┘
                                                          │ Token
                                                          ▼
┌──────────────────┐     ┌───────────────────┐   ┌──────────────────┐
│  Agent           │────▶│  CopilotExecutor  │──▶│  CopilotClient   │
│  Orchestrator    │     │  ResolveToken()   │   │  (SDK CLI proc)  │
│  (background)    │     └───────────────────┘   └──────────────────┘
└──────────────────┘

         ┌──────────────────────────────────────────────────┐
         │  CopilotAuthMonitorService (every 5 min)         │
         │  ├─ Token expiring soon? → RefreshTokenAsync()   │
         │  ├─ Probe returns AuthFailed? → try refresh first│
         │  └─ Refresh succeeds → SetTokens() + cookie flag │
         └──────────────────────────────────────────────────┘
```

**OAuth Configuration** (in `Program.cs`):
- `SaveTokens = true` — the OAuth access token and refresh token persist in the encrypted auth cookie, surviving server restarts
- Token restoration middleware: on the first authenticated request after a server restart, extracts access token, refresh token, and expiry from the cookie and populates `CopilotTokenProvider`
- Cookie write-back middleware: when tokens are refreshed server-side, merges updated tokens into the existing auth cookie on the next HTTP request
- Scopes: `read:user`, `user:email`
- GitHub App credentials stored in user-secrets (`GitHub:ClientId`, `GitHub:ClientSecret`, `GitHub:AppId`)

**Token Refresh** (GitHub App user-to-server tokens expire after 8 hours):
- `CopilotAuthMonitorService` proactively refreshes tokens 30 minutes before expiry via `ICopilotAuthProbe.RefreshTokenAsync()`
- On auth probe failure (401/403), attempts a refresh before degrading
- Refresh calls `POST https://github.com/login/oauth/access_token` with `grant_type=refresh_token`
- GitHub rotates refresh tokens on each use; the new refresh token is stored
- Refresh token lifetime: ~6 months (180 days); access token: 8 hours
- On successful refresh, `HasPendingCookieUpdate` is set → next HTTP request writes new tokens to the cookie

**Behavior**:
- Before any user logs in: executor uses config token or env vars; falls back to `StubExecutor` if none available
- After user logs in: OAuth token is captured → executor creates/recreates `CopilotClient` with user's token → agents produce real responses
- On server restart: first authenticated request restores the token from the auth cookie via middleware — no re-login needed
- On token expiry: automatic refresh via monitor service — no re-login needed (for up to 6 months)
- On logout: all tokens are cleared → executor falls back to config token or stub on next call

### Implementation: `StubExecutor`

**File**: `src/AgentAcademy.Server/Services/StubExecutor.cs`

Returns a deterministic offline notice when the Copilot SDK is unavailable:

```
⚠️ Agent {Name} ({Role}) is offline — the Copilot SDK is not connected.
Log in via GitHub OAuth or check server logs to activate.
```

The message includes the agent's name and role so users can identify which agent is offline. `IsFullyOperational` is always `false`.

### DI Registration

**File**: `src/AgentAcademy.Server/Program.cs`

```csharp
builder.Services.AddSingleton<IAgentExecutor, CopilotExecutor>();
```

`CopilotExecutor` is registered as a singleton. It internally creates and manages a `StubExecutor` fallback — consumers only depend on `IAgentExecutor`.

### Tests

**File**: `tests/AgentAcademy.Server.Tests/StubExecutorTests.cs`

| Test | Validates |
|------|-----------|
| `IsFullyOperational_ReturnsFalse` | Stub reports non-operational |
| `RunAsync_ReturnsOfflineNotice_ForKnownRoles` | All 5 known roles produce offline notice with agent name/role |
| `RunAsync_ReturnsOfflineNotice_ForUnknownRole` | Unknown roles also get offline notice |
| `RunAsync_ThrowsOnCancellation` | Cancellation token honored |
| `InvalidateSessionAsync_DoesNotThrow` | No-op is safe |
| `InvalidateRoomSessionsAsync_DoesNotThrow` | No-op is safe |
| `DisposeAsync_DoesNotThrow` | No-op is safe |
| `RunAsync_WithNullRoomId_ReturnsOfflineNotice` | Null room handled |
| `RunAsync_ReturnsDeterministicMessage` | Same input always produces same output |
| `StubExecutor_ImplementsIAgentExecutor` | Interface compliance |
| `CopilotExecutor_ImplementsIAgentExecutor` | Interface compliance |

## Invariants

- `IAgentExecutor.RunAsync` never returns null or throws for valid inputs (except cancellation).
- `CopilotExecutor` never leaves the system without an executor — it always falls back to `StubExecutor`.
- Session keys are deterministic: `{agentId}:{roomId ?? "default"}`.
- Expired sessions are cleaned up within `CleanupInterval` (2 minutes).
- All sessions are invalidated on workspace/project switch via `InvalidateAllSessionsAsync()`.

## Known Gaps

- **Single-user token model**: `CopilotTokenProvider` stores one global token (last authenticated user). In a multi-user deployment, User B's login overwrites User A's token. Acceptable for the current single-user / small-team use case. A per-user `ConcurrentDictionary<userId, token>` model would be needed for true multi-tenancy. — *Tracked in #2*
- ~~**No token/usage tracking**~~ — **Resolved**: `LlmUsageTracker` captures `AssistantUsageEvent` from the Copilot SDK on every LLM call (including session priming). Persists per-request metrics (model, input/output/cache tokens, cost, duration, reasoning effort) to `llm_usage` table. Room-level aggregation via `GET /api/rooms/{id}/usage`, per-agent breakdown via `/usage/agents`, individual records via `/usage/records`. Global usage via `GET /api/usage` with optional `hoursBack` filter.
- ~~**No tool calling**~~ — **Resolved**: SDK tool calling is wired up via `AgentToolRegistry` and `AgentToolFunctions`. See "SDK Tool Calling" section below.
- ~~**No per-project session resume**~~ — **Resolved**: On workspace switch, `ConversationSessionService.ArchiveAllActiveSessionsAsync()` summarizes all active conversation sessions via LLM before clearing SDK sessions. When the user returns, `GetSessionContextAsync()` retrieves the archived summary and the orchestrator injects it into agent prompts for continuity. Empty sessions (no messages) are archived without summaries. Fallback summaries are generated when the executor is offline.

### Tool Constraint Gaps (Accepted Design Constraints)

These are intentional safety constraints, not missing features:

- ~~**`read_file` restricts paths to project directory**~~ — **Accepted**: Path traversal prevention is a security feature. Agents cannot read files outside the workspace.
- ~~**`read_file` truncates content at 12KB**~~ — **Accepted**: Prevents agents from loading huge files into LLM context. Agents can use `startLine`/`endLine` parameters for targeted reads.
- ~~**`search_code` caps results at 50 matches**~~ — **Accepted**: Prevents unbounded search results from flooding LLM context. Agents can refine queries with `path` and `glob` filters.
- ~~**`update_task_status` restricts to safe statuses**~~ — **Accepted**: Agents can set Active, Blocked, AwaitingValidation, InReview, Queued but not Completed/Cancelled. Prevents agents from unilaterally closing tasks without human validation.

### Architectural Gaps (Accepted for Single-User Product)

- ~~**No agent-to-agent direct communication**~~ — **Accepted**: Agents communicate via rooms and DMs, mediated by `AgentOrchestrator`. This is intentional — room-based communication provides auditable, observable message flow. Direct peer-to-peer would bypass logging and rate limiting.
- ~~**No streaming of agent responses to UI**~~ — **Accepted**: `CopilotExecutor` enables SDK streaming internally (`Streaming = true`) but collects deltas into a single response before returning to the orchestrator. True streaming to the frontend would require significant SignalR/SSE changes for marginal UX benefit given typical response times.
- ~~**No agent hot-reload**~~ — **Accepted**: `AgentCatalogLoader` reads `agents.json` once at startup (singleton). Catalog changes require server restart. Agent *configuration* overrides (model, prompt, templates) are hot-reloadable via DB — only structural changes (adding/removing agents) need restart.
- ~~**No agent versioning**~~ — **Accepted**: `AgentDefinition` has no version field. Agent behavior changes are tracked via `agent_configs.UpdatedAt` for overrides and git history for catalog changes. Formal versioning would add complexity with little benefit for a single-user product.

### Genuine Gaps (Low Priority)

- ~~**No per-agent resource quotas**~~ — **Resolved**: `AgentQuotaService` enforces per-agent resource limits configured in `agent_configs` (nullable `MaxRequestsPerHour`, `MaxTokensPerHour`, `MaxCostPerHour` columns — null = unlimited). Request-rate limiting uses an authoritative in-memory sliding window (1-hour window). Token/cost checks are best-effort via DB aggregation against the `llm_usage` table (concurrent calls may slightly overshoot). Quota is checked in `CopilotExecutor.RunAsync` before the circuit breaker, and each retry attempt counts toward the request limit. Quota violations throw `AgentQuotaExceededException`, caught by `AgentOrchestrator.RunAgentAsync` which returns a system-style pause message. Configuration via `PUT /api/agents/{id}/quota`, status via `GET /api/agents/{id}/quota`. Cache invalidated on config change. Composite index `(AgentId, RecordedAt)` on `llm_usage` ensures efficient aggregation.
- **No prompt injection mitigation**: User-supplied text (room messages, DMs, task descriptions) is interpolated verbatim into agent prompts in `AgentOrchestrator.BuildConversationPrompt` and `BuildBreakoutPrompt`. No input sanitization or prompt boundary markers exist. Low risk in single-user context (the user *is* the operator), but would need attention for any multi-user deployment.
- ~~**No agent-level rate limiting**~~ — **Resolved**: `AgentQuotaService` includes per-agent LLM call-rate limiting via an in-memory sliding window. Each LLM attempt (including retries in `SendAndCollectWithRetryAsync`) is recorded. Configurable via `MaxRequestsPerHour` in `agent_configs`. See "No per-agent resource quotas" above for full details.

### SDK Tool Calling

> **Status: Implemented** — Read-only and write tools registered per agent based on `EnabledTools`. Write tools scoped to calling agent identity.

The Copilot SDK supports registering C# methods as tools that the LLM can invoke via structured function calls. This is more reliable than text-based command parsing.

#### Architecture

```
AgentDefinition.EnabledTools ["task-state", "code"]
         │
         ▼
IAgentToolRegistry.GetToolsForAgent(enabledTools)
         │  resolves tool groups → AIFunction list
         ▼
List<AIFunction> [list_tasks, list_rooms, show_agents, read_file, search_code]
         │
         ▼
SessionConfig.Tools = [.. tools]
SessionConfig.OnPermissionRequest = AgentPermissionHandler.Create(toolNames, logger)
```

#### Tool Groups

| Group | Tools | Type | Agents |
|-------|-------|------|--------|
| `task-state` | `list_tasks`, `list_rooms`, `show_agents` | Read-only (shared) | All agents |
| `code` | `read_file`, `search_code` | Read-only (shared) | Engineers, Writer |
| `task-write` | `create_task`, `update_task_status`, `add_task_comment` | Write (per-agent) | All agents |
| `memory` | `remember`, `recall` | Write/Read (per-agent) | All agents |
| `chat` | (platform concept, not an SDK tool) | — | All agents |

#### Tool Functions

**File**: `src/AgentAcademy.Server/Services/AgentToolFunctions.cs`

**Read-only tools** (static, agent-agnostic):

| Tool | Description | Parameters |
|------|-------------|------------|
| `list_tasks` | Lists tasks with status, assignee, metadata | `status?` (filter) |
| `list_rooms` | Lists rooms with status and participants | `includeArchived?` (bool) |
| `show_agents` | Lists agents with location and state | (none) |
| `read_file` | Reads file content with optional line range | `path`, `startLine?`, `endLine?` |
| `search_code` | Searches codebase via `git grep` | `query`, `path?`, `glob?`, `ignoreCase?` |

**Write tools** (contextual, scoped to calling agent via inner wrapper classes):

| Tool | Description | Parameters |
|------|-------------|------------|
| `create_task` | Creates a new task and room | `title`, `description`, `successCriteria`, `preferredRoles?`, `type?` |
| `update_task_status` | Updates task status, reports blockers, posts notes | `taskId`, `status?`, `blocker?`, `note?` |
| `add_task_comment` | Adds a comment/finding to a task | `taskId`, `content`, `commentType?` |
| `remember` | Stores a memory that persists across sessions | `key`, `value`, `category`, `ttl?` |
| `recall` | Searches and retrieves memories (FTS5 + LIKE fallback) | `query?`, `category?`, `key?`, `includeExpired?` |

**Safety**:
- `read_file` restricts paths to the project directory (path traversal denied)
- `read_file` truncates content at 12KB to prevent huge responses
- `search_code` caps results at 50 matches
- `update_task_status` restricts to safe statuses (Active, Blocked, AwaitingValidation, InReview, Queued — cannot set Completed/Cancelled)
- Write tools capture `agentId` at session creation via closures — agents cannot impersonate other agents
- Memory isolation: agents only see their own memories plus `shared` category memories from other agents

**Implementation**: Read-only tools are created once and shared. Write tools use inner wrapper classes (`TaskWriteToolWrapper`, `MemoryToolWrapper`) that capture agent identity via closures. All tools use `IServiceScopeFactory` to resolve scoped services at invocation time.

#### Registry

**File**: `src/AgentAcademy.Server/Services/AgentToolRegistry.cs`
**Interface**: `src/AgentAcademy.Server/Services/IAgentToolRegistry.cs`
**DI**: Singleton

Maps `AgentDefinition.EnabledTools` group names to `AIFunction` instances. Read-only groups (`task-state`, `code`) use shared tool instances. Contextual groups (`task-write`, `memory`) create per-agent tool instances at resolution time, requiring `agentId` and `agentName` parameters. Group names are case-insensitive. Unknown groups are silently ignored. Duplicate groups don't produce duplicate tools.

#### Permission Handler

**File**: `src/AgentAcademy.Server/Services/AgentPermissionHandler.cs`

`AgentPermissionHandler.Create(registeredToolNames, logger)` returns a `PermissionRequestHandler` delegate that:
- Approves all permissions when no tools are registered (backward-compatible)
- Approves only safe permission kinds (`custom-tool`, `read`, `tool`) when tools are registered
- Denies dangerous permission kinds (`shell`, `write`, `url`, `mcp`) with `DeniedByRules`
- Logs all approved and denied permission requests for diagnostics

Note: The SDK's `write` permission kind refers to file write operations by the SDK itself, not our custom tool write operations. Our custom tools (including write tools) use the `custom-tool` or `tool` permission kind.

#### CopilotExecutor Integration

**File**: `src/AgentAcademy.Server/Services/CopilotExecutor.cs`

In `GetOrCreateSessionEntryAsync`, the executor:
1. Calls `_toolRegistry.GetToolsForAgent(agent.EnabledTools, agent.Id, agent.Name)` to resolve tools (passing agent identity for contextual groups)
2. Creates `SessionConfig` with `Tools = [.. tools]`
3. Sets `OnPermissionRequest = AgentPermissionHandler.Create(toolNames, _logger)`
4. Logs tool registration for diagnostics

#### Tests

**File**: `tests/AgentAcademy.Server.Tests/AgentToolTests.cs` — 62 tests

| Test Class | Tests | Validates |
|------------|-------|-----------|
| `AgentToolRegistryTests` | 17 | Group resolution (static + contextual), dedup, case-insensitive, all tool names, planner/engineer config, task-write/memory groups, no-agentId fallback |
| `AgentToolFunctionsTests` | 15 | Tool creation, list_tasks/rooms/agents execution, read_file (happy path, not found, path traversal, directory, line range), search_code (results, no results, glob filter, path traversal), FindProjectRoot |
| `AgentWriteToolTests` | 23 | create_task (valid, missing title, invalid type, with type), update_task_status (valid, invalid, blocker, blocker+status, note, nonexistent, no args), add_task_comment (valid, finding type, invalid type, nonexistent), remember (create, upsert, TTL, invalid category, invalid TTL), recall (empty, after remember, by category, by key, agent isolation, shared visibility, expired exclusion, expired inclusion) |
| `AgentPermissionHandlerTests` | 7 | No-tools approval, tool-call approval, read approval, shell denial, write denial, URL denial, multiple safe kinds |

### Conversation Session Management

> **Status: Implemented** — Prevents context accumulation that degrades agent performance.

**Problem**: Copilot SDK sessions accumulate all prompts and responses internally. Over many rounds, agents process an ever-growing context window, leading to slower responses and degraded quality.

**Solution**: Conversation sessions (epochs) create logical boundaries within rooms. When message count exceeds a configurable threshold, the conversation is LLM-summarized and a new session begins with clean context.

**Components**:

- **`ConversationSessionEntity`** (`src/AgentAcademy.Server/Data/Entities/ConversationSessionEntity.cs`): Tracks epoch boundaries per room. Fields: `Id`, `RoomId`, `RoomType` (Main/Breakout), `SequenceNumber`, `Status` (Active/Archived), `Summary`, `MessageCount`.
- **`ConversationSessionService`** (`src/AgentAcademy.Server/Services/ConversationSessionService.cs`): Manages epoch lifecycle — creation, threshold checks, LLM summarization, rotation, and SDK session invalidation.
- **`SystemSettingsService`** (`src/AgentAcademy.Server/Services/SystemSettingsService.cs`): Configurable thresholds via `conversation.mainRoomEpochSize` (default 50) and `conversation.breakoutEpochSize` (default 30).

**Epoch rotation flow**:
1. Before each conversation round, `AgentOrchestrator` calls `CheckAndRotateAsync(roomId)`
2. If message count ≥ threshold, loads session messages and generates an LLM summary
3. Archives current session with summary, creates new active session
4. Invalidates all SDK sessions for the room via `InvalidateRoomSessionsAsync()`
5. New prompts include the archived session summary under `=== PREVIOUS CONVERSATION SUMMARY ===`

**Prompt deduplication**: `BuildConversationPrompt` and `BuildBreakoutPrompt` no longer include `agent.StartupPrompt` — it's already sent as session priming in `CopilotExecutor.GetOrCreateSessionEntryAsync`. This eliminates the largest source of redundant context.

**Message tagging**: `MessageEntity.SessionId` and `BreakoutMessageEntity.SessionId` tag messages by epoch. `BuildRoomSnapshotAsync` loads only messages from the active session (plus legacy untagged messages for backwards compatibility).

**Graceful degradation**: If LLM summarization fails (Copilot offline), a fallback summary with participant names and message count is used.

## SSE Activity Stream

> **Status: Implemented** — Alternative to SignalR for environments without WebSocket support.

### Endpoint

**File**: `src/AgentAcademy.Server/Controllers/ActivityController.cs`

`GET /api/activity/stream` — Server-Sent Events stream of `ActivityEvent`.

- Content-Type: `text/event-stream`
- Event name: `activityEvent`
- Payload: JSON-serialized `ActivityEvent` (camelCase)
- On connect, replays recent events from `ActivityBroadcaster` buffer (up to 100)
- Uses a bounded channel (256 capacity, drop-oldest) to bridge `ActivityBroadcaster` callbacks to the async SSE writer
- Gracefully handles client disconnect via `CancellationToken`
- Disables nginx buffering via `X-Accel-Buffering: no` header

### Client Hook

**File**: `src/agent-academy-client/src/useActivitySSE.ts`

`useActivitySSE(onEvent, enabled?)` — Drop-in alternative to `useActivityHub`.

- Uses the browser `EventSource` API
- Same `ConnectionStatus` type as SignalR hook
- Auto-reconnects with exponential backoff on connection loss
- `enabled` parameter (default `true`) allows conditional activation

### Transport Selection

**File**: `src/agent-academy-client/src/useWorkspace.ts`

Transport is selected via `localStorage` key `aa-transport`:
- `"signalr"` (default) — uses `useActivityHub`
- `"sse"` — uses `useActivitySSE`

Both hooks are always called (React Rules of Hooks), but only the active one creates a connection. The inactive hook receives `enabled = false` and reports `"disconnected"`.

## Agent Configuration Overrides

> **Status: Implemented** — DB schema, merge service, and orchestrator integration are compiled and tested.

### Architecture

```
┌──────────────────────────────────┐
│      agents.json (catalog)       │
│  Static agent definitions        │
│  loaded at startup               │
└──────────────┬───────────────────┘
               │ AgentDefinition
               ▼
┌──────────────────────────────────┐
│       AgentConfigService         │
│  • Reads agent_configs table     │
│  • Merges overrides + templates  │
│  • Returns effective definition  │
└──────────────┬───────────────────┘
               │ Effective AgentDefinition
               ▼
┌──────────────────────────────────┐
│   AgentOrchestrator / Executor   │
│  • Uses effective Model          │
│  • Uses effective StartupPrompt  │
└──────────────────────────────────┘
```

### Instruction Layering

The effective startup prompt is built by layering (not direct swap):

```
Effective = [StartupPromptOverride ?? CatalogStartupPrompt]
          + [InstructionTemplate.Content, if assigned]
          + [CustomInstructions, if set]
```

Each layer is separated by `\n\n`. Whitespace-only values are treated as unset.

### Database Schema

**Table: `agent_configs`**

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| `AgentId` | TEXT | PK | Matches catalog agent ID |
| `StartupPromptOverride` | TEXT | nullable | Replaces catalog StartupPrompt |
| `ModelOverride` | TEXT | nullable | Replaces catalog Model |
| `CustomInstructions` | TEXT | nullable | Appended after prompt + template |
| `InstructionTemplateId` | TEXT | FK nullable, SetNull | Links to instruction_templates |
| `UpdatedAt` | DATETIME | required | Last modification time |

**Table: `instruction_templates`**

| Column | Type | Constraint | Description |
|--------|------|------------|-------------|
| `Id` | TEXT | PK | GUID identifier |
| `Name` | TEXT | required, unique | Human-readable name |
| `Description` | TEXT | nullable | What this template does |
| `Content` | TEXT | required | Instruction text appended to prompt |
| `CreatedAt` | DATETIME | required | Creation time |
| `UpdatedAt` | DATETIME | required | Last modification time |

**Entities**: `src/AgentAcademy.Server/Data/Entities/AgentConfigEntity.cs`, `InstructionTemplateEntity.cs`
**Migration**: `20260329224834_AddAgentConfigOverrides`

### Service: `AgentConfigService`

**File**: `src/AgentAcademy.Server/Services/AgentConfigService.cs`
**DI**: Scoped (uses scoped `AgentAcademyDbContext`)

```csharp
public sealed class AgentConfigService
{
    Task<AgentDefinition> GetEffectiveAgentAsync(AgentDefinition catalogAgent);
    Task<List<AgentDefinition>> GetEffectiveAgentsAsync(IEnumerable<AgentDefinition> catalogAgents);
}
```

| Method | Description |
|--------|-------------|
| `GetEffectiveAgentAsync` | Queries DB for override, merges with catalog, returns effective definition |
| `GetEffectiveAgentsAsync` | Batch version — single query for all agent overrides |
| `MergeAgent` (internal static) | Pure merge logic for testability |
| `BuildEffectivePrompt` (internal static) | Layering logic: base + template + custom |

**Behavior**:
- If no override row exists for an agent → returns catalog definition unchanged
- Whitespace-only overrides are treated as unset (fall through to catalog)
- Identity fields (`Id`, `Name`, `Role`, `Summary`, `CapabilityTags`, `EnabledTools`, `AutoJoinDefaultRoom`, `GitIdentity`, `Permissions`) are never modified
- Only `StartupPrompt` and `Model` can be overridden

### Orchestrator Integration

**File**: `src/AgentAcademy.Server/Services/AgentOrchestrator.cs`

The orchestrator resolves `AgentConfigService` from its scoped `IServiceScopeFactory` and applies overrides before building prompts or calling the executor:

| Method | Change |
|--------|--------|
| `RunConversationRoundAsync` | Resolves effective agent for planner and each conversation participant |
| `RunBreakoutLoopAsync` | Resolves effective agent before breakout rounds |
| `HandleBreakoutCompleteAsync` | Resolves effective agent for presenting agent |
| `RunReviewCycleAsync` | Resolves effective reviewer agent |

`WorkspaceRuntime` continues using `_catalog.Agents` for identity-only operations (location tracking, room assignment, task claims). Config overrides are not needed for these operations.

`CopilotExecutor` is unchanged — it receives the already-merged `AgentDefinition` and uses its `Model` and `StartupPrompt` properties as before.

### Tests

**File**: `tests/AgentAcademy.Server.Tests/AgentConfigServiceTests.cs` — 14 tests

| Test | Validates |
|------|-----------|
| `GetEffectiveAgent_NoOverride_ReturnsCatalogUnchanged` | No override → identity pass-through |
| `GetEffectiveAgent_ModelOverride_ReplacesModel` | Model swap |
| `GetEffectiveAgent_StartupPromptOverride_ReplacesPrompt` | Prompt replacement |
| `GetEffectiveAgent_CustomInstructions_AppendedToPrompt` | Custom appended to catalog |
| `GetEffectiveAgent_InstructionTemplate_AppendedBetweenPromptAndCustom` | Layering order |
| `GetEffectiveAgent_FullLayering_OverridePromptPlusTemplatePlusCustom` | Override + template + custom |
| `GetEffectiveAgent_EmptyOverrides_TreatedAsNoOverride` | Whitespace handling |
| `GetEffectiveAgents_MixedOverrides_AppliedCorrectly` | Bulk query with partial overrides |
| `BuildEffectivePrompt_NoOverrides_ReturnsCatalogPrompt` | Static pure function |
| `BuildEffectivePrompt_AllLayers_DoubleNewlineSeparated` | Separator format |
| `BuildEffectivePrompt_OnlyTemplate_AppendedToCatalog` | Template-only layer |
| `GetEffectiveAgent_PreservesAllIdentityFields` | Full identity preservation |
| `AgentConfig_InstructionTemplateFk_SetNullOnDelete` | FK cascade behavior |
| `InstructionTemplate_NameIsUnique` | Unique constraint enforcement |

### REST API Endpoints

#### Agent Configuration

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/agents/{agentId}/config` | Returns effective config (merged catalog + DB override) and raw override details |
| `PUT` | `/api/agents/{agentId}/config` | Creates or updates an agent configuration override |
| `POST` | `/api/agents/{agentId}/config/reset` | Deletes override, reverts agent to catalog defaults |

**GET `/api/agents/{agentId}/config`** response:
```json
{
  "agentId": "planner-1",
  "effectiveModel": "claude-opus-4.6",
  "effectiveStartupPrompt": "...(merged prompt)...",
  "hasOverride": true,
  "override": {
    "startupPromptOverride": "...",
    "modelOverride": "gpt-5.4",
    "customInstructions": "...",
    "instructionTemplateId": "abc-123",
    "instructionTemplateName": "Verification-First",
    "updatedAt": "2026-03-29T..."
  }
}
```

**PUT `/api/agents/{agentId}/config`** request — all fields nullable (null clears that override):
```json
{
  "startupPromptOverride": "...",
  "modelOverride": "gpt-5.4",
  "customInstructions": "...",
  "instructionTemplateId": "abc-123"
}
```

**Validation**:
- `agentId` must exist in the agent catalog → `404` if not found
- `instructionTemplateId` must reference an existing template → `400` if invalid

**POST `/api/agents/{agentId}/config/reset`** — no request body, returns the catalog default config.

#### Instruction Templates

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/instruction-templates` | Lists all templates, ordered by name |
| `GET` | `/api/instruction-templates/{id}` | Returns a single template |
| `POST` | `/api/instruction-templates` | Creates a new template |
| `PUT` | `/api/instruction-templates/{id}` | Updates an existing template |
| `DELETE` | `/api/instruction-templates/{id}` | Deletes a template (FK SetNull on agent_configs) |

**POST/PUT request**:
```json
{
  "name": "Verification-First",
  "description": "Agents verify before presenting",
  "content": "You must verify all code..."
}
```

**Validation**:
- `name` required, must be unique → `409 Conflict` on duplicate
- `content` required → `400` if empty
- DELETE returns `404` if template not found; agent_configs referencing the deleted template have `InstructionTemplateId` set to null via FK cascade

**Implementation**: Endpoints on `AgentController` (config) and `InstructionTemplateController` (templates). DTOs are file-local records. Service methods on `AgentConfigService`.

#### CRUD Test Coverage

| Test | Verifies |
|------|----------|
| `GetConfigOverride_NoOverride_ReturnsNull` | No DB row returns null |
| `GetConfigOverride_WithOverride_ReturnsEntity` | DB row with fields |
| `GetConfigOverride_IncludesTemplateNavigation` | Include() loads template |
| `UpsertConfig_CreatesNew_WhenNoOverrideExists` | Insert path |
| `UpsertConfig_UpdatesExisting_WhenOverrideExists` | Update path, single row |
| `UpsertConfig_NullFields_ClearOverride` | Null clears fields |
| `UpsertConfig_WithValidTemplate_SetsFK` | FK + navigation loaded |
| `UpsertConfig_WithInvalidTemplate_ThrowsArgumentException` | Invalid template ID |
| `UpsertConfig_SetsUpdatedAt` | Timestamp set |
| `DeleteConfig_ExistingOverride_ReturnsTrueAndRemoves` | Delete path |
| `DeleteConfig_NoOverride_ReturnsFalse` | Missing row |
| `CreateTemplate_ReturnsNewTemplate` | Insert with all fields |
| `CreateTemplate_DuplicateName_ThrowsInvalidOperationException` | Unique name |
| `GetAllTemplates_ReturnsOrderedByName` | OrderBy(Name) |
| `GetAllTemplates_Empty_ReturnsEmptyList` | Empty DB |
| `GetTemplate_Exists_ReturnsTemplate` | Single lookup |
| `GetTemplate_NotFound_ReturnsNull` | Missing row |
| `UpdateTemplate_Exists_UpdatesFields` | Update path |
| `UpdateTemplate_NotFound_ReturnsNull` | Missing row |
| `UpdateTemplate_DuplicateNameWithOtherTemplate_ThrowsInvalidOperationException` | Name conflict with other |
| `UpdateTemplate_SameNameOnSameTemplate_Succeeds` | No self-conflict |
| `DeleteTemplate_Exists_ReturnsTrueAndRemoves` | Delete path |
| `DeleteTemplate_NotFound_ReturnsFalse` | Missing row |
| `DeleteTemplate_NullifiesFkOnAgentConfigs` | FK SetNull cascade |

### Frontend: Agent Configuration UI

> **Status: Implemented** — Settings panel with agent config cards and template management.

**Components**:

| File | Purpose |
|------|---------|
| `src/agent-academy-client/src/AgentConfigCard.tsx` | Per-agent expandable config card in Settings |
| `src/agent-academy-client/src/TemplateCard.tsx` | Per-template CRUD card in Settings |
| `src/agent-academy-client/src/SettingsPanel.tsx` | Settings overlay — Agents, Templates, Notifications sections |
| `src/agent-academy-client/src/api.ts` | TypeScript types and API functions for config + template endpoints |

**SettingsPanel sections** (top to bottom):
1. **Agents** — lists all catalog agents as expandable cards
2. **Instruction Templates** — lists all templates with Create New button
3. **Notifications** — existing notification provider cards (unchanged)

**AgentConfigCard** (per agent):
- Collapsed: agent name, role, model badge, "Customized" badge if override exists
- Expanded: fetches config from `GET /api/agents/{agentId}/config`, shows form:
  - Model Override (text input, placeholder shows catalog default)
  - Startup Prompt Override (textarea)
  - Instruction Template (dropdown populated from template list)
  - Custom Instructions (textarea)
  - Save (PUT config) / Reset to Defaults (POST reset with confirmation dialog)

**TemplateCard** (per template):
- Collapsed: template name, description
- Expanded: edit form (name, description, content textarea), Save/Delete buttons
- Delete shows confirmation dialog; FK SetNull cascade clears agent assignments
- "Create New" variant: inline form with Cancel/Create buttons

**API functions** added to `api.ts`:

| Function | Method | Route |
|----------|--------|-------|
| `getAgentConfig(agentId)` | GET | `/api/agents/{agentId}/config` |
| `upsertAgentConfig(agentId, req)` | PUT | `/api/agents/{agentId}/config` |
| `resetAgentConfig(agentId)` | POST | `/api/agents/{agentId}/config/reset` |
| `getInstructionTemplates()` | GET | `/api/instruction-templates` |
| `getInstructionTemplate(id)` | GET | `/api/instruction-templates/{id}` |
| `createInstructionTemplate(req)` | POST | `/api/instruction-templates` |
| `updateInstructionTemplate(id, req)` | PUT | `/api/instruction-templates/{id}` |
| `deleteInstructionTemplate(id)` | DELETE | `/api/instruction-templates/{id}` |

### Seed Templates

> **Status: Implemented** — 3 built-in templates seeded via EF migration.

**Migration**: `20260330000510_SeedInstructionTemplates`

| Template | Stable ID | Description |
|----------|-----------|-------------|
| Verification-First | `b1a2c3d4-...` | Verify all code with builds/tests before presenting |
| Pushback-Enabled | `c2b3d4e5-...` | Evaluate requests critically, push back on problems |
| Code Review Focus | `d3c4e5f6-...` | Find real bugs and security issues, ignore style |

Templates are inserted in `Up()` and deleted in `Down()` using stable GUIDs for idempotency.

## Revision History

| Date | Change | Task |
|------|--------|------|
| Initial | Created agent execution system — interface, CopilotExecutor, StubExecutor | copilot-executor |
| 2026-03-28 | Fixed CopilotExecutor auth: added OnPermissionRequest + IConfiguration token support | copilot-auth-sse |
| 2026-03-28 | Added SSE activity stream as SignalR alternative | copilot-auth-sse |
| 2026-03-28 | OAuth token → Copilot SDK activation: CopilotTokenProvider, token-change-aware executor, SaveTokens=true | auth-sdk-flow |
| 2026-04-01 | Added proactive SDK auth probe with transition-based notifications | proactive-auth-probe |
| 2026-03-28 | Documented CLI path configuration (Copilot:CliPath, system vs bundled binary) | cli-path-docs |
| 2026-03-28 | StubExecutor: replaced canned role-based responses with deterministic offline notice | stub-offline-notice |
| 2026-03-29 | Agent configuration overrides — DB schema, AgentConfigService, orchestrator integration | agent-config-phase1 |
| 2026-03-29 | Agent config API — CRUD endpoints for agent config overrides and instruction templates | agent-config-phase2 |
| 2026-03-30 | Frontend agent config UI — Settings panel with agent config cards, template management, 3 seed templates | agent-config-phase3-4 |
| 2026-04-04 | LLM usage tracking — `LlmUsageTracker` captures `AssistantUsageEvent` from Copilot SDK. Persists per-request metrics (model, input/output/cache tokens, cost, duration, reasoning effort) to `llm_usage` table. REST APIs: room usage aggregation, per-agent breakdown, individual records, global usage with time filter. Resolves "No token/usage tracking" known gap. Adversarial review (GPT-5.3 Codex): 4 findings — 3 fixed (decimal precision, unsafe casts, input validation), 1 accepted (multi-query race). 20 new tests. | usage-tracking |
| 2026-04-05 | SDK tool calling — `AgentToolRegistry` maps `EnabledTools` groups to `AIFunction` objects. 5 read-only tools: `list_tasks`, `list_rooms`, `show_agents` (task-state group), `read_file`, `search_code` (code group). `AgentPermissionHandler` denies dangerous permission kinds (shell/write/url). `CopilotExecutor` passes tools in `SessionConfig`. Resolves "No tool calling" known gap. Adversarial review (GPT-5.3 Codex, Claude Opus 4.6, Claude Sonnet 4.5): 14 findings — 7 fixed (permission handler blanket approve, stderr deadlock, path traversal bypass, FindProjectRoot fallback, max-count per-file, no timeout, regex vs fixed-string). 34 new tests. Note: `list_agents` renamed to `show_agents` (2026-04-06) to avoid conflict with Copilot CLI built-in tool of the same name. | sdk-tool-calling |
| 2026-04-05 | Agent write tools — 5 new tools in 2 groups: `task-write` (create_task, update_task_status, add_task_comment) and `memory` (remember, recall). Inner wrapper classes capture agent identity via closures. `IAgentToolRegistry` extended with agentId/agentName parameters for contextual groups. Reuses `RememberHandler.ValidCategories` and `RecallHandler.SearchWithFts5Async`. All 6 agents updated. 35 new tests (1154 total). | agent-write-tools |
| 2026-04-05 | Session history UI — `ConversationSessionService` extended with `GetRoomSessionsAsync`, `GetAllSessionsAsync`, `GetSessionStatsAsync` query methods with pagination, status filtering, and `hoursBack` time window. New `SessionController` (`GET /api/sessions`, `GET /api/sessions/stats`), room sessions endpoint (`GET /api/rooms/{roomId}/sessions`). Frontend: `SessionHistoryPanel` in dashboard with stats cards, filter tabs, expandable summaries, pagination. `ChatPanel` session resume indicator shows when agents have archived context. 21 new backend tests (1319 total), 16 new frontend tests (218 total). | session-history |
| 2026-04-05 | OAuth refresh token — `CopilotTokenProvider` extended with `RefreshToken`, `ExpiresAtUtc`, `IsTokenExpiringSoon`, `CanRefresh`, cookie write-back flag. `ICopilotAuthProbe.RefreshTokenAsync()` exchanges refresh tokens at GitHub's OAuth endpoint. `CopilotAuthMonitorService` proactively refreshes 30 min before expiry and attempts refresh before degrading on auth failure. `Program.cs` captures refresh token in OAuth callback, restores from cookie on restart, merges refreshed tokens into cookie. Access tokens auto-renew for up to 6 months without re-authentication. 21 new tests (1343 total). Adversarial review (GPT-5.2, Claude Sonnet 4, Claude Haiku 4.5): timeout handling, token clobbering, and cookie error handling fixed. | token-refresh |
