# 000 — System Overview

## Purpose
High-level architecture of the Agent Academy platform: what it does, how components connect, and the design principles that guide development.

## Current Behavior

> **Status: Implemented** — All core components are operational: room-based collaboration, agent execution via Copilot SDK, real-time SignalR streaming, Discord notifications, project/workspace management, and command system.

Agent Academy is a multi-agent collaboration platform that orchestrates AI agents to work together on software engineering tasks. The system provides:

- A room-based collaboration model where agents join, discuss, and execute tasks
- Real-time communication via SignalR
- Pluggable notification providers (Discord first)
- Spec-first development workflow enforced by conventions

### Architecture

```
┌─────────────────────────────────────────────────────────┐
│                   React 19 + Vite                        │
│              (Fluent UI v9 components)                   │
│                                                          │
│  ┌───────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐  │
│  │   Chat    │ │  Tasks   │ │ Commands │ │ Settings │  │
│  │  Panel    │ │  Panel   │ │  Panel   │ │  Panel   │  │
│  └─────┬─────┘ └────┬─────┘ └────┬─────┘ └────┬─────┘  │
│        │             │            │             │        │
│  ┌─────┴─────────────┴────────────┴─────────────┴────┐  │
│  │    useWorkspace (state) + api.ts (REST client)    │  │
│  └───────────────────────┬───────────────────────────┘  │
│                          │ SignalR / SSE + REST          │
└──────────────────────────┼──────────────────────────────┘
                           │
┌──────────────────────────┼──────────────────────────────┐
│               ASP.NET Core 8 Web API                     │
│                                                          │
│  ┌────────────────────────────────────────────────────┐  │
│  │ Controllers (REST) + SignalR Hub + SSE Stream      │  │
│  └─────────────────────────┬──────────────────────────┘  │
│                            │                             │
│  ┌─────────────────────────┼──────────────────────────┐  │
│  │           Domain Services Layer                   │  │
│  │  (rooms, agents, tasks, messages, activity)        │  │
│  └──────┬──────────┬──────────┬──────────┬────────────┘  │
│         │          │          │          │                │
│  ┌──────┴───┐ ┌────┴────┐ ┌──┴───────┐ ┌┴────────────┐  │
│  │ Orchestr │ │ Command │ │ Copilot  │ │ Notification │  │
│  │ -ator    │ │ Pipeline│ │ Executor │ │ Manager      │  │
│  │          │ │ Parse → │ │ SDK +    │ │ Discord,     │  │
│  │ Convers. │ │ Auth →  │ │ Tools +  │ │ Slack,       │  │
│  │ Breakout │ │ Rate →  │ │ Circuit  │ │ Console      │  │
│  │ DMs      │ │ Handle  │ │ Breaker  │ │              │  │
│  └──────────┘ └─────────┘ └──────────┘ └──────────────┘  │
│                            │                             │
│  ┌─────────────────────────┼──────────────────────────┐  │
│  │   EF Core + SQLite (rooms, tasks, messages,        │  │
│  │   sessions, usage, memories, config, audit)        │  │
│  └────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────┘
```

### Component Responsibilities

| Component | Project | Responsibility |
|-----------|---------|---------------|
| Web API | `AgentAcademy.Server` | REST endpoints, SignalR hub, SSE stream, DI composition root |
| Shared Models | `AgentAcademy.Shared` | Domain types, enums, DTOs shared between server and tests |
| Frontend | `agent-academy-client` | React SPA with Fluent UI, SignalR/SSE client, workspace state management |
| Tests | `AgentAcademy.Server.Tests` | xUnit integration and unit tests |
| Domain Services | `AgentAcademy.Server` | State management layer: rooms, agents, tasks, messages, activity, plans — injected directly by controllers and handlers |
| AgentOrchestrator | `AgentAcademy.Server` | Queue-based message dispatch, startup recovery; delegates to ConversationRoundRunner, DirectMessageRouter, and RoundContextLoader |
| CopilotExecutor | `AgentAcademy.Server` | Copilot SDK sessions, tool registration, circuit breaker, retry logic |
| CommandPipeline | `AgentAcademy.Server` | Parse → authorize → rate-limit → dispatch → audit for agent commands |
| NotificationManager | `AgentAcademy.Server` | Multi-provider notification routing (Discord, Slack, Console) |

## Design Principles

1. **Spec-first**: The spec describes what IS, not what we wish. Code follows spec; spec follows reality.
2. **Pluggable notifications**: Notification delivery is abstracted behind `INotificationProvider`. Discord is the first implementation; others can be added without changing core logic.
3. **Adversarial review**: Agents review each other's work before tasks are marked complete.
4. **Thin controllers**: HTTP controllers delegate to service classes. Business logic never lives in a controller.
5. **Immutable domain types**: Core domain types are C# records — immutable by default.

## Interfaces & Contracts

### SignalR Real-Time Events

The server exposes a SignalR hub at `/hubs/activity` for real-time event streaming.

**Hub**: `ActivityHub` (`AgentAcademy.Server.Hubs`)
- Thin hub — no custom server methods. Clients connect and receive events.

**Broadcaster**: `ActivityHubBroadcaster` (`AgentAcademy.Server.Hubs`)
- `IHostedService` that subscribes to `ActivityBroadcaster` on startup.
- On each `ActivityEvent`, calls `IHubContext<ActivityHub>.Clients.All.SendAsync("activityEvent", evt)`.
- Errors are logged, not propagated — a SignalR failure won't crash the activity pipeline.

**Client event**: `activityEvent` — receives an `ActivityEvent` record (see [001-domain-model](../001-domain-model/spec.md)).

**CORS**: Default policy allows `http://localhost:5173` with credentials (required for SignalR WebSocket transport).

## Invariants

- Every spec claim must be verifiable against actual code
- Features marked "Planned" must not be described as if they exist
- The `specs/` directory is the single source of truth

## Known Gaps

- ~~Architecture diagram is aspirational — actual component interactions are richer than shown (e.g., command pipeline, activity broadcaster, workspace scoping not depicted).~~ — **Resolved**: Updated to show Domain Services Layer, Orchestrator, Command Pipeline, CopilotExecutor, and Notification Manager subsystems.
- No multi-user auth model — single-user token via `CopilotTokenProvider`. — *Tracked in #2, accepted design constraint for single-user product*
- No session persistence across server restarts — in-memory Copilot SDK sessions are lost. — *Tracked in #3, mitigated by LLM-generated session summaries on workspace switch*

## Revision History

| Date | Change | Task |
|------|--------|------|
| Initial | Created system overview spec | scaffold-solution |
| 2025-03-28 | Added SignalR hub, CORS, ActivityHubBroadcaster | signalr-hub |
| 2026-03-29 | Updated status from Planned to Implemented — all core components operational | project-scoped-rooms |
