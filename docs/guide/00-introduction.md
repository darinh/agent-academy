# Agent Academy — User Guide

> **Audience**: Developers who want to use Agent Academy to build software products with a team of AI agents.
>
> **Version**: Written against the codebase as of 2026-04-08. Gaps are identified inline with ⚠️ GAP callouts.

## What is Agent Academy?

Agent Academy is a **room-based multi-agent collaboration platform** for building software. You provide a brief describing what you want built, and a team of AI agents — each with a distinct role — collaborates through structured phases to deliver working code.

The platform orchestrates the full software development lifecycle:

```
Brief → Planning → Implementation → Review → Merge
         ↑                                      |
         └──────── iterate ─────────────────────┘
```

### What makes it different from "chat with an AI"?

| Dimension | Single-agent chat | Agent Academy |
|-----------|-------------------|---------------|
| **Agents** | One | 5+ specialized agents with distinct roles |
| **Memory** | Context window only | Persistent per-agent memory across sessions |
| **Structure** | Freeform | Phase-gated lifecycle (Plan → Build → Review → Merge) |
| **Code** | Suggestions only | Agents commit to branches, create PRs, merge |
| **Review** | Self-review | Adversarial review by a dedicated reviewer agent |
| **Context** | Degrades over time | Automatic session compaction with summaries |
| **Observability** | None | Dashboard with usage, errors, audit trails |
| **Integration** | Copy/paste | Git branches, GitHub PRs, Discord/Slack notifications |

### The Agent Team

| Agent | Role | What they do |
|-------|------|-------------|
| **Aristotle** | Planner | Leads planning discussions, creates tasks, coordinates the team |
| **Archimedes** | Architect | Designs system architecture, makes structural decisions |
| **Hephaestus** | Backend Engineer | Implements backend tasks in breakout rooms, writes code, commits |
| **Athena** | Frontend Engineer | Implements frontend tasks in breakout rooms, writes code, commits |
| **Socrates** | Reviewer | Reviews completed work, approves or requests changes |
| **Thucydides** | Technical Writer | Manages specs, writes documentation, tracks spec changes |

### How it works — the 30-second version

1. **Onboard a project** — point Agent Academy at a git repo
2. **Enter Planning phase** — describe what you want, agents discuss and create tasks
3. **Enter Implementing phase** — agents work on task branches in isolated breakout rooms
4. **Enter Reviewing phase** — reviewer agent examines the work, approves or rejects
5. **Enter Committing phase** — approved tasks merge to `develop`, PRs created
6. **Iterate** — return to Planning for the next feature

The human (you) triggers phase transitions, provides the brief, answers questions, and can intervene at any point via chat, DMs, or direct command execution.

## Guide Structure

| Section | What it covers |
|---------|---------------|
| [01 — Core Concepts](01-core-concepts.md) | Mental model: rooms, agents, sessions, phases, commands, memory |
| [02 — Building a Product](02-building-a-product.md) | End-to-end walkthrough from empty repo to shipped feature |
| [03 — Operations Reference](03-operations-reference.md) | Session management, monitoring, troubleshooting, configuration |
| [04 — Gap Analysis](04-gap-analysis.md) | Every missing feature and functionality gap, rated and categorized |

## Prerequisites

- **Agent Academy server** running (ASP.NET Core on port 5066)
- **GitHub Copilot** authentication configured (server needs Copilot SDK access)
- **Node.js 18+** for the frontend dev server
- **Git** installed on the server host
- **A repository** or empty directory to work in
- **Frontend** at http://localhost:5173 (Vite dev server)

> ⚠️ **GAP**: No Docker image or one-command setup. Server requires manual `dotnet run` + `npm run dev`. No quickstart script exists.

## Quick Start

```bash
# Terminal 1 — Backend
cd src/AgentAcademy.Server
dotnet run

# Terminal 2 — Frontend
cd src/agent-academy-client
npm run dev

# Open http://localhost:5173
```

> ⚠️ **GAP**: No `docker compose up` or unified launcher. The `package.json` at the root has a `concurrently` script but it's not documented as the primary way to start.
