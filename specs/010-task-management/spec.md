# 010 — Task Management & Git Workflow

**Status**: Implemented

## Purpose

Defines the task lifecycle, local branch-based Git workflow, review pipeline, agent identity model, and the frontend task management UI. This is the system that turns collaboration room discussions into tracked, reviewable, mergeable work.

## Overview

When agents work on tasks, they follow a branch-based local squash-merge workflow:

1. A task is created (manually or automatically, e.g., after onboarding)
2. An agent claims the task and is moved to a dedicated breakout room on a `task/{slug}-{suffix}` branch
4. When complete, the agent sets the task status to `InReview`
5. Socrates (Reviewer) performs adversarial review on the task branch
6. If changes are requested, the agent makes fixes on the same task branch
7. On approval, Socrates or Aristotle issues `MERGE_TASK` to squash-merge into `develop`

All of this is visible in the frontend: a task list below the Main Collaboration Room showing in-progress, incomplete, and completed work with rich metadata.

> **Note**: The system also includes metadata fields for GitHub PR integration (`PullRequestNumber`, `PullRequestUrl`, `PullRequestStatus`), but the current implementation does not use GitHub PRs. Tasks are merged via local `MERGE_TASK` command which invokes `GitService.SquashMergeAsync()` (see `src/AgentAcademy.Server/Commands/Handlers/MergeTaskHandler.cs`).

---

## 1. Task Model

### TaskSnapshot

> **Source**: `src/AgentAcademy.Shared/Models/Tasks.cs`

`TaskSnapshot` is an immutable record used for API responses and SignalR broadcasts:

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `string` | Unique task identifier |
| `Title` | `string` | Task title |
| `Description` | `string` | Task description |
| `SuccessCriteria` | `string?` | Acceptance criteria |
| `Status` | `TaskStatus` | Current lifecycle status |
| `Type` | `TaskType` | `Feature`, `Bug`, `Chore`, `Spike` — defaults to `Feature` |
| `CurrentPhase` | `string?` | Current work phase (e.g., `Planning`, `Implementation`) |
| `CurrentPlan` | `string?` | Agent's working plan for the task |
| `ValidationStatus` | `string?` | Status of validation pass |
| `ValidationSummary` | `string?` | Summary of validation results |
| `ImplementationStatus` | `string?` | Status of implementation work |
| `ImplementationSummary` | `string?` | Summary of implementation progress |
| `PreferredRoles` | `List<string>` | Roles best suited for this task |
| `CreatedAt` | `DateTime` | When the task was created |
| `UpdatedAt` | `DateTime` | Last modification timestamp |
| `Size` | `TaskSize?` | `XS`, `S`, `M`, `L`, `XL` — estimated effort |
| `StartedAt` | `DateTime?` | When work began (first commit or status → Active) |
| `CompletedAt` | `DateTime?` | When task was marked complete |
| `AssignedAgentId` | `string?` | The named agent responsible (e.g., `software-engineer-1`) |
| `AssignedAgentName` | `string?` | Display name (e.g., `Hephaestus`) |
| `UsedFleet` | `bool` | Whether a fleet/subagent swarm was used |
| `FleetModels` | `List<string>` | Models used in the fleet (if any) |
| `BranchName` | `string?` | Local task branch: `task/{slug}-{suffix}` |
| `PullRequestUrl` | `string?` | GitHub PR URL (planned — not used) |
| `PullRequestNumber` | `int?` | GitHub PR number (planned — not used) |
| `PullRequestStatus` | `PullRequestStatus?` | PR status (planned — not used) |
| `ReviewerAgentId` | `string?` | Always `reviewer-1` (Socrates) unless overridden |
| `ReviewRounds` | `int` | Number of review iterations (incremented on approve and request-changes) |
| `TestsCreated` | `List<string>` | Test files/names created to prove the work |
| `CommitCount` | `int` | Number of commits on the task branch |
| `MergeCommitSha` | `string?` | SHA of the squash-merge commit created by `MERGE_TASK` |
| `CommentCount` | `int` | Number of task comments (computed at query time) |

### TaskEntity

> **Source**: `src/AgentAcademy.Server/Data/Entities/TaskEntity.cs`

`TaskEntity` is the EF Core persistence model. It has the same fields as `TaskSnapshot` plus:

| Field | Type | Description |
|-------|------|-------------|
| `RoomId` | `string?` | FK to the room where this task was created |
| `Room` | `RoomEntity?` | Navigation property |

### Enums

> **Source**: `src/AgentAcademy.Shared/Models/Enums.cs`

```csharp
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskStatus
{
    Queued,              // Waiting to be started
    Active,              // Work in progress
    Blocked,             // Cannot proceed (blocker recorded)
    AwaitingValidation,  // Implementation done, awaiting validation pass
    InReview,            // Work completed, awaiting reviewer approval
    ChangesRequested,    // Reviewer requested fixes
    Approved,            // Reviewer approved, ready to merge
    Merging,             // Merge in progress
    Completed,           // Merged and done
    Cancelled            // Task cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskType { Feature, Bug, Chore, Spike }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskSize { XS, S, M, L, XL }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PullRequestStatus { Open, ReviewRequested, ChangesRequested, Approved, Merged, Closed }
```

### TaskCommentEntity

> **Source**: `src/AgentAcademy.Server/Data/Entities/TaskCommentEntity.cs`

```csharp
public class TaskCommentEntity
{
    public string Id { get; set; }
    public string TaskId { get; set; }
    public string AgentId { get; set; }
    public string AgentName { get; set; }
    public string CommentType { get; set; } // Comment, Finding, Evidence, Blocker
    public string Content { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

`CommentType` values: `Comment` (general note), `Finding` (review observation), `Evidence` (verification proof), `Blocker` (blocking issue). Only the task's assignee, reviewer, or planner can add comments.

---

## 2. Agent Identity & Git Attribution

### Agent Git Identity

> **Source**: `src/AgentAcademy.Shared/Models/Agents.cs`, `src/AgentAcademy.Server/Config/agents.json`

Each named agent has a Git identity for commits:

| Agent | Git Author | Email |
|-------|-----------|-------|
| Aristotle | `Aristotle (Planner)` | `aristotle@agent-academy.local` |
| Archimedes | `Archimedes (Architect)` | `archimedes@agent-academy.local` |
| Hephaestus | `Hephaestus (Engineer)` | `hephaestus@agent-academy.local` |
| Athena | `Athena (Engineer)` | `athena@agent-academy.local` |
| Socrates | `Socrates (Reviewer)` | `socrates@agent-academy.local` |
| Thucydides | `Thucydides (Writer)` | `thucydides@agent-academy.local` |

### Git Config

`AgentDefinition` includes an optional `GitIdentity`:

```csharp
public record AgentGitIdentity(
    string AuthorName,
    string AuthorEmail
);
```

Configured in `agents.json` (PascalCase keys):

```json
{
  "id": "software-engineer-1",
  "name": "Hephaestus",
  "GitIdentity": {
    "AuthorName": "Hephaestus (Engineer)",
    "AuthorEmail": "hephaestus@agent-academy.local"
  }
}
```

### Fleet Usage

When a named agent uses a fleet (subagent swarm), the named agent is still the commit author. The fleet models are recorded in `FleetModels` on the task. The named agent is responsible for reviewing fleet output before committing.

---

## 3. Git Workflow

### Branch Naming (Implemented)

> **Source**: `src/AgentAcademy.Server/Services/GitService.cs:53-75`

Task branches use the pattern:

```
task/{slug}-{suffix}
```

Where `{suffix}` is a six-character GUID. Examples:
- `task/fix-login-crash-a1b2c3`
- `task/add-dashboard-charts-d4e5f6`

### Target Branch

- Default target: `develop`
- Squash-merge via `MERGE_TASK` always targets `develop`

### Branch Naming (Planned — GitHub Integration)

When GitHub PR integration is added, branches will use the pattern:

```
agents/{agent-name}/{task-slug}
```

This allows attributing branches to named agents in the remote repository.

### Commit Format

Agents follow conventional commits. The `MERGE_TASK` handler generates the squash-merge commit message using a type prefix:

| TaskType | Prefix |
|----------|--------|
| `Feature` | `feat: ` |
| `Bug` | `fix: ` |
| `Chore` | `chore: ` |
| `Spike` | `docs: ` |

Commit message format: `{prefix}{task.Title}`

> **Source**: `src/AgentAcademy.Server/Commands/Handlers/MergeTaskHandler.cs:23-33`

---

## 3.5. Breakout Room Branch Workflow

When a task is assigned, the platform creates a dedicated breakout room and task branch. The agent works in isolation on its own branch, and commands (such as `SHELL git-commit`) execute on the task branch — not `develop`.

### Branch Creation

> **Source**: `src/AgentAcademy.Server/Services/GitService.cs:53-75`

- Branch name: `task/{slug}-{suffix}` (six-character GUID suffix generated at branch creation)
- Created automatically when a task is assigned
- Agent work goes on the task branch, not `develop`

### Assignment Behavior

> **Source**: `src/AgentAcademy.Server/Services/AgentOrchestrator.cs:452-563`, `src/AgentAcademy.Server/Services/WorkspaceRuntime.cs:1943-2027`

- Task assignment creates a `BreakoutRoomEntity` and a `TaskItemEntity` linked to it
- The orchestrator ensures the breakout room has a persisted `TaskId`; if none exists yet, it creates a new `TaskEntity` for that breakout and stores the link before branch creation continues (`EnsureTaskForBreakoutAsync`)
- The generated branch name is written exactly once to the linked task and is never reassigned from room, agent, or title heuristics (`UpdateTaskBranchAsync` is write-once with conflict logging)
- The system posts an assignment notice in the main room and moves the assignee into the breakout room

### Completion Flow

1. Agent finishes work on the task branch → the linked task status moves to `InReview` (not `Completed`)
2. The existing reviewer cycle can still post review feedback in the collaboration room, but task approval remains an explicit command transition
3. Reviewer approves via `APPROVE_TASK` → task status moves to `Approved`
4. Reviewer (or Planner) executes `MERGE_TASK` command
5. `MERGE_TASK` squash-merges the task branch to `develop`
6. On successful merge → task status moves to `Completed`, merge commit SHA recorded on TaskEntity
7. On merge conflict → merge is aborted, the task returns to `Approved`, and an error is returned to the caller

### Task Identity on Assignment

When a task is assigned, the breakout room's persisted `TaskId` is the only source of truth for the linked `TaskEntity`:

1. If the breakout room already has a `TaskId`, the orchestrator reuses that task
2. Otherwise, it creates a new `TaskEntity`, stores the new `TaskId` on the breakout room, and then records the generated branch on that task

The branch workflow must not infer task identity from title matches, room status, agent status, or "unassigned task" heuristics. If a write would replace a different existing `BranchName`, the operation fails and logs the conflict instead of mutating task metadata.

> **Source**: `src/AgentAcademy.Server/Services/WorkspaceRuntime.cs:977-1005` (write-once `UpdateTaskBranchAsync`), `src/AgentAcademy.Server/Services/WorkspaceRuntime.cs:1888-1920` (`SetBreakoutTaskIdAsync`)

### Rejection Flow

After a task is approved (or even completed/merged), a reviewer or planner can reject it via `REJECT_TASK`:

1. Task status transitions from `Approved` or `Completed` → `ChangesRequested`
2. If the task was `Completed` with a `MergeCommitSha`, the merge commit is reverted on develop via `git revert --no-edit`
3. The `MergeCommitSha` and `CompletedAt` fields are cleared for completed tasks
4. The reviewer and review round count are updated
5. A rejection message (with `❌ Rejected` prefix) is posted to the task's room
6. The most recent archived breakout room for the task is reopened (status → `Active`, agent moved back to `Working` state)
7. The rejection reason is posted into the breakout room so the assigned agent sees it
8. A `TaskRejected` activity event is published

**Role gate**: Only `Planner`, `Reviewer`, or `Human` roles may invoke `REJECT_TASK`.

**Required arguments**: `taskId`, `reason`

> **Source**: `src/AgentAcademy.Server/Commands/Handlers/RejectTaskHandler.cs`, `src/AgentAcademy.Server/Services/WorkspaceRuntime.cs` (`RejectTaskAsync`, `TryReopenBreakoutForTaskAsync`), `src/AgentAcademy.Server/Services/GitService.cs` (`RevertCommitAsync`)

---

## 4. Review Pipeline (Socrates)

### Review Trigger

When a task transitions to `InReview`:
1. A system status message is posted to the collaboration room announcing the task is ready for review
2. Socrates reviews the work on the task branch
3. Socrates approves via `APPROVE_TASK` or requests changes via `REQUEST_CHANGES`

### Review Process

Socrates may use multiple models for review depth:

1. **Primary review**: Socrates's own model (`claude-opus-4.6`)
2. **Cross-model review**: Socrates may invoke additional models as fleet reviewers:
   - `gpt-5.4` for logic/correctness
   - `claude-sonnet-4.5` for style/architecture
   - Other models as configured

### Review Outcomes

| Command | Effect |
|---------|--------|
| `APPROVE_TASK` | Sets task status to `Approved`, increments `ReviewRounds`, posts approval message in room |
| `REQUEST_CHANGES` | Sets task status to `ChangesRequested`, increments `ReviewRounds`, posts feedback in room |

> **Source**: `src/AgentAcademy.Server/Commands/Handlers/ApproveTaskHandler.cs`, `RequestChangesHandler.cs`
> **Source**: `src/AgentAcademy.Server/Services/WorkspaceRuntime.cs:1110-1175` (ReviewRounds increment logic)

> **Note**: `APPROVE_TASK`, `REQUEST_CHANGES`, and `REJECT_TASK` enforce Planner/Reviewer/Human role gates at the handler level. Engineers and other roles are denied.

### Fix & Re-review Cycle

```
1. Owning agent sees ChangesRequested
2. Agent makes fixes in a new breakout round
3. Agent sets task status back to InReview
4. Socrates re-reviews
5. Review rounds tracked via TaskEntity.ReviewRounds field
```

### Post-Approval Merge

After Socrates approves a task (`APPROVE_TASK` command), a reviewer or planner invokes `MERGE_TASK` to squash-merge the task branch:

```
1. Socrates issues APPROVE_TASK command
2. Task status → Approved
3. Socrates or Aristotle issues MERGE_TASK: TaskId={id}
4. GitService.SquashMergeAsync(branchName, commitMessage) executes with commit subject `{prefix}{task.Title}`
5. Task updated: CompletedAt, MergeCommitSha, CommitCount
6. Task status → Completed
7. System posts success message to room
```

`{prefix}` is derived from `TaskEntity.Type`: `Feature -> feat: `, `Bug -> fix: `, `Chore -> chore: `, `Spike -> docs: `.

> **Source**: `src/AgentAcademy.Server/Commands/Handlers/MergeTaskHandler.cs`, `src/AgentAcademy.Server/Services/GitService.cs:186-213`

---

## 5. GitHub Integration

**Status**: Implemented (Phase 1 — push + PR creation via `gh` CLI).

The system can push task branches to GitHub and create pull requests via the `gh` CLI. Task entities are updated with PR URL, number, and status.

### Authentication

GitHub operations use the `gh` CLI's stored credentials. The `gh` CLI must be authenticated on the server via `gh auth login`. Required scopes: `repo` (full repository access).

> **Source**: `src/AgentAcademy.Server/Services/GitHubService.cs`

The `GET /api/github/status` endpoint reports whether `gh` is authenticated and returns the repository slug.

> **Source**: `src/AgentAcademy.Server/Controllers/GitHubController.cs`

### Service: `IGitHubService` / `GitHubService`

> **Source**: `src/AgentAcademy.Server/Services/IGitHubService.cs`, `src/AgentAcademy.Server/Services/GitHubService.cs`

```csharp
public interface IGitHubService
{
    Task<bool> IsConfiguredAsync();
    Task<string?> GetRepositorySlugAsync();
    Task<PullRequestInfo> CreatePullRequestAsync(string branch, string title, string body, string baseBranch = "develop");
    Task<PullRequestInfo> GetPullRequestAsync(int prNumber);
}
```

`GitHubService` follows the same process-shell pattern as `GitService` — it spawns `gh` CLI subprocesses with `ArgumentList` for safe argument passing. The `gh` executable path is configurable for testability.

### `CREATE_PR` Command

> **Source**: `src/AgentAcademy.Server/Commands/Handlers/CreatePrHandler.cs`

Creates a GitHub pull request for a task.

| Argument | Required | Default | Description |
|----------|----------|---------|-------------|
| `taskId` | ✅ | — | Task to create PR for |
| `title` | — | Task title | Custom PR title |
| `body` | — | Task description + success criteria | Custom PR body |
| `baseBranch` | — | `develop` | Target branch for the PR |

**Role gate**: Assigned agent, Planner, Reviewer, or Human. Engineers can only create PRs for tasks assigned to them.

**Workflow**:
1. Validates task exists, has a branch, and has no existing PR
2. Checks GitHub is configured (`IsConfiguredAsync`)
3. Pushes branch to remote (`GitService.PushBranchAsync`)
4. Creates PR via `gh pr create` (`GitHubService.CreatePullRequestAsync`)
5. Updates task entity with PR URL, number, and `Open` status
6. Posts a system status message to the task's room

**Error handling**: On any failure (push or PR creation), returns `CommandErrorCode.Execution` with the error message. Task entity is not modified on failure.

### API Endpoints

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `GET /api/github/status` | GET | Returns `{ isConfigured, repository }` |

### Human Command Registry

`CREATE_PR` is registered in `HumanCommandRegistry` and available in the frontend command palette. It is also in the `CommandController` allowlist as an async command (returns 202 + polling).

### Push Support

> **Source**: `src/AgentAcademy.Server/Services/GitService.cs` — `PushBranchAsync`

`GitService.PushBranchAsync(branch)` runs `git push --set-upstream origin {branch}` under the git lock. This is used by `CREATE_PR` to push task branches before opening a PR.

### Known Gaps (Phase 2)

- ~~**No PR status sync**: Task PR status is set to `Open` on creation but not updated when the PR is reviewed, approved, or merged on GitHub. Needs webhook or polling.~~ — **resolved**: `PullRequestSyncService` polls GitHub every 2 minutes via `gh pr view --json reviewDecision`. Maps `REVIEW_REQUIRED` → `ReviewRequested`, `APPROVED` → `Approved`, `CHANGES_REQUESTED` → `ChangesRequested`, merged → `Merged`, closed → `Closed`. Emits `TaskPrStatusChanged` activity events. Skips tasks with terminal PR statuses. Error isolation per PR — one failure doesn't block others. 36 tests.
- **No review comments**: Cannot post or read PR review comments from Agent Academy.
- **No PR merge via API**: `MERGE_TASK` still uses local squash-merge. Future: option to merge via GitHub API.
- **No OAuth flow**: Relies on server-side `gh auth login`. A user-facing OAuth flow would enable self-service setup.

---

## 6. Frontend: Task List Panel

### Components

> **Source**: `src/agent-academy-client/src/TaskListPanel.tsx`, `src/agent-academy-client/src/TaskStatePanel.tsx`

The frontend includes two task-related components:
- **TaskListPanel** — displays the full task list with grouping and metadata
- **TaskStatePanel** — shows task state details

Both are integrated into the main `App.tsx` layout.

### Layout

```
┌──────────────────────────────────────────────┐
│ Workspace Header (room name, phase pill)     │
├──────────────────────────────────────────────┤
│ ▼ Tasks (3 active, 2 pending, 5 completed)   │
│                                              │
│ ● IN PROGRESS                                │
│ ┌──────────────────────────────────────────┐ │
│ │ [M] Fix auth middleware     🔧 Hephaestus│ │
│ │     PR #42 · In Review · 3 commits       │ │
│ └──────────────────────────────────────────┘ │
│                                              │
│ ○ PENDING                                    │
│ ┌──────────────────────────────────────────┐ │
│ │ [S] Update API docs        📝 Thucydides │ │
│ │     Queued · No branch yet               │ │
│ └──────────────────────────────────────────┘ │
│                                              │
│ ▶ COMPLETED (5)  ← collapsible              │
│                                              │
├──────────────────────────────────────────────┤
│ Tab Bar (Conversation, Plan, Timeline...)    │
├──────────────────────────────────────────────┤
│ Tab Content                                  │
└──────────────────────────────────────────────┘
```

### Task Card Content

Each task card shows:
- Size badge: `[XS]` `[S]` `[M]` `[L]` `[XL]`
- Title
- Assigned agent name + role icon
- Status indicator (dot color matches status)
- Branch name (if created)
- PR link (if opened) with PR status
- Commit count
- Duration (started → now or started → completed)
- Fleet indicator (if fleet was used)

### Task Detail (expandable or click-through)

Expanded view shows:
- Full description and success criteria
- Review history (rounds, comments summary)
- Tests created
- Timeline of status changes
- Agent attribution

### Grouping & Sorting

1. **In Progress** — `Active`, `InReview`, `ChangesRequested`, `Approved`, `Merging`
2. **Pending** — `Queued`, `Blocked`
3. **Completed** — `Completed`, `Cancelled` (collapsible, collapsed by default)

Within each group, sort by `UpdatedAt` descending.

---

## 6.5. Task Comments

Agents can attach structured comments to tasks to record findings, evidence, and blockers during work.

### Comment Types

| Type | Purpose |
|------|---------|
| `Comment` | General note or update (default) |
| `Finding` | Review observation or code issue |
| `Evidence` | Verification proof (test results, screenshots, logs) |
| `Blocker` | Blocking issue that prevents progress |

### Authorization

Only the following agents can comment on a task:
- The task's assigned agent (assignee)
- The task's reviewer
- Any agent with the `Planner` role

> **Source**: `src/AgentAcademy.Server/Commands/Handlers/AddTaskCommentHandler.cs`

### Ordering

Comments are ordered by `CreatedAt` ascending.

### API

- `GET /api/tasks/{id}/comments` — returns all comments for a task, ordered by creation time
- Command: `ADD_TASK_COMMENT` (see 007-agent-commands)

> **Source**: `src/AgentAcademy.Server/Controllers/CollaborationController.cs:62-77`

---

## 7. API Endpoints

### Task Management

> **Source**: `src/AgentAcademy.Server/Controllers/CollaborationController.cs`

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `POST /api/tasks` | POST | Create a new task |
| `GET /api/tasks` | GET | List all tasks with full metadata |
| `GET /api/tasks/{id}` | GET | Get single task detail |
| `GET /api/tasks/{id}/comments` | GET | List task comments, ordered by creation time |
| `PUT /api/tasks/{id}/assign` | PUT | Assign agent to task |
| `PUT /api/tasks/{id}/status` | PUT | Update task status |
| `PUT /api/tasks/{id}/branch` | PUT | Record branch name |
| `PUT /api/tasks/{id}/pr` | PUT | Record PR info |
| `PUT /api/tasks/{id}/complete` | PUT | Mark complete with final metadata |

### Review Pipeline

Reviews happen via agent commands (`APPROVE_TASK`, `REQUEST_CHANGES`), not REST endpoints. There are no REST review endpoints.

### GitHub Integration (Planned)

See section 5 for planned GitHub API endpoints.

---

## 8. Auto-Spec Task on Onboard

> **Source**: `src/AgentAcademy.Server/Controllers/WorkspaceController.cs:138-195`

When a project is onboarded and has no existing specs (`!scan.HasSpecs`), the system automatically creates a spec generation task:

1. Checks for an existing task with title `"Generate Project Specification"` via `FindTaskByTitleAsync` to prevent duplicates
2. If no existing task: creates a `TaskAssignmentRequest` with:
   - Title: `"Generate Project Specification"`
   - Description: analyze codebase, generate spec in `specs/`
   - SuccessCriteria: spec files created and committed
   - PreferredRoles: `["Planner", "TechnicalWriter"]`
3. Calls `WorkspaceRuntime.CreateTaskAsync(request)`
4. Triggers `AgentOrchestrator.HandleHumanMessageAsync(roomId)` to kick off agent work
5. Returns `specTaskCreated: true` and `roomId` in `OnboardResult`

`OnboardResult`:
```csharp
public record OnboardResult(
    ProjectScanResult Scan,
    WorkspaceMeta Workspace,
    bool SpecTaskCreated = false,
    string? RoomId = null
);
```

---

## 9. Orchestration Integration

### Task Assignment Flow

> **Source**: `src/AgentAcademy.Server/Services/AgentOrchestrator.cs:452-563`

When a task is created (via `/api/tasks` or auto-spec):

1. Orchestrator evaluates preferred roles
2. Selects the most appropriate named agent
3. Agent acknowledges task in the room
4. Creates breakout room and task item (`CreateBreakoutRoomAsync`, `CreateTaskItemAsync`)
5. Ensures task entity linked to breakout (`EnsureTaskForBreakoutAsync`)
6. Creates task branch via `GitService.CreateTaskBranchAsync`
7. Records branch on task via `UpdateTaskBranchAsync` (write-once)
8. Agent begins work, posting progress messages to the room

### Task Creation Gating

> **Source**: `src/AgentAcademy.Server/Services/AgentOrchestrator.cs:452-473`

Task creation via TASK ASSIGNMENT blocks in agent responses is role-gated:
- Only agents with the `Planner` role can create tasks
- Exception: any agent can create a task with `Type: Bug`
- Non-planner agents attempting to create non-Bug tasks will have their TASK ASSIGNMENT block converted to a proposal message posted in the room (not a task)

### Review Notification Flow

When task status → `InReview`:

1. Activity event: `TaskReviewRequested`
2. Socrates receives the event
3. Socrates fetches diff using `SHOW_DIFF` command
4. Socrates performs review
5. Socrates posts review in room
6. Task status updated based on outcome (via `APPROVE_TASK` or `REQUEST_CHANGES`)

### Agent Communication

Agents communicate progress while working:
- `AgentThinking` / `AgentFinished` activity events
- System status messages via `WorkspaceRuntime.PostSystemStatusAsync()`
- Direct room messages via `WorkspaceRuntime.PostMessageAsync()`

Agents typically post:
- "Starting work on {task title}" when claimed
- Progress updates during implementation
- "Ready for review" when submitting to Socrates

---

## 10. Task-Related Commands

All task commands are implemented as `ICommandHandler` implementations.

| Command | Handler | Roles | Description |
|---------|---------|-------|-------------|
| `CLAIM_TASK` | `ClaimTaskHandler` | Any | Agent claims an unassigned task; auto-activates if queued |
| `RELEASE_TASK` | `ReleaseTaskHandler` | Any (own task only) | Unassigns the requesting agent from a task |
| `UPDATE_TASK` | `UpdateTaskHandler` | Any | Updates status/blocker/note; allowed statuses: Active, Blocked, AwaitingValidation, InReview, Queued |
| `APPROVE_TASK` | `ApproveTaskHandler` | Any (convention: Reviewer) | Approves task, increments ReviewRounds |
| `REQUEST_CHANGES` | `RequestChangesHandler` | Any (convention: Reviewer) | Requests changes with required findings, increments ReviewRounds |
| `MERGE_TASK` | `MergeTaskHandler` | Planner, Reviewer | Squash-merges task branch to develop; reports conflicting files on failure and suggests REBASE_TASK |
| `REBASE_TASK` | `RebaseTaskHandler` | Any | Rebases task branch onto develop; supports `dryRun=true` for conflict-only check |
| `CANCEL_TASK` | `CancelTaskHandler` | Planner, Reviewer | Cancels task, optionally deletes branch (default: yes) |
| `ADD_TASK_COMMENT` | `AddTaskCommentHandler` | Assignee, Reviewer, Planner | Adds structured comment (Comment/Finding/Evidence/Blocker) |
| `LIST_TASKS` | `ListTasksHandler` | Any | Lists tasks with optional `status` and `assignee` filters |
| `SHOW_REVIEW_QUEUE` | `ShowReviewQueueHandler` | Any | Returns tasks in `InReview` or `AwaitingValidation` |

> **Source**: `src/AgentAcademy.Server/Commands/Handlers/`

---

## 11. WorkspaceRuntime Task Methods

> **Source**: `src/AgentAcademy.Server/Services/WorkspaceRuntime.cs`

| Method | Line | Description |
|--------|------|-------------|
| `CreateTaskAsync` | 719 | Creates task with room, plan, agents |
| `GetTasksAsync` | 876 | Returns all tasks |
| `FindTaskByTitleAsync` | 915 | Finds latest non-cancelled task by exact title |
| `AssignTaskAsync` | 929 | Sets assigned agent on task |
| `UpdateTaskStatusAsync` | 954 | Updates task status |
| `UpdateTaskBranchAsync` | 977 | Write-once branch assignment |
| `UpdateTaskPrAsync` | 1010 | Records PR info on task |
| `CompleteTaskAsync` | 1026 | Marks task completed with metadata |
| `ClaimTaskAsync` | 1049 | Agent claims task (prevents double-claim) |
| `ReleaseTaskAsync` | 1082 | Agent releases task |
| `ApproveTaskAsync` | 1110 | Approves task, increments ReviewRounds |
| `RequestChangesAsync` | 1146 | Requests changes, increments ReviewRounds |
| `GetReviewQueueAsync` | 1182 | Returns tasks awaiting review |
| `PostTaskNoteAsync` | 1202 | Posts note to task's room |
| `AddTaskCommentAsync` | 1218 | Adds structured comment |
| `GetTaskCommentsAsync` | 1248 | Returns comments for a task |
| `GetTaskCommentCountAsync` | 1264 | Returns comment count |
| `SetBreakoutTaskIdAsync` | 1888 | Write-once breakout→task link |
| `GetBreakoutTaskIdAsync` | 1920 | Gets task ID for breakout |
| `TransitionBreakoutTaskToInReviewAsync` | 1930 | Moves breakout task to InReview |
| `EnsureTaskForBreakoutAsync` | 1943 | Creates or reuses task for breakout |

---

## Invariants

1. A task's `AssignedAgentName` must correspond to a configured agent in `agents.json` (convention — not enforced in code)
2. All commits on an agent branch are authored by that agent's git identity — `GitService.CommitAsync` and `SquashMergeAsync` pass `--author` when `AgentGitIdentity` is present in the `CommandContext`
3. A task in `InReview` must have a non-null `BranchName`
4. Task review commands (`APPROVE_TASK`, `REQUEST_CHANGES`, `REJECT_TASK`) are role-gated to Planner, Reviewer, and Human roles at the handler level
5. A task cannot transition to `Completed` without a `MergeCommitSha` (enforced by `MERGE_TASK` handler flow)
6. Named agents are responsible for fleet output — fleet models are recorded but the agent is the author
7. `UpdateTaskBranchAsync` is write-once — if a branch is already set and differs, the operation logs a conflict and does not mutate (enforced in code)
8. `SetBreakoutTaskIdAsync` is write-once — same conflict-logging behavior (enforced in code)

## Known Gaps

- ~~**GitHub PR integration not implemented** — task model has PR fields but no API service exists~~ — **resolved**: `IGitHubService` / `GitHubService` wraps `gh` CLI for PR creation and status queries. `CREATE_PR` command pushes branch + opens PR + updates task entity. `PullRequestSyncService` polls for status changes every 2 minutes. `GET /api/github/status` reports auth status. Phase 2 gaps: no review comments, no PR merge via API, no OAuth flow.
- ~~No remote push capability — all work is local-only~~ — **resolved**: `GitService.PushBranchAsync` pushes branches to remote origin. Used by `CREATE_PR` command.
- ~~No `REJECT_TASK` command for reverting approved tasks back to `ChangesRequested`~~ — **resolved**: `REJECT_TASK` handler supports `Approved` → `ChangesRequested` (simple status change + breakout reopen) and `Completed` → `ChangesRequested` (reverts merge commit on develop + breakout reopen). Role-gated to Planner, Reviewer, Human. 19 tests.
- ~~Agent git identity configuration exists but commits are not yet attributed to agents~~ — **resolved**: `GitService.CommitAsync` and `SquashMergeAsync` now accept `AgentGitIdentity` and pass `--author` to git. `CommandContext` carries the identity from `AgentDefinition.GitIdentity`. Wired through `ShellCommandHandler` (SHELL git-commit) and `MergeTaskHandler` (MERGE_TASK).
- ~~Conflict resolution during `MERGE_TASK` is abort-only (no interactive resolution)~~ — **resolved**: `MERGE_TASK` now detects conflicting files on merge failure and reports them with a suggestion to use `REBASE_TASK`. New `REBASE_TASK` command rebases task branches onto develop with conflict detection and abort-on-failure. Dry-run mode (`dryRun=true`) checks for conflicts without modifying the branch. `MergeConflictException` carries conflicting file paths. `DetectMergeConflictsAsync` performs non-destructive conflict checks. 18 new tests.
- ~~No formal limit on review rounds (tracked but not enforced)~~ — **resolved**: `MaxReviewRounds = 5` enforced in `RequestChangesAsync` and `RejectTaskAsync`. Tasks that exceed the limit cannot enter another review cycle — reviewer gets an error suggesting cancellation. 2 tests added.
- ~~`APPROVE_TASK` and `REQUEST_CHANGES` lack role gates — any agent can invoke them~~ — **resolved**: Both handlers now enforce Planner/Reviewer/Human role gates, matching `REJECT_TASK` and `MERGE_TASK`. 2 tests added.

## Revision History

| Date | Change | Author |
|------|--------|--------|
| 2026-04-05 | GitHub PR integration (Phase 1) — IGitHubService/GitHubService via gh CLI. CREATE_PR command pushes branch + opens PR + updates task entity. GitService.PushBranchAsync. GET /api/github/status endpoint. HumanCommandRegistry + CommandController allowlist. 23 new tests (980 total). | Anvil |
| 2026-04-04 | REBASE_TASK command + MERGE_TASK conflict reporting — rebase task branches onto develop with conflict detection, dry-run mode, MergeConflictException. MERGE_TASK now detects conflicting files on failure and suggests REBASE_TASK. 18 new tests (888 total). | Anvil |
| 2026-04-04 | REJECT_TASK command — reverts Approved/Completed tasks to ChangesRequested, reverts merge commit for completed tasks, reopens breakout rooms. Role-gated to Planner/Reviewer/Human. 19 tests. | Anvil |
| 2026-04-04 | Reconciled spec with code — Partial → Implemented. Documented all TaskSnapshot/TaskEntity fields, TaskStatus.AwaitingValidation, command table, WorkspaceRuntime method index, auto-spec dedup, role gate gaps, write-once invariants | Anvil |
| 2026-04-01 | Remove unimplemented PR workflow content — marked GitHub integration as Planned | Thucydides |
| 2026-03-28 | Initial spec — Planned | Copilot (via Anvil) |
