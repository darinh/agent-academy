# Agent Academy

[![CI](https://github.com/darinh/agent-academy/actions/workflows/ci.yml/badge.svg?branch=develop)](https://github.com/darinh/agent-academy/actions/workflows/ci.yml)

A multi-agent collaboration platform that orchestrates AI agents to work together on software engineering tasks.

Built with ASP.NET Core 8, React 19, and the GitHub Copilot SDK.

## Quick Start

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js 20+](https://nodejs.org/)

### Install & Run

```bash
# Clone
git clone https://github.com/darinh/agent-academy.git
cd agent-academy

# Build backend
dotnet build

# Install frontend dependencies
cd src/agent-academy-client
npm install
cd ../..

# Run backend
dotnet run --project src/AgentAcademy.Server

# Run frontend (separate terminal)
cd src/agent-academy-client
npm run dev
```

### Run Tests

```bash
# Backend tests
dotnet test

# Frontend tests
cd src/agent-academy-client
npm test
```

## Architecture

```
React 19 + Fluent UI ←→ ASP.NET Core 8 Web API ←→ EF Core + SQLite
        ↕ SignalR                    ↕ Copilot SDK
```

- **Backend** (`src/AgentAcademy.Server`): ASP.NET Core Web API with SignalR for real-time communication
- **Shared Models** (`src/AgentAcademy.Shared`): Domain types shared between server and tests
- **Frontend** (`src/agent-academy-client`): React 19 SPA with Vite and Fluent UI v9
- **Tests** (`tests/AgentAcademy.Server.Tests`): xUnit integration and unit tests

## Documentation

Detailed specifications live in [`specs/`](./specs/README.md) — this is the single source of truth for what the system does.

## Contributing

This project follows a **spec-first workflow**. Before implementing a feature:

1. Write or update the spec in `specs/`
2. Get the spec reviewed
3. Implement to match the spec
4. Verify the spec matches the delivered code

See [`.github/copilot-instructions.md`](./.github/copilot-instructions.md) for conventions and detailed workflow.
