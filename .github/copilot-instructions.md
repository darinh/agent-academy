# Agent Academy — Copilot Instructions

## Project Overview
Agent Academy is a multi-agent collaboration platform that orchestrates AI agents to work together on software engineering tasks. Built with ASP.NET Core 8, React 19, and the GitHub Copilot SDK.

## Tech Stack
- **Backend**: ASP.NET Core 8 (C#), GitHub Copilot SDK (NuGet: GitHub.Copilot.SDK)
- **Frontend**: React 19, Vite 6, Fluent UI v9
- **Real-time**: SignalR
- **Database**: EF Core + SQLite
- **Notifications**: Pluggable provider interface (Discord first via Discord.Net)
- **Testing**: xUnit (server), Vitest (client)

## Build & Test Commands
```
# Build everything
dotnet build
cd src/agent-academy-client && npm run build

# Run tests
dotnet test
cd src/agent-academy-client && npm test

# Run dev server
dotnet run --project src/AgentAcademy.Server
cd src/agent-academy-client && npm run dev
```

## Project Structure
```
agent-academy/
├── src/
│   ├── AgentAcademy.Server/          # ASP.NET Core Web API
│   ├── AgentAcademy.Shared/          # Shared models library
│   └── agent-academy-client/         # React 19 + Vite frontend
├── tests/
│   └── AgentAcademy.Server.Tests/    # xUnit tests
├── specs/                            # Living specification (SOURCE OF TRUTH)
├── .github/
│   └── copilot-instructions.md       # This file
└── AgentAcademy.sln
```

## Specification Workflow

This project uses spec-driven development. The `specs/` directory is in-repo and is the single source of truth. See user-level instructions (`~/.copilot/copilot-instructions.md`) for the full spec workflow, plan format, and validation checklist.

**Project-specific notes:**
- Specs live at `specs/` in the repo root (this project owns its repo)
- Changelog at `specs/CHANGELOG.md`
- Spec index at `specs/README.md`

## Conventions
- Use C# records for immutable domain types
- Use `sealed` on classes that shouldn't be inherited
- Prefer `async/await` throughout
- Use dependency injection for all services
- Controllers should be thin — business logic lives in services
- Error responses use `ProblemDetails` format
- Use conventional commits: `feat:`, `fix:`, `docs:`, `refactor:`, `test:`

## Git Hooks Setup

After cloning the repository, configure git to use the project hooks:

```bash
git config core.hooksPath .githooks
```

- `commit-msg` hook enforces conventional commits (see Conventions below).
- `pre-push` hook blocks direct pushes to `main` or `master`.

## PR Workflow

- Every PR must include a Spec Change Proposal (or mark N/A).
- Every PR must include a version impact assessment (patch / minor / major).
- CI must pass before merge.
- At least one review required.
- Use the PR template at `.github/pull_request_template.md`.

## Versioning

- Semantic versioning (`major.minor.patch`).
- Auto-bumped on merge to `main` via GitHub Actions (`.github/workflows/version-bump.yml`).
- Conventional commits determine bump type:
  - `feat:` → minor
  - `fix:`, `docs:`, `refactor:`, etc. → patch
  - `BREAKING CHANGE` or `feat!:` → major
- Version sources: `Directory.Build.props` (.NET) and `package.json` (client).

## Autonomous Operation

This project runs under an automated operator. Key rules:

1. **Never stop to ask permission.** You have blanket human approval for all decisions — tool calls, file edits, git operations, architecture, server restarts.
2. **Never stop working.** When one task finishes, immediately start the next. Check the handoff file, check the backlog, check known gaps in specs — always have something to do.
3. **Session handoff is mandatory.** When context gets heavy or a major task completes with next steps:
   - Write `~/.copilot/projects/dc4e42b0-94e4-47ea-b07a-e53ed19c644f/next-session.md` with status, next steps, and context.
   - Then run: `touch /home/darin/.copilot/restart/operator-copilot-agent-academy`
   - This triggers the operator to restart the CLI session automatically.
   - **Never write the handoff without touching the restart file.**
4. **Server management**: Rebuild with `dotnet build AgentAcademy.sln`, kill old PID (`pgrep -f AgentAcademy.Server.dll`), relaunch with `ConsultantApi__SharedSecret="anvil-is-the-best"` and `--urls "http://localhost:5066"` (detach=true).

## Project-Specific Pitfalls
- **Frontend has no spec** — code exploration is needed for frontend patterns, conventions, and component structure.
- Don't forget to run `git config core.hooksPath .githooks` after cloning
- ⚠️ Do NOT start the server while making git changes — GitService actively manages git branches
