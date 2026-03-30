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
│ Console  │ Discord  │  Slack?    │  Custom?  │
│ Provider │ Provider │  (future)  │  (future) │
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
| `ConfigureAsync()` | `Task` | Apply provider-specific settings |
| `ConnectAsync()` | `Task` | Establish connection |
| `DisconnectAsync()` | `Task` | Tear down connection |
| `SendNotificationAsync()` | `Task<bool>` | Deliver a notification message |
| `RequestInputAsync()` | `Task<UserResponse?>` | Collect user input (null if unsupported) |
| `SendAgentQuestionAsync()` | `Task<(bool, string?)>` | Send agent question to human; returns sent status + error detail |
| `GetConfigSchema()` | `ProviderConfigSchema` | Describe required configuration fields |

### NotificationManager

- **Thread-safe**: Uses `ConcurrentDictionary` for provider storage
- **Fan-out delivery**: `SendToAllAsync` sends to every connected provider
- **Failure isolation**: Individual provider failures are logged, never propagated
- **Input collection**: `RequestInputFromAnyAsync` tries providers in order, returns first non-null response
- **Agent questions**: `SendAgentQuestionAsync` returns `(bool Sent, string? Error)` tuple — surfaces actual provider errors instead of generic failure messages; tries all providers before failing

### Built-in Provider: Console

The `ConsoleNotificationProvider` serves as the reference implementation:
- Always configured and connected
- Logs notifications via `ILogger`
- Cannot collect input (returns `null`)
- Zero configuration fields

### REST API

All endpoints under `/api/notifications`:

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/providers` | List all providers with status |
| `GET` | `/providers/{id}/schema` | Get provider's config schema |
| `POST` | `/providers/{id}/configure` | Apply configuration |
| `POST` | `/providers/{id}/connect` | Connect provider |
| `POST` | `/providers/{id}/disconnect` | Disconnect provider |
| `POST` | `/test` | Send test notification to all |

### Shared Types

Defined in `AgentAcademy.Shared.Models.Notifications`:

- `NotificationType` — enum: AgentThinking, NeedsInput, TaskComplete, TaskFailed, SpecReview, Error
- `NotificationMessage` — record: Type, Title, Body, RoomId?, AgentName?, Actions?
- `InputRequest` — record: Prompt, RoomId?, AgentName?, Choices?, AllowFreeform
- `UserResponse` — record: Content, SelectedChoice?, ProviderId
- `ProviderConfigSchema` — record: ProviderId, DisplayName, Description, Fields
- `ConfigField` — record: Key, Label, Type, Required, Description?, Placeholder?

### DI Registration

In `Program.cs`:
- `NotificationManager` registered as singleton
- `ConsoleNotificationProvider` registered as singleton
- `DiscordNotificationProvider` registered as singleton
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
- **Known limitation**: Config values (including secrets like bot tokens) are stored in plaintext. Acceptable for local single-user deployments; encryption is a separate enhancement.

### Frontend Integration

The `NotificationSetupWizard` component is accessible via the **Settings** tab in the main workspace tab bar.

- Uses the `SettingsRegular` icon from `@fluentui/react-icons`
- Wizard renders in **inline mode** (no overlay backdrop) when used as tab content
- Wizard supports both `inline` and overlay modes via the `inline` prop
- `onClose` prop is optional when in inline mode

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

**Project resolution**: When creating a channel for a room, the provider resolves the room's project name via `WorkspaceRuntime.GetProjectNameForRoomAsync(roomId)` which follows the chain: `roomId → RoomEntity.WorkspacePath → WorkspaceEntity.ProjectName`. If `ProjectName` is null, falls back to the workspace directory basename.

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

**Input collection** (`RequestInputAsync`):
- **Choice mode**: Sends buttons for each choice, waits for `InteractionCreated` event on the sent message
- **Freeform mode**: Sends prompt embed, waits for next non-bot text message in the channel
- Returns `null` on timeout or cancellation

**Error handling**:
- Send failures are logged and return `false` (never throw)
- Missing permissions (50013) caught specifically with actionable log message
- `IsConnected` reflects actual `DiscordSocketClient.ConnectionState`
- Disconnections are logged via the client's `Disconnected` event
- Connection uses `SemaphoreSlim` to prevent concurrent connect/disconnect races

## Interfaces & Contracts

### File Locations

| File | Purpose |
|------|---------|
| `src/AgentAcademy.Shared/Models/Notifications.cs` | Shared model types |
| `src/AgentAcademy.Server/Notifications/INotificationProvider.cs` | Provider interface |
| `src/AgentAcademy.Server/Notifications/NotificationManager.cs` | Provider orchestrator |
| `src/AgentAcademy.Server/Notifications/ConsoleNotificationProvider.cs` | Reference provider |
| `src/AgentAcademy.Server/Notifications/DiscordNotificationProvider.cs` | Discord bot provider |
| `src/AgentAcademy.Server/Notifications/ActivityNotificationBroadcaster.cs` | Activity event → notification bridge |
| `src/AgentAcademy.Server/Data/Entities/NotificationConfigEntity.cs` | Config persistence entity |
| `src/AgentAcademy.Server/Controllers/NotificationController.cs` | REST API (with config persistence) |
| `src/agent-academy-client/src/NotificationSetupWizard.tsx` | Discord setup wizard UI |
| `src/agent-academy-client/src/NotificationSetupWizard.css` | Wizard styles (overlay + inline) |
| `tests/AgentAcademy.Server.Tests/NotificationManagerTests.cs` | Manager unit tests |
| `tests/AgentAcademy.Server.Tests/DiscordNotificationProviderTests.cs` | Discord provider unit tests |
| `tests/AgentAcademy.Server.Tests/ActivityNotificationBroadcasterTests.cs` | Bridge unit tests (35 tests) |

## Invariants

1. **Provider isolation**: A failing provider never prevents other providers from receiving notifications
2. **Thread safety**: All `NotificationManager` operations are safe for concurrent access
3. **No silent failures**: Every provider error is logged with context
4. **Null safety**: `RequestInputAsync` returns null (not throws) when input cannot be collected
5. **Idempotent registration**: Re-registering a provider with the same ID replaces the previous one

## Known Gaps

- No Slack provider implementation yet
- No persistent notification history or delivery tracking
- No retry/backoff on transient provider failures
- No authentication on notification API endpoints (will be covered by system-wide auth)
- `RequestInputFromAnyAsync` uses insertion order, not priority-based selection
- Discord provider freeform input captures the next message from any non-bot user in the channel (not sender-scoped)
- Provider config values (including secrets) stored in plaintext in SQLite — encryption enhancement pending
- Settings tab currently shows only Discord wizard; will need expansion for multiple providers
- DiceBear avatar URLs are an external dependency — consider caching/bundling if availability matters
- Room channels are not cleaned up when rooms are archived/completed

## Revision History

- **2026-03-29**: Discord room-based channel routing — per-room Discord channels under "Agent Academy" category, webhook-based agent identity (custom sender name + DiceBear avatar), bidirectional message bridging (Discord replies trigger orchestrator), error propagation via `(bool, string?)` tuple return, graceful permission fallback, missing-permissions catch with actionable logging. 3 adversarial reviews, 7 findings fixed.
- **2026-03-29**: Discord → orchestrator fix — human messages from Discord now call `HandleHumanMessage(roomId)` to wake up agents (was storing messages without triggering response).
- **2026-03-28**: Activity bridge — `ActivityNotificationBroadcaster` hosted service wires 7 event types to notification providers; config persistence via `notification_configs` table with atomic upsert; non-blocking auto-restore on startup; Settings tab in frontend with inline wizard mode; 35 new unit tests (commit `691ec89`)
- **2025-07-27**: Discord provider — `DiscordNotificationProvider` with embed notifications, button-based choices, freeform input, connection lifecycle management; 36 unit tests
- **2025-07-11**: Initial implementation — interface, manager, console provider, REST API, tests
