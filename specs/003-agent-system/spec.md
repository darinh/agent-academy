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
- **Token-change awareness**: When the resolved token changes (e.g., user logs in after server was using a config token), the old `CopilotClient` is disposed, all sessions are cleared, and a new client is created with the new token. Failure state is reset so the new token gets a fresh attempt. If the executor was in an auth-failed state, a recovery message is posted to the main room.
- **Error classification**: `SessionErrorEvent.Data.ErrorType` is classified into typed exceptions:
  - `authentication` → `CopilotAuthException` (definitive — no retry, triggers auth failure notification)
  - `authorization` → `CopilotAuthorizationException` (no retry — token lacks required permissions)
  - `quota`, `rate_limit` → `CopilotQuotaException` (retried with longer backoff: 5s/15s/30s, max 3 attempts)
  - Other/unknown → `CopilotTransientException` (retried with backoff: 2s/4s/8s, max 3 attempts)
- **Auth failure handling**: On definitive auth failure, the executor sets `IsAuthFailed = true`, posts a re-authentication notice to the main room, and notifies via the notification system. When the user re-authenticates (token changes), the flag is cleared and a recovery message is posted automatically.
- **Permission handling**: Sessions are created with `OnPermissionRequest = PermissionHandler.ApproveAll` (required by SDK v0.2.0). Safe because no SDK tools are registered in session config. Must be revisited when tool calling is wired up.
- **Session-per-agent-per-room**: Sessions keyed by `{agentId}:{roomId}`, default room is `"default"`.
- **Streaming aggregation**: Subscribes to `AssistantMessageDeltaEvent` for incremental tokens, uses `AssistantMessageEvent` for the final complete content.
- **Session priming**: Sends `AgentDefinition.StartupPrompt` as the first message to establish agent identity. The startup prompt is NOT repeated in per-round prompts — it lives only in the session priming to avoid redundant context accumulation.
- **Model selection**: Uses `AgentDefinition.Model` in `SessionConfig`, defaults to `"gpt-5"`.
- **TTL cleanup**: Sessions expire after 10 minutes of inactivity; a background timer runs every 2 minutes.
- **Automatic fallback**: If `CopilotClient.StartAsync()` fails or any individual call fails (after retry exhaustion), delegates to `StubExecutor`.
- **Graceful disposal**: All sessions and the client are disposed on shutdown.

### Token Provider: `CopilotTokenProvider`

**File**: `src/AgentAcademy.Server/Services/CopilotTokenProvider.cs`

Singleton service bridging GitHub OAuth login to `CopilotExecutor`:
- `SetToken(string)` — called during `OnCreatingTicket` in the OAuth flow to capture the user's access token. Records `TokenSetAt` timestamp for diagnostics.
- `ClearToken()` — called by `AuthController.Logout` to remove the stored token and timestamp.
- `Token` — volatile read; available to the executor even during background orchestration (where `HttpContext` is null).
- `TokenSetAt` — UTC timestamp of when the token was last set (nullable, null if never set). Used by the health endpoint for diagnostics.

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
- **No per-project session resume**: Sessions are cleared on project switch. If a user returns to a previous project, agents start fresh — they don't resume their prior conversation context.

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
| 2026-03-28 | Documented CLI path configuration (Copilot:CliPath, system vs bundled binary) | cli-path-docs |
| 2026-03-28 | StubExecutor: replaced canned role-based responses with deterministic offline notice | stub-offline-notice |
| 2026-03-29 | Agent configuration overrides — DB schema, AgentConfigService, orchestrator integration | agent-config-phase1 |
| 2026-03-29 | Agent config API — CRUD endpoints for agent config overrides and instruction templates | agent-config-phase2 |
| 2026-03-30 | Frontend agent config UI — Settings panel with agent config cards, template management, 3 seed templates | agent-config-phase3-4 |
