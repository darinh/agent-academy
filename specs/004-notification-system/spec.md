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
| `GetConfigSchema()` | `ProviderConfigSchema` | Describe required configuration fields |

### NotificationManager

- **Thread-safe**: Uses `ConcurrentDictionary` for provider storage
- **Fan-out delivery**: `SendToAllAsync` sends to every connected provider
- **Failure isolation**: Individual provider failures are logged, never propagated
- **Input collection**: `RequestInputFromAnyAsync` tries providers in order, returns first non-null response

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

The `DiscordNotificationProvider` connects to Discord via the Discord.Net library (`DiscordSocketClient`).

**Configuration** (via `ConfigureAsync`):
| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `BotToken` | `secret` | Yes | Discord bot token from developer portal |
| `GuildId` | `string` | Yes | Discord server (guild) ID |
| `ChannelId` | `string` | Yes | Target text channel ID |

**Connection lifecycle**:
- `ConfigureAsync` — validates and stores bot token, guild ID, channel ID
- `ConnectAsync` — creates `DiscordSocketClient`, logs in with bot token, waits for Ready event (30s timeout)
- `DisconnectAsync` — stops and disposes the client; implements `IAsyncDisposable`

**Notification delivery** (`SendNotificationAsync`):
- Formats as a Discord embed with type-based color coding:
  - 🔵 Blue — AgentThinking
  - 🟡 Gold — NeedsInput
  - 🟢 Green — TaskComplete
  - 🔴 Red — TaskFailed, Error
  - 🟣 Purple — SpecReview
- Includes Room and Agent fields when present
- Action buttons rendered as Discord button components

**Input collection** (`RequestInputAsync`):
- **Choice mode**: Sends buttons for each choice, waits for `InteractionCreated` event on the sent message
- **Freeform mode**: Sends prompt embed, waits for next non-bot text message in the channel
- Returns `null` on timeout or cancellation

**Error handling**:
- Send failures are logged and return `false` (never throw)
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

- No webhook-based notification support yet (Slack provider)
- No persistent notification history or delivery tracking
- No retry/backoff on transient provider failures
- No authentication on notification API endpoints (will be covered by system-wide auth)
- `RequestInputFromAnyAsync` uses insertion order, not priority-based selection
- Discord provider freeform input captures the next message from any non-bot user in the channel (not sender-scoped)
- Provider config values (including secrets) stored in plaintext in SQLite — encryption enhancement pending
- Settings tab currently shows only Discord wizard; will need expansion for multiple providers

## Revision History

- **2026-03-28**: Activity bridge — `ActivityNotificationBroadcaster` hosted service wires 7 event types to notification providers; config persistence via `notification_configs` table with atomic upsert; non-blocking auto-restore on startup; Settings tab in frontend with inline wizard mode; 35 new unit tests (commit `691ec89`)
- **2025-07-27**: Discord provider — `DiscordNotificationProvider` with embed notifications, button-based choices, freeform input, connection lifecycle management; 36 unit tests
- **2025-07-11**: Initial implementation — interface, manager, console provider, REST API, tests
