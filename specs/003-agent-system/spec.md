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
    Task<string> RunAsync(AgentDefinition agent, string prompt, string? roomId, CancellationToken ct = default);
    Task InvalidateSessionAsync(string agentId, string? roomId);
    Task InvalidateRoomSessionsAsync(string roomId);
    Task DisposeAsync();
}
```

| Member | Description |
|--------|-------------|
| `IsFullyOperational` | `true` when backed by a live LLM provider |
| `RunAsync` | Sends prompt, returns complete response text |
| `InvalidateSessionAsync` | Disposes a single cached session |
| `InvalidateRoomSessionsAsync` | Disposes all sessions for a room |
| `DisposeAsync` | Releases all resources |

### Implementation: `CopilotExecutor`

**File**: `src/AgentAcademy.Server/Services/CopilotExecutor.cs`
**NuGet**: `GitHub.Copilot.SDK` v0.2.0

Key behaviors:
- **Lazy client initialization**: `CopilotClient` is created on first use and cached.
- **Token resolution chain**: Resolved in priority order via `ResolveToken()`:
  1. **User OAuth token** — from `CopilotTokenProvider` (captured during GitHub OAuth login)
  2. **Config token** — `Copilot:GitHubToken` from `IConfiguration` (appsettings / user-secrets)
  3. **Environment / CLI** — `null` passed to SDK, which checks `COPILOT_GITHUB_TOKEN`, `GH_TOKEN`, `GITHUB_TOKEN`, or copilot CLI login state
- **CLI path configuration**: `Copilot:CliPath` in `appsettings.json` controls which copilot binary the SDK uses. Must be set to `"copilot"` (system PATH) or an explicit path to an already-authenticated copilot CLI. The SDK's bundled binary (at `runtimes/linux-x64/native/copilot`) ships with no auth state and will fail to connect. The system copilot CLI (e.g., `~/.local/bin/copilot`) uses existing `copilot auth login` credentials.
- **Token-change awareness**: When the resolved token changes (e.g., user logs in after server was using a config token), the old `CopilotClient` is disposed, all sessions are cleared, and a new client is created with the new token. Failure state is reset so the new token gets a fresh attempt.
- **Permission handling**: Sessions are created with `OnPermissionRequest = PermissionHandler.ApproveAll` (required by SDK v0.2.0). Safe because no SDK tools are registered in session config. Must be revisited when tool calling is wired up.
- **Session-per-agent-per-room**: Sessions keyed by `{agentId}:{roomId}`, default room is `"default"`.
- **Streaming aggregation**: Subscribes to `AssistantMessageDeltaEvent` for incremental tokens, uses `AssistantMessageEvent` for the final complete content.
- **Session priming**: Sends `AgentDefinition.StartupPrompt` as the first message to establish agent identity.
- **Model selection**: Uses `AgentDefinition.Model` in `SessionConfig`, defaults to `"gpt-5"`.
- **TTL cleanup**: Sessions expire after 10 minutes of inactivity; a background timer runs every 2 minutes.
- **Automatic fallback**: If `CopilotClient.StartAsync()` fails or any individual call fails, delegates to `StubExecutor`.
- **Request timeout**: 2-minute timeout per `SendAsync` call.
- **Graceful disposal**: All sessions and the client are disposed on shutdown.

### Token Provider: `CopilotTokenProvider`

**File**: `src/AgentAcademy.Server/Services/CopilotTokenProvider.cs`

Singleton service bridging GitHub OAuth login to `CopilotExecutor`:
- `SetToken(string)` — called during `OnCreatingTicket` in the OAuth flow to capture the user's access token.
- `ClearToken()` — called by `AuthController.Logout` to remove the stored token.
- `Token` — volatile read; available to the executor even during background orchestration (where `HttpContext` is null).

### Authentication Flow → Copilot SDK Activation

```
┌────────────────┐     ┌───────────────────┐     ┌──────────────────┐
│  User Browser  │────▶│  GitHub OAuth      │────▶│  OnCreatingTicket │
│  GET /login    │     │  (GitHub.com)      │     │  (Program.cs)     │
└────────────────┘     └───────────────────┘     └────────┬─────────┘
                                                          │ SetToken()
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
```

**OAuth Configuration** (in `Program.cs`):
- `SaveTokens = true` — the OAuth access token persists in the encrypted auth cookie, surviving server restarts
- Token restoration middleware: on the first authenticated request after a server restart, extracts the token from the cookie and populates `CopilotTokenProvider`
- Scopes: `read:user`, `user:email`
- GitHub App credentials stored in user-secrets (`GitHub:ClientId`, `GitHub:ClientSecret`, `GitHub:AppId`)

**Behavior**:
- Before any user logs in: executor uses config token or env vars; falls back to `StubExecutor` if none available
- After user logs in: OAuth token is captured → executor creates/recreates `CopilotClient` with user's token → agents produce real responses
- On server restart: first authenticated request restores the token from the auth cookie via middleware — no re-login needed
- On logout: token is cleared → executor falls back to config token or stub on next call

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

- **Single-user token model**: `CopilotTokenProvider` stores one global token (last authenticated user). In a multi-user deployment, User B's login overwrites User A's token. Acceptable for the current single-user / small-team use case. A per-user `ConcurrentDictionary<userId, token>` model would be needed for true multi-tenancy.
- **No token/usage tracking**: `CopilotExecutor` does not yet track input/output tokens or cost. The v1 `AgentEventTracker` integration is pending.
- **No tool calling**: The Copilot SDK supports registering C# methods as tools callable by the model. Not yet wired up. When enabled, `OnPermissionRequest` must be changed from `ApproveAll` to a restrictive handler.
- **Session compaction**: The SDK may support session compaction for long conversations. Not yet implemented.
- **No per-project session resume**: Sessions are cleared on project switch. If a user returns to a previous project, agents start fresh — they don't resume their prior conversation context.

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

## Revision History

| Date | Change | Task |
|------|--------|------|
| Initial | Created agent execution system — interface, CopilotExecutor, StubExecutor | copilot-executor |
| 2026-03-28 | Fixed CopilotExecutor auth: added OnPermissionRequest + IConfiguration token support | copilot-auth-sse |
| 2026-03-28 | Added SSE activity stream as SignalR alternative | copilot-auth-sse |
| 2026-03-28 | OAuth token → Copilot SDK activation: CopilotTokenProvider, token-change-aware executor, SaveTokens=true | auth-sdk-flow |
| 2026-03-28 | Documented CLI path configuration (Copilot:CliPath, system vs bundled binary) | cli-path-docs |
| 2026-03-28 | StubExecutor: replaced canned role-based responses with deterministic offline notice | stub-offline-notice |
| 2026-03-29 | Agent configuration overrides — DB schema, AgentConfigService, orchestrator integration | agent-config-phase1 |
| 2026-03-29 | Agent config API — CRUD endpoints for agent config overrides and instruction templates | agent-config-phase2 |
