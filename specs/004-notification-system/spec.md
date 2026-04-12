# 004 — Notification System

## Purpose

Defines the pluggable notification provider architecture for Agent Academy. Notifications are the primary channel for communicating agent activity, collecting user input, and integrating with external messaging platforms (Slack, Discord, etc.).

## Current Behavior — Implemented

### Architecture

```
┌──────────────────────────────────────────────┐
│              NotificationManager             │
│         (singleton, thread-safe)             │
│                                              │
│  ConcurrentDictionary<string, IProvider>     │
│                                              │
│  SendToAllAsync()      → fan-out broadcast   │
│  RequestInputFromAnyAsync() → first-wins     │
├──────────┬──────────┬────────────────────────┤
│ Console  │ Discord  │  Slack     │  Custom?  │
│ Provider │ Provider │  Provider  │  (future) │
└──────────┴──────────┴────────────┴───────────┘
```

### Core Interface

**`INotificationProvider`** — implemented by every notification channel:

| Member | Type | Description |
|--------|------|-------------|
| `ProviderId` | `string` | Unique identifier (e.g., `"console"`, `"slack"`) |
| `DisplayName` | `string` | Human-readable name for UI |
| `IsConfigured` | `bool` | Whether provider has been configured |
| `IsConnected` | `bool` | Whether provider is actively connected |
| `LastError` | `string?` | Most recent connection error message, or null if last connection succeeded. Default interface implementation returns `null`. Surfaced in the provider status API so the UI can explain why a configured provider is not connected. Cleared at the start of each connection attempt. |
| `ConfigureAsync()` | `Task` | Apply provider-specific settings |
| `ConnectAsync()` | `Task` | Establish connection |
| `DisconnectAsync()` | `Task` | Tear down connection |
| `SendNotificationAsync()` | `Task<bool>` | Deliver a notification message |
| `RequestInputAsync()` | `Task<UserResponse?>` | Collect user input (null if unsupported) |
| `SendAgentQuestionAsync()` | `Task<(bool, string?)>` | Send agent question to human; returns sent status + error detail |
| `SendDirectMessageAsync()` | `Task<bool>` | Send a direct message notification (e.g., to Discord channel) |
| `GetConfigSchema()` | `ProviderConfigSchema` | Describe required configuration fields |
| `OnRoomRenamedAsync()` | `Task` | Update external resources on room rename (default: no-op) |
| `OnRoomClosedAsync()` | `Task` | Clean up external resources on room archive (default: no-op) |

### NotificationManager

- **Thread-safe**: Uses `ConcurrentDictionary` for provider storage
- **Fan-out delivery**: `SendToAllAsync` sends to every connected provider
- **Failure isolation**: Individual provider failures are logged, never propagated
- **Input collection**: `RequestInputFromAnyAsync` iterates connected providers (order not guaranteed — uses `ConcurrentDictionary.Values`), returns first non-null response
- **Agent questions**: `SendAgentQuestionAsync` returns `(bool Sent, string? Error)` tuple — surfaces actual provider errors instead of generic failure messages; tries all providers before failing

### Built-in Provider: Console

The `ConsoleNotificationProvider` serves as the reference implementation:
- Always configured and connected
- Logs notifications via `ILogger`
- Cannot collect input (returns `null`)
- Zero configuration fields

### Connection Error Handling

Providers surface connection failures through the `LastError` property so the frontend can display actionable error messages instead of generic "not connected" states.

**Behavior contract:**
- `LastError` is cleared (`null`) at the start of each `ConnectAsync()` call
- On connection failure, `LastError` is set to a human-readable message explaining the cause
- The `GET /providers` endpoint includes `LastError` in `ProviderStatusDto`, so the UI can display it when `IsConfigured == true && IsConnected == false`
- The `POST /providers/{id}/connect` endpoint returns the provider's `LastError` (not the raw exception) in the error response body

**Discord-specific error extraction:**
The `DiscordNotificationProvider` parses known Discord gateway close codes and HTTP errors into actionable messages:
- **4014 (Disallowed Intent)**: Missing privileged intents — instructs the user to enable Message Content Intent in the Developer Portal
- **4004 (Auth Failed)**: Invalid token at the gateway level
- **401 Unauthorized**: HTTP auth failure during login — typically an expired or malformed bot token
- Other exceptions: fall back to the exception message

**Frontend display:**
When a provider has `IsConfigured == true`, `IsConnected == false`, and `LastError` is non-null, the Settings panel displays an error banner with the `LastError` text. The Notification Setup Wizard includes a step about enabling the Message Content Intent.

### REST API

All endpoints under `/api/notifications`:

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/providers` | List all providers with status (returns `ProviderStatusDto[]` — see below) |
| `GET` | `/providers/{id}/schema` | Get provider's config schema |
| `POST` | `/providers/{id}/configure` | Apply configuration |
| `POST` | `/providers/{id}/connect` | Connect provider |
| `POST` | `/providers/{id}/disconnect` | Disconnect provider |
| `POST` | `/test` | Send test notification to all |
| `GET` | `/deliveries` | List notification delivery records (filterable by channel, providerId, status, roomId) |
| `GET` | `/deliveries/stats` | Aggregate delivery counts by status within a time window |

### Shared Types

Defined in `AgentAcademy.Shared.Models.Notifications`:

- `NotificationType` — enum: AgentThinking, NeedsInput, TaskComplete, TaskFailed, SpecReview, Error
- `NotificationMessage` — record: Type, Title, Body, RoomId?, AgentName?, Actions?
- `InputRequest` — record: Prompt, RoomId?, AgentName?, Choices?, AllowFreeform
- `UserResponse` — record: Content, SelectedChoice?, ProviderId
- `ProviderConfigSchema` — record: ProviderId, DisplayName, Description, Fields
- `ConfigField` — record: Key, Label, Type, Required, Description?, Placeholder?
- `ProviderStatusDto` — record: ProviderId, DisplayName, IsConfigured, IsConnected, LastError?

### DI Registration

In `Program.cs`:
- `NotificationManager` registered as singleton
- `ConsoleNotificationProvider` registered as singleton
- `DiscordNotificationProvider` registered as singleton
- `DiscordMessageSender` registered as singleton
- `DiscordMessageRouter` registered as singleton
- `DiscordChannelManager` registered as singleton
- `DiscordInputHandler` registered as singleton
- `SlackNotificationProvider` registered as singleton
- `ActivityNotificationBroadcaster` registered as hosted service
- All providers registered with manager at startup
- Saved provider configs auto-restored from DB on startup (non-blocking)

### Activity Event Bridge

The `ActivityNotificationBroadcaster` is an `IHostedService` that subscribes to `ActivityBroadcaster` and forwards selected activity events to `NotificationManager.SendToAllAsync()`.

**Notifiable event types** (all others are filtered out):

| ActivityEventType | NotificationType | Title Format |
|---|---|---|
| `MessagePosted` | `NeedsInput` | "New message in {Room}" |
| `TaskCreated` | `TaskComplete` | "Task created: {Actor}" |
| `AgentErrorOccurred` | `Error` | "Agent error: {Actor}" |
| `AgentWarningOccurred` | `Error` | "Agent warning: {Actor}" |
| `CommandExecuted` | `TaskComplete` | "Command executed: {Actor}" |
| `CommandDenied` | `Error` | "Command denied: {Actor}" |
| `CommandFailed` | `Error` | "Command failed: {Actor}" |

**Design decisions**:
- **Human message suppression**: `MessagePosted` events where `ActorId == "human"` are filtered out before forwarding. This prevents Discord from echoing back messages the user just typed (whether from Discord or the web UI). Humans don't need to be notified about their own messages.
- Noisy events (`AgentThinking`, `AgentFinished`, `PresenceUpdated`, `PhaseChanged`) are excluded
- Follows the same `IHostedService` pattern as `ActivityHubBroadcaster` (SignalR bridge)
- Fire-and-forget: exceptions are logged but never propagate to the `ActivityBroadcaster`
- `MapToNotification()` is `internal static` for direct unit testing

### Configuration Persistence

Provider configuration is persisted in the `notification_configs` SQLite table via `NotificationConfigEntity`:

| Column | Type | Description |
|--------|------|-------------|
| `Id` | `int` | Auto-increment primary key |
| `ProviderId` | `string` | Provider identifier (e.g., "discord") |
| `Key` | `string` | Config key (e.g., "BotToken") |
| `Value` | `string` | Config value |
| `UpdatedAt` | `DateTime` | Last update timestamp |

- Unique index on `(ProviderId, Key)` — enforced by the DB
- Upsert via `INSERT ... ON CONFLICT DO UPDATE` (atomic, no race conditions)
- On startup: saved configs are loaded, providers are configured and connected in a background `Task.Run` (non-blocking)

#### Configuration Encryption

Secret configuration values (e.g., Discord bot tokens, Slack OAuth tokens) are encrypted at rest using the `ConfigEncryptionService`, which wraps ASP.NET Core Data Protection API.

**Service Registration** (in `Program.cs` line 292):
```csharp
builder.Services.AddSingleton<ConfigEncryptionService>();
```

**Implementation** (`src/AgentAcademy.Server/Notifications/ConfigEncryptionService.cs`):
- Uses `IDataProtectionProvider.CreateProtector("AgentAcademy.NotificationConfig")` for consistent key derivation
- Encrypts values with `Protect()`, prepends `ENC.v1:` prefix to ciphertext
- Decrypts with `TryDecrypt()` — returns `false` only on actual decryption failure (not for plaintext/null/empty)
- `IsEncrypted()` static method checks for `ENC.v1:` prefix

**Encryption Contract:**
- **Prefix:** `ENC.v1:` identifies encrypted values, enables versioned format evolution
- **Empty/null handling:** `Encrypt()` returns input as-is if null or empty; `TryDecrypt()` returns `true` for null/empty
- **Plaintext tolerance:** Values without `ENC.v1:` prefix are treated as plaintext, returned successfully by `TryDecrypt()`
- **Failure semantics:** `TryDecrypt()` returns `false` only when decryption fails (e.g., key rotation without migration), sets `result = ""`

**Field Type Detection** (schema-driven):

The controller determines which fields to encrypt by inspecting the provider's `ConfigSchema`:

```csharp
var schema = provider.GetConfigSchema();
var secretKeys = schema.Fields
    .Where(f => string.Equals(f.Type, "secret", StringComparison.OrdinalIgnoreCase))
    .Select(f => f.Key)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
```

Fields with `Type = "secret"` are encrypted before database persistence; all others stored as plaintext.

**Encryption on Write** (`NotificationController.Configure`, lines 90-104):

When `POST /api/notifications/providers/{id}/configure` is called:
1. Provider's config schema is fetched
2. Fields marked `Type = "secret"` are identified
3. Secret values are passed through `ConfigEncryptionService.Encrypt()` before DB upsert
4. Non-secret values stored as plaintext

**Decryption on Read** (`Program.cs` startup auto-restore, lines 415-446):

On server startup, saved configs are restored from DB:
1. Schema fetched to identify secret fields (same logic as write path)
2. Secret values passed through `ConfigEncryptionService.TryDecrypt()`
3. Decryption failure logs warning, skips provider auto-restore: `"Notification provider '{ProviderId}' has undecryptable config keys: {Keys}. Reconfiguration required."`
4. Non-secret values used as-is
5. Provider configured and connected with decrypted config

**Transparent Migration from Plaintext:**

Existing plaintext values (from before encryption was implemented) are handled gracefully:
- On read: `TryDecrypt()` recognizes lack of `ENC.v1:` prefix, returns plaintext as-is with `success = true`
- On next write: plaintext value is encrypted and persisted with `ENC.v1:` prefix
- No manual migration required — happens automatically when provider is reconfigured

**Key Storage:**

ASP.NET Core Data Protection keys are persisted at `~/.local/share/AgentAcademy/DataProtection-Keys/` (explicit configuration in `Program.cs` via `PersistKeysToFileSystem`). Key rotation is handled automatically by the Data Protection API.

### Frontend Integration

The `NotificationSetupWizard` component is accessible via the **Settings** tab in the main workspace tab bar. It supports **any registered notification provider** through a data-driven approach.

- Accepts a `providerId` prop — determines which provider to configure
- Fetches config schema dynamically from `GET /api/notifications/providers/{id}/schema`
- **Step 1** — Provider-specific setup instructions (Discord: Developer Portal + invite URL; Slack: app creation + OAuth scopes; unknown: generic fallback)
- **Step 2** — Dynamic credential form rendered from the schema's `fields` array (secret fields use `type="password"`)
- **Step 3** — Configure → Connect → Test flow (identical for all providers)
- Wizard renders in **inline mode** (no overlay backdrop) when used as tab content
- Wizard supports both `inline` and overlay modes via the `inline` prop
- `onClose` prop is optional when in inline mode
- Provider-specific instruction components: `DiscordInstructions`, `SlackInstructions`, `GenericInstructions`

### Discord Provider

The `DiscordNotificationProvider` connects to Discord via the Discord.Net library (`DiscordSocketClient`). Supports room-based channel routing with webhook-based agent identity.

**Configuration** (via `ConfigureAsync`):
| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `BotToken` | `secret` | Yes | Discord bot token from developer portal |
| `GuildId` | `string` | Yes | Discord server (guild) ID |
| `ChannelId` | `string` | Yes | Fallback notification channel ID |

**Required bot permissions** (server-level role, not per-channel):
- Manage Channels — create room channels and categories
- Manage Webhooks — create webhooks for agent identity
- Send Messages — post in channels
- Create Public Threads — ASK_HUMAN question threads
- Send Messages in Threads — reply in threads

**Connection lifecycle**:
- `ConfigureAsync` — validates and stores bot token, guild ID, channel ID
- `ConnectAsync` — creates `DiscordSocketClient`, logs in with bot token, waits for Ready event (30s timeout), rebuilds channel mappings from existing Discord state
- `DisconnectAsync` — stops and disposes the client; implements `IAsyncDisposable`

#### Room-Based Channel Routing

Each Agent Academy room gets a dedicated Discord channel under a project-specific category. Rooms are grouped by their workspace's project name — each onboarded project gets its own Discord category. Messages are routed by `RoomId` from the `NotificationMessage`.

**Discord server structure**:
```
📁 Nonogram Rooms (category — auto-created per project)
  💬 main-collaboration-room                   ← mirrors AA room
  💬 task-implement-solver                     ← mirrors AA task room
📁 Agent Academy Rooms (category — another project)
  💬 main-collaboration-room                   ← mirrors AA room
📁 Agent Academy Messages (category — DM channels)
  💬 aristotle                                 ← agent DM channel
    🧵 What database should I use?             ← DM thread
📁 Rooms (category — legacy rooms without workspace)
  💬 main-collaboration-room                   ← legacy room
```

**Project resolution**: When creating a channel for a room, the provider resolves the room's project name via `RoomService.GetProjectNameForRoomAsync(roomId)` which follows the chain: `roomId → RoomEntity.WorkspacePath → WorkspaceEntity.ProjectName`. If `ProjectName` is null, falls back to the workspace directory basename.

**Category naming**: Room categories are named `"{projectName} Rooms"` for workspace-scoped rooms. DM/message categories are named `"{projectName} Messages"`. Legacy rooms (no `WorkspacePath`) use the `"Rooms"` category. Categories are cached in memory keyed by project name.

**Channel creation**: Lazy (on first message to a room). Categories and channels are created inside a `_channelCreateLock` semaphore to prevent duplicates.

**Channel naming**: Room channels use the room name in kebab-case. Agent DM channels use the agent name in kebab-case. Discord text channels cannot have spaces or uppercase — this is a platform constraint. Example: "Main Collaboration Room" → `main-collaboration-room`, "Aristotle" → `aristotle`.

**Channel topic**: Room channels use descriptive text with a `· ID: {roomId}` tag for startup recovery. DM channels use `"Direct messages — {agentName} · Room: {roomId}"`.

**Fallback**: If room channel creation fails (e.g., missing permissions), notifications fall back to the configured `_channelId` default channel. A warning is logged with the specific permissions needed.

**Startup recovery** (`RebuildChannelMappingAsync`): Scans existing Discord categories to rebuild in-memory channel mappings. Room channels identified by `"* Rooms"` suffix (and legacy `"AA: *"`, `"Agent Academy"`). DM channels identified by `"* Messages"` suffix (and legacy `"aa-*"` prefix). Room IDs parsed from channel topics (supports both old and new formats).

#### Webhook-Based Agent Identity

Messages sent to room channels use Discord webhooks, allowing each agent to appear as a distinct sender with a custom name and avatar.

**Implementation**:
- One webhook per room channel, named "Agent Academy", cached in `_webhooks` dictionary
- Webhook creation synchronized inside `_channelCreateLock` (Discord has a 15-webhook-per-channel limit)
- Agent display names: humanized from agent ID (e.g., `"planner-1"` → `"Planner 1"`) or used as-is if already a name
- Agent avatars: DiceBear Identicons (`https://api.dicebear.com/9.x/identicon/png?seed={agentName}`)
- Regular messages: plain text via webhook (clean, native appearance)
- Error/system messages: compact embed via webhook (colored, timestamped)
- Fallback: if webhook creation fails, messages fall back to regular bot embed format

#### Bidirectional Message Bridging

Human messages posted in Discord room channels are routed back to the correct Agent Academy room.

**Flow**: Discord message → `OnAgentChannelMessageReceived` → check `_channelToRoom` mapping → `PostHumanMessageAsync(roomId, content)` → `_orchestrator.HandleHumanMessage(roomId)` → agents respond.

- Webhook messages (from the bot itself) are ignored to prevent loops
- Non-text messages get a hint: "Please reply with text"
- Successful delivery confirmed with ✅ reaction
- Failed delivery: error message posted in Discord channel
- The orchestrator trigger (`HandleHumanMessage`) is critical — without it, messages are stored but agents never respond

#### ASK_HUMAN — Agent-to-Human Question Bridge

Creates a dedicated Discord structure for agent questions with threaded replies.

**Flow**: Agent → `DM` command → `DmHandler` → `NotificationManager.SendAgentQuestionAsync` → `DiscordProvider.SendAgentQuestionAsync` → category/channel/thread creation → human replies routed back via `PostHumanMessageAsync` + `HandleHumanMessage`.

**Discord structure**: `"{ProjectName} Messages"` category, channel per agent (kebab-case agent name), thread per DM. Channel topics include `· Room: {roomId}` for startup recovery.

**Error propagation**: `NotificationManager.SendAgentQuestionAsync` returns `(bool Sent, string? Error)` tuple. The handler surfaces actual error details to the agent (e.g., "Provider 'discord' error: Missing Permissions") instead of a generic "no provider connected" message. Provider failover: tries all connected providers before returning failure, collecting the last error.

**Missing permissions catch**: Specific `Discord.Net.HttpException` catch for error code 50013 logs the exact permissions needed.

**Notification delivery** (`SendNotificationAsync`):
- Routes by `RoomId` to room-specific channels when available
- Falls back to configured `_channelId` default channel when no `RoomId` or on permission failure
- Color coding for embeds (fallback format):
  - 🔵 Blue — AgentThinking
  - 🟡 Gold — NeedsInput
  - 🟢 Green — TaskComplete
  - 🔴 Red — TaskFailed, Error
  - 🟣 Purple — SpecReview
- Action buttons rendered as Discord button components

**Input collection** (`RequestInputAsync` — delegated to `DiscordInputHandler`):
- `DiscordInputHandler` is a stateless helper that receives `DiscordSocketClient`, channel ID, and owner ID as method parameters (no mutable state or `Configure()`).
- **Choice mode**: Sends buttons for each choice, waits for `InteractionCreated` event on the sent message
- **Freeform mode**: Sends prompt embed, waits for next non-bot text message in the channel
- Returns `null` on timeout or cancellation

**Error handling**:
- Send failures are logged and return `false` (never throw)
- Missing permissions (50013) caught specifically with actionable log message
- `IsConnected` reflects actual `DiscordSocketClient.ConnectionState`
- Disconnections are logged via the client's `Disconnected` event
- Connection uses `SemaphoreSlim` to prevent concurrent connect/disconnect races

### Slack Provider

The `SlackNotificationProvider` connects to Slack via the Slack Web API using raw `HttpClient` (no external Slack NuGet dependency). Sends notifications, agent questions, and DMs to Slack channels.

**Configuration** (via `ConfigureAsync`):
| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `BotToken` | `secret` | Yes | Slack bot token (xoxb-...) from https://api.slack.com/apps |
| `DefaultChannelId` | `string` | Yes | Fallback channel ID for notifications |

**Required bot scopes** (OAuth & Permissions):
- `chat:write` — post messages in channels
- `chat:write.customize` — customize bot name/icon per message
- `channels:manage` — create and rename channels
- `channels:read` — list channels for startup recovery
- `channels:join` — join channels the bot creates

**Connection lifecycle**:
- `ConfigureAsync` — validates and stores bot token and default channel ID
- `ConnectAsync` — creates `SlackApiClient`, validates token via `auth.test`, rebuilds channel mappings from existing Slack channels
- `DisconnectAsync` — marks provider as disconnected (no persistent connection to tear down — Slack Web API is stateless)

#### HTTP API Client (`SlackApiClient`)

Thin wrapper around `HttpClient` for Slack Web API methods. All responses are deserialized into strongly-typed `SlackBaseResponse` derivatives.

**Supported methods**:
| Slack API Method | Client Method | Purpose |
|---|---|---|
| `auth.test` | `AuthTestAsync` | Validate bot token |
| `chat.postMessage` | `PostMessageAsync` | Send messages to channels |
| `conversations.create` | `CreateChannelAsync` | Create room channels |
| `conversations.list` | `ListChannelsAsync` | List channels for recovery |
| `conversations.setTopic` | `SetChannelTopicAsync` | Set channel topic with room ID |
| `conversations.rename` | `RenameChannelAsync` | Rename room channels |
| `conversations.archive` | `ArchiveChannelAsync` | Archive closed room channels |
| `conversations.join` | `JoinChannelAsync` | Join created channels |

#### Room-Based Channel Routing

Each Agent Academy room gets a dedicated Slack channel. Channel names are derived from the project name and a truncated room ID (e.g., `agent-academy-a1b2c3d4`).

**Channel creation**: Lazy (on first message to a room). Created inside a `_channelCreateLock` semaphore to prevent duplicates. If channel name is taken, the provider searches for the existing channel and reuses it.

**Channel naming**: `{project-name}-{roomId[0:8]}` in kebab-case, lowercased, max 80 chars. `ToSlackChannelName()` strips invalid characters (Slack allows only lowercase alphanumeric, hyphens, underscores).

**Channel topic**: Room channels use `"Agent Academy room · ID: {roomId}"` for startup recovery.

**Fallback**: If room channel creation fails, notifications fall back to the configured `DefaultChannelId`.

**Startup recovery** (`RebuildChannelMappingAsync`): Scans all Slack channels via `conversations.list` pagination. Channels with `"ID: {roomId}"` in their topic are mapped back to their room.

#### Agent Identity

Messages include the agent's name as the `username` parameter and a role-based emoji as `icon_emoji`. This requires the `chat:write.customize` scope. Role → emoji mapping:

| Role | Emoji |
|------|-------|
| Planner | :crystal_ball: |
| Architect | :building_construction: |
| SoftwareEngineer | :computer: |
| Reviewer | :mag: |
| Validator | :white_check_mark: |
| TechnicalWriter | :pencil: |
| Human | :bust_in_silhouette: |
| (default) | :robot_face: |

#### Message Formatting

Notifications use Slack Block Kit:
- **Header section**: Type emoji + bold title
- **Body section**: Message body (truncated to 2900 chars for Slack's 3000-char block text limit)
- **Context**: Agent name and room ID

Text is escaped for Slack mrkdwn (`&` → `&amp;`, `<` → `&lt;`, `>` → `&gt;`).

#### Agent Questions

Questions are posted to the room channel with Block Kit formatting (header, section, context). No threading — questions appear as standalone messages. Reply routing back to Agent Academy is not supported in this version (would require Slack Events API or Socket Mode).

#### Limitations (vs. Discord)

- **No input collection**: `RequestInputAsync` returns null (would require Events API / Socket Mode)
- **No reply routing**: Human replies in Slack are not routed back to Agent Academy
- **No webhook-based identity**: Uses `username`/`icon_emoji` params instead (requires `chat:write.customize` scope)
- **No category grouping**: Slack doesn't have channel categories like Discord
- **Stateless connection**: No persistent WebSocket — each API call is independent HTTP

## Interfaces & Contracts

### File Locations

| File | Purpose |
|------|---------|
| `src/AgentAcademy.Shared/Models/Notifications.cs` | Shared model types |
| `src/AgentAcademy.Server/Notifications/INotificationProvider.cs` | Provider interface |
| `src/AgentAcademy.Server/Notifications/NotificationManager.cs` | Provider orchestrator |
| `src/AgentAcademy.Server/Notifications/ConsoleNotificationProvider.cs` | Reference provider |
| `src/AgentAcademy.Server/Notifications/DiscordNotificationProvider.cs` | Discord bot provider (connection lifecycle, thin delegation wrapper) |
| `src/AgentAcademy.Server/Notifications/DiscordMessageSender.cs` | Discord outbound message delivery (room channels, agent questions, DMs, webhooks) |
| `src/AgentAcademy.Server/Notifications/DiscordMessageRouter.cs` | Discord inbound message routing (Discord → Agent Academy rooms) |
| `src/AgentAcademy.Server/Notifications/DiscordInputHandler.cs` | Discord input collection (choice buttons, freeform text) |
| `src/AgentAcademy.Server/Notifications/SlackApiClient.cs` | Slack Web API HTTP wrapper |
| `src/AgentAcademy.Server/Notifications/SlackNotificationProvider.cs` | Slack notification provider |
| `src/AgentAcademy.Server/Notifications/ActivityNotificationBroadcaster.cs` | Activity event → notification bridge |
| `src/AgentAcademy.Server/Notifications/ConfigEncryptionService.cs` | Config value encryption service |
| `src/AgentAcademy.Server/Data/Entities/NotificationConfigEntity.cs` | Config persistence entity |
| `src/AgentAcademy.Server/Controllers/NotificationController.cs` | REST API (with config persistence) |
| `src/agent-academy-client/src/NotificationSetupWizard.tsx` | Multi-provider setup wizard UI |
| `src/agent-academy-client/src/NotificationSetupWizard.css` | Wizard styles (overlay + inline) |
| `src/AgentAcademy.Server/Notifications/NotificationRetryPolicy.cs` | Retry with exponential backoff for transient failures |
| `tests/AgentAcademy.Server.Tests/NotificationManagerTests.cs` | Manager unit tests |
| `tests/AgentAcademy.Server.Tests/NotificationRetryPolicyTests.cs` | Retry policy + manager retry behavior tests |
| `tests/AgentAcademy.Server.Tests/DiscordNotificationProviderTests.cs` | Discord provider unit tests |
| `tests/AgentAcademy.Server.Tests/SlackNotificationProviderTests.cs` | Slack provider unit tests |
| `tests/AgentAcademy.Server.Tests/SlackApiClientTests.cs` | Slack API client unit tests |
| `tests/AgentAcademy.Server.Tests/ActivityNotificationBroadcasterTests.cs` | Bridge unit tests (35 tests) |

## Invariants

1. **Provider isolation**: A failing provider never prevents other providers from receiving notifications
2. **Thread safety**: All `NotificationManager` operations are safe for concurrent access
3. **No silent failures**: Every provider error is logged with context
4. **Null safety**: `RequestInputAsync` returns null (not throws) when input cannot be collected
5. **Idempotent registration**: Re-registering a provider with the same ID replaces the previous one
6. **Transient retry**: Provider calls that fail with transient errors (timeouts, network failures, HTTP 429/5xx) are retried up to 3 times with exponential backoff before being logged as failures

### Retry Policy

`NotificationRetryPolicy` provides retry with exponential backoff for transient notification provider failures. Applied by `NotificationManager` to all outbound provider calls except `RequestInputFromAnyAsync` (interactive, latency-sensitive).

| Parameter | Value | Description |
|-----------|-------|-------------|
| `MaxRetries` | 3 | Maximum retry attempts per provider call |
| `BaseDelayMs` | 200 | Initial delay before first retry |
| `MaxDelayMs` | 2000 | Cap on backoff delay |
| `JitterMs` | 50 | Random ±50ms added to each delay |

**Delay progression**: 200ms → 400ms → 800ms → 1600ms → 2000ms (capped), each ±50ms jitter.

**Transient exception classification** (`IsTransient`):
- `TimeoutException` — transient
- `HttpRequestException` — transient (network failure)
- `IOException` — transient (socket errors)
- `Discord.Net.HttpException` with HTTP 429 (rate limit) or 5xx (server error) — transient
- Wrapped exceptions with transient `InnerException` — transient
- `ArgumentException`, `InvalidOperationException`, permission errors (403) — **not** transient (no retry)

**Methods with retry**: `SendToAllAsync`, `SendAgentQuestionAsync`, `SendDirectMessageDisplayAsync`, `NotifyRoomRenamedAsync`
**Methods without retry**: `RequestInputFromAnyAsync` — interactive input collection is latency-sensitive and should fail fast

### Delivery Tracking

Every outbound notification attempt is persisted to the `notification_deliveries` table via `NotificationDeliveryTracker`. This provides a complete audit trail for notification observability.

**Tracked channels**: `Broadcast`, `AgentQuestion`, `DirectMessage`, `RoomRenamed`, `RoomClosed`

**Delivery statuses**:
- `Delivered` — provider returned success
- `Skipped` — provider returned false (unable to deliver, e.g., unsupported method)
- `Failed` — exception after all retries exhausted; error message recorded

**Entity** (`NotificationDeliveryEntity`):

| Field | Type | Description |
|-------|------|-------------|
| `Id` | int (PK) | Auto-increment identifier |
| `Channel` | string | Delivery channel type |
| `Title` | string? | Notification title or question summary |
| `Body` | string? | Message body (truncated to 500 chars) |
| `RoomId` | string? | Room context |
| `AgentId` | string? | Associated agent |
| `ProviderId` | string | Provider that attempted delivery |
| `Status` | string | Delivery outcome |
| `Error` | string? | Error message on failure |
| `AttemptedAt` | DateTime | When the attempt occurred |

**Service** (`NotificationDeliveryTracker`):
- Singleton, uses `IServiceScopeFactory` to create scoped `AgentAcademyDbContext` instances
- Recording failures are caught and logged — never breaks notification flow
- Supports filtered queries (by channel, provider, status, room) and time-windowed stats

**API endpoints**:
- `GET /api/notifications/deliveries?channel=&providerId=&status=&roomId=&limit=50&offset=0` — paginated delivery history
- `GET /api/notifications/deliveries/stats?hours=24` — delivery counts grouped by status

**Integration**: `NotificationManager` calls the tracker after each provider attempt in `SendToAllAsync`, `SendAgentQuestionAsync`, `SendDirectMessageDisplayAsync`, and `NotifyRoomRenamedAsync`. The tracker is an optional dependency (null-safe) so existing tests continue working without a DB.

## Known Gaps

- ~~No Slack provider implementation yet~~ — **resolved**: `SlackNotificationProvider` sends notifications, agent questions, and DMs via Slack Web API. Room-based channel routing, agent identity via username/icon_emoji, startup channel recovery. No input collection (would require Events API).
- ~~No persistent notification history or delivery tracking~~ — **resolved**: `NotificationDeliveryTracker` records every outbound delivery attempt per provider with status, error, and context. Query via REST API.
- ~~No retry/backoff on transient provider failures~~ — **resolved**: `NotificationRetryPolicy` with exponential backoff (200ms base, 3 retries, 2s cap, ±50ms jitter). Applied to all outbound provider calls except `RequestInputFromAnyAsync`.
- ~~No authentication on notification API endpoints~~ — **resolved**: System-wide `FallbackPolicy` in `Program.cs` requires `RequireAuthenticatedUser()` on all endpoints without `[AllowAnonymous]`. `NotificationController` has no `[AllowAnonymous]`, so all notification endpoints are protected when auth is enabled.
- ~~`RequestInputFromAnyAsync` uses insertion order, not priority-based selection~~ — **Accepted**: Single-provider deployment makes this moot. If multiple providers needed, add `Priority` property and sort.
- ~~Discord provider freeform input captures the next message from any non-bot user in the channel (not sender-scoped)~~ — **Resolved**: Optional `OwnerId` config field scopes freeform input capture to the configured user.
- ~~Provider config values (including secrets) stored in plaintext in SQLite~~ — **resolved**: `ConfigEncryptionService` encrypts secret config values (Type = "secret" in schema) using ASP.NET Core Data Protection API before DB persistence. Versioned `ENC.v1:` prefix enables transparent migration of existing plaintext values. `TryDecrypt` API distinguishes decrypt failure from legitimate empty values. Explicit key-ring persistence at `~/.local/share/AgentAcademy/DataProtection-Keys/`.
- ~~Settings tab currently shows only Discord wizard; will need expansion for multiple providers~~ — **resolved**: `NotificationSetupWizard` now accepts `providerId` prop, fetches schema dynamically, supports Discord, Slack, and any future provider with provider-specific instructions and generic fallback
- ~~DiceBear avatar URLs are an external dependency~~ — **Accepted**: Only used for Discord webhook avatars (1 call site). Discord falls back to default avatar if URL fails. Frontend uses Fluent UI Avatar with role colors.
- ~~Room channels are not cleaned up when rooms are archived/completed~~ — **resolved**: `OnRoomClosedAsync` deletes Discord channel, clears webhook/mapping caches. `ActivityNotificationBroadcaster` routes `RoomClosed` events to providers.

## Revision History

- **2026-04-12**: Structural refactor — extracted `DiscordMessageSender` (outbound delivery) and `DiscordMessageRouter` (inbound routing) from `DiscordNotificationProvider` (776→500 lines). Provider is now a thin connection lifecycle wrapper. Also fixed event handler lifecycle (named handlers properly unsubscribed), startup race (router attached after channel rebuild), and room-send fallback (unified default-send path). Zero behavioral changes.

- **2026-04-12**: Structural refactor — extracted `DiscordInputHandler` from `DiscordNotificationProvider`. Stateless handler receives `DiscordSocketClient`, channel ID, and owner ID as method parameters (no mutable state). `DiscordNotificationProvider` retains notification delivery, connection lifecycle, and channel management. Zero behavioral changes.

- **2026-04-05**: Multi-provider setup wizard — `NotificationSetupWizard` refactored from Discord-only to accept `providerId` prop. Fetches config schema from `GET /api/notifications/providers/{id}/schema`. Provider-specific instructions for Discord (Developer Portal, invite URL generator) and Slack (app creation, OAuth scopes). Generic fallback for unknown providers. Dynamic credential form from schema fields. Settings panel now routes any provider to the wizard. 19 new frontend tests (138 total vitest).

- **2026-04-05**: Slack notification provider — `SlackNotificationProvider` delivers notifications, agent questions, and DMs via Slack Web API using raw `HttpClient` (no NuGet dependency). `SlackApiClient` wraps 8 Slack API methods. Room-based channel routing with lazy creation, startup recovery via channel topic parsing, agent identity via `username`/`icon_emoji`, Block Kit message formatting. No input collection (stateless HTTP — would need Events API). 58 new tests (1250 total). 3 adversarial reviews.

- **2026-04-04**: Room channel cleanup — `OnRoomClosedAsync` added to `INotificationProvider`. Discord provider deletes channel, disposes webhook, and clears mapping caches when a room is archived. `ActivityNotificationBroadcaster` routes `RoomClosed` events as structural provider notifications. `NotificationManager.NotifyRoomClosedAsync` fans out to all providers with retry. 7 new tests.

- **2026-04-04**: Notification delivery tracking — `NotificationDeliveryTracker` records every outbound notification attempt per provider to `notification_deliveries` table. Tracks 5 channels (Broadcast, AgentQuestion, DirectMessage, RoomRenamed, RoomClosed) with Delivered/Skipped/Failed status. REST API for delivery history and stats. 18 new tests. Adversarial review by GPT-5.3 Codex.

- **2026-04-04**: Retry with exponential backoff — `NotificationRetryPolicy` (200ms base, 3 retries, 2s cap, ±50ms jitter) applied to `SendToAllAsync`, `SendAgentQuestionAsync`, `SendDirectMessageDisplayAsync`, `NotifyRoomRenamedAsync`. Transient classification: timeouts, network failures, HTTP 429/5xx; excludes 4xx auth/config errors. HttpClient timeout (`TaskCanceledException`) retried when caller token not cancelled. 33 new tests. Adversarial review by GPT-5.3 Codex — 3 findings, 2 fixed.

- **2026-03-29**: Discord room-based channel routing — per-room Discord channels under "Agent Academy" category, webhook-based agent identity (custom sender name + DiceBear avatar), bidirectional message bridging (Discord replies trigger orchestrator), error propagation via `(bool, string?)` tuple return, graceful permission fallback, missing-permissions catch with actionable logging. 3 adversarial reviews, 7 findings fixed.
- **2026-03-29**: Discord → orchestrator fix — human messages from Discord now call `HandleHumanMessage(roomId)` to wake up agents (was storing messages without triggering response).
- **2026-03-28**: Activity bridge — `ActivityNotificationBroadcaster` hosted service wires 7 event types to notification providers; config persistence via `notification_configs` table with atomic upsert; non-blocking auto-restore on startup; Settings tab in frontend with inline wizard mode; 35 new unit tests (commit `691ec89`)
- **2025-07-27**: Discord provider — `DiscordNotificationProvider` with embed notifications, button-based choices, freeform input, connection lifecycle management; 36 unit tests
- **2025-07-11**: Initial implementation — interface, manager, console provider, REST API, tests
