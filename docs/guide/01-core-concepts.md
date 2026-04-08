# Core Concepts

This guide explains the mental model you need to work effectively with Agent Academy. If you're new to the platform, read this section first — understanding these concepts makes everything else click into place.

Agent Academy is a **room-based multi-agent collaboration platform** for building software products. You provide a brief or goal, and a team of AI agents (powered by GitHub Copilot SDK / LLMs) collaborates through structured phases to implement it. The system runs as an ASP.NET Core 8 API backend with a React 19 SPA frontend, using SignalR for real-time updates and SQLite for persistence.

---

## 1. Workspaces

A **workspace** is the top-level container for all work on a single project. Think of it as Agent Academy's representation of your repository.

### What a Workspace Contains

- **A directory on the host filesystem** — the actual code repository
- **Rooms** where agents collaborate
- **Agents** assigned to the workspace
- **Tasks** being planned and implemented
- **Plans** and architectural decisions
- **Sessions** (conversation epochs) with message history
- **Memories** accumulated by agents over time

### Creating a Workspace

You create a workspace by either:

1. **Scanning an existing repository** — Point Agent Academy at a directory with a git repo. The system runs an "onboarding" process to index the codebase.
2. **Pointing at an empty directory** — Start a greenfield project. The system initializes git and creates the workspace structure.

During workspace creation, the system:
- Creates a **default room** for initial collaboration
- Indexes the codebase (if it exists) to enable semantic search
- Initializes the agent roster
- Sets up the task management database

### Active Workspace Constraint

**Only one workspace can be active at a time.** This is a hard constraint in the current architecture.

When you switch workspaces:
- All active sessions in the current workspace are **archived**
- Agents in breakout rooms are returned to the main room
- The new workspace's state is loaded
- A fresh session starts in the new workspace's default room

> ⚠️ **GAP**: There's no multi-workspace dashboard or quick switcher. Switching workspaces requires navigating to Settings > Workspace and selecting from a dropdown. If you work on multiple projects, you'll context-switch frequently with no visual indication of which workspace is active in the UI header.

### Filesystem Coupling

The workspace directory is the **source of truth**. Agent Academy reads and writes files directly to this location. If you modify files outside of Agent Academy (e.g., in your IDE), the agents will see those changes the next time they read the file.

Conversely, when agents write files via commands like `COMMIT_CHANGES`, those changes appear immediately in the workspace directory. You can verify this with `git status` or by opening files in your editor.

> ⚠️ **GAP**: There's no file watching or automatic refresh. If you edit a file externally while an agent is working on it in a breakout room, the agent won't know until it re-reads the file. This can lead to merge conflicts or overwritten changes.

---

## 2. Rooms

A **room** is the primary collaboration unit — like a Slack channel where agents discuss and coordinate work.

### Default Room

When you create a workspace, the system creates a **default room** automatically. This is where initial planning happens. The default room is named after the workspace and cannot be deleted.

You can create additional rooms for parallel workstreams, but the default room is always the entry point.

> ⚠️ **GAP**: There's no UI to create additional rooms beyond the default. All collaboration currently happens in one main room per workspace. If you have multiple unrelated features in flight, they share the same conversation stream.

### Room Phases

Rooms operate in a **phase-based workflow**. The current phase gates what kind of work can happen:

| Phase | Purpose | What Happens Here |
|-------|---------|-------------------|
| **Idle** | No active work | Human can chat with agents, review history, configure settings. Agents respond to questions but don't initiate work. |
| **Planning** | Requirements & design | Aristotle (the planner) leads discussions. Agents collaborate on requirements, architecture, and task breakdown. Human provides the brief here. |
| **Implementing** | Building features | Tasks are created, breakout rooms open, agents work on `task/{slug}` branches. Each task is isolated. |
| **Reviewing** | QA & approval | Socrates (the reviewer) evaluates completed work. Approve/RequestChanges/Reject cycle. Human can also approve/reject. |
| **Committing** | Merging & cleanup | Approved tasks are squash-merged to `develop`. PRs can be created to GitHub. Task branches are deleted. |

### Phase Transitions

Phase transitions are **manual** — you trigger them from the UI toolbar. The orchestrator (see §10) adjusts its behavior based on the current phase.

**Typical workflow:**

1. Start in **Idle**
2. Transition to **Planning** → provide your brief, agents discuss, planner creates tasks
3. Transition to **Implementing** → agents work on tasks in breakout rooms
4. Transition to **Reviewing** → reviewer checks completed work
5. Transition to **Committing** → approved tasks merge to `develop`
6. Return to **Idle** or start a new cycle

> ⚠️ **GAP**: There's no phase prerequisite enforcement. You can transition to Implementing before any tasks are created, or to Reviewing before any tasks are completed. The system won't stop you, but the orchestrator will have nothing to do.

> ⚠️ **GAP**: There's no guided workflow wizard or phase transition checklist. New users often don't know when to transition phases or what should happen in each phase. The UI shows the current phase but doesn't explain what actions are appropriate.

### Agent Roster

Each room has an **agent roster** — the list of agents "in" the room. Only rostered agents can participate in room conversations.

When you create a workspace, the default room roster includes all configured agents from `agents.json`. You can adjust the roster in Settings > Agents.

> ⚠️ **GAP**: There's no per-room roster configuration. All rooms share the same agent roster. If you want different agent combinations for different workstreams, you can't achieve that today.

---

## 3. Breakout Rooms

A **breakout room** is a temporary sub-room created for task implementation. It's where focused, isolated work happens.

### Why Breakout Rooms Exist

In the main room, all agents see all messages. During implementation, you want agents to work independently without cross-talk. Breakout rooms provide **isolation**:

- One agent works on one task in one breakout room
- The breakout has its own message stream, separate from the main room
- Other agents don't see breakout messages (they're not distracted)
- The breakout is tied to a git branch (`task/{slug}`)

### Lifecycle

**Creation:** When a task is assigned to an agent (either automatically or manually), the system:
1. Creates a `task/{slug}` branch from `develop`
2. Opens a breakout room for that task
3. Moves the agent to the breakout (changes `AgentLocation` from `MainRoom` to `Breakout`)
4. Starts the agent's first turn in the breakout context

**During work:** The agent has full access to file commands, shell, code search, etc. The agent commits incrementally to the task branch. The main room continues independently — other agents are working on other tasks.

**Closure:** The breakout closes when:
- The task is completed (agent executes `COMPLETE_TASK`)
- The task is cancelled by the human
- The system crashes and runs recovery (all breakouts close)
- The task is explicitly rejected and reverted

When the breakout closes, the agent is moved back to the main room. The breakout's message history is preserved in the database.

### Breakout Message Visibility

Breakout messages are **hidden from the main room** by default. You can view them in the UI by:
- Opening the Tasks panel
- Clicking on the task
- Viewing the "Conversation" tab in the task detail drawer

This keeps the main room focused on coordination while allowing you to drill into task-level details when needed.

> ⚠️ **GAP**: There's no way to "pop out" a breakout room into a separate window or side panel. If you want to monitor a task in progress while also watching the main room, you have to toggle back and forth in the UI.

### Breakout Stuck Detection

The orchestrator monitors breakout rooms for **stuck agents** — if an agent spins in a loop without progress (e.g., repeatedly failing to fix a test), the system will eventually time out and mark the task as `Failed`.

> ⚠️ **GAP**: The stuck detection logic is rudimentary (message count threshold). It can't distinguish between "agent is iterating productively" and "agent is spinning uselessly." You may need to manually cancel tasks that are stuck.

---

## 4. Agents

**Agents** are the AI personas that collaborate in rooms. Each agent has a specialized role, configured in the `agents.json` catalog.

### Agent Configuration

Each agent has:

- **Name** — e.g., "Aristotle"
- **Role** — e.g., "Planner & Architect"
- **System prompt** — instructions defining the agent's personality, expertise, and behavior
- **Model** — e.g., `claude-sonnet-4`, `gpt-4o`
- **Git identity** — author name and email for commits
- **Tool permissions** — which commands the agent is allowed to execute (role-based)
- **Startup prompt** — injected into the first turn when the agent joins a room

### Default Agent Roster

The default configuration includes:

| Agent | Role | Primary Responsibilities |
|-------|------|--------------------------|
| **Aristotle** | Planner | Leads planning phase, coordinates the team, creates and assigns tasks |
| **Archimedes** | Architect | Designs system architecture, makes structural and technical decisions |
| **Hephaestus** | Backend Engineer | Implements backend tasks, writes server-side code, commits changes |
| **Athena** | Frontend Engineer | Implements frontend tasks, writes client-side code, commits changes |
| **Socrates** | Reviewer | Reviews completed work, provides feedback, approves or requests changes |
| **Thucydides** | Technical Writer & Spec Manager | Maintains specs, writes documentation, ensures traceability |

> ⚠️ **GAP**: There's no UI to add, remove, or reorder agents in the roster. You must edit `agents.json` manually and restart the server. This is risky if you make a syntax error.

### Agent State

Agents transition through these states:

- **Idle** — waiting in the main room, not actively working
- **Thinking** — LLM is generating a response
- **Speaking** — message is being streamed to the UI
- **Working** — agent is in a breakout room, implementing a task

The UI shows agent state in the Agents panel (typically a colored indicator or badge).

### Agent Customization

You can override agent configuration **per workspace** via Settings > Agents. This allows you to:

- Change the model (e.g., use GPT-5 instead of Claude for Archimedes)
- Adjust system instructions (e.g., add domain-specific knowledge)
- Modify templates (e.g., change the commit message format)

These overrides are stored in the workspace database and don't affect other workspaces.

> ⚠️ **GAP**: There's no version control for agent configuration changes. If you modify an agent's prompt and the agent starts behaving poorly, there's no "undo" or history of what changed. You have to manually revert your edits.

### Git Identity

Each agent has a distinct git identity. When an agent commits to a task branch, the commit author is the agent's configured name and email. This makes it easy to see which agent wrote which code in `git log`.

Example:
```
commit abc123
Author: Archimedes <archimedes@agent-academy.local>
Date:   Mon Jan 6 14:23:15 2025 -0800

    Implement user authentication
```

> ⚠️ **GAP**: There's no way to configure per-task or per-commit co-authorship. If a human guides an agent through a fix, the commit is still attributed solely to the agent. You'd need to manually add `Co-authored-by` trailers to give credit.

---

## 5. Sessions (Conversation Epochs)

A **session** is a bounded epoch of conversation within a room. It's Agent Academy's mechanism for managing unbounded context growth.

### Why Sessions Exist

LLM context windows are finite. If a room conversation grows indefinitely, eventually:
- The context window fills up
- Token costs skyrocket
- The LLM's attention degrades (older messages are forgotten)

Sessions solve this by **compacting** the conversation history periodically.

### Session Rotation

Each room tracks a sequence of sessions: Session 1, Session 2, Session 3, etc.

When the current session hits a **message count threshold** (configurable in Settings > Advanced), the system performs **session rotation**:

1. The current session is **summarized** — key decisions, unresolved questions, and context are extracted
2. The session is **archived** — messages are marked as `archived` in the database
3. A **new session** starts with a fresh message list
4. The summary is injected into the new session's context

This gives agents a "clean slate" while preserving continuity via the summary.

### Context Invalidation

Session rotation **invalidates the LLM's cached context**. The next time an agent speaks, it receives:
- The session summary (condensed history)
- Recent messages from the new session
- System prompt + workspace context
- Relevant memories (see §9)

The agent does **not** receive the full archived message history. This keeps token usage bounded.

### Manual Rotation

> ⚠️ **GAP**: Session rotation is automatic only. There's no "Force Rotate Now" button. If you notice the conversation is getting bloated or off-track, you can't manually trigger compaction. You have to wait for the threshold to be reached or artificially inflate the message count.

### Session Visibility

You can view archived sessions in **Dashboard > Conversation Sessions**. Each session shows:
- Session number
- Start and end timestamps
- Message count
- Summary (if generated)

Clicking on a session expands the full message history.

> ⚠️ **GAP**: There's no export or download feature for sessions. If you want to extract the full conversation for documentation or postmortem, you have to manually copy-paste from the UI or query the SQLite database directly.

### Configuration

The session rotation threshold is configured in **Settings > Advanced > Epoch Size**. The default is typically 50-100 messages.

Lower thresholds = more frequent rotation = lower token costs but more aggressive compression.  
Higher thresholds = longer context = better continuity but higher costs.

> ⚠️ **GAP**: There's no guidance on what threshold to choose for different project types. A greenfield React app might need different settings than a legacy Java monolith with complex architecture. You're left to experiment.

---

## 6. Phases & the Build Cycle

The **phase system** is the backbone of Agent Academy's workflow. Phases gate what kind of work happens in a room and guide the orchestrator's behavior.

### Phase Descriptions

#### Idle

**Purpose:** Downtime between build cycles. No active feature work.

**What you can do:**
- Chat with agents (ask questions, request explanations)
- Review conversation history
- Configure workspace settings
- Browse tasks and memories
- Read specs

**What agents do:**
- Respond to direct questions
- Don't initiate new work or create tasks
- May update memories based on conversations

**When to use:** Between build cycles, during onboarding, when debugging configuration issues.

---

#### Planning

**Purpose:** Define what to build and how to build it.

**What happens:**
- You provide the **brief** — a description of the feature, bug, or improvement
- Aristotle (the planner) leads the discussion
- Agents ask clarifying questions
- Agents propose architecture and implementation approach
- Aristotle creates **tasks** based on the plan
- Thucydides may update specs to reflect the planned work

**How the orchestrator behaves:**
- Planner speaks first in each round
- Tagged agents (via `@AgentName`) or idle agents respond
- Maximum 3 rounds per planning trigger (prevents runaway discussions)
- Commands like `CREATE_TASK` are authorized for the planner

**When to transition to next phase:**  
When tasks are created and the plan feels complete. Check the Tasks panel — if you have a reasonable breakdown, move to Implementing.

> ⚠️ **GAP**: There's no signal that planning is "done." You have to manually judge whether the task breakdown is sufficient. The system won't warn you if critical requirements are missing or if the plan is ambiguous.

---

#### Implementing

**Purpose:** Build the features defined in the plan.

**What happens:**
- Tasks are assigned to agents (automatically or manually)
- Each task gets a **breakout room** and a **`task/{slug}` git branch**
- Agents work in isolation, committing incrementally
- Agents execute `COMPLETE_TASK` when done

**How the orchestrator behaves:**
- Manages the **breakout loop** — cycles through active breakouts, giving each agent a turn
- Detects stuck agents (too many messages without progress)
- Does **not** run planning-phase logic (no multi-round discussions)

**Git behavior:**
- Each task branch is created from `develop`
- Agents commit directly to the task branch
- No automatic merging during this phase

**When to transition to next phase:**  
When all (or most) tasks are completed. Check the Tasks panel — if tasks are in `InReview` status, move to Reviewing.

> ⚠️ **GAP**: There's no task prioritization or dependency management. If Task B depends on Task A, the system won't enforce the order. Agents may work on B before A is merged, leading to conflicts or wasted work.

---

#### Reviewing

**Purpose:** Quality assurance and approval.

**What happens:**
- Socrates (the reviewer) evaluates completed tasks
- The reviewer examines code changes, runs tests, checks against requirements
- The reviewer can:
  - **Approve** — task proceeds to Committing phase
  - **Request Changes** — task reopens, agent makes fixes
  - **Reject** — task is cancelled, branch may be deleted

**How the orchestrator behaves:**
- Runs the reviewer on each task in `InReview` status
- Serializes reviews (one at a time to avoid overwhelming the reviewer)
- Humans can also approve/reject from the Tasks panel

**When to transition to next phase:**  
When all tasks are approved. If tasks are rejected, you may need to loop back to Implementing or Planning.

> ⚠️ **GAP**: There's no bulk approve or conditional approval. If you have 10 tasks and you've manually verified them outside Agent Academy, you still have to click "Approve" 10 times in the UI. No automation for "approve all passing tests" or similar rules.

---

#### Committing

**Purpose:** Integrate approved work into the main branch.

**What happens:**
- Approved tasks are **squash-merged** to `develop`
- Task branches are deleted
- Optionally, PRs are created to GitHub (if GitHub integration is configured)
- Optionally, PRs are auto-merged (if configured)

**How the orchestrator behaves:**
- Executes merges sequentially to avoid conflicts
- Creates PR descriptions from task metadata
- Cleans up breakout rooms

**When to transition to next phase:**  
After all tasks are merged. Typically, you return to **Idle** or start a new Planning cycle.

> ⚠️ **GAP**: There's no rollback or undo for merges. If a task is merged and you realize it broke something, you have to manually revert the commit in git. The system doesn't track merge history or provide a "Revert Task X" button.

> ⚠️ **GAP**: There's no automatic PR creation for the entire build cycle. If you want a single PR that encompasses all tasks from this cycle, you have to create it manually via git/GitHub. The system only creates per-task PRs.

---

### Phase Transition Mechanics

**How to transition:**  
Click the phase dropdown in the UI toolbar and select the next phase. The server updates the room's `CurrentPhase` property and broadcasts the change via SignalR.

**What the orchestrator does:**  
On the next orchestrator tick (typically every few seconds), it reads the new phase and adjusts its behavior accordingly.

**No phase validation:**  
The system doesn't enforce a linear phase progression. You can jump from Planning directly to Committing if you want (though this would be nonsensical). The orchestrator will execute whatever logic is appropriate for the current phase, regardless of whether it makes sense.

> ⚠️ **GAP**: There's no phase transition log or audit trail. If you're troubleshooting why a build cycle went wrong, you can't see who transitioned phases when, or whether phases were skipped.

---

## 7. Tasks

A **task** is the unit of work in Agent Academy. Tasks are created during the Planning phase and drive the Implementation phase.

### Task Creation

**Who can create tasks:**  
Only Aristotle (the planner) can create tasks via the `CREATE_TASK` command. This is enforced by role-based permissions.

**Exception:** Any agent can create a **bug report** task if they encounter an issue during implementation.

**What a task contains:**
- **Title** — concise summary (e.g., "Implement user login API")
- **Description** — detailed requirements, acceptance criteria
- **Items** — subtasks or checklist items (e.g., "Create POST /login endpoint", "Add JWT generation")
- **Spec links** — references to spec sections (if Thucydides is managing specs)
- **Assigned agent** — which agent will implement this

**Task slug:**  
Each task gets a URL-friendly slug derived from the title. This slug is used for:
- Git branch name: `task/{slug}`
- Breakout room identifier
- File paths in logs

### Task Lifecycle

Tasks progress through these states:

```
Pending → InProgress → InReview → Approved → Merged
                                 ↓
                              Rejected → Cancelled
```

| State | Meaning |
|-------|---------|
| **Pending** | Task created but not started. Waiting for agent assignment. |
| **InProgress** | Breakout room is open, agent is working on the task branch. |
| **InReview** | Agent completed work, task is awaiting reviewer or human approval. |
| **Approved** | Reviewer or human approved the work. Ready to merge. |
| **Rejected** | Reviewer or human rejected the work. May reopen or cancel. |
| **Merged** | Task branch squash-merged to `develop`. Task is complete. |
| **Cancelled** | Task aborted (manually or due to failure). Branch may be deleted. |

### Git Branch Coupling

Each task is tightly coupled to a git branch:

- Branch name: `task/{slug}` (e.g., `task/implement-user-login-api`)
- Created from: `develop` branch
- Merge target: `develop` branch
- Merge strategy: **squash merge** (all commits collapsed into one)

**Implications:**
- If you manually delete the task branch in git, the task becomes orphaned
- If you manually merge the branch outside Agent Academy, the task's state won't update
- If you manually commit to the task branch, the agent will see those commits

> ⚠️ **GAP**: There's no sync mechanism to detect manual git operations. If you interact with task branches outside Agent Academy, the UI state may become stale or incorrect.

### Task Items (Subtasks)

Tasks can have **items** — checklist-style subtasks. These are purely organizational (no automation).

Example:
```
Task: Implement user login API
Items:
  - [ ] Create POST /login endpoint
  - [ ] Validate credentials against database
  - [ ] Generate JWT token
  - [ ] Return token in response
```

Agents may reference items in their messages, but the system doesn't track item completion separately.

> ⚠️ **GAP**: There's no UI to check off items or track subtask progress. Items are displayed as static text. If you want to track progress, you have to read the agent's messages manually.

### Task Comments

You and agents can add **comments** to tasks. Comments are threaded under the task in the Tasks panel.

Use cases:
- Agent asks a clarifying question
- Human provides feedback during review
- Agent posts evidence of test success

Comments are **not** part of the breakout room conversation. They're metadata attached to the task entity.

> ⚠️ **GAP**: There's no notifications for new task comments. If an agent posts a question while you're looking at a different task, you won't know unless you happen to check that task's detail panel.

### Task Evidence

Agents can attach **evidence** to tasks — screenshots, log snippets, test output, etc. This is typically used during review to prove that the work meets acceptance criteria.

Evidence is stored as JSON blobs in the task's `Evidence` field.

> ⚠️ **GAP**: There's no structured evidence viewer. Evidence is displayed as raw JSON in the UI. If an agent attaches a base64-encoded screenshot, you have to manually decode it to view it.

### Task Assignment

**Automatic assignment:**  
When Aristotle creates a task, it can specify an agent in the task payload. The orchestrator automatically assigns the task to that agent and opens the breakout room.

**Manual assignment:**  
You can reassign a task from the Tasks panel. Click the task, then select a different agent from the dropdown. This closes the old breakout (if open) and opens a new one.

> ⚠️ **GAP**: There's no workload balancing. If Aristotle assigns 10 tasks to Hephaestus and 0 to Athena, the system won't rebalance. You have to manually redistribute if you notice an imbalance.

### Task Rejection & Revert

If a task is rejected after being merged, the system can **revert the merge**:

1. Human clicks "Reject" on a merged task
2. The system creates a revert commit on `develop`
3. The task's state changes to `Rejected`
4. Optionally, the task can be reopened for rework

> ⚠️ **GAP**: Revert logic is brittle. If the revert causes merge conflicts (because other tasks have since modified the same files), the revert fails and the task remains merged. You have to manually fix this in git.

---

## 8. Commands

**Commands** are structured actions that agents (and humans) execute to interact with the system, the codebase, and external services.

### Command Format

Commands are embedded in agent messages using this syntax:

```
COMMAND_NAME: { json_payload }
```

Example from an agent message:

```
I'll start by reading the authentication module.

READ_FILE: { "path": "src/auth/login.ts" }
```

The command parser extracts `READ_FILE` and the JSON payload, then executes the command.

### Command Pipeline

Every command goes through this pipeline:

1. **Parse** — extract command name and payload from message
2. **Authorize** — check if the agent has permission to execute this command (role-based)
3. **Rate Limit** — enforce per-agent and per-command rate limits (prevents runaway loops)
4. **Execute** — run the command handler (e.g., read file, run shell, commit changes)
5. **Audit** — log the command execution with full payload and result

If authorization fails, the command is rejected and the agent receives an error message.

### Role-Based Permissions

Each agent has a set of allowed commands based on their role. Examples:

| Agent | Can Execute | Cannot Execute |
|-------|-------------|----------------|
| **Aristotle** (Planner) | CREATE_TASK, SEARCH_CODE, READ_FILE, REMEMBER | COMMIT_CHANGES, MERGE_PR |
| **Archimedes** (Engineer) | READ_FILE, WRITE_FILE, SHELL, COMMIT_CHANGES, COMPLETE_TASK | CREATE_TASK, APPROVE_TASK |
| **Socrates** (Reviewer) | READ_FILE, SEARCH_CODE, APPROVE_TASK, REQUEST_CHANGES | WRITE_FILE, COMMIT_CHANGES |

Permissions are defined in `agents.json` under each agent's `permissions` array.

> ⚠️ **GAP**: There's no UI to view or edit command permissions. You have to manually edit `agents.json`. If you want to grant Archimedes the ability to create tasks (e.g., for bug reports), you have to stop the server, edit the file, and restart.

### Common Commands

#### File Operations
- `READ_FILE` — read file contents
- `WRITE_FILE` — write or overwrite file
- `APPEND_FILE` — append to file
- `DELETE_FILE` — delete file
- `LIST_DIRECTORY` — list files in directory

#### Code Navigation
- `SEARCH_CODE` — semantic code search (uses indexed codebase)
- `FIND_DEFINITION` — find symbol definition
- `FIND_REFERENCES` — find all references to a symbol

#### Shell Execution
- `SHELL` — run a shell command (e.g., `npm test`, `git status`)
  - Agents cannot run interactive commands (no stdin)
  - Output is captured and returned to the agent
  - Long-running commands time out after 60 seconds

#### Task Management
- `CREATE_TASK` — create a new task (planner only)
- `COMPLETE_TASK` — mark current task complete (engineer in breakout)
- `ASSIGN_TASK` — assign task to agent
- `COMMENT_ON_TASK` — add comment to task

#### Review
- `APPROVE_TASK` — approve task (reviewer only)
- `REQUEST_CHANGES` — request changes to task (reviewer only)
- `REJECT_TASK` — reject task (reviewer only)

#### Git & GitHub
- `COMMIT_CHANGES` — commit staged changes to current branch
- `CREATE_PR` — create GitHub pull request
- `MERGE_PR` — merge GitHub pull request
- `PUSH_BRANCH` — push current branch to remote

#### Memory
- `REMEMBER` — store a memory (decision, lesson, pattern, etc.)
- `RECALL` — search memories (FTS5 full-text search)
- `FORGET` — delete a memory

### Human-Initiated Commands

You can also execute commands from the **Commands** tab in the UI. This is useful for:
- Manually triggering a file read to inspect something
- Running a shell command to check test status
- Forcing a memory recall to see what agents have learned

Human-initiated commands bypass role permissions (you have full access).

> ⚠️ **GAP**: The Commands tab has no autocomplete or schema validation. You have to manually type JSON payloads. If you mistype a parameter, the command fails with a generic error message.

### Rate Limiting

Commands are rate-limited to prevent abuse:

- **Per-agent limits** — e.g., max 10 `SHELL` commands per minute
- **Per-command limits** — e.g., max 5 `CREATE_PR` commands globally per hour

If an agent hits a rate limit, the command is rejected and the agent receives a "rate limit exceeded" error.

> ⚠️ **GAP**: Rate limit thresholds are hardcoded. There's no UI to adjust them. If you're working on a large codebase and an agent legitimately needs to read 100 files, it may hit the limit and get stuck.

### Command Audit Trail

All command executions are logged to the `CommandAudits` table with:
- Timestamp
- Agent name
- Command name
- Payload (full JSON)
- Result (success/failure)
- Error message (if failed)

You can query this table directly in SQLite to debug issues or analyze agent behavior.

> ⚠️ **GAP**: There's no UI to view the audit trail. You have to use a SQLite client or write custom queries. A "Command History" panel would make debugging much easier.

---

## 9. Agent Memory

**Agent memory** is a persistent knowledge store that survives session rotation. It's how agents accumulate learnings over time.

### Why Memory Exists

Without memory, every session rotation would wipe the agents' context. They'd forget:
- Decisions made in previous sessions
- Lessons learned from past mistakes
- Patterns discovered in the codebase
- Architectural constraints

Memory allows agents to "remember" key facts across sessions, even after the message history is compacted.

### Memory Categories

Memories are categorized by type:

| Category | Purpose | Example |
|----------|---------|---------|
| **decision** | Architectural or design decisions | "We chose PostgreSQL over MySQL because the client requires JSONB support." |
| **lesson** | Learnings from failures or mistakes | "Always run `npm install` after modifying package.json — skipping it causes build failures." |
| **pattern** | Code patterns or idioms in the codebase | "All API routes use Zod for validation. Import from `src/validation/schemas.ts`." |
| **fact** | Factual knowledge about the project | "The CI pipeline runs on GitHub Actions, config in `.github/workflows/ci.yml`." |
| **constraint** | Hard constraints or limitations | "The database is SQLite — no concurrent writes, no stored procedures." |

### Storing Memories

Agents use the `REMEMBER` command:

```
REMEMBER: {
  "category": "lesson",
  "content": "When running tests, always use `npm test -- --verbose` to see full error output. The default mode hides stack traces.",
  "tags": ["testing", "npm", "debugging"]
}
```

The memory is stored in the `Memories` table with:
- Unique ID
- Agent name (who created it)
- Category
- Content
- Tags (for search)
- Timestamp
- Access count (how many times it's been recalled)
- Last accessed timestamp

### Retrieving Memories

Agents use the `RECALL` command to search memories:

```
RECALL: {
  "query": "testing errors verbose output",
  "limit": 5
}
```

The system uses **FTS5 full-text search** on the content and tags fields. Matching memories are returned ranked by relevance.

### Shared Memories

Memories are **shared across all agents**. When Archimedes stores a lesson, Socrates can recall it.

This creates a **collective knowledge base** that improves over time.

> ⚠️ **GAP**: There's no memory scoping or privacy. If you want agent-specific memories (e.g., Socrates' review checklist that other agents shouldn't see), you can't enforce that. All memories are global.

### Memory Injection

When the orchestrator builds an agent's prompt, it **automatically injects relevant memories**:

1. Extract key terms from the current conversation context
2. Search memories using those terms
3. Include top N matching memories in the system prompt

This happens transparently — agents don't have to explicitly `RECALL` to benefit from past learnings.

> ⚠️ **GAP**: The injection logic is opaque. You can't see which memories were injected into an agent's prompt. If an agent is behaving oddly because of a stale memory, it's hard to diagnose.

### Memory Staleness

Memories have an optional **TTL (time-to-live)**. By default, memories older than 30 days without access are considered "stale."

Stale memories are deprioritized in search results but not deleted.

> ⚠️ **GAP**: There's no UI to browse, edit, or delete memories. If an agent stores an incorrect memory (e.g., "We use MySQL" when you actually use PostgreSQL), you have to manually delete it from the SQLite database or ask an agent to `FORGET` it by ID (which requires you to know the ID).

### Forgetting Memories

Agents can delete memories with the `FORGET` command:

```
FORGET: { "id": "mem_abc123" }
```

This is rarely used in practice because agents don't proactively clean up stale memories.

> ⚠️ **GAP**: There's no bulk forget or memory management UI. If your workspace accumulates hundreds of memories over months, there's no way to prune outdated ones without manual database surgery.

---

## 10. The Orchestrator

The **orchestrator** is the engine that drives agent turn-taking and coordinates work across phases.

### What the Orchestrator Does

- **Serializes agent turns** — ensures only one agent speaks at a time (per room)
- **Manages the turn queue** — decides which agent speaks next
- **Enforces phase-specific behavior** — different logic for Planning vs. Implementing vs. Reviewing
- **Detects stuck agents** — identifies loops or failures and intervenes
- **Injects context** — builds agent prompts with spec context, memories, and DMs

The orchestrator runs as a background service, ticking every few seconds to process the next turn.

### Turn Queue

Each room has a **turn queue** — a list of agents waiting to speak.

**How agents enter the queue:**
- **Manual trigger** — you click "Run Orchestrator" in the UI
- **Agent tagging** — an agent's message includes `@AgentName`, tagging another agent
- **Phase transition** — entering Planning or Reviewing auto-enqueues relevant agents
- **Task assignment** — assigning a task enqueues the assigned agent in the breakout room

**Queue processing:**
- The orchestrator pops the next agent from the queue
- Builds the agent's prompt (system prompt + conversation history + context)
- Calls the LLM API to generate a response
- Parses and executes any commands in the response
- Streams the response to the UI via SignalR
- Moves to the next agent in the queue

### Phase-Specific Behavior

#### Planning Phase

- **Planner speaks first** — Aristotle is always enqueued first
- **Tagged agents respond** — if Aristotle's message includes `@Archimedes`, Archimedes is enqueued next
- **Max 3 rounds** — after 3 back-and-forth exchanges, the orchestrator stops to prevent runaway discussions
- **Idle agents can contribute** — if no one is tagged, idle agents may volunteer

#### Implementing Phase

- **Breakout loop** — the orchestrator cycles through all active breakout rooms
- **One turn per breakout** — each agent in a breakout gets one turn per loop iteration
- **Stuck detection** — if an agent posts >10 messages in a breakout without completing the task, the task is marked as `Failed`
- **No main room turns** — the orchestrator ignores the main room during Implementation

#### Reviewing Phase

- **Reviewer is enqueued** — Socrates is automatically enqueued for each task in `InReview` status
- **One task at a time** — reviews are serialized (no parallelism)
- **Human can approve/reject** — you can bypass the reviewer by manually approving from the Tasks panel

#### Committing Phase

- **Merge loop** — the orchestrator merges approved tasks sequentially
- **PR creation** — if GitHub integration is configured, PRs are created automatically
- **Cleanup** — task branches are deleted, breakout rooms are closed

### DM Injection

If you send a **DM (direct message)** to an agent via the UI, the orchestrator injects that DM into the agent's next turn prompt.

Example:
```
[DM from Human]: The API key should be read from environment variable OPENAI_API_KEY, not hardcoded.
```

The agent sees this as part of its system message and responds accordingly.

> ⚠️ **GAP**: There's no DM history or threading. If you send multiple DMs to an agent, only the most recent one is injected. Older DMs are lost unless the agent happened to respond to them.

### Spec Context Injection

If a `specs/` directory exists in the workspace, the orchestrator automatically reads spec files and injects relevant sections into agent prompts.

**How it works:**
- On startup, the orchestrator indexes all `.md` files in `specs/`
- When building an agent's prompt, it searches specs for keywords from the current conversation
- Top N matching spec sections are included in the prompt

This gives agents access to requirements, API contracts, and architectural docs without explicitly reading files.

> ⚠️ **GAP**: Spec injection is all-or-nothing. If a spec file is huge (e.g., 10,000 lines), the entire matching section is injected, potentially overwhelming the context window. There's no chunking or truncation.

### Stuck Detection Logic

The orchestrator monitors breakout rooms for **stuck agents**:

- Count messages in the breakout
- If message count > threshold (default: 10) and task is still `InProgress`, mark as `Failed`
- Close the breakout, move agent back to main room

> ⚠️ **GAP**: Stuck detection is primitive. It can't distinguish between productive iteration and useless spinning. A threshold of 10 messages might be too low for complex tasks or too high for simple ones. There's no adaptive logic.

---

## 11. Notifications

Agent Academy can send **notifications** to external services (Discord, Slack, console) when significant events occur.

### Supported Providers

- **Console** — logs to stdout (always enabled)
- **Discord** — sends messages to a Discord channel via webhook
- **Slack** — sends messages to a Slack channel via webhook

### Configuration

Configure notifications in **Settings > Notifications**:

1. Select a provider (Discord or Slack)
2. Enter the webhook URL
3. Optionally configure per-agent avatars (Discord only)
4. Click "Test" to send a test message
5. Click "Save" to enable

### What Gets Notified

- **Agent messages** — when an agent posts a message in a room
- **Task events** — task created, completed, approved, rejected, merged
- **Phase transitions** — when the room phase changes
- **Errors** — when commands fail or agents crash

### Discord Integration

Discord notifications use **webhooks** with per-agent customization:

- Each agent can have a custom avatar URL (configured in `agents.json`)
- Agent messages appear as if posted by the agent (using the webhook's username override)
- Commands and code snippets are formatted with syntax highlighting

Example Discord message:
```
[Archimedes] I've completed the login API implementation.

COMPLETE_TASK: { "taskId": "task_123" }

The endpoint is available at POST /api/auth/login.
```

> ⚠️ **GAP**: Discord integration is webhook-only. There's no bot mode with interactive buttons or slash commands. You can't click "Approve Task" from Discord — you have to use the web UI.

### Slack Integration

Slack notifications use **Block Kit** for rich formatting:

- Messages are formatted as structured blocks (header, section, code, etc.)
- Agent avatars are displayed (if configured)
- Links to the web UI are included for context

> ⚠️ **GAP**: Like Discord, Slack integration is webhook-only. No interactive components. You can't reply to an agent from Slack.

### Fan-Out Delivery

Notifications are delivered to **all configured providers** simultaneously. If you have Discord, Slack, and console enabled, every event generates 3 notifications.

> ⚠️ **GAP**: There's no per-provider filtering. You can't say "send task events to Slack but agent messages to Discord." It's all-or-nothing for each provider.

### Notification Failures

If a webhook delivery fails (e.g., network error, invalid URL), the failure is logged but doesn't block the system. Agent Academy continues running normally.

> ⚠️ **GAP**: There's no retry logic or delivery confirmation. If a notification is lost due to a transient error, it's gone forever. You won't know unless you check the logs.

---

## 12. Consultant API

The **Consultant API** is an HTTP API that allows external tools (like the Copilot CLI or a custom "Anvil" agent) to interact with Agent Academy programmatically.

### Use Case

Imagine you're a senior engineer supervising an Agent Academy build cycle. You don't want to sit in the web UI all day, but you need to:

- Monitor what agents are doing
- Intervene when an agent goes off-track
- Inject expert knowledge at critical moments
- Execute commands that agents can't (e.g., approve a task)

The Consultant API lets you do this from the command line or a script.

### Authentication

The API uses **shared-secret authentication**:

```
X-Consultant-Key: your-secret-key
```

The secret key is configured in the server's `appsettings.json`:

```json
{
  "ConsultantApi": {
    "SharedSecret": "your-secret-key"
  }
}
```

If the header is missing or incorrect, the API returns `401 Unauthorized`.

> ⚠️ **GAP**: There's no key rotation or multi-key support. If the key is compromised, you have to update `appsettings.json` and restart the server. No graceful key rollover.

### Capabilities

The Consultant API uses the **same endpoints** as the web UI, authenticated via the `X-Consultant-Key` header instead of cookies:

#### Read Rooms & Messages

```
GET /api/rooms
GET /api/rooms/{roomId}/messages?after={lastMessageId}&limit=50
```

Returns the current room list and message history (including breakout rooms). Use the `after` parameter for pagination/polling.

#### Send Messages as Human

```
POST /api/rooms/{roomId}/human
{
  "content": "Please prioritize the authentication task."
}
```

The message appears in the room as if you typed it in the web UI.

#### Send Direct Messages to Agents

```
POST /api/dm/threads/{agentId}/messages
{
  "content": "Use bcrypt for password hashing, not MD5."
}
```

The DM is injected into the agent's next turn prompt.

#### Execute Commands

```
POST /api/commands/execute
{
  "command": "APPROVE_TASK",
  "args": { "taskId": "task_123" }
}
```

Executes the command as if triggered by a human. Check command status for async commands via `GET /api/commands/{correlationId}`.

### Polling-Based

The API is **polling-based** — no webhooks or streaming.

To monitor a room in real-time, you have to poll `GET /api/rooms/{roomId}/messages?after={lastId}` periodically (e.g., every 5 seconds). Consider the round complete after 30 seconds of no new messages.

> ⚠️ **GAP**: No SSE (Server-Sent Events) or WebSocket support for the Consultant API. The web UI uses SignalR for real-time updates, but external tools are stuck polling. This is inefficient and adds latency.

### Allowed Commands

The Consultant API can execute a **subset** of commands:

- Code/workspace operations (READ_FILE, SEARCH_CODE, LIST_FILES)
- Shell execution (SHELL)
- Task management (APPROVE_TASK, REQUEST_CHANGES, MERGE_TASK)
- Memory operations (REMEMBER, RECALL, FORGET)
- Git operations (COMMIT_CHANGES, CREATE_PR, MERGE_PR)

Commands that require agent context (e.g., COMPLETE_TASK from within a breakout) are not allowed.

> ⚠️ **GAP**: There's no API documentation or OpenAPI spec. You have to read the source code to discover which commands are allowed and what payloads they expect.

---

## 13. State Recovery

Agent Academy is designed to recover from crashes and unexpected shutdowns.

### What Happens on Crash

If the server process crashes (e.g., unhandled exception, out of memory, SIGKILL):

1. **Active breakouts are closed** — agents are moved back to the main room
2. **Agent states reset to Idle** — any "Thinking" or "Working" states are cleared
3. **Abandoned tasks marked** — tasks that were `InProgress` are marked as `Abandoned`
4. **Session rotation** — the current session is summarized and archived

This prevents the system from getting stuck in an inconsistent state.

### Supervised Restart

The server can request a **supervised restart** by exiting with code **75**.

This is used when:
- Configuration changes require a restart (e.g., modifying `agents.json`)
- A recoverable error occurs (e.g., database schema migration)

A wrapper script (e.g., `systemd` service or a shell loop) watches for exit code 75 and restarts the process automatically.

### Frontend Recovery

The frontend (React SPA) detects server crashes via the **health endpoint**:

```
GET /api/health
```

If the health check fails or returns a different instance ID, the UI shows a **reconnect banner**:

```
[!] Server restarted. Click here to reconnect.
```

Clicking the banner reloads the page and re-establishes the SignalR connection.

> ⚠️ **GAP**: The frontend doesn't auto-reconnect. You have to manually click the banner. If you're away from your desk when the server restarts, you'll come back to a stale UI with no indication that it's disconnected.

### Auth Failures

If the SignalR connection fails due to authentication (e.g., JWT expired), the frontend triggers a **re-login flow**:

1. Redirect to the login page
2. User re-authenticates
3. Redirect back to the workspace

This **does not** trigger a server restart — it's purely a client-side re-auth.

> ⚠️ **GAP**: There's no seamless token refresh. If your session expires while you're in the middle of a build cycle, you have to log in again and lose your scroll position / UI state.

### Database Recovery

The SQLite database is the source of truth for all state. On startup, the server:

1. Opens the database connection
2. Runs any pending migrations (EF Core)
3. Loads active workspaces, rooms, agents, tasks, sessions

If the database is corrupted or missing, the server fails to start. There's no automatic repair.

> ⚠️ **GAP**: There's no database backup or snapshot feature. If the SQLite file is corrupted (e.g., due to disk failure), you lose all state. You have to manually back up the `.db` file periodically.

### Crash Logs

Crash logs are written to `logs/` in the workspace directory. Each crash generates a timestamped log file with:

- Exception stack trace
- Agent states at time of crash
- Active breakouts and tasks
- Last N messages in each room

> ⚠️ **GAP**: Crash logs are plain text files. There's no centralized logging or crash analytics. If you want to analyze crash patterns, you have to manually grep through log files.

---

## Summary & Mental Model

Here's the mental model to carry forward:

1. **Workspace** = project container (directory + git repo + database)
2. **Room** = collaboration space with phases (Idle → Planning → Implementing → Reviewing → Committing)
3. **Breakout Room** = isolated workspace for one agent on one task
4. **Agent** = specialized AI persona with role, permissions, and git identity
5. **Session** = bounded conversation epoch (compacted periodically to manage context)
6. **Task** = unit of work with lifecycle (Pending → InProgress → InReview → Approved → Merged)
7. **Command** = structured action (READ_FILE, SHELL, CREATE_TASK, etc.) with authorization and audit
8. **Memory** = persistent knowledge store (survives session rotation, shared across agents)
9. **Orchestrator** = turn-taking engine (queues agents, enforces phase behavior, injects context)
10. **Notifications** = external event delivery (Discord, Slack, console)
11. **Consultant API** = programmatic interface for external tools (polling-based, shared-secret auth)
12. **State Recovery** = crash handling (close breakouts, reset agents, summarize session)

When you understand these concepts, the UI behavior and workflow make sense. The next sections of this guide will walk you through practical workflows using this mental model.

---

## Next Steps

Now that you understand the core concepts, proceed to:

- **[02-building-a-product.md](02-building-a-product.md)** — End-to-end walkthrough from project setup to shipped feature
- **[03-operations-reference.md](03-operations-reference.md)** — Session management, monitoring, troubleshooting, configuration
- **[04-gap-analysis.md](04-gap-analysis.md)** — Known limitations and missing features
