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

## Specification Workflow (MANDATORY)

The `specs/` directory is the single source of truth for what this system does. Every claim in the spec must be verifiable against actual code.

### Before Every Change
Produce a Spec Change Proposal:
- Which spec sections are affected
- Change type: NEW_CAPABILITY | MODIFICATION | BUG_FIX_CODE | BUG_FIX_SPEC
- What the proposed changes are
- How to verify accuracy after implementation

### After Every Change
- Update the affected spec sections to match the delivered code
- Verify every spec claim against actual code (file paths, function names)
- Update `specs/CHANGELOG.md`

### If Spec and Code Diverge
1. Investigate WHY they diverged
2. Fix whichever is wrong (code or spec)
3. Update THIS FILE with a new convention or pitfall to prevent recurrence

### Spec Document Template
Each spec section follows:
- **Purpose**: What this section covers
- **Current Behavior**: Verified description (or "Planned" for unimplemented features)
- **Interfaces & Contracts**: Types, APIs, data shapes
- **Invariants**: Rules that must always hold
- **Known Gaps**: Where implementation is incomplete
- **Revision History**: Changes linked to tasks

## Conventions
- Use C# records for immutable domain types
- Use `sealed` on classes that shouldn't be inherited
- Prefer `async/await` throughout
- Use dependency injection for all services
- Controllers should be thin — business logic lives in services
- Error responses use `ProblemDetails` format
- Use conventional commits: `feat:`, `fix:`, `docs:`, `refactor:`, `test:`

## Branching Strategy

- `main` — stable releases only. Never push directly. Protected branch.
- `develop` — integration branch. PRs from feature branches merge here.
- `feat/xxx`, `fix/xxx`, `docs/xxx` — feature branches off `develop`.
- All work happens on feature branches. PRs go to `develop`.
- `develop` → `main` only via PR when ready for release.

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

## Common Pitfalls
- Don't write aspirational specs — write factual specs describing what IS
- Don't skip spec verification after implementation
- Don't add features without updating the spec
- Don't hardcode configuration values — use `appsettings.json` or environment variables
- Don't push directly to `main` — always use a feature branch and PR
- Don't forget to run `git config core.hooksPath .githooks` after cloning
