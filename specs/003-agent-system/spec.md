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
│  • Canned role-based responses  │
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
- **Lazy client initialization**: `CopilotClient` is created on first use and cached as a singleton.
- **Token configuration**: Reads `Copilot:GitHubToken` from `IConfiguration`. When set, passes it to `CopilotClientOptions.GitHubToken`. When empty, falls back to environment variables (`COPILOT_GITHUB_TOKEN`, `GH_TOKEN`, `GITHUB_TOKEN`) or CLI login.
- **Permission handling**: Sessions are created with `OnPermissionRequest = PermissionHandler.ApproveAll` (required by SDK v0.2.0).
- **Session-per-agent-per-room**: Sessions keyed by `{agentId}:{roomId}`, default room is `"default"`.
- **Streaming aggregation**: Subscribes to `AssistantMessageDeltaEvent` for incremental tokens, uses `AssistantMessageEvent` for the final complete content.
- **Session priming**: Sends `AgentDefinition.StartupPrompt` as the first message to establish agent identity.
- **Model selection**: Uses `AgentDefinition.Model` in `SessionConfig`, defaults to `"gpt-5"`.
- **TTL cleanup**: Sessions expire after 10 minutes of inactivity; a background timer runs every 2 minutes.
- **Automatic fallback**: If `CopilotClient.StartAsync()` fails or any individual call fails, delegates to `StubExecutor`.
- **Request timeout**: 2-minute timeout per `SendAsync` call.
- **Graceful disposal**: All sessions and the client are disposed on shutdown.

### Implementation: `StubExecutor`

**File**: `src/AgentAcademy.Server/Services/StubExecutor.cs`

Returns canned responses based on `AgentDefinition.Role`:
- **Planner**: 4 templates about task decomposition and sequencing
- **Architect**: 4 templates about design patterns and abstractions
- **SoftwareEngineer**: 4 templates about implementation approaches
- **Reviewer**: 4 templates about code review concerns
- **TechnicalWriter**: 3 templates about spec change proposals
- **Default**: 2 generic templates

Templates are randomly selected. `IsFullyOperational` is always `false`.

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
| `RunAsync_ReturnsNonEmptyResponse_ForKnownRoles` | All 5 known roles produce output |
| `RunAsync_ReturnsResponse_ForUnknownRole` | Fallback templates work |
| `RunAsync_ThrowsOnCancellation` | Cancellation token honored |
| `InvalidateSessionAsync_DoesNotThrow` | No-op is safe |
| `InvalidateRoomSessionsAsync_DoesNotThrow` | No-op is safe |
| `DisposeAsync_DoesNotThrow` | No-op is safe |
| `RunAsync_WithNullRoomId_ReturnsResponse` | Null room handled |
| `RunAsync_ResponseVariesBetweenCalls` | Random selection works |
| `StubExecutor_ImplementsIAgentExecutor` | Interface compliance |
| `CopilotExecutor_ImplementsIAgentExecutor` | Interface compliance |

## Invariants

- `IAgentExecutor.RunAsync` never returns null or throws for valid inputs (except cancellation).
- `CopilotExecutor` never leaves the system without an executor — it always falls back to `StubExecutor`.
- Session keys are deterministic: `{agentId}:{roomId ?? "default"}`.
- Expired sessions are cleaned up within `CleanupInterval` (2 minutes).

## Known Gaps

- **No token/usage tracking**: `CopilotExecutor` does not yet track input/output tokens or cost. The v1 `AgentEventTracker` integration is pending.
- **No tool calling**: The Copilot SDK supports registering C# methods as tools callable by the model. Not yet wired up.
- **Session compaction**: The SDK may support session compaction for long conversations. Not yet implemented.

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

## Revision History

| Date | Change | Task |
|------|--------|------|
| Initial | Created agent execution system — interface, CopilotExecutor, StubExecutor | copilot-executor |
| 2026-03-28 | Fixed CopilotExecutor auth: added OnPermissionRequest + IConfiguration token support | copilot-auth-sse |
| 2026-03-28 | Added SSE activity stream as SignalR alternative | copilot-auth-sse |
