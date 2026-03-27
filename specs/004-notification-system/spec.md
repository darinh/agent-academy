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
│ Console  │  Slack?  │  Discord?  │  Custom?  │
│ Provider │ (future) │  (future)  │  (future) │
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
- Console provider registered with manager at startup

## Interfaces & Contracts

### File Locations

| File | Purpose |
|------|---------|
| `src/AgentAcademy.Shared/Models/Notifications.cs` | Shared model types |
| `src/AgentAcademy.Server/Notifications/INotificationProvider.cs` | Provider interface |
| `src/AgentAcademy.Server/Notifications/NotificationManager.cs` | Provider orchestrator |
| `src/AgentAcademy.Server/Notifications/ConsoleNotificationProvider.cs` | Reference provider |
| `src/AgentAcademy.Server/Controllers/NotificationController.cs` | REST API |
| `tests/AgentAcademy.Server.Tests/NotificationManagerTests.cs` | Unit tests |

## Invariants

1. **Provider isolation**: A failing provider never prevents other providers from receiving notifications
2. **Thread safety**: All `NotificationManager` operations are safe for concurrent access
3. **No silent failures**: Every provider error is logged with context
4. **Null safety**: `RequestInputAsync` returns null (not throws) when input cannot be collected
5. **Idempotent registration**: Re-registering a provider with the same ID replaces the previous one

## Known Gaps

- No webhook-based notification support yet (Slack, Discord providers)
- No persistent notification history or delivery tracking
- No retry/backoff on transient provider failures
- No authentication on notification API endpoints (will be covered by system-wide auth)
- `RequestInputFromAnyAsync` uses insertion order, not priority-based selection

## Revision History

- **2025-07-11**: Initial implementation — interface, manager, console provider, REST API, tests
