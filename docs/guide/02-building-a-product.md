# Building a Product with Agent Academy

This guide walks you through building a complete software product using Agent Academy's multi-agent collaboration platform. You'll learn how to onboard a project, plan features with AI agents, implement code through autonomous breakout sessions, review changes, and merge to production.

## Prerequisites

Before starting, ensure you have:

- **Agent Academy server running** on `http://localhost:5066`
  - ASP.NET Core backend with GitHub Copilot SDK authentication configured
  - Server wrapper script (`run-server.sh`) managing the process
- **Frontend application** accessible at `http://localhost:5173`
- **A project to work on**: either an existing repository or a new directory
- **Git** installed and configured
- **GitHub CLI (`gh`)** installed and authenticated (for PR creation)
- **Copilot authentication token** configured in your environment

You can verify the server is running:

```bash
curl http://localhost:5066/health
# Expected: {"status":"healthy","timestamp":"2024-..."}
```

## Tutorial Overview

This tutorial demonstrates building a simple task management API from scratch. We'll:

1. **Onboard** a new project into Agent Academy
2. **Plan** the product features with AI agents
3. **Implement** features through autonomous agent breakouts
4. **Review** the code changes
5. **Commit and merge** approved work
6. **Iterate** with new features

Estimated time: 60-90 minutes for first run-through.

---

## Phase 1: Project Setup

### Starting the Application

1. Open your browser to `http://localhost:5173`
2. You should see the **ProjectSelectorPage** with three tabs:
   - **Existing**: Previously onboarded projects
   - **Onboard**: Import an existing repository
   - **Create**: Start a new project from scratch

### Option A: Onboarding an Existing Repository

If you have an existing codebase:

1. Click the **Onboard** tab
2. In the "Repository Path" field, enter the full path to your repository:
   ```
   /home/username/projects/my-task-api
   ```
   Or click **Browse** to use the directory picker

3. Click **Scan Repository**
   - The server inspects the directory for:
     - Git repository status (`.git` directory)
     - Technology stack (looks for `package.json`, `requirements.txt`, `.csproj`, etc.)
     - Existing specifications (`specs/` or `docs/spec/`)
     - Current branch name
     - Recent commit history

4. Review the scan results displayed:
   ```
   ✓ Git repository detected
   Branch: main (3 commits)
   Stack: ASP.NET Core 9, C# 12
   Specifications: Not found
   Last commit: 2 days ago by username
   ```

5. Click **Onboard Project**
   - A dialog appears showing:
     - **Project Name**: Auto-detected from directory name (editable)
     - **Path**: Full repository path
     - **Stack Badges**: Technology identifiers
     - **Generate Specs**: Checkbox (enabled by default if no specs found)

6. Configure onboarding options:
   - Edit the project name if desired
   - Check **Generate Initial Specifications** to have agents analyze your code and create baseline specs
   - Choose spec format: **Markdown** (default) or **JSON**

7. Click **Confirm Onboarding**
   - Server creates workspace metadata in `.agent-academy/` directory
   - If spec generation enabled, agents analyze the codebase and create `specs/` directory
   - Progress indicator shows: "Analyzing codebase... 45%"

8. When complete, the app transitions to the **Main Shell**

> ⚠️ **GAP**: Onboarding doesn't validate repository health (uncommitted changes, detached HEAD, merge conflicts). Could fail silently if repo is in a bad state.

> ⚠️ **GAP**: No way to configure which branch should be the default integration branch during onboarding (assumes `main` or `develop`).

### Option B: Creating a New Project

For this tutorial, we'll create a new project from scratch:

1. Click the **Create** tab
2. Enter a new directory path:
   ```
   /home/username/projects/task-api-demo
   ```
   Make sure the parent directory exists but the final directory doesn't

3. Click **Create Project**
   - A dialog prompts for:
     - **Project Name**: `Task API Demo` (editable)
     - **Initialize Git**: Checkbox (enabled by default)
     - **Initial Branch**: `main` (editable)
     - **Technology Stack**: Dropdown (ASP.NET Core / Node.js / Python / Go / Other)

4. Configure your new project:
   - Name: **Task API Demo**
   - Initialize Git: ✓ Enabled
   - Initial Branch: `develop` (we'll use Git Flow)
   - Stack: **ASP.NET Core**

5. Click **Create and Onboard**
   - Server creates the directory structure
   - Initializes git repository: `git init && git checkout -b develop`
   - Creates workspace metadata: `.agent-academy/workspace.json`
   - Creates empty `specs/` directory
   - Adds `.gitignore` with Agent Academy patterns

6. The app transitions to the **Main Shell**

> ⚠️ **GAP**: No project template selection (e.g., "Web API", "React SPA", "Microservice"). All new projects start empty.

### Understanding the Main Shell

After onboarding, you're in the main workspace interface:

**Top Bar:**
- Project name and path
- Active branch indicator
- Phase selector dropdown (currently: **Idle**)
- Notification bell icon

**Left Sidebar:**
- **Agents Panel**: Shows all available agents (Aristotle, Archimedes, Hephaestus, Athena, Socrates, Thucydides)
  - Avatar icons
  - Status indicators (idle/thinking/working)
  - Click an agent to view their session
- **Rooms Panel**: Lists main room and any breakout rooms
  - Main room always visible
  - Breakout rooms appear during implementation

**Center Panel:**
- **ChatPanel**: Main conversation area
  - Message stream (agents and human messages)
  - Thinking indicators when agents are processing
  - Message input at bottom

**Right Sidebar (Tabbed):**
- **Tasks**: Task list with status, assignee, branch
- **Plan**: Current project plan (set by agents)
- **Timeline**: Activity feed (commits, phase changes, task updates)
- **Dashboard**: Real-time stats (tasks, commits, messages)
- **Commands**: Manual command execution interface

**Bottom Drawer (Optional):**
- **DM Panel**: Send direct messages to specific agents
- **Settings**: Configuration and preferences

The workspace is now ready for planning.

---

## Phase 2: Planning Your Product

### Transitioning to Planning Phase

1. In the top bar, click the **Phase** dropdown (currently showing "Idle")
2. Select **Planning**
3. The phase indicator updates and the UI shows:
   - Chat input placeholder changes to: "Describe what you want to build..."
   - Agents become active (status changes from "idle" to "ready")

### Sending Your Product Brief

1. Click in the chat input at the bottom of ChatPanel
2. Type your product requirements. For this tutorial, use:

   ```
   I want to build a RESTful Task Management API with the following features:

   - Create, read, update, delete tasks
   - Each task has: title, description, status (todo/in-progress/done), priority (low/medium/high), due date
   - Filter tasks by status and priority
   - Search tasks by title/description
   - User authentication with JWT tokens
   - PostgreSQL database backend
   - OpenAPI/Swagger documentation
   - Docker containerization

   The API should follow REST best practices, include comprehensive error handling, 
   and have at least 80% test coverage.
   ```

3. Press **Enter** or click **Send**
   - Your message appears in the chat with a "You" label
   - The orchestrator picks up the message and begins agent rounds

### Watching Agent Discussion

The orchestrator coordinates agents in rounds (max 3 per trigger event):

**Round 1:**

1. **Aristotle (Planner)** speaks first (usually within 5-10 seconds):
   - Analyzes your requirements
   - Proposes high-level architecture
   - Identifies technical decisions needed
   - You'll see: "🤔 Aristotle is thinking..." indicator, then message appears

   Example response:
   ```
   I'll help design this Task Management API. Let me break this down into 
   architectural components:

   **Architecture Proposal:**
   - ASP.NET Core 9 Web API with minimal APIs
   - PostgreSQL with Entity Framework Core
   - JWT authentication middleware
   - Repository pattern for data access
   - CQRS for command/query separation
   
   **Key Technical Decisions:**
   1. Use Vertical Slice Architecture for feature organization
   2. FluentValidation for request validation
   3. Serilog for structured logging
   4. xUnit + TestContainers for integration tests
   
   I'll create tasks for each major component.
   ```

2. **Archimedes (System Architect)** may respond next:
   - Validates the architectural approach
   - Suggests infrastructure considerations
   - Proposes database schema

   Example:
   ```
   Aristotle's architecture is sound. For the database schema:

   **Tasks Table:**
   - Id (UUID, PK)
   - Title (varchar(200))
   - Description (text, nullable)
   - Status (enum: Todo, InProgress, Done)
   - Priority (enum: Low, Medium, High)
   - DueDate (timestamp, nullable)
   - UserId (UUID, FK)
   - CreatedAt, UpdatedAt (timestamps)

   **Users Table:**
   - Id (UUID, PK)
   - Email (varchar(255), unique)
   - PasswordHash (varchar(255))
   - CreatedAt (timestamp)

   We should add indexes on Status and UserId for filtering performance.
   ```

**Round 2:**

3. **Socrates (Reviewer)** might chime in:
   - Questions design choices
   - Suggests testing strategies
   - Identifies potential issues

   Example:
   ```
   Good architectural direction. Questions:

   1. Should we support task assignment to multiple users?
   2. Do we need audit logging for task changes?
   3. Should filtering be case-sensitive?
   4. Rate limiting strategy for the API?

   For testing, recommend:
   - Unit tests for business logic (80%+ coverage)
   - Integration tests with TestContainers for database
   - API tests with WebApplicationFactory
   ```

4. **Aristotle** responds to questions and **creates tasks**:
   ```
   Good points, Socrates. Keeping it simple for v1:
   - Single user per task (we can extend later)
   - Audit logging: yes, add UpdatedBy field
   - Case-insensitive filtering
   - Basic rate limiting with ASP.NET middleware

   Creating implementation tasks now.

   [CREATE_TASK] project-setup
   [CREATE_TASK] database-schema
   [CREATE_TASK] authentication
   [CREATE_TASK] task-crud-endpoints
   [CREATE_TASK] filtering-search
   [CREATE_TASK] api-documentation
   [CREATE_TASK] docker-configuration
   [CREATE_TASK] integration-tests
   ```

**Round 3:**

5. Other agents may contribute:
   - Suggesting libraries
   - Proposing edge cases
   - Discussing implementation order

> ⚠️ **GAP**: No way to pause/stop agent discussion if it's going off-track. You have to wait for the round to complete.

### Observing Task Creation

As Aristotle creates tasks, they appear in the **Tasks** tab on the right sidebar:

1. Click the **Tasks** tab
2. You'll see a list of task cards:
   ```
   ┌─────────────────────────────────────────┐
   │ 🆕 project-setup                        │
   │ Status: Pending                         │
   │ Assignee: Unassigned                    │
   │ Branch: —                               │
   │ Created: Just now                       │
   └─────────────────────────────────────────┘
   ```

3. Click a task card to see full details:
   - **Title**: Project Setup and Configuration
   - **Description**: Initialize ASP.NET Core project, install dependencies, configure database context...
   - **Acceptance Criteria**: Bulleted list of requirements
   - **Estimated Effort**: Small / Medium / Large
   - **Dependencies**: (empty for first task)

4. Tasks are displayed in creation order by default

> ⚠️ **GAP**: No drag-and-drop task reordering. Cannot manually prioritize or change execution sequence.

> ⚠️ **GAP**: Cannot manually assign a task to a specific agent. Assignment is automatic during Implementation phase.

### Reviewing the Plan

1. Click the **Plan** tab
2. The plan document appears (set by Aristotle using the SET_PLAN command):

   ```markdown
   # Task Management API - Implementation Plan

   ## Overview
   Build a production-ready RESTful API for task management with authentication,
   filtering, and containerization.

   ## Architecture
   - Vertical Slice Architecture
   - PostgreSQL + Entity Framework Core
   - JWT authentication
   - CQRS pattern

   ## Implementation Phases
   1. Foundation (project-setup, database-schema)
   2. Core Features (authentication, task-crud-endpoints)
   3. Advanced Features (filtering-search)
   4. Delivery (api-documentation, docker-configuration, integration-tests)

   ## Success Criteria
   - All CRUD operations functional
   - 80%+ test coverage
   - API documented with Swagger
   - Docker image builds successfully
   ```

3. The plan provides context for all agents during implementation

> ⚠️ **GAP**: Cannot edit the plan from the UI. It's read-only once set by an agent.

### Clarifying Requirements

If agents have questions or you want to add details:

1. Type a follow-up message in the chat:
   ```
   For authentication, use email/password registration. Passwords should be hashed 
   with bcrypt. JWT tokens should expire after 24 hours.
   ```

2. Agents process the clarification and may update task descriptions

3. You can also send **Direct Messages** to specific agents:
   - Click the **DM** icon in the bottom drawer
   - Select an agent (e.g., Archimedes)
   - Type your message: "Should we use PostgreSQL 16 or 17?"
   - Agent receives the DM in their next prompt
   - Response appears in the DM panel (not main chat)

> ⚠️ **GAP**: DM history is not persisted across sessions. DMs are ephemeral context injections.

### Checking Task Dependencies

1. In the Tasks tab, look for tasks with a **Dependencies** section
2. Example: "task-crud-endpoints" depends on "database-schema" and "authentication"
3. The orchestrator ensures dependent tasks don't start until dependencies are complete

> ⚠️ **GAP**: No visual dependency graph. You have to read each task card to understand the dependency tree.

### Ready to Implement

Once agent discussion settles (no new messages for 10+ seconds) and all tasks are created:

1. Check the Tasks tab: Should see 8 tasks all in "Pending" status
2. Check the Timeline tab: Should see task creation events
3. Check the Dashboard tab: 
   - Tasks: 8 (Pending: 8)
   - Agents: 4 active
   - Rooms: 1 (main)

You're ready to move to implementation.

---

## Phase 3: Implementation

### Transitioning to Implementation Phase

1. Click the **Phase** dropdown in the top bar
2. Select **Implementing**
3. The orchestrator begins task assignment:
   - Identifies pending tasks with no unmet dependencies
   - Assigns each task to an appropriate agent (based on agent role and current workload)
   - Creates breakout rooms
   - Creates task branches

### Watching Breakout Room Creation

**Within 5-10 seconds of entering Implementation phase:**

1. The **Rooms Panel** updates to show new breakout rooms:
   ```
   📍 Main Room (4 agents)
   
   Breakouts:
   🔨 project-setup (Hephaestus)
   🔨 database-schema (Hephaestus)
   ```

2. Task cards in the **Tasks** tab update:
   ```
   ┌─────────────────────────────────────────┐
   │ 🔨 project-setup                        │
   │ Status: InProgress                      │
   │ Assignee: Hephaestus                    │
   │ Branch: task/project-setup-a3f1        │
   │ Started: Just now                       │
   └─────────────────────────────────────────┘
   ```

3. The **Timeline** shows activity:
   ```
   🔀 Branch created: task/project-setup-a3f1
   🔨 Task started: project-setup (Hephaestus)
   🏠 Breakout room created: project-setup
   ```

4. In the **Agents Panel**, Hephaestus's status changes to "working" (pulsing icon)

> ⚠️ **GAP**: Multiple tasks can be assigned to the same agent simultaneously, causing serial execution. No parallelization awareness in UI.

### Branch Naming Convention

Task branches follow the pattern: `task/{slug}-{suffix}`

- `{slug}`: kebab-case task ID
- `{suffix}`: Short random suffix for uniqueness (e.g., `a3f1`)

Examples:
- `task/project-setup-a3f1`
- `task/database-schema-b7d2`
- `task/authentication-c4e9`

All task branches are created from the current integration branch (usually `develop`).

> ⚠️ **GAP**: No branch conflict detection. If `develop` changes during implementation, task branches can become stale.

### Observing Agent Work in Breakout Rooms

1. In the **Rooms Panel**, click on a breakout room: `🔨 project-setup`
2. The center panel switches to **AgentSessionPanel** showing:
   - Breakout room name in the header
   - Agent avatar and status
   - Chat messages (agent's internal monologue and commands)
   - File operations and command results

**Example breakout chat:**

```
[Hephaestus in project-setup]

Let me start by creating the ASP.NET Core project structure.

[SEARCH_CODE] query: "*.csproj"
→ No results found

Good, this is a clean slate. I'll create the project.

[SHELL] command: dotnet new webapi -n TaskApi -o src/TaskApi
→ The template "ASP.NET Core Web API" was created successfully.

[READ_FILE] path: src/TaskApi/TaskApi.csproj
→ <Project Sdk="Microsoft.NET.Sdk.Web">
    <PropertyGroup>
      <TargetFramework>net9.0</TargetFramework>
      ...

Now I'll install Entity Framework Core and PostgreSQL packages.

[SHELL] command: cd src/TaskApi && dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
→ Package 'Npgsql.EntityFrameworkCore.PostgreSQL' added successfully.

[COMMIT_CHANGES] message: "feat: initialize ASP.NET Core Web API project"
→ Changes committed to task/project-setup-a3f1
   Files changed: 8
   Insertions: 156
   Deletions: 0

[CHECK_GATES] aspect: "build"
→ Running: dotnet build
   Build succeeded. 0 Warning(s). 0 Error(s).
   ✓ Build gate passed

[RECORD_EVIDENCE] type: "verification", description: "Project builds successfully"
→ Evidence recorded

Task complete. All acceptance criteria met.
```

**Key Commands You'll See:**

- **READ_FILE**: Agent reads source code or configuration
- **SEARCH_CODE**: Agent searches for code patterns
- **SHELL**: Agent runs shell commands (dotnet, npm, git, etc.)
- **COMMIT_CHANGES**: Agent commits work to the task branch
- **CHECK_GATES**: Agent validates build/test/lint gates
- **RECORD_EVIDENCE**: Agent documents verification steps

3. The chat auto-scrolls as new messages arrive
4. Each command shows its result (success/failure)
5. Errors appear in red; warnings in yellow

> ⚠️ **GAP**: Cannot intervene in a breakout while it's running. No "pause" button. Have to wait for agent to finish or get stuck.

### Monitoring Progress

**Dashboard Tab:**

1. Click the **Dashboard** tab in the right sidebar
2. Real-time stats update:
   ```
   📊 Tasks
   Total: 8
   Pending: 5
   InProgress: 2
   InReview: 0
   Approved: 1
   Completed: 0
   
   📊 Agents
   Idle: 2
   Working: 2
   
   📊 Activity (last hour)
   Commits: 3
   Commands executed: 47
   Messages: 18
   ```

**Timeline Tab:**

1. Click the **Timeline** tab
2. Activity feed shows events in reverse chronological order:
   ```
   2 min ago  ✓ Gate passed: build (project-setup)
   3 min ago  📝 Commit: "feat: initialize ASP.NET Core Web API project"
   5 min ago  🔨 Task started: database-schema (Hephaestus)
   5 min ago  🔨 Task started: project-setup (Hephaestus)
   6 min ago  🔄 Phase changed: Planning → Implementing
   ```

3. Click an event to see details (opens a popover or modal)

> ⚠️ **GAP**: No filtering or search in timeline. Long-running projects have thousands of events with no way to find specific ones.

> ⚠️ **GAP**: No progress indicators showing how far along a task is (e.g., "Step 3 of 7 complete").

### Agent Session Rotation (Context Management)

If a breakout session grows too large (configured limit, e.g., 100 messages):

1. The orchestrator triggers **session rotation**:
   - Old messages are summarized into a brief history
   - Copilot SDK session is invalidated
   - Fresh session starts with:
     - Summary of previous session
     - Current task context
     - Spec excerpts
     - Agent memories
   - Epoch number increments (visible in Dashboard > Conversation Sessions)

2. In the AgentSessionPanel, you'll see:
   ```
   ─────── SESSION ROTATED (Epoch 2) ───────
   Previous session summary: Initialized project, installed dependencies, 
   configured database context, ran initial tests.
   Continuing work on authentication implementation...
   ```

3. Agent continues seamlessly (from their perspective, it's one continuous task)

**To view session details:**

1. Click **Dashboard** > **Conversation Sessions** button
2. See a table of all sessions:
   ```
   Session ID         | Room              | Epoch | Messages | Started
   ──────────────────────────────────────────────────────────────────
   sess-a3f1-01       | project-setup     | 1     | 87       | 10m ago
   sess-a3f1-02       | project-setup     | 2     | 34       | 3m ago
   sess-main-01       | Main Room         | 1     | 12       | 15m ago
   ```

3. Click a session to view its full message history

> ⚠️ **GAP**: No way to manually trigger session rotation. It's automatic based on message count only (not token count or cost).

### Task Completion

When an agent finishes their work:

1. The agent's last message in the breakout: "Task complete. All acceptance criteria met."
2. Task status changes from **InProgress** to **InReview**
3. Agent returns to the main room (visible in Agents Panel)
4. Breakout room closes (removed from Rooms Panel)
5. Timeline event: "✅ Task completed: project-setup (Hephaestus)"

> ⚠️ **GAP**: No notification or alert when a task completes. You have to watch the UI.

### Stuck Detection

If an agent gets stuck (repeating itself, making no progress):

1. Stuck detection triggers after:
   - Agent commits the same file 3+ times in a row
   - Agent repeats the same error 5+ times
   - No progress for 20+ consecutive commands

2. The breakout terminates automatically:
   ```
   ⚠️ Stuck behavior detected. Terminating breakout session.
   Reason: Repeated compilation errors on same file (6 attempts)
   ```

3. Task status changes to **Blocked**
4. Timeline event: "🚫 Task blocked: authentication (stuck detection)"

**Recovering from stuck tasks:**

1. Click the task card in the Tasks tab
2. Click **View Details**
3. Read the agent's chat history to understand what went wrong
4. Options:
   - **Reassign**: Manually restart with different agent (if feature exists)
   - **Fix Manually**: Switch to the task branch and fix the issue yourself
   - **Provide Guidance**: Send a DM to the agent with hints
   - **Cancel Task**: Mark it as cancelled

> ⚠️ **GAP**: No "retry with different approach" button. Can't give the agent a hint and restart automatically.

> ⚠️ **GAP**: Stuck detection is primitive. Doesn't detect logical errors or wrong implementations, only repetitive failures.

### Parallel Implementation

Multiple tasks can run simultaneously:

1. If you have 8 tasks and 4 agents, expect ~4 breakouts active at once
2. Agents are assigned based on:
   - Role suitability (Hephaestus for backend, Socrates for testing)
   - Current workload (idle agents assigned first)
   - Task dependencies (blocked tasks wait)

3. The Rooms Panel shows all active breakouts:
   ```
   📍 Main Room (1 agent)
   
   Breakouts:
   🔨 project-setup (Hephaestus) ✓ Complete
   🔨 database-schema (Hephaestus)
   🔨 authentication (Aristotle)
   🔨 api-documentation (Socrates)
   ```

> ⚠️ **GAP**: No concurrency limit configuration. If you have 50 tasks, all could start simultaneously, overwhelming the system.

### Intervening During Implementation

**Sending messages to working agents:**

1. Click the **DM** icon in the bottom drawer
2. Select the agent currently working (e.g., Hephaestus in "database-schema")
3. Type your message:
   ```
   Use PostgreSQL 16, not 17. Also add a migration for seed data.
   ```
4. The DM is injected into the agent's next prompt (within 30 seconds)
5. Agent acknowledges and adjusts:
   ```
   [Hephaestus in database-schema]
   
   Noted from human DM: Use PostgreSQL 16 and add seed data migration. 
   Updating database configuration...
   ```

**Executing commands manually:**

1. Click the **Commands** tab in the right sidebar
2. You'll see a form with fields:
   - **Command Type**: Dropdown (READ_FILE, SEARCH_CODE, SHELL, etc.)
   - **Parameters**: JSON input based on command type
3. Example: Read a file the agent just created
   ```json
   {
     "path": "src/TaskApi/Data/AppDbContext.cs"
   }
   ```
4. Click **Execute**
5. Result appears in the output panel below:
   ```csharp
   using Microsoft.EntityFrameworkCore;
   
   namespace TaskApi.Data;
   
   public class AppDbContext : DbContext
   {
       public DbSet<TaskItem> Tasks { get; set; }
       public DbSet<User> Users { get; set; }
       ...
   ```

> ⚠️ **GAP**: Manual command execution is not recorded in any task history. It's ephemeral investigation.

> ⚠️ **GAP**: Cannot send a message to all agents at once (broadcast). Have to DM each individually.

### Viewing Code Changes

**While a task is in progress:**

1. Click the task card in the Tasks tab
2. Click **View Branch**
3. A panel opens showing:
   - Branch name
   - Commit list (most recent first)
   - Option to open in external tool (VS Code, GitHub Desktop)

**To see a diff:**

1. Use git from the terminal:
   ```bash
   cd /home/username/projects/task-api-demo
   git diff develop..task/database-schema-b7d2
   ```

> ⚠️ **GAP**: No built-in diff viewer in the UI. Have to use external tools or command line.

> ⚠️ **GAP**: Cannot view changes as they happen in real-time. Only committed changes visible.

---

## Phase 4: Code Review

### Transitioning to Review Phase

1. Wait until at least one task is in **InReview** status
2. Click the **Phase** dropdown
3. Select **Reviewing**
4. The orchestrator triggers review process:
   - Socrates (or configured reviewer) examines each InReview task
   - Reads the code changes
   - Checks against acceptance criteria
   - Posts review comments

### Watching Automated Review

**In the main room chat:**

1. Socrates posts a message:
   ```
   I'll review the completed tasks. Starting with project-setup.
   
   [READ_FILE] path: src/TaskApi/TaskApi.csproj
   [READ_FILE] path: src/TaskApi/Program.cs
   [SEARCH_CODE] query: "DbContext"
   
   Reviewing against acceptance criteria:
   ✓ ASP.NET Core project initialized
   ✓ Dependencies installed
   ✓ Database context configured
   ✓ Build succeeds
   ✓ All files properly organized
   
   Code quality observations:
   - Good use of minimal APIs
   - Connection string should use environment variable, not appsettings.json
   - Missing .editorconfig for code style consistency
   
   Recommendation: REQUEST_CHANGES
   ```

2. Socrates uses the **REQUEST_CHANGES** command:
   ```
   [REQUEST_CHANGES] task: project-setup
   Comments:
   - Move connection string to environment variable
   - Add .editorconfig for style enforcement
   ```

3. Task status changes from **InReview** to **ChangesRequested**
4. Timeline event: "🔄 Changes requested: project-setup (Socrates)"

### Agent Rework Cycle

When changes are requested:

1. The task is reassigned to the original agent (Hephaestus)
2. A new breakout room opens: `🔨 project-setup (revision)`
3. Agent sees the review comments in their prompt
4. Agent makes fixes:
   ```
   [Hephaestus in project-setup (revision)]
   
   Addressing review feedback from Socrates:
   
   1. Moving connection string to environment variable...
   [SHELL] command: dotnet user-secrets init
   
   2. Adding .editorconfig for style consistency...
   [SHELL] command: dotnet new editorconfig
   
   [COMMIT_CHANGES] message: "fix: use environment variable for connection string, add .editorconfig"
   
   Changes complete. Re-submitting for review.
   ```

5. Task status changes back to **InReview**
6. Socrates reviews again (automatically in Reviewing phase)
7. If satisfied, Socrates **approves**:
   ```
   [APPROVE_TASK] task: project-setup
   
   All feedback addressed. Code quality is good. Ready to merge.
   ```

8. Task status changes to **Approved**

> ⚠️ **GAP**: No limit on review cycles. A task could bounce back and forth indefinitely.

> ⚠️ **GAP**: Cannot see review comments in the task card UI. Have to read main room chat to find them.

### Manual Review (Human Override)

You can review tasks yourself:

1. Click a task card in **InReview** status
2. Click **Review Task** button
3. A review panel opens:
   - List of files changed
   - Commit messages
   - Acceptance criteria checklist
   - Text area for comments
   - Buttons: **Approve** | **Request Changes** | **Reject**

4. To approve:
   - Click **Approve**
   - Task status changes to **Approved**
   - Agent's work is validated

5. To request changes:
   - Type comments in the text area:
     ```
     - Add null checks to the CreateTask method
     - Use async/await for database operations
     ```
   - Click **Request Changes**
   - Task reopens, agent gets your comments

6. To reject:
   - Type a rejection reason
   - Click **Reject**
   - Task status changes to **Cancelled**
   - Changes may be reverted (optional)

> ⚠️ **GAP**: Human review comments aren't structured. Agent might misinterpret free-form text.

> ⚠️ **GAP**: No side-by-side diff in the review panel. Very hard to review code without seeing changes.

### Multiple Reviews in Parallel

If multiple tasks are **InReview**:

1. Socrates reviews them sequentially (one at a time)
2. Review order is typically:
   - Tasks with no dependencies first
   - Tasks by creation order

> ⚠️ **GAP**: No way to prioritize which task gets reviewed first.

> ⚠️ **GAP**: Only one reviewer (Socrates). Can't configure multiple reviewers or require approval from specific agents.

### Review Metrics

The **Dashboard** tab shows review stats:

```
📊 Review Activity
Approved today: 3
Changes requested: 2
Average review time: 4 min
```

> ⚠️ **GAP**: No detailed review metrics (lines reviewed, comments per task, approval rate by agent).

---

## Phase 5: Commit & Merge

### Transitioning to Committing Phase

1. Ensure at least one task is in **Approved** status
2. Click the **Phase** dropdown
3. Select **Committing**
4. The orchestrator begins merge operations

### Automated Squash Merge

For each approved task:

1. The orchestrator uses the **MERGE_TASK** command:
   ```
   [Orchestrator]
   
   Merging approved tasks to develop branch...
   
   [MERGE_TASK] task: project-setup, strategy: squash
   → Squashing 3 commits from task/project-setup-a3f1
   → Squash commit message: "feat: initialize ASP.NET Core Web API project (#1)"
   → Merging to develop
   → Success. Branch task/project-setup-a3f1 deleted.
   ```

2. Timeline event: "✅ Task merged: project-setup → develop"
3. Task status changes to **Completed**
4. The task branch is deleted (cleanup)

**Squash merge behavior:**

- All commits on the task branch are squashed into one
- Commit message is derived from the task title
- If a GitHub issue/PR exists, it's referenced (e.g., `#1`)
- The squash commit is added to the integration branch (`develop`)

> ⚠️ **GAP**: No option to choose merge strategy (merge commit, rebase, squash). Always squashes.

> ⚠️ **GAP**: No merge conflict detection or resolution. If develop has changed, merge can fail silently.

### Creating Pull Requests

If you want to create PRs on GitHub:

1. Use the **CREATE_PR** command manually from the Commands tab:
   ```json
   {
     "command": "CREATE_PR",
     "args": {
       "taskId": "authentication-c4e9"
     }
   }
   ```

2. The server runs `gh pr create`:
   ```bash
   gh pr create \
     --title "Add JWT authentication to Task API" \
     --body "..." \
     --base develop \
     --head task/authentication-c4e9
   ```

3. PR metadata is stored on the task:
   - PR number: `#42`
   - PR URL: `https://github.com/user/task-api-demo/pull/42`
   - PR status: `open`

4. Timeline event: "🔀 PR created: #42 (authentication)"

**Merging PRs:**

1. After human review on GitHub, use the **MERGE_PR** command:
   ```json
   {
     "command": "MERGE_PR",
     "args": {
       "taskId": "authentication-c4e9"
     }
   }
   ```

2. The server uses GitHub API to merge the PR
3. Task status updates to **Completed**

> ⚠️ **GAP**: PR creation is manual. Not automatic when tasks are approved.

> ⚠️ **GAP**: Cannot sync PR status from GitHub. If someone merges a PR externally, Agent Academy doesn't know.

### Merge Failures

If a merge fails (e.g., conflicts):

1. The orchestrator logs an error:
   ```
   ❌ Merge failed: task/authentication-c4e9
   Reason: Merge conflict in src/TaskApi/Program.cs
   ```

2. Task status changes to **Blocked**
3. You must resolve manually:
   ```bash
   git checkout develop
   git merge task/authentication-c4e9
   # Fix conflicts in editor
   git add .
   git commit
   ```

4. After manual resolution, mark the task as merged in the UI:
   - Click the task card
   - Click **Mark as Merged**
   - Task status changes to **Completed**

> ⚠️ **GAP**: No conflict resolution UI. Must drop to command line.

> ⚠️ **GAP**: Agent Academy doesn't automatically detect when you manually merge a task branch.

### Viewing Merge History

1. Click the **Timeline** tab
2. Filter by event type: **Merges**
3. See a list of all merged tasks:
   ```
   10 min ago  ✅ Task merged: integration-tests → develop
   15 min ago  ✅ Task merged: api-documentation → develop
   20 min ago  ✅ Task merged: filtering-search → develop
   ```

4. Click an event to see:
   - Squash commit SHA
   - Files changed
   - Lines added/removed
   - Link to commit on GitHub (if remote exists)

> ⚠️ **GAP**: No way to undo/revert a merge from the UI. Must use git manually.

### All Tasks Merged

When all tasks are merged:

1. The Dashboard shows:
   ```
   📊 Tasks
   Total: 8
   Completed: 8
   ```

2. You can return to **Idle** phase or **Planning** for the next feature

---

## Phase 6: Iterate

### Starting a New Feature Cycle

After completing the first set of tasks:

1. Transition to **Idle** phase
2. The main room clears (previous planning messages are archived)
3. Session context is compacted:
   - Old messages summarized
   - Agent memories persist (agents remember architectural decisions)
   - Specs should be updated to reflect delivered code

**Updating Specs:**

Before planning the next feature, verify specs are current:

1. Open `specs/` directory in your editor
2. Check if specs reflect the delivered code
3. If agents forgot to update specs, you can:
   - Update them manually
   - Ask agents to update: "Please update specs to reflect the current codebase"

**Planning the Next Feature:**

1. Transition to **Planning** phase again
2. Send a new product brief:
   ```
   Next feature: Add support for task comments.
   
   Users should be able to:
   - Add comments to any task
   - Edit/delete their own comments
   - View all comments on a task chronologically
   - Mention other users in comments (@username)
   
   Comments should be stored in the database and returned via a new endpoint.
   ```

3. Agents reference their memories from the previous cycle:
   ```
   [Aristotle]
   
   Based on our existing architecture (Vertical Slice, EF Core, JWT auth),
   I'll design the comments feature to fit seamlessly...
   ```

4. New tasks are created
5. Repeat the Implementation → Review → Commit cycle

### Agent Memory Continuity

Agents use the **REMEMBER** command to persist learnings:

Example from previous cycle:
```
[Hephaestus in database-schema]

[REMEMBER] key: "database-naming-convention"
Value: "Use snake_case for table/column names in PostgreSQL per team convention"

This will help me stay consistent in future schema changes.
```

When starting a new feature:
```
[Hephaestus in comments-feature]

Checking my memories...
Remembered: database-naming-convention = "Use snake_case for table/column names..."
I'll use snake_case for the comments table.
```

> ⚠️ **GAP**: No UI to view/edit agent memories. They're invisible to humans.

> ⚠️ **GAP**: Memories don't expire. Stale information can persist indefinitely.

### Session Compaction

After several feature cycles, the main room session grows large:

1. Automatic compaction triggers (e.g., after 200 messages)
2. Old messages are summarized:
   ```
   ─────── SESSION COMPACTED ───────
   Summary of previous work:
   - Cycle 1: Built Task API with CRUD, auth, filtering (8 tasks completed)
   - Cycle 2: Added comments feature (3 tasks completed)
   - Key decisions: Vertical slice architecture, PostgreSQL, JWT auth
   ```

3. Fresh session starts
4. Agents retain context via summaries and memories

---

## Advanced Scenarios

### Using the Consultant API

For power users who prefer CLI/API interaction:

**List all rooms:**
```bash
curl -H "X-Consultant-Key: your-secret-key" \
  http://localhost:5066/api/rooms
```

Response:
```json
[
  {
    "id": "room-main-abc123",
    "name": "Main Room",
    "phase": "Planning",
    "activeAgents": ["aristotle", "archimedes", "socrates"],
    "messageCount": 47
  }
]
```

**Send a message:**
```bash
curl -X POST \
  -H "X-Consultant-Key: your-secret-key" \
  -H "Content-Type: application/json" \
  -d '{"content":"Build a user profile management feature"}' \
  http://localhost:5066/api/rooms/room-main-abc123/human
```

**Poll for new messages:**
```bash
curl -H "X-Consultant-Key: your-secret-key" \
  "http://localhost:5066/api/rooms/room-main-abc123/messages?after=msg-456"
```

Response:
```json
[
  {
    "id": "msg-457",
    "sender": "Aristotle",
    "content": "I'll design the profile management feature...",
    "timestamp": "2024-04-08T15:30:00Z"
  }
]
```

**Execute a command:**
```bash
curl -X POST \
  -H "X-Consultant-Key: your-secret-key" \
  -H "Content-Type: application/json" \
  -d '{
    "command": "READ_FILE",
    "args": {"path": "src/TaskApi/Program.cs"}
  }' \
  http://localhost:5066/api/commands/execute
```

> ⚠️ **GAP**: API authentication uses a shared secret key. No per-user API tokens or OAuth.

> ⚠️ **GAP**: No webhooks. Must poll for new messages.

### Recovering from Server Crashes

If the Agent Academy server crashes:

1. The wrapper script (`run-server.sh`) detects the crash
2. Server automatically restarts
3. On startup, recovery process runs:
   - All breakout rooms are closed
   - Agents in breakouts return to main room
   - Tasks InProgress are reset to Pending
   - Main room session is restored from database

4. You'll see a banner in the UI:
   ```
   ⚠️ Server restarted. Some agent work may have been lost.
   Tasks reset: authentication, filtering-search
   ```

5. To resume:
   - Check which tasks were reset
   - Transition back to **Implementing** phase
   - Tasks will be reassigned and restarted

> ⚠️ **GAP**: No checkpointing during agent work. If a breakout is 90% done and server crashes, it starts from scratch.

> ⚠️ **GAP**: No way to manually save/restore workspace state.

### Handling Authentication Failures

If your GitHub Copilot SDK token expires:

1. Agents will fail to think (error messages in breakouts)
2. The UI shows a banner:
   ```
   🔒 Authentication required. Your Copilot token has expired.
   [Re-authenticate]
   ```

3. Click **Re-authenticate**
4. You're redirected to GitHub OAuth flow
5. After success, you're returned to Agent Academy
6. Agents resume work automatically

> ⚠️ **GAP**: Active breakouts are terminated during re-auth. Work in progress is lost.

### Configuring Agents

**Per-agent settings:**

1. Click the **Settings** icon in the top bar
2. Navigate to **Agents** tab
3. You'll see a card for each agent:
   ```
   ┌─────────────────────────────────────┐
   │ 🎭 Aristotle (Planner)              │
   │ Model: claude-sonnet-4              │
   │ Custom Instructions: (none)         │
   │ Instruction Template: default       │
   │ [Edit]                               │
   └─────────────────────────────────────┘
   ```

4. Click **Edit** on Aristotle's card
5. Modify settings:
   - **Model**: Dropdown (claude-sonnet-4, gpt-5.4, gpt-5-mini, etc.)
   - **Custom Instructions**: Text area for additional prompt instructions
     ```
     Always consider cost-effectiveness when proposing solutions.
     Prefer open-source libraries over commercial products.
     ```
   - **Instruction Template**: Dropdown of saved templates
     - `default`: Standard agent prompt
     - `detailed-planner`: Extra emphasis on documentation
     - `fast-implementer`: Optimized for speed over detail

6. Click **Save**
7. Changes take effect on the agent's next turn (not mid-conversation)

**Instruction Templates:**

1. In Settings > Agents, click **Manage Templates**
2. See a list of templates:
   ```
   default
   detailed-planner
   fast-implementer
   security-focused
   ```

3. Click **New Template**
4. Enter:
   - **Name**: `cost-optimizer`
   - **Content**:
     ```
     You are a cost-conscious engineer. Always:
     - Choose the cheapest viable solution
     - Minimize API calls and token usage
     - Prefer static solutions over dynamic ones
     - Document cost implications of decisions
     ```

5. Click **Create**
6. Now you can assign `cost-optimizer` to any agent

> ⚠️ **GAP**: No template versioning. If you edit a template, all agents using it are affected immediately.

> ⚠️ **GAP**: Cannot preview how instructions affect agent behavior before committing.

### Setting Up Notifications

**Notification wizard:**

1. Click **Settings** > **Notifications**
2. Click **Add Notification Channel**
3. Choose provider: **Discord** | **Slack** | **Console** (stdout logging)

**Discord setup:**

1. Select **Discord**
2. Enter webhook URL:
   ```
   https://discord.com/api/webhooks/123456789/abcdefg
   ```
3. Configure options:
   - **Agent Avatars**: ✓ Enabled (each agent gets unique avatar)
   - **Events**: Checkboxes for:
     - ✓ New messages
     - ✓ Phase changes
     - ✓ Task updates
     - ✓ Errors
     - ☐ All commands (noisy)

4. Click **Connect**
5. Server sends a test message to Discord:
   ```
   [Agent Academy] 
   Notification channel connected successfully! 🎉
   ```

6. Click **Save**

**Slack setup:**

1. Select **Slack**
2. Enter webhook URL or use OAuth (button)
3. If OAuth: redirected to Slack, authorize the app, returned to Agent Academy
4. Select channel: `#agent-academy` (dropdown)
5. Configure events (same as Discord)
6. Click **Connect** and **Save**

**Notification examples:**

Discord message when a task completes:
```
[Hephaestus] ✅ Task completed: database-schema
Branch: task/database-schema-b7d2
Commits: 4
Files changed: 7
```

Slack message when a phase changes:
```
🔄 Phase changed: Implementing → Reviewing
8 tasks ready for review
```

> ⚠️ **GAP**: No notification filtering (e.g., only notify for high-priority tasks).

> ⚠️ **GAP**: No email notifications.

> ⚠️ **GAP**: Cannot customize notification message format.

### Managing Large Codebases

For projects with thousands of files:

**Spec strategy:**
- Break specs into multiple files (one per domain/module)
- Agents receive only relevant spec excerpts in their context
- Example structure:
  ```
  specs/
    01-architecture.md
    02-authentication.md
    03-task-management.md
    04-comments.md
  ```

**Search performance:**
- SEARCH_CODE uses ripgrep (fast even on large codebases)
- Agents should use targeted searches with file patterns:
  ```
  [SEARCH_CODE] query: "class.*Controller", filePattern: "**/*Controller.cs"
  ```

**Session rotation:**
- Lower the epoch size in Settings > Advanced:
  ```
  Epoch Size: 50 messages (default: 100)
  ```
- More frequent compaction = smaller context = faster agent responses

> ⚠️ **GAP**: No way to exclude directories from agent access (e.g., `node_modules`, `bin`, `obj`). Agents can waste time searching irrelevant files.

> ⚠️ **GAP**: No caching of file reads. If 10 agents read `Program.cs`, it's read 10 times.

### Multi-Repository Projects

Agent Academy currently supports one repository per workspace.

**Workaround for microservices:**

1. Create a monorepo with all services:
   ```
   task-api-monorepo/
     services/
       api/
       auth/
       notifications/
     specs/
       ...
   ```

2. Onboard the monorepo as a single project
3. Use task descriptions to scope work to specific services:
   ```
   Task: Add user registration
   Description: Implement registration endpoint in services/auth/...
   ```

> ⚠️ **GAP**: No native multi-repository support. Cannot coordinate changes across separate repos.

> ⚠️ **GAP**: No dependency tracking between services in different repos.

---

## Troubleshooting

### Task Stuck in InProgress

**Symptoms:**
- Task shows InProgress for 20+ minutes
- No new commits or commands in breakout room
- Agent appears idle but breakout still open

**Solutions:**

1. Check the breakout chat (click room in Rooms Panel)
   - Look for error messages or repeated failures
   - Agent might be waiting for a long-running command

2. Send a DM to the agent:
   ```
   Are you stuck? Please summarize your current status.
   ```

3. If no response after 5 minutes:
   - Manually cancel the task:
     - Click task card > **Cancel Task**
     - Breakout room closes
     - Agent returns to main room
   - Reassign or fix manually

> ⚠️ **GAP**: No "force stop breakout" button. Have to cancel the entire task.

### Agent Repeating Same Mistakes

**Symptoms:**
- Review → Changes Requested → Review → Changes Requested (loop)
- Agent commits same fix multiple times
- Stuck detection doesn't trigger

**Solutions:**

1. Send detailed DM with explicit instructions:
   ```
   You keep adding null checks to CreateTask, but the issue is in UpdateTask.
   Please focus on UpdateTask method only. Here's the correct fix:
   [paste code snippet]
   ```

2. If agent still fails:
   - Approve the task manually (override agent review)
   - Switch to the task branch yourself:
     ```bash
     git checkout task/authentication-c4e9
     # Make the fix manually
     git commit -m "fix: correct null check in UpdateTask"
     git checkout develop
     ```
   - Mark task as completed

> ⚠️ **GAP**: Cannot give agents code snippets directly in the UI. DMs are text-only.

### Merge Conflicts

**Symptoms:**
- MERGE_TASK fails with conflict error
- Task stuck in Approved status

**Solutions:**

1. Merge manually:
   ```bash
   git checkout develop
   git merge task/filtering-search-e5a3
   # Resolve conflicts in editor
   git add .
   git commit
   ```

2. Update task status:
   - Click task card
   - Click **Mark as Merged**
   - Enter the merge commit SHA
   - Task moves to Completed

> ⚠️ **GAP**: Agent Academy doesn't help with conflict resolution. Entirely manual.

### Server Performance Issues

**Symptoms:**
- UI feels sluggish
- Agent responses delayed
- High CPU/memory usage on server

**Solutions:**

1. Check Dashboard > Stats:
   - If 10+ breakouts active simultaneously, you're overloaded
   - Pause new task assignments:
     - Switch to **Idle** phase
     - Wait for active breakouts to complete

2. Reduce concurrency:
   - In Settings > Advanced:
     ```
     Max Concurrent Breakouts: 4 (default: unlimited)
     ```

3. Optimize agent context:
   - Lower epoch size (fewer messages per session)
   - Split large specs into smaller files
   - Clear old sessions: Settings > Advanced > **Clear Session History**

4. Restart server (graceful):
   ```bash
   # The wrapper script handles restart
   pkill -TERM dotnet
   # Wait 10 seconds, server restarts
   ```

> ⚠️ **GAP**: No resource monitoring in the UI (CPU, memory, API rate limits).

> ⚠️ **GAP**: No automatic throttling when system is overloaded.

---

## Best Practices

### 1. Write Detailed Product Briefs

**Bad:**
```
Build a task API
```

**Good:**
```
Build a RESTful Task Management API with:
- CRUD operations for tasks
- Each task: title (required, max 200 chars), description (optional), 
  status (enum: todo/in-progress/done), priority (enum: low/medium/high), 
  due date (ISO 8601)
- Filtering by status and priority (case-insensitive)
- Full-text search on title and description
- JWT authentication (email/password registration)
- PostgreSQL backend with migrations
- 80%+ test coverage
- OpenAPI docs
- Docker containerization
```

### 2. Review Plans Before Implementation

After agents create tasks:

1. Read the plan in the Plan tab
2. Check task descriptions for clarity
3. Verify dependencies are correct
4. Ask clarifying questions if anything is ambiguous

**Don't skip this!** It's much cheaper to fix planning issues than implementation issues.

### 3. Monitor Breakouts Actively

Don't just transition to Implementing and walk away:

1. Watch the first 2-3 tasks closely
2. If agents are making mistakes, intervene early with DMs
3. Check Dashboard periodically for stuck tasks

### 4. Keep Specs Updated

After each feature cycle:

1. Review `specs/` directory
2. Ensure specs reflect delivered code (not aspirational)
3. If agents forgot to update, either:
   - Ask them: "Update specs to match current codebase"
   - Update manually

### 5. Use Conventional Commits

Agents generally follow conventional commits (feat:, fix:, docs:), but you should too:

```bash
git commit -m "feat: add pagination to task list endpoint"
git commit -m "fix: prevent duplicate task creation"
git commit -m "docs: update API documentation for filters"
```

This makes merge history readable.

### 6. Leverage Agent Memories

When agents discover important patterns or decisions, they should **REMEMBER**:

```
[REMEMBER] key: "error-handling-pattern"
Value: "All API endpoints return ProblemDetails RFC 7807 format for errors"
```

If they forget, remind them:
```
Please remember this decision for future tasks.
```

### 7. Phase Discipline

Don't skip phases or change erratically:

- **Idle**: Between feature cycles, no active work
- **Planning**: Requirements gathering and task creation ONLY
- **Implementing**: Autonomous agent work, minimal human intervention
- **Reviewing**: Code review ONLY (not new implementation)
- **Committing**: Merge operations ONLY

Skipping phases confuses the orchestrator.

> ⚠️ **GAP**: Nothing enforces phase discipline. You can jump to Implementing with zero tasks and the system won't complain.

### 8. Graceful Shutdowns

When ending a work session:

1. Ensure no breakouts are active (wait for them to complete or cancel)
2. Transition to **Idle** phase
3. Close the browser tab

The server persists state automatically, so you can return later and resume.

---

## Next Steps

You've learned the complete workflow for building a product with Agent Academy:

✅ Onboard a project (existing or new)  
✅ Plan features with AI agents  
✅ Implement through autonomous breakouts  
✅ Review code changes  
✅ Merge to integration branch  
✅ Iterate with new features  

**Further reading:**

- **[03-operations-reference.md](03-operations-reference.md)**: Session management, monitoring, troubleshooting, configuration
- **[04-gap-analysis.md](04-gap-analysis.md)**: Known limitations and improvement opportunities
- **[01-core-concepts.md](01-core-concepts.md)**: Deep dive into rooms, agents, sessions, and commands

**Community:**

- GitHub Discussions: Ask questions, share workflows
- Discord: Real-time help from other Agent Academy users
- Issue Tracker: Report bugs and request features

**Feedback:**

Agent Academy is under active development. If you encounter gaps or have suggestions, please file an issue on GitHub.

Happy building! 🚀
