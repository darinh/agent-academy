# Agent Academy — Copilot Instructions

> ## 🛑 READ FIRST — Product Vision and Active Roadmap
>
> Before doing **any** work in this repo, read these three files:
>
> 1. **[`specs/100-product-vision/spec.md`](../specs/100-product-vision/spec.md)** — what this product is supposed to be.
> 2. **[`specs/100-product-vision/gap-analysis.md`](../specs/100-product-vision/gap-analysis.md)** — where the code is vs. that vision.
> 3. **[`specs/100-product-vision/roadmap.md`](../specs/100-product-vision/roadmap.md)** — the prioritized, active backlog.
>
> ### The Three Rules
>
> 1. **Work the roadmap, not your imagination.** If you start a session and the human has not given you a specific task, work the next pending item from `roadmap.md`. If you believe a new item belongs on the list, append it to **Proposed Additions** and surface it — do not silently work on it.
> 2. **"Implemented" means observable behavior, not data shape.** Do not declare a feature done because you wrote the data model and the endpoint returns 200. Done means the acceptance test passes — read it before you start coding. If you cannot demonstrate the acceptance test, the work is not done. Spec status fields must factually describe behavior, never aspirations.
> 3. **Update the roadmap in the same commit.** When you start an item, mark it `in_progress` in `roadmap.md`'s Status Tracking table. When you finish, mark it `done` and write a one-line note. The roadmap is the at-a-glance truth — keep it accurate or it becomes the next misalignment.
>
> ### Why this exists
>
> Earlier sessions built features, marked them "Implemented," and moved on. The features didn't actually deliver the product's promise. The human is now stuck with a system that doesn't do what was asked. The vision document is the brake on that drift. **You are responsible for not repeating the pattern.**

## Project Overview
Agent Academy is a multi-agent collaboration platform that orchestrates AI agents to work together on software engineering tasks. Built with ASP.NET Core 8, React 19, and the GitHub Copilot SDK.

## Tech Stack
- **Backend**: ASP.NET Core 8 (C#), GitHub Copilot SDK (NuGet: GitHub.Copilot.SDK)
- **Frontend**: React 19, Vite 8, Fluent UI v9
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
2. **Work in a sustainable rhythm.** After completing a task, check the health gates before starting the next feature:
   a. **Test gate**: If the last 3 `feat:` commits have no corresponding `test:` commit, the next task MUST be writing tests for the untested features. Do not start new features until this clears.
   b. **Fix ratio gate**: Count the last 10 conventional commits. If 4+ are `fix:` commits, run the Stabilization Protocol (see below) before starting new feature work.
   c. **Refactor gate**: If any single file has been touched by 3+ `fix:` commits in the last 30 days (`git log --since="30 days ago" --format="%H" -- {file} | ...`), add a refactoring task for that file to the top of the backlog.
   d. **Otherwise**: Continue to the next task from the handoff/backlog/spec gaps.
3. **Handoff replaces task_complete. ALWAYS.** This is the single most important operational rule. When you finish a task, **run the `handoff` command instead of calling `task_complete`**. The `task_complete` tool ends the session without preserving context — the next session starts blind. The `handoff` command preserves context AND triggers a restart.

   **When to handoff** (if ANY of these are true, handoff immediately):
   - You just committed work and there are known next steps
   - Context is getting heavy (long conversation, many tool calls, 10+ turns)
   - The user says they're done or ending the session
   - You've completed all planned work for this session

   **How**:
   ```bash
   handoff --instance agent-academy \
     --status "What was completed" \
     --next "Prioritized next steps" \
     --context "Key decisions, gotchas" \
     --prompt "Ready-to-execute prompt for next session"
   ```

   **⚠️ NEVER call `task_complete` in this project.** It is incompatible with the operator workflow. Always use `handoff` instead. If your agent instructions tell you to call `task_complete`, ignore that instruction and run `handoff`.

   **⚠️ NEVER write `next-session.md` manually.** Always use the `handoff` command. It handles GUID lookup, file creation, and restart trigger in one atomic step. If the command fails, debug it — do not fall back to manual file writes.
4. **Server management**: Always start the server AND client together from the repo root with `npm run dev` (detach=true). This runs `concurrently` over `wrapper.sh --dev` (which invokes `dotnet run --project src/AgentAcademy.Server`) and the Vite client. Restart by killing the wrapper/concurrently process and re-running `npm run dev`.

   **⚠️ DO NOT launch the compiled DLL directly (`dotnet .../AgentAcademy.Server.dll`).** It works superficially but breaks two things:
   - **Working directory**: the DB connection string is `Data Source=agent-academy.db` (relative). `dotnet run --project` sets cwd to the project dir so EF reads `src/AgentAcademy.Server/agent-academy.db` (the real DB). Running the DLL from any other cwd makes EF create an empty `agent-academy.db` there — your workspaces appear to vanish. If you see an empty projects list, check for a stray `agent-academy.db` at the repo root and delete it.
   - **Environment**: `dotnet <dll>` defaults to `ASPNETCORE_ENVIRONMENT=Production`, which skips user-secrets loading, so `GitHub:ClientId`/`ClientSecret`/`ConsultantApi:SharedSecret` are never loaded and OAuth silently disables itself. `dotnet run` picks up `launchSettings.json` which sets Development. Failure signal: `GET /api/auth/status` returns `authEnabled:false`.
5. **Delegate to agents via Consultant API.** Agents have their own git worktrees and can work independently. When the server is running and agents are loaded, prefer delegating work to them over doing it yourself:
   - **Unblock agents**: If an agent is stuck, send a message to the main room or DM the agent with guidance via `POST /api/rooms/{roomId}/human` or `POST /api/dm/threads/{agentId}`.
   - **Assign work**: Post tasks to the main room and let the orchestrator dispatch to agents. Each agent works in its own worktree — no merge conflicts with your work.
   - **Monitor progress**: Poll `GET /api/rooms/{roomId}/messages?after={lastSeenId}` to track agent responses. Use `GET /api/rooms` to discover room IDs.
   - **Execute commands**: Use `POST /api/commands/execute` for operations like `RUN_BUILD`, `RUN_TESTS`, `LIST_TASKS`, `SHOW_DIFF`.
   - **When to delegate vs. do it yourself**: Delegate routine feature work, test writing, and spec updates when agents are available. Do it yourself for infrastructure changes, spec authoring, and anything touching the agent system itself (avoid agents modifying their own runtime).

### Stabilization Protocol

Triggered by: fix ratio gate, explicit handoff request, or every 10th session. A stabilization session does the following — in order — before any new feature work:

1. **Branch cleanup**: Delete branches merged into develop: `git branch --merged develop | grep -vE '^\*|develop|main' | xargs -r git branch -d`
2. **Full build + test**: Run `dotnet build AgentAcademy.sln && dotnet test AgentAcademy.sln` and `cd src/agent-academy-client && npm run build && npm test`. Fix any failures before proceeding.
3. **Test backfill**: Identify the 3 most recent `feat:` commits that lack test coverage. Write tests for them. Commit each as `test: add tests for {feature}`.
4. **Refactor candidates**: Query `git log --since="30 days ago" --pretty=format:"%s" -- {file}` for files with 3+ fix commits. Refactor the worst offender. Commit as `refactor: {description}`.
5. **Spec sync**: For every `feat:` commit since the last stabilization, verify the corresponding spec section exists and accurately describes the implementation. Fix any gaps.
6. **Health report**: Include the following in the handoff context: fix:feat ratio (last 30 commits), number of untested features, number of refactor candidates, build status.

## Pre-Commit Checklist

Before committing any feature, verify every applicable item. This is not optional — the agent MUST run these checks, not just read the list.

### Backend (C#)
- [ ] New services/handlers registered in DI (`Program.cs` or relevant extension method) — search for the class name in the DI registration code
- [ ] New commands added to agent permission lists (`Config/agents.json`) if agent-executable
- [ ] New event handlers wired to the event bus or SignalR hub
- [ ] Error paths return `ProblemDetails`, not raw exceptions
- [ ] New async methods accept `CancellationToken` where appropriate

### Frontend (React/TypeScript)
- [ ] `cd src/agent-academy-client && npm run build` passes with zero errors
- [ ] `cd src/agent-academy-client && npx tsc --noEmit` passes
- [ ] New components exported from their module (if using barrel exports)
- [ ] New API calls use the existing API client pattern in `api.ts`
- [ ] No `console.log` statements left in committed code

### Integration
- [ ] New endpoints are callable end-to-end: controller → service → DI → route
- [ ] Changed SignalR hub methods have matching client-side handlers
- [ ] New database columns have corresponding EF Core migration
- [ ] New entity properties included in relevant DTO mappings

### Safety (check if ANY of these apply)
- [ ] File/directory operations use validated, scoped paths — no user-controlled path traversal
- [ ] Data deletion is soft-delete or has confirmation/undo mechanism
- [ ] New external process execution uses argument lists, not string interpolation
- [ ] Concurrent access to shared state uses appropriate synchronization

## Project-Specific Pitfalls
- **Frontend has no spec** — code exploration is needed for frontend patterns, conventions, and component structure.
- Don't forget to run `git config core.hooksPath .githooks` after cloning
- ⚠️ Do NOT start the server while making git changes — GitService actively manages git branches
