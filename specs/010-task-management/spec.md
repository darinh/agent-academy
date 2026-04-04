# 010 — Task Management & Git Workflow

**Status**: Partial

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

## 1. Task Model (Extended)

### Current State

`TaskSnapshot` has: `Id`, `Title`, `Description`, `SuccessCriteria`, `Status`, `CurrentPhase`, `CurrentPlan`, `PreferredRoles`, `CreatedAt`, `UpdatedAt`.

### New Fields

Add to `TaskSnapshot` and `TaskEntity`:

| Field | Type | Description |
|-------|------|-------------|
| `Type` | `TaskType` enum | `Feature`, `Bug`, `Chore`, `Spike` — defaults to `Feature` |
| `Size` | `TaskSize` enum | `XS`, `S`, `M`, `L`, `XL` — estimated effort |
| `StartedAt` | `DateTime?` | When work began (first commit or status → Active) |
| `CompletedAt` | `DateTime?` | When task was marked complete |
| `AssignedAgentId` | `string?` | The named agent responsible (e.g., `software-engineer-1`) |
| `AssignedAgentName` | `string?` | Display name (e.g., `Hephaestus`) |
| `UsedFleet` | `bool` | Whether a fleet/subagent swarm was used |
| `FleetModels` | `List<string>` | Models used in the fleet (if any) |
| `BranchName` | `string?` | Local task branch: `task/{slug}-{suffix}` |
| `PullRequestUrl` | `string?` | GitHub PR URL |
| `PullRequestNumber` | `int?` | GitHub PR number |
| `PullRequestStatus` | `PullRequestStatus?` | `Open`, `ReviewRequested`, `ChangesRequested`, `Approved`, `Merged`, `Closed` |
| `ReviewerAgentId` | `string?` | Always `reviewer-1` (Socrates) unless overridden |
| `ReviewRounds` | `int` | Number of review iterations |
| `TestsCreated` | `List<string>` | Test files/names created to prove the work |
| `CommitCount` | `int` | Number of commits on the task branch |
| `MergeCommitSha` | `string?` | SHA of the squash-merge commit created by `MERGE_TASK` |

### New Enums

```csharp
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskType { Feature, Bug, Chore, Spike }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskSize { XS, S, M, L, XL }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PullRequestStatus { Open, ReviewRequested, ChangesRequested, Approved, Merged, Closed }
```

### TaskCommentEntity

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

### Extended TaskStatus

Add to existing `TaskStatus` enum:

- `InReview` — Work completed, awaiting reviewer approval
- `ChangesRequested` — Socrates requested fixes
- `Approved` — PR approved, ready to merge
- `Merging` — Merge in progress

---

## 2. Agent Identity & Git Attribution

### Agent Git Identity

Each named agent has a Git identity for commits and PRs:

| Agent | Git Author | Email |
|-------|-----------|-------|
| Aristotle | `Aristotle (Planner)` | `aristotle@agent-academy.local` |
| Archimedes | `Archimedes (Architect)` | `archimedes@agent-academy.local` |
| Hephaestus | `Hephaestus (Engineer)` | `hephaestus@agent-academy.local` |
| Athena | `Athena (Engineer)` | `athena@agent-academy.local` |
| Socrates | `Socrates (Reviewer)` | `socrates@agent-academy.local` |
| Thucydides | `Thucydides (Writer)` | `thucydides@agent-academy.local` |

### Git Config

Add to `AgentDefinition`:

```csharp
public record AgentGitIdentity(
    string AuthorName,
    string AuthorEmail
);
```

Add to `agents.json`:

```json
{
  "id": "software-engineer-1",
  "name": "Hephaestus",
  "gitIdentity": {
    "authorName": "Hephaestus (Engineer)",
    "authorEmail": "hephaestus@agent-academy.local"
  }
}
```

### Fleet Usage

When a named agent uses a fleet (subagent swarm), the named agent is still the commit author. The fleet models are recorded in `FleetModels` on the task. The named agent is responsible for reviewing fleet output before committing.

---

## 3. Git Workflow

### Branch Naming

```
agents/{agent-name}/{task-slug}
```

Examples:
- `agents/hephaestus/fix-login-crash`
- `agents/athena/add-dashboard-charts`
- `agents/thucydides/update-api-docs`

### Target Branch

- Default target: `develop`
- If a feature branch exists that collects related PRs: target that branch instead
- The target branch is determined at task creation or can be specified manually

### Workflow Steps (Planned)

**Note**: The following workflow describes a future GitHub integration. The current implementation uses local branch workflow without remote push or PR creation. See section 3.5 for the implemented local branch workflow.

```
1. Agent receives task assignment
2. Agent creates branch: git checkout -b agents/{name}/{slug}
3. Agent works, making commits with their git identity
4. Agent pushes branch to remote
5. Agent opens PR via GitHub API:
   - Title: task title
   - Body: task description + success criteria + test summary
   - Base: develop (or feature branch)
   - Labels: agent name, task size
6. Agent sets task status to InReview
7. Socrates is notified
```

### Commit Format (Planned)

Agents follow conventional commits:

```
feat(scope): description

Body explaining what and why.

Tested-by: list of test files
Task-id: {task-id}
```

Author is always the named agent's git identity, never the fleet model.

---

## 3.5. Breakout Room Branch Workflow

When a task is assigned, the platform creates a dedicated breakout room and task branch. The agent works in isolation on its own branch, and commands (such as `SHELL git-commit`) execute on the task branch — not `develop`.

### Branch Creation

- Branch name: `task/{slug}-{suffix}` (six-character GUID suffix generated at branch creation)
- Created automatically when a task is assigned
- Agent work goes on the task branch, not `develop`

### Assignment Behavior

- Task assignment creates a `BreakoutRoomEntity` and a `TaskItemEntity` linked to it
- The orchestrator ensures the breakout room has a persisted `TaskId`; if none exists yet, it creates a new `TaskEntity` for that breakout and stores the link before branch creation continues
- The generated branch name is written exactly once to the linked task and is never reassigned from room, agent, or title heuristics
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

### Known Gaps

- No `REJECT_TASK` command for branch-based tasks. If a reviewer finds issues after approval, the only recourse is `git revert`. A rejection flow (setting task back to `ChangesRequested` and spawning a new breakout) is a future enhancement.

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
| `APPROVE_TASK` | Sets task status to `Approved`, posts approval message in room |
| `REQUEST_CHANGES` | Sets task status to `ChangesRequested`, posts feedback in room |

> **Source**: `src/AgentAcademy.Server/Commands/Handlers/ApproveTaskHandler.cs`, `RequestChangesHandler.cs`

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

> **Source**: `src/AgentAcademy.Server/Commands/Handlers/MergeTaskHandler.cs`, `src/AgentAcademy.Server/Services/GitService.cs`

---

## 5. GitHub Integration (Planned)

**Status**: Planned. The task model includes PR-related fields (`PullRequestUrl`, `PullRequestNumber`, `PullRequestStatus`) to support future GitHub integration, but no GitHub API service exists. The current implementation uses local branch workflow with `MERGE_TASK` command.

### Planned Authentication

Agent Academy will need GitHub API access to:
- Push branches to remote
- Open PRs
- Leave review comments
- Approve PRs
- Merge PRs via API

**Approach**: GitHub OAuth App or GitHub App installation

1. User authenticates to Agent Academy via GitHub OAuth
2. Agent Academy receives an access token scoped to the user's repositories
3. All GitHub API calls are made with this token
4. Agent identity is conveyed via commit author (not API caller)

### Planned Required Scopes

- `repo` — full repository access (branches, PRs, commits)
- `read:user` — user profile for display

### Planned Service: `GitHubService`

```csharp
public interface IGitHubService
{
    // Auth
    Task<bool> IsAuthenticatedAsync();
    Task<string> GetAuthUrlAsync(string callbackUrl);
    Task StoreTokenAsync(string code);

    // Branches
    Task PushBranchAsync(string repo, string branchName);

    // PRs
    Task<PullRequestInfo> CreatePullRequestAsync(string repo, CreatePrRequest request);
    Task<PullRequestInfo> GetPullRequestAsync(string repo, int prNumber);
    Task AddReviewCommentAsync(string repo, int prNumber, string body, string? path, int? line);
    Task ApprovePullRequestAsync(string repo, int prNumber, string body);
    Task RequestChangesAsync(string repo, int prNumber, string body);
    Task MergePullRequestAsync(string repo, int prNumber, MergeMethod method);
}
```

---

## 6. Frontend: Task List Panel

### Location

Below the Main Collaboration Room workspace header, above or alongside the tab bar. Visible when a workspace is loaded.

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

### Ordering

Comments are ordered by `CreatedAt` ascending.

### API

- `GET /api/tasks/{id}/comments` — returns all comments for a task, ordered by creation time
- Command: `ADD_TASK_COMMENT` (see 007-agent-commands, Phase 1B)

---

## 7. API Endpoints

### Task Management (extend existing)

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `GET /api/tasks` | GET | List all tasks with full metadata |
| `GET /api/tasks/{id}` | GET | Get single task detail |
| `PUT /api/tasks/{id}/assign` | PUT | Assign agent to task |
| `PUT /api/tasks/{id}/status` | PUT | Update task status |
| `PUT /api/tasks/{id}/branch` | PUT | Record branch name |
| `PUT /api/tasks/{id}/pr` | PUT | Record PR info |
| `PUT /api/tasks/{id}/tests` | PUT | Record tests created |
| `PUT /api/tasks/{id}/complete` | PUT | Mark complete with final metadata |
| `GET /api/tasks/{id}/comments` | GET | List task comments, ordered by creation time |

### GitHub Integration

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `GET /api/github/auth` | GET | Get OAuth authorization URL |
| `GET /api/github/callback` | GET | OAuth callback handler |
| `GET /api/github/status` | GET | Check auth status |
| `DELETE /api/github/auth` | DELETE | Revoke token |

### Review Pipeline

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `POST /api/tasks/{id}/review/request` | POST | Request Socrates review |
| `POST /api/tasks/{id}/review/complete` | POST | Socrates posts review result |
| `POST /api/tasks/{id}/review/approve` | POST | Socrates approves |

---

## 8. Auto-Spec Task on Onboard

### Current Bug

`WorkspaceController.OnboardProject` scans and activates the workspace but does NOT create a spec generation task when `!hasSpecs`. The frontend dialog says "spec will be generated automatically" but nothing happens.

### Fix

Port v1 logic from `local-agent-host/server/src/index.ts:252-301`:

In `OnboardProject`, after scan, if `!scan.HasSpecs`:
1. Create a `TaskAssignmentRequest`:
   - Title: `"Generate Project Specification"`
   - Description: analyze codebase, generate spec in `specs/`
   - SuccessCriteria: spec files created and committed
   - PreferredRoles: `["Planner", "TechnicalWriter"]`
2. Call `WorkspaceRuntime.CreateTaskAsync(request)`
3. Call `AgentOrchestrator.HandleHumanMessageAsync(roomId)`
4. Return `specTaskCreated: true` and `roomId` in `OnboardResult`

Update `OnboardResult`:
```csharp
public record OnboardResult(
    ProjectScanResult Scan,
    WorkspaceMeta Workspace,
    bool SpecTaskCreated = false,
    string? RoomId = null
);
```

Update client `OnboardResult` type in `api.ts` to match.

---

## 9. Orchestration Integration

### Task Assignment Flow

When a task is created (via `/api/tasks` or auto-spec):

1. Orchestrator evaluates preferred roles
2. Selects the most appropriate named agent
3. Agent acknowledges task in the room
4. Agent creates a branch (via `IGitOperationsService`)
5. Agent begins work, posting progress messages to the room
6. Agent records commits, tests, and progress on the task record

### Task Creation Gating

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

## 10. Implementation Phases

### Phase 1: Auto-Spec Task Bug Fix
- Fix `WorkspaceController.OnboardProject` to auto-create spec task
- Update `OnboardResult` types (server + client)
- Ensure orchestrator kicks off and agents communicate progress
- **Estimated scope**: 1 session

### Phase 2: Task Model Extension
- Add new fields to `TaskSnapshot`, `TaskEntity`
- Add new enums (`TaskSize`, `PullRequestStatus`)
- Add new `TaskStatus` values
- Create/run EF migration
- Add API endpoints for task metadata updates
- **Estimated scope**: 1 session

### Phase 3: Frontend Task List
- New `TaskListPanel` component
- Task cards with metadata display
- Grouping/sorting logic
- Integrate below workspace header
- **Estimated scope**: 1 session

### Phase 4: GitHub Integration (Future)
- GitHub OAuth flow
- `IGitHubService` implementation
- Remote branch push
- PR creation, review comments, approval, merge via API
- **Estimated scope**: 2-3 sessions

---

## Invariants

1. A task's `AssignedAgentName` must correspond to a configured agent in `agents.json`
2. All commits on an agent branch must be authored by that agent's git identity
3. A task in `InReview` must have a non-null `BranchName`
4. Socrates is the only agent that can approve tasks (unless the review pipeline is explicitly overridden)
5. A task cannot transition to `Completed` without a `MergeCommitSha` (written by `MERGE_TASK` handler)
6. Named agents are responsible for fleet output — fleet models are recorded but the agent is the author

## Known Gaps

- **GitHub PR integration not implemented** — task model has PR fields but no API service exists
- No remote push capability — all work is local-only
- No `REJECT_TASK` command for reverting approved tasks back to `ChangesRequested`
- Agent git identity configuration not fully utilized (planned for future GitHub integration)
- Conflict resolution during `MERGE_TASK` is abort-only (no interactive resolution)
- No formal limit on review rounds (tracked but not enforced)

## Revision History

| Date | Change | Author |
|------|--------|--------|
| 2026-04-01 | Remove unimplemented PR workflow content — marked GitHub integration as Planned | Thucydides |
| 2026-03-28 | Initial spec — Planned | Copilot (via Anvil) |
