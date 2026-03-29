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

## Session Handoff Protocol

Agents use `.github/next-session.md` for continuity across sessions.

### On Session Start
When the user greets you (e.g., "hey", "hello", "hi"), **immediately**:

1. **Check for unmerged work**: Run `git branch --no-merged develop` (or the integration branch). If any feature branches have unmerged commits, tell the user: *"Found unmerged work on branch X (N commits). Want to continue that, merge it, or start fresh?"*
2. **Read handoff**: Check if `.github/next-session.md` exists. If it does:
   - Read it and use it as your starting context.
   - Tell the user what was left in progress and what you're picking up.
   - Delete the file after reading it (it's a one-time handoff, not permanent docs).
3. **Log the session**: Insert a row into the `session_log` table in the session database (see Session History below).

### On Session End (automatic)
Write `.github/next-session.md` when any of these are true:
- You've completed a large task and there are known next steps.
- You sense the context window is getting large (long conversation, many tool calls). Don't wait to be asked — proactively write the file and tell the user: *"Context is getting heavy. I've written the handoff to `.github/next-session.md` — start a new session and I'll pick up where we left off."*
- The user says they're ending the session.

### Handoff File Format
```markdown
# Session Handoff

## Status
[What was just completed — be specific about commits, branches, files changed]

## In Progress
[What was actively being worked on when the session ended, if anything]

## Next Steps
[Prioritized list of what the next agent should do]

## Context
[Key decisions made, architectural notes, gotchas discovered — anything the next agent needs to avoid re-deriving from scratch]

## Prompt
[A ready-to-paste prompt the next agent can execute immediately, e.g.:
"Work in ~/projects/agent-academy on develop. The X feature is done. Next: implement Y. Check file Z for context. Use the anvil agent."]
```

### Rules
- The file is `.github/next-session.md` — always this path, never somewhere else.
- It is gitignored (add it to `.gitignore` if not already there).
- It is ephemeral — read once, then delete. Not documentation.
- Write it proactively. The user should never have to ask for it.

### Session History

Use a `session_log` table in the **session SQL database** to record a persistent history of work across sessions. This survives session boundaries and can be queried later for auditing, context, or review.

```sql
CREATE TABLE IF NOT EXISTS session_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    started_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    ended_at DATETIME,
    branch TEXT,
    task_summary TEXT NOT NULL,
    commits TEXT,           -- comma-separated SHAs
    files_changed TEXT,     -- comma-separated paths
    tests_before INTEGER,
    tests_after INTEGER,
    learnings TEXT,         -- key decisions, gotchas, patterns discovered
    status TEXT DEFAULT 'in_progress' CHECK(status IN ('in_progress', 'completed', 'abandoned'))
);
```

**On session start**: `INSERT INTO session_log (branch, task_summary) VALUES ('{branch}', '{what you''re working on}');`

**On session end**: `UPDATE session_log SET ended_at = CURRENT_TIMESTAMP, commits = '{shas}', files_changed = '{files}', tests_before = N, tests_after = M, learnings = '{notes}', status = 'completed' WHERE id = {id};`

**To review history**: `SELECT * FROM session_log ORDER BY started_at DESC LIMIT 20;`
