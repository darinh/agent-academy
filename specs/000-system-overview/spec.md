# 000 — System Overview

## Purpose
High-level architecture of the Agent Academy platform: what it does, how components connect, and the design principles that guide development.

## Current Behavior

> **Status: Planned** — Project scaffolded, no features implemented yet.

Agent Academy is a multi-agent collaboration platform that orchestrates AI agents to work together on software engineering tasks. The system provides:

- A room-based collaboration model where agents join, discuss, and execute tasks
- Real-time communication via SignalR
- Pluggable notification providers (Discord first)
- Spec-first development workflow enforced by conventions

### Architecture

```
┌─────────────────────────────────────────────────────┐
│                  React 19 + Vite                     │
│              (Fluent UI v9 components)               │
│                                                      │
│  ┌──────────┐  ┌──────────┐  ┌──────────────────┐  │
│  │ Room View │  │ Task Board│  │ Agent Dashboard  │  │
│  └─────┬────┘  └─────┬────┘  └────────┬─────────┘  │
│        │              │                │             │
│        └──────────────┼────────────────┘             │
│                       │ SignalR + REST               │
└───────────────────────┼──────────────────────────────┘
                        │
┌───────────────────────┼──────────────────────────────┐
│              ASP.NET Core 8 Web API                   │
│                                                       │
│  ┌────────────┐  ┌─────────────┐  ┌───────────────┐ │
│  │ Controllers │  │  Services    │  │  SignalR Hub  │ │
│  └─────┬──────┘  └──────┬──────┘  └───────┬───────┘ │
│        │                │                  │          │
│        └────────────────┼──────────────────┘          │
│                         │                             │
│  ┌──────────────────────┼─────────────────────────┐  │
│  │              EF Core + SQLite                   │  │
│  └─────────────────────────────────────────────────┘  │
│                                                       │
│  ┌─────────────────┐  ┌───────────────────────────┐  │
│  │  Copilot SDK    │  │  Notification Providers    │  │
│  │  (Agent Runner) │  │  (Discord.Net, etc.)       │  │
│  └─────────────────┘  └───────────────────────────┘  │
└───────────────────────────────────────────────────────┘
```

### Component Responsibilities

| Component | Project | Responsibility |
|-----------|---------|---------------|
| Web API | `AgentAcademy.Server` | REST endpoints, SignalR hub, DI composition root |
| Shared Models | `AgentAcademy.Shared` | Domain types, enums, DTOs shared between server and tests |
| Frontend | `agent-academy-client` | React SPA with Fluent UI, SignalR client |
| Tests | `AgentAcademy.Server.Tests` | xUnit integration and unit tests |

## Design Principles

1. **Spec-first**: The spec describes what IS, not what we wish. Code follows spec; spec follows reality.
2. **Pluggable notifications**: Notification delivery is abstracted behind `INotificationProvider`. Discord is the first implementation; others can be added without changing core logic.
3. **Adversarial review**: Agents review each other's work before tasks are marked complete.
4. **Thin controllers**: HTTP controllers delegate to service classes. Business logic never lives in a controller.
5. **Immutable domain types**: Core domain types are C# records — immutable by default.

## Interfaces & Contracts

> Planned — no interfaces implemented yet. See [001-domain-model](../001-domain-model/spec.md) for planned type definitions.

## Invariants

- Every spec claim must be verifiable against actual code
- Features marked "Planned" must not be described as if they exist
- The `specs/` directory is the single source of truth

## Known Gaps

- No code implemented yet — this is the initial scaffold
- Architecture diagram is aspirational; actual implementation may evolve
- Copilot SDK integration approach TBD (depends on SDK capabilities)

## Revision History

| Date | Change | Task |
|------|--------|------|
| Initial | Created system overview spec | scaffold-solution |
