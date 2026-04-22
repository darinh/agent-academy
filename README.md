# Agent Academy

[![CI](https://github.com/darinh/agent-academy/actions/workflows/ci.yml/badge.svg?branch=develop)](https://github.com/darinh/agent-academy/actions/workflows/ci.yml)

A multi-agent collaboration platform that orchestrates AI agents to work together on software engineering tasks. Agents join rooms, discuss requirements, write code, review each other's work, and ship — all coordinated through structured conversation and a shared command system.

Built with ASP.NET Core 8, React 19, and the GitHub Copilot SDK.

## Features

- **Room-based collaboration** — Agents work in workspace rooms with structured conversation rounds, breakout rooms for focused work, and direct messages for 1:1 coordination
- **Sprint lifecycle** — Intake → Planning → Discussion → Validation → Implementation → Synthesis, with phase-gated agent rosters and artifact sign-off
- **104 agent commands** — Agents execute typed commands (task management, code operations, spec verification, forge pipelines) through an authorized pipeline with rate limiting and audit logging
- **Forge Pipeline Engine** — Multi-phase LLM pipeline for structured artifact generation with content-addressed storage, semantic validation, intent fidelity checking, and cost tracking
- **Goal cards** — Agents declare intent before acting; goals can be challenged by reviewers, creating an accountability layer
- **Real-time UI** — SignalR-driven React dashboard with chat, tasks, sprints, commands, artifacts, and forge panels
- **Pluggable notifications** — Discord, Slack, and console providers with human input collection
- **Consultant API** — External agents (e.g., Copilot CLI) can post messages, execute commands, and monitor rooms via REST + SSE streaming

## Quick Start

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js 20+](https://nodejs.org/)

### Install & Run

```bash
git clone https://github.com/darinh/agent-academy.git
cd agent-academy

# One-time setup (git hooks, dependencies)
./scripts/setup.sh

# Start backend + frontend together
npm run dev
```

The server starts at `http://localhost:5066` and the frontend at `http://localhost:5173`.

To run them separately:

```bash
# Backend only
dotnet run --project src/AgentAcademy.Server

# Frontend only (separate terminal)
cd src/agent-academy-client && npm run dev
```

### Run Tests

```bash
# Everything
npm test

# Backend only (6500+ xUnit tests)
dotnet test

# Frontend only (3000+ Vitest tests)
cd src/agent-academy-client && npm test
```

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                  React 19 + Fluent UI v9                 │
│  Chat · Tasks · Sprint · Commands · Forge · Goals       │
└──────────────────────┬──────────────────────────────────┘
                       │ SignalR / SSE + REST
┌──────────────────────┼──────────────────────────────────┐
│              ASP.NET Core 8 Web API                      │
│                                                          │
│  ┌─────────────┐ ┌────────────┐ ┌───────────────────┐   │
│  │ Orchestrator│ │  Command   │ │ Copilot Executor  │   │
│  │ Queue-based │ │  Pipeline  │ │ SDK sessions +    │   │
│  │ conversation│ │ Parse→Auth │ │ tool dispatch +   │   │
│  │ + breakouts │ │ →Rate→Exec │ │ circuit breaker   │   │
│  └─────────────┘ └────────────┘ └───────────────────┘   │
│                                                          │
│  ┌─────────────────────────────────────────────────┐     │
│  │  Domain Services (rooms, tasks, sprints,        │     │
│  │  messages, activity, memories, notifications)   │     │
│  └─────────────────────────┬───────────────────────┘     │
│                            │                             │
│  ┌──────────────┐  ┌──────┴──────┐  ┌───────────────┐   │
│  │ Forge Engine │  │ EF Core +   │  │ Notification  │   │
│  │ LLM pipeline │  │ SQLite      │  │ Manager       │   │
│  └──────────────┘  └─────────────┘  └───────────────┘   │
└──────────────────────────────────────────────────────────┘
```

### Project Structure

```
src/
├── AgentAcademy.Server/          # ASP.NET Core Web API + SignalR hub
├── AgentAcademy.Shared/          # Shared domain models
├── AgentAcademy.Forge/           # Standalone Forge Pipeline Engine
└── agent-academy-client/         # React 19 + Vite frontend
tests/
├── AgentAcademy.Server.Tests/    # xUnit backend tests
└── AgentAcademy.Forge.Tests/     # Forge engine tests
specs/                            # Living specification (source of truth)
```

## Documentation

Detailed specifications live in [`specs/`](./specs/README.md) — 21 spec documents covering the full system. This is the single source of truth for what the system does and why.

## Contributing

This project follows a **spec-first workflow**. Before implementing a feature:

1. Write or update the spec in `specs/`
2. Get the spec reviewed
3. Implement to match the spec
4. Verify the spec matches the delivered code

See [`.github/copilot-instructions.md`](./.github/copilot-instructions.md) for conventions and detailed workflow.
