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
- All providers registered with manager at startup

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
| `src/AgentAcademy.Server/Controllers/NotificationController.cs` | REST API |
| `tests/AgentAcademy.Server.Tests/NotificationManagerTests.cs` | Manager unit tests |
| `tests/AgentAcademy.Server.Tests/DiscordNotificationProviderTests.cs` | Discord provider unit tests |

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

## Revision History

- **2025-07-27**: Discord provider — `DiscordNotificationProvider` with embed notifications, button-based choices, freeform input, connection lifecycle management; 36 unit tests
- **2025-07-11**: Initial implementation — interface, manager, console provider, REST API, tests
