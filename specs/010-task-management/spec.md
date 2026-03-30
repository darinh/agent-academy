# 010 — Task Management & Git Workflow

**Status**: Planned

## Purpose

Defines the task lifecycle, agent Git workflow, PR review pipeline, agent identity model, and the frontend task management UI. This is the system that turns collaboration room discussions into tracked, reviewable, mergeable work.

## Overview

When agents work on tasks, they follow a structured workflow:

1. A task is created (manually or automatically, e.g., after onboarding)
2. An agent claims the task and creates a branch in the loaded project's repository
3. The agent works, making commits attributed to their identity
4. When complete, the agent opens a PR to the target branch
5. Socrates (Reviewer) performs adversarial review, potentially with multiple models
6. If changes are requested, the owning agent fixes and re-submits
7. On approval, the agent merges the PR and updates the task record

All of this is visible in the frontend: a task list below the Main Collaboration Room showing in-progress, incomplete, and completed work with rich metadata.

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
| `BranchName` | `string?` | Git branch: `agents/{agent-name}/{branch-slug}` |
| `PullRequestUrl` | `string?` | GitHub PR URL |
| `PullRequestNumber` | `int?` | GitHub PR number |
| `PullRequestStatus` | `PullRequestStatus?` | `Open`, `ReviewRequested`, `ChangesRequested`, `Approved`, `Merged`, `Closed` |
| `ReviewerAgentId` | `string?` | Always `reviewer-1` (Socrates) unless overridden |
| `ReviewRounds` | `int` | Number of review iterations |
| `TestsCreated` | `List<string>` | Test files/names created to prove the work |
| `CommitCount` | `int` | Number of commits on the task branch |

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

- `InReview` — PR opened, awaiting Socrates review
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

### Workflow Steps

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

### Commit Format

Agents follow conventional commits:

```
feat(scope): description

Body explaining what and why.

Tested-by: list of test files
Task-id: {task-id}
```

Author is always the named agent's git identity, never the fleet model.

---

## 4. PR Review Pipeline (Socrates)

### Review Trigger

When a task transitions to `InReview`:
1. Socrates receives a notification (via collaboration room message + activity event)
2. Socrates fetches the PR diff via GitHub API
3. Socrates performs adversarial review

### Review Process

Socrates may use multiple models for review depth:

1. **Primary review**: Socrates's own model (`claude-opus-4.6`)
2. **Cross-model review**: Socrates may invoke additional models as fleet reviewers:
   - `gpt-5.4` for logic/correctness
   - `claude-sonnet-4.5` for style/architecture
   - Other models as configured

### Review Outcomes

| Outcome | Action |
|---------|--------|
| **Approved** | Socrates approves PR via GitHub API, posts approval message in room, notifies owning agent |
| **Changes Requested** | Socrates leaves PR comments via GitHub API, posts summary in room, sets task status to `ChangesRequested`, notifies owning agent |

### Fix & Re-review Cycle

```
1. Owning agent sees ChangesRequested
2. Agent makes fixes, pushes commits to same branch
3. Agent posts message: "Ready for re-review"
4. Agent sets task status back to InReview
5. Socrates re-reviews (may focus on changed files only)
6. Max review rounds: configurable (default: 3)
```

### Post-Approval

```
1. Socrates approves PR
2. Task status → Approved
3. Owning agent merges PR (squash merge preferred)
4. Owning agent updates task: CompletedAt, CommitCount, PullRequestStatus → Merged
5. Task status → Completed
```

---

## 5. GitHub Integration

### Authentication

Agent Academy needs GitHub API access to:
- Create branches (or push to remote)
- Open PRs
- Leave review comments
- Approve PRs
- Merge PRs

**Approach**: GitHub OAuth App or GitHub App installation

1. User authenticates to Agent Academy via GitHub OAuth
2. Agent Academy receives an access token scoped to the user's repositories
3. All GitHub API calls are made with this token
4. Agent identity is conveyed via commit author (not API caller)

### Required Scopes

- `repo` — full repository access (branches, PRs, commits)
- `read:user` — user profile for display

### Service: `GitHubService`

New service encapsulating all GitHub API operations:

```csharp
public interface IGitHubService
{
    // Auth
    Task<bool> IsAuthenticatedAsync();
    Task<string> GetAuthUrlAsync(string callbackUrl);
    Task StoreTokenAsync(string code);

    // Branches
    Task CreateBranchAsync(string repo, string branchName, string baseBranch);

    // PRs
    Task<PullRequestInfo> CreatePullRequestAsync(string repo, CreatePrRequest request);
    Task<PullRequestInfo> GetPullRequestAsync(string repo, int prNumber);
    Task AddReviewCommentAsync(string repo, int prNumber, string body, string? path, int? line);
    Task ApprovePullRequestAsync(string repo, int prNumber, string body);
    Task RequestChangesAsync(string repo, int prNumber, string body);
    Task MergePullRequestAsync(string repo, int prNumber, MergeMethod method);

    // Git operations (on local workspace)
    Task<string> CreateAgentBranchAsync(string workspacePath, AgentGitIdentity identity, string taskSlug);
    Task CommitAsync(string workspacePath, AgentGitIdentity identity, string message);
    Task PushAsync(string workspacePath, string branchName);
}
```

### Git Operations Service: `GitOperationsService`

For local git operations (branch, commit, push) on the loaded project:

```csharp
public interface IGitOperationsService
{
    Task<string> CreateBranchAsync(string repoPath, string branchName, string? baseBranch = null);
    Task CommitAsync(string repoPath, string authorName, string authorEmail, string message);
    Task PushAsync(string repoPath, string branchName);
    Task<string> GetCurrentBranchAsync(string repoPath);
    Task<string> GetRemoteUrlAsync(string repoPath);
    Task<(string owner, string repo)> ParseRemoteAsync(string repoPath);
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
3. Socrates fetches PR diff
4. Socrates performs review
5. Socrates posts review in room + on GitHub PR
6. Task status updated based on outcome

### Agent Communication

Agents MUST communicate progress while working. This is already supported by the orchestrator:
- `AgentThinking` / `AgentFinished` activity events
- System status messages via `WorkspaceRuntime.PostSystemStatusAsync()`
- Direct room messages via `WorkspaceRuntime.PostMessageAsync()`

The orchestrator should ensure agents post at minimum:
- "Starting work on {task title}" when claimed
- Progress updates during implementation
- "Opening PR #{number}" when PR is created
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

### Phase 4: Git Operations Service
- `IGitOperationsService` implementation
- Agent git identity configuration in `agents.json`
- Branch creation, commit attribution, push
- **Estimated scope**: 1 session

### Phase 5: GitHub Integration
- GitHub OAuth flow
- `IGitHubService` implementation
- PR creation, review comments, approval, merge
- **Estimated scope**: 1-2 sessions

### Phase 6: Review Pipeline (Socrates)
- Review trigger on `InReview` status
- Multi-model adversarial review
- PR comment posting
- Fix/re-review cycle
- Auto-merge on approval
- **Estimated scope**: 1-2 sessions

---

## Invariants

1. A task's `AssignedAgentName` must correspond to a configured agent in `agents.json`
2. All commits on an agent branch must be authored by that agent's git identity
3. A task in `InReview` must have a non-null `PullRequestNumber`
4. Socrates is the only agent that can approve PRs (unless the review pipeline is explicitly overridden)
5. A task cannot transition to `Completed` without `PullRequestStatus == Merged` (for tasks that produce code)
6. Named agents are responsible for fleet output — fleet models are recorded but the agent is the author

## Known Gaps

- GitHub App vs OAuth App decision (App provides better permissions model but more setup)
- How to handle repos without GitHub remotes (local-only projects)
- Agent identity for non-GitHub Git hosts (GitLab, Bitbucket)
- Rate limiting on GitHub API calls
- Conflict resolution when multiple agents push to related branches
- Security: token storage and rotation

## Revision History

| Date | Change | Author |
|------|--------|--------|
| 2026-03-28 | Initial spec — Planned | Copilot (via Anvil) |
