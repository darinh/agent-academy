# 03 — Operations Reference

This section covers operational aspects of Agent Academy: session lifecycle management, context strategies, monitoring and observability, troubleshooting procedures, and configuration reference.

---

## Session Management & Context Strategy

### Understanding Conversation Sessions (Epochs)

Agent Academy uses a **session rotation** strategy to manage LLM context windows. Understanding how sessions work is critical for operating the platform effectively.

#### What is a Session?

A **session** (also called an **epoch**) is a bounded sequence of messages within a room's conversation history. Key characteristics:

- **Per-room isolation**: Each room has its own active session
- **Per-breakout isolation**: Breakout rooms have independent sessions from their parent room
- **Message counting**: Sessions track message count as agents and humans post
- **Automatic rotation**: When the message count hits the configured threshold, rotation occurs

#### The Rotation Process

When a session reaches its message threshold:

1. **Summarization**: The LLM generates a summary of the entire session's conversation
2. **Archival**: The session is marked as `archived`, storing the summary and metadata
3. **New session creation**: A fresh active session is created for the room
4. **SDK invalidation**: All Copilot SDK agent sessions are invalidated, forcing agents to get fresh context
5. **Summary injection**: The archived session's summary is injected into the new session's context, providing continuity

```
[Session 1: Messages 1-50] → Threshold hit → LLM summarizes →
[Session 1: ARCHIVED with summary] + [Session 2: ACTIVE, starts with summary of Session 1]
```

#### Why Session Rotation Matters

**Context window limits**: LLMs have finite context windows (typically 32k-200k tokens). An unbounded conversation would eventually:
- Degrade response quality as the context fills
- Hit hard limits and fail
- Become prohibitively expensive

**The tradeoff**: Session rotation trades perfect recall for sustained quality:
- **What you lose**: Perfect verbatim history of every message
- **What you gain**: Consistent agent performance, cost control, fresh context
- **Bridge mechanism**: Agent memories (via `REMEMBER` command) persist critical facts across rotations

#### Session Visibility

**Dashboard Panel**: Navigate to **Dashboard > Conversation Sessions** to view:

| Column | Description |
|--------|-------------|
| **Room** | Room name or "Main Room" |
| **Agent** | Agent involved (or "System" for orchestrator sessions) |
| **Message Count** | Current message count in session |
| **Status** | `active` or `archived` |
| **Created** | Session start timestamp |
| **Archived** | Session end timestamp (archived only) |
| **Summary** | AI-generated summary (archived only) |

**Filtering**: Use the room filter dropdown to view sessions for a specific room.

**API Access**:
```http
GET /api/sessions
GET /api/sessions?roomId={roomId}
```

Response includes session ID, message count, status, and summary text for archived sessions.

#### Configuring Epoch Size

Epoch size determines how many messages accumulate before rotation. Tuning this value is a critical operational decision.

**Via UI**:
1. Navigate to **Settings > Advanced**
2. Locate **Conversation Epoch Thresholds** section
3. Adjust values for different session types
4. Save settings

**Via API**:
```http
PUT /api/settings
Content-Type: application/json

{
  "epochSize": {
    "default": 50,
    "planning": 40,
    "breakout": 30
  }
}
```

**Sizing guidance**:

| Epoch Size | Use Case | Pros | Cons |
|------------|----------|------|------|
| **20-30** | Rapid experimentation, frequent context refresh | Fresh context, lower token costs | Frequent rotations, more summaries in history |
| **40-60** | Standard operations (recommended) | Good balance of context and freshness | Moderate rotation frequency |
| **80-100** | Deep planning sessions, complex discussions | Maximum context retention | Higher token costs, risk of context degradation |

**Symptoms of wrong sizing**:
- **Too small**: Agents lose track of recent decisions, summaries feel lossy
- **Too large**: Agents become slow, confused by large context, high costs

#### Heavy Context Scenarios

Certain workflows generate large amounts of context quickly:

**1. Large Planning Discussions**
- **Pattern**: Multiple agents discussing complex architecture across many rounds
- **Growth rate**: 10+ agents × 5-10 messages each = 50-100 messages rapidly
- **Mitigation**: Use smaller epoch size (30-40), rely on agent memories for critical decisions

**2. Breakout Rooms with Complex Tasks**
- **Pattern**: Agent reads many files, accumulates large code snippets in context
- **Growth rate**: Each `READ_FILE` adds ~500-2000 tokens; 10 files = 20k tokens
- **Mitigation**: 
  - Breakout sessions have independent thresholds (typically lower)
  - Spec injection system reloads fresh specs each turn (not accumulated)
  - Agent should summarize findings via `REMEMBER` before returning

**3. Multi-Round Code Review**
- **Pattern**: Reviewer requests changes, engineer iterates, multiple review cycles
- **Growth rate**: Each cycle adds full file diffs to context
- **Mitigation**: Session rotation between cycles, use task evidence system

**4. Workspace Switch**
- **Trigger**: Admin changes workspace via `PUT /api/workspace` with new workspace path
- **Impact**: Archives ALL active sessions across ALL rooms
- **Reason**: New workspace = new context, agents need clean slate

#### Manual Session Management

You can force session rotation manually when needed.

**When to compact manually**:
- Context feels stale (agents referencing outdated information)
- Agent seems confused or repetitive despite being within threshold
- After resolving a major issue, want to "reset" the conversation
- Before starting a complex new initiative

**Via API** (from Collaboration Controller):
```http
POST /api/rooms/{roomId}/compact

Response:
{
  "archivedSessionId": "sess_abc123",
  "newSessionId": "sess_def456",
  "messageCount": 47,
  "summary": "Session summary generated by LLM..."
}
```

**Effect**:
- Immediately rotates the session regardless of message count
- Agents get fresh context on next turn
- Previous session becomes archived with full summary

> ⚠️ **GAP**: No UI button for manual compaction — must use API directly via Tools or Consultant API

#### Context Strategy Best Practices

**Use agent memories liberally**:
```
Agent: "REMEMBER key_architectural_decision: We chose microservices over monolith due to team autonomy requirements"
```
Memories survive session rotation and are injected into context on recall.

**Lean on specs**:
Specifications are injected fresh each turn, not accumulated in context. Keep authoritative information in specs rather than conversational messages.

**Break down large tasks**:
Instead of one 200-message planning session, break into phases with explicit checkpoints. Each checkpoint can be a natural session boundary.

**Monitor session health**:
Regularly check Dashboard > Conversation Sessions. If you see sessions with unusually high message counts (>80), consider compacting or investigating why.

**Coordinate with task lifecycle**:
- Start new tasks with fresh sessions when possible
- Compact after major milestones (review complete, merge done)
- Use task evidence to capture critical context that must survive rotation

---

## Monitoring & Observability

### Dashboard Overview

The **Dashboard** tab provides centralized monitoring across six key panels.

#### 1. Overview Panel

High-level system health:

- **Room Count**: Total collaboration rooms in workspace
- **Agent Count**: Active catalog + custom agents
- **Active Tasks**: Tasks in progress (not completed/cancelled)
- **Recent Events**: Last 10 significant events (task created, phase changed, etc.)

**Use case**: Quick health check — are agents working? Are tasks progressing?

#### 2. LLM Usage Panel

Token consumption and cost tracking:

| Metric | Description |
|--------|-------------|
| **Total Tokens** | Cumulative input + output tokens across all LLM calls |
| **Request Count** | Number of LLM API requests made |
| **Estimated Cost** | Dollar cost based on model pricing (USD) |
| **By Agent** | Breakdown of usage per agent |
| **By Room** | Breakdown of usage per room |

**Cost attribution**:
- Each agent's model is known (from catalog or override)
- Usage is tracked per-agent, per-request
- Costs calculated using current pricing tables for GPT-4, Claude, etc.

**Use case**: Budget monitoring, identifying expensive agents or rooms

> ⚠️ **GAP**: No cost alerts or budget thresholds — monitoring is passive only

#### 3. Agent Errors Panel

Real-time error log:

| Column | Description |
|--------|-------------|
| **Timestamp** | When error occurred |
| **Agent** | Agent that encountered the error |
| **Room** | Room context (or "System" for orchestrator errors) |
| **Error Message** | Exception message or error description |
| **Severity** | Info / Warning / Error / Critical |

**Common errors to watch**:
- `CopilotAuthException`: Auth token expired or invalid
- `CommandValidationException`: Agent tried to execute disallowed command
- `ToolExecutionException`: Tool (file read, git, etc.) failed
- `SessionRotationException`: Summary generation failed

**Filtering**: Click column headers to sort, use search box to filter by agent or message text.

> ⚠️ **GAP**: No error aggregation or pattern detection — must manually spot trends

#### 4. Command Audit Log

Complete history of command execution:

| Column | Description |
|--------|-------------|
| **Timestamp** | Command execution time |
| **Agent** | Agent that executed (or "Human" for manual commands) |
| **Room** | Execution context |
| **Command** | Command name (e.g., `READ_FILE`, `CREATE_TASK`) |
| **Status** | `success`, `failed`, `rejected` |
| **Duration** | Execution time in milliseconds |
| **Details** | Parameters and result summary |

**Audit use cases**:
- Security: Track who executed `SHELL` commands
- Debugging: Trace sequence of commands leading to an error
- Performance: Identify slow operations

**Export**: Audit log is backed by SQL database; query directly for reporting:
```sql
SELECT * FROM CommandExecutions 
WHERE AgentId = 'aristotle' AND Timestamp > datetime('now', '-1 day')
ORDER BY Timestamp DESC;
```

> ⚠️ **GAP**: No command history search UI — must query database directly

#### 5. Conversation Sessions Panel

Detailed session lifecycle tracking (covered in Session Management section above).

**Operational metrics**:
- Average session duration (created → archived)
- Average messages per session
- Rotation frequency (sessions created per hour)

**Use case**: Diagnosing context issues, verifying rotation is happening

#### 6. Server Instance History

Tracks server restarts and crashes:

| Column | Description |
|--------|-------------|
| **Instance ID** | Unique ID for each server instance |
| **Started** | Server start timestamp |
| **Stopped** | Server stop timestamp (if graceful) |
| **Reason** | Why server stopped: restart, crash, update, manual |
| **Uptime** | Duration instance was running |

**Crash detection**:
- Server startup checks for previous instance without graceful shutdown
- Crash recovery runs: closes abandoned breakout rooms, restores state

**Use case**: Tracking reliability, diagnosing instability

> ⚠️ **GAP**: No crash alerting — must check dashboard to discover crashes

### Real-Time Indicators

Visual feedback in the UI for live operations:

#### Agent Thinking Indicators

**Location**: Sidebar, next to each agent's name

**States**:
- **Idle** (no indicator): Agent has no pending turns
- **Thinking** (animated dots): Agent has received turn, waiting for LLM response
- **Typing** (typing indicator): Agent is processing tools, not yet responded

**Timing**:
- Typical LLM response: 2-10 seconds
- Complex tool usage: 10-30 seconds
- If thinking >60s: check Dashboard > Agent Errors

#### Phase Badge

**Location**: Toolbar, main room header

**Values**:
- `PLANNING` (blue): Planner agents active, engineers idle
- `IMPLEMENTATION` (green): Engineers executing tasks
- `REVIEW` (yellow): Reviewers evaluating work
- `MERGED` (purple): Task merged, back to planning

**Use case**: Know which agents should be active at a glance

#### Activity Feed (Timeline Panel)

**Location**: Timeline tab

**Event types**:
- Agent posted message
- Command executed
- Task created/approved/merged
- Session rotated
- Breakout room opened/closed

**Format**: Chronological, most recent first, auto-updates via SignalR

**Use case**: Real-time monitoring of system activity without switching tabs

#### SignalR Connection Status

**Location**: Bottom-right corner of UI

**States**:
- **Connected** (green dot): Live updates active
- **Reconnecting** (yellow dot): Connection lost, attempting reconnect
- **Disconnected** (red dot): No real-time updates, refresh needed

**Impact when disconnected**:
- Messages won't appear until manual refresh
- Agent thinking indicators won't update
- Must poll for status

#### Circuit Breaker Banner

**Location**: Top of screen, full-width alert

**Trigger**: Copilot SDK authentication fails repeatedly (3+ times in short window)

**Message**: "Copilot authentication failed. Please re-authenticate."

**Action**: Click "Re-authenticate" to refresh token

**Background**: Circuit breaker prevents hammering auth endpoint; banner appears when tripped

### Health Endpoint

Programmatic health check for monitoring systems:

```http
GET /api/health/instance

Response 200 OK:
{
  "instanceId": "inst_20240315_143022",
  "uptime": "PT2H15M33S",
  "executorStatus": "running",
  "workspacePath": "/home/user/agent-academy-workspace",
  "copilotAuthStatus": "valid",
  "agentCount": 5,
  "roomCount": 3,
  "activeTaskCount": 2,
  "databaseConnected": true
}
```

**Monitoring integration**:
- Use for uptime checks (200 = healthy, 5xx = unhealthy)
- Alert on `copilotAuthStatus: "expired"` or `executorStatus: "stopped"`
- Track uptime for SLA monitoring

> ⚠️ **GAP**: No Prometheus/OpenTelemetry export — must poll health endpoint

### Workspace Overview

**Location**: Overview tab (when workspace selected)

**Room-level stats**:
- Active agents in room
- Message count (current session)
- Task summary (active tasks, recent completions)
- Recent activity timeline

**Per-agent stats**:
- Current location (main room vs. breakout)
- Last activity timestamp
- Token usage in this workspace

**Use case**: Quick assessment of workspace health before diving into specific rooms

---

## Troubleshooting Guide

### Agent Not Responding

**Symptom**: You send a message, but agent doesn't reply or shows no thinking indicator.

#### Diagnostic Steps

**1. Check Agent Thinking Status**

Look at sidebar. Is agent showing "Thinking" (animated dots)?

- **Yes, thinking**: Agent is waiting for LLM response. Be patient (30-60s).
- **No indicator**: Agent may not have been triggered.

**2. Verify Phase Appropriateness**

Check the phase badge in toolbar:

- **PLANNING phase**: Only planner agents (Aristotle) respond to main room messages
- **IMPLEMENTATION phase**: Only engineers respond in task breakout rooms
- **REVIEW phase**: Only reviewers (Socrates) respond in review rooms

**Solution**: If wrong phase, inappropriate agents won't respond. This is expected behavior.

**3. Check Agent Errors**

Navigate to **Dashboard > Agent Errors**. Look for recent errors for this agent.

**Common errors**:
- `CopilotAuthException`: See "Auth Failures" section below
- `AgentConfigurationException`: Agent config invalid, check Settings > Agents
- `RateLimitException`: Too many requests, wait and retry

**4. Check Circuit Breaker**

Is there a banner at top of screen saying "Copilot authentication failed"?

- **Yes**: Copilot auth is broken, all agents will fail. Click "Re-authenticate".
- **No**: Auth is fine, issue is elsewhere.

**5. Try Direct Messaging**

Use the DM feature to inject a message directly to the agent:

1. Click agent name in sidebar
2. Type message in DM input
3. Send

DMs are injected into the agent's context on next turn, potentially "unsticking" the agent.

**6. Check Executor Status**

```http
GET /api/health/instance
```

Verify `executorStatus: "running"`. If `"stopped"`, the orchestrator isn't triggering agents.

**Solution**: Restart server via `RESTART_SERVER` command or manual restart.

### Session Seems Stale / Agent Repeating Itself

**Symptom**: Agent gives responses that ignore recent context, or repeats the same suggestion multiple times.

#### Diagnostic Steps

**1. Check Session Message Count**

Navigate to **Dashboard > Conversation Sessions**, find the room's active session.

- **Message count > 80**: Session is very large, context may be degraded
- **Message count 50-80**: Normal, but consider compacting if quality is poor
- **Message count < 50**: Probably not a session issue

**2. Trigger Manual Compaction**

Force session rotation immediately:

```http
POST /api/rooms/{roomId}/compact
```

Or use Commands tab > `COMPACT_SESSION` (if available).

**Effect**: Agents get fresh context with summary of previous discussion.

**3. Review Epoch Size Configuration**

Navigate to **Settings > Advanced > Conversation Epoch Thresholds**.

- **Current threshold > 80**: Consider lowering to 50-60
- **Current threshold < 30**: May be too small, causing excessive rotation

**4. Check Agent Memories**

Agent may have forgotten critical context. Review:

```http
GET /api/memories?agentId={agentId}
```

If critical decisions are missing, manually record them:

```
REMEMBER architectural_pattern: We decided on event-driven architecture for decoupling
```

**5. Consider Infinite Loop**

If agent is truly repeating the same action (not just similar suggestions), it may be stuck in a loop.

**For breakout rooms**: Stuck detection should auto-terminate after N unproductive rounds. Check Dashboard for termination events.

**For main room**: May need manual intervention — compact session or DM the agent with explicit new direction.

### Breakout Room Stuck

**Symptom**: Breakout room has been open for a long time (>30 minutes) with no progress.

#### Expected Behavior

**Stuck detection system**:
- Monitors breakout rooms for productivity signals (code changes, meaningful tool usage)
- After N rounds (typically 5-10) with no progress, auto-terminates the breakout
- Agent returns to main room with failure report

#### If Stuck Detection Didn't Fire

**1. Check Breakout Status**

```http
GET /api/rooms?includeArchived=false
```

Look for breakout rooms with `status: "active"` and old `createdAt` timestamp.

**2. Check Agent Errors**

Agent may have crashed inside the breakout, leaving it orphaned.

Navigate to **Dashboard > Agent Errors**, filter by agent and timestamp.

**3. Manual Termination**

If stuck detection failed, use crash recovery by executing the RESTART_SERVER command from the Commands tab, or trigger a supervised restart:

```http
POST /api/commands/execute
{"command": "RESTART_SERVER", "args": {}}
```

**Crash recovery process**:
- On startup, server scans for breakout rooms with old timestamps
- Closes abandoned breakouts
- Restores agents to main room

> ⚠️ **GAP**: No manual breakout termination endpoint — must restart server

**4. Review Task State**

If breakout was for a task, check task status:

```http
GET /api/tasks/{taskId}
```

If task is stuck in `IN_PROGRESS`, consider cancelling:

```
CANCEL_TASK <taskId> reason="Breakout room stuck, unable to complete"
```

### Auth Failures

**Symptom**: Banner says "Copilot authentication failed" or agents all fail with `CopilotAuthException`.

#### Understanding Token Lifecycle

**Token chain**:
1. **Cached token**: Server stores valid token in memory
2. **Token expiration**: Tokens expire after ~1 hour
3. **Refresh attempt**: Server tries to refresh using refresh token
4. **Refresh failure**: If refresh fails, circuit breaker trips
5. **Re-login prompt**: Frontend shows banner with re-login button

#### Resolution Steps

**1. Click Re-Authenticate**

The banner has a button. Clicking it:
- Redirects to Copilot auth flow
- Obtains new tokens
- Stores in server cache
- Clears circuit breaker

**2. Verify Token Refresh**

Check server logs for:
```
[INFO] CopilotAuthService: Token refreshed successfully
[WARN] CopilotAuthService: Token refresh failed, circuit breaker opened
```

**3. Check Copilot SDK Configuration**

Verify `appsettings.json`:
```json
{
  "CopilotSdk": {
    "ClientId": "your-client-id",
    "Authority": "https://github.com/login/oauth/authorize",
    "TokenEndpoint": "https://github.com/login/oauth/access_token"
  }
}
```

**4. Manual Token Reset**

If UI re-auth doesn't work, check the Copilot auth status:

```http
GET /api/auth/status
```

If the token is invalid, the frontend will show a re-login banner. Use `POST /api/auth/logout` to clear state and re-authenticate via UI.

#### Important: Auth Recovery Does NOT Restart Server

**Previous behavior** (pre-v0.8): Auth failure triggered server restart.

**Current behavior**: Auth recovery is in-place:
- Circuit breaker prevents hammering failed auth
- Agents queue turns until auth is restored
- Once re-authenticated, agents resume

**No data loss**: Conversations, tasks, state all preserved.

### Git Conflicts

**Symptom**: Task merge fails with "Merge conflict" error in Dashboard > Agent Errors.

#### Understanding Task Branches

**Branch model**:
- `develop`: Integration branch
- `task/{taskId}`: Task-specific feature branch

**Merge process**:
1. `APPROVE_TASK` triggers merge attempt
2. Server executes: `git checkout develop && git merge task/{taskId}`
3. If conflicts: merge aborted, task remains in `IN_REVIEW` phase

#### Resolution Steps

**1. Identify Conflicting Files**

Check server filesystem (SSH or file access required):

```bash
cd /path/to/workspace
git status
```

Look for `Unmerged paths` section.

**2. Manual Resolution**

**Option A: Resolve conflicts manually**:
```bash
# Edit conflicting files, resolve markers
git add <resolved-files>
git commit -m "Resolve merge conflicts in task XYZ"
git checkout develop
git merge task/{taskId}
```

**Option B: Reject task and revert**:
```
REQUEST_CHANGES <taskId> reason="Merge conflicts with recent develop changes, please rebase"
```

Then cancel or reset the task:
```
CANCEL_TASK <taskId>
```

**3. Prevent Future Conflicts**

- Keep `develop` branch stable; merge tasks promptly
- Use smaller tasks to reduce conflict surface area
- Review task diffs before approval to spot potential conflicts

> ⚠️ **GAP**: No in-UI conflict resolution — must use server filesystem access

### Database Issues

**Symptom**: Server fails to start with "Database locked" or "Unable to open database" error.

#### Database Architecture

**Location**: SQLite database at workspace root: `{workspace}/agent-academy.db`

**Contents**:
- Rooms, agents, messages, sessions
- Tasks, commands, memories
- Settings, templates

**Permissions**: Server process must have read/write access to database file and directory.

#### Resolution Steps

**1. Check File Permissions**

```bash
ls -la /path/to/workspace/agent-academy.db
```

Verify:
- File is owned by user running server process
- Permissions are at least `644` (preferably `600` for security)
- Directory permissions allow write (for SQLite journal files)

**2. Check for Lock Files**

SQLite creates lock files during operation:

```bash
ls -la /path/to/workspace/agent-academy.db*
```

Look for `agent-academy.db-shm`, `agent-academy.db-wal` files.

**If found and server is NOT running**: Delete them:
```bash
rm agent-academy.db-shm agent-academy.db-wal
```

**3. Database Corruption**

If database is corrupted (rare), you'll see errors like "database disk image is malformed".

**Recovery**:
```bash
# Backup corrupted DB
mv agent-academy.db agent-academy.db.corrupt

# Server will create new DB on next start
# Re-onboard workspace to rebuild state
```

**Data loss**: All history, tasks, memories lost. Only do this as last resort.

**4. Verify SQLite Version**

Server requires SQLite 3.35+:

```bash
sqlite3 --version
```

If outdated, update SQLite on host system.

> ⚠️ **GAP**: No database backup/restore workflow — manual file copy only

---

## Configuration Reference

### Agent Configuration

#### Agent Catalog

**Location**: `src/AgentAcademy.Server/Config/agents.json`

**Structure**:
```json
{
  "agents": [
    {
      "id": "aristotle",
      "name": "Aristotle",
      "role": "planner",
      "defaultModel": "gpt-4-turbo",
      "systemPrompt": "You are Aristotle, the master planner...",
      "capabilities": ["CREATE_TASK", "PLANNING"],
      "permissions": ["CREATE_TASK", "READ_FILE", "SEARCH_CODE"]
    }
  ]
}
```

**Fields**:
- `id`: Unique agent identifier (used in API calls, logs)
- `name`: Display name in UI
- `role`: Organizational role (planner, engineer, reviewer, qa, writer)
- `defaultModel`: Default LLM model (can be overridden per-workspace)
- `systemPrompt`: Base instructions for agent behavior
- `capabilities`: High-level capabilities (informational)
- `permissions`: Commands agent is allowed to execute

#### Catalog Agents Reference

| Agent ID | Name | Role | Key Permissions |
|----------|------|------|-----------------|
| `planner-1` | Aristotle | Planner | `CREATE_TASK`, `APPROVE_TASK`, `MERGE_TASK`, `REMEMBER`, planning commands |
| `architect-1` | Archimedes | Architect | `READ_FILE`, `SEARCH_CODE`, `REMEMBER`, architecture commands |
| `engineer-1` | Hephaestus | Backend Engineer | `READ_FILE`, `SEARCH_CODE`, `SHELL`, `COMMIT_CHANGES`, code-write commands |
| `engineer-2` | Athena | Frontend Engineer | `READ_FILE`, `SEARCH_CODE`, `SHELL`, `COMMIT_CHANGES`, code-write commands |
| `reviewer-1` | Socrates | Reviewer | `APPROVE_TASK`, `REQUEST_CHANGES`, `READ_FILE`, `SEARCH_CODE` |
| `writer-1` | Thucydides | Technical Writer | `READ_FILE`, `SEARCH_CODE`, spec management, code commands |

**Note**: Default models are defined in `agents.json`. Override per-workspace via Settings > Agents.

#### Per-Workspace Overrides

**Via UI**:
1. Navigate to **Settings > Agents**
2. Select agent to configure
3. Override fields:
   - **Model**: Choose different LLM model
   - **Custom Instructions**: Additional instructions appended to system prompt
   - **Instruction Template**: Select reusable template (see below)
4. Save changes

**Via API**:
```http
PUT /api/agents/{agentId}/config
Content-Type: application/json

{
  "modelOverride": "claude-3-opus",
  "customInstructions": "Always explain your reasoning step-by-step.",
  "instructionTemplateId": "verbose-explainer"
}
```

**Precedence**: Workspace override > Catalog default

**Scope**: Overrides apply only to specific workspace, don't affect other workspaces.

#### Instruction Templates

**Purpose**: Reusable prompt fragments for common behavioral modifications.

**Location**: **Settings > Instruction Templates**

**Creating a template**:
1. Click "New Template"
2. Enter:
   - **Name**: Template identifier (e.g., `security-focused`)
   - **Description**: What this template does
   - **Content**: Prompt text to append to system prompt
3. Save

**Example template** (`security-focused`):
```
When reviewing code or creating tasks, prioritize security concerns:
- Flag potential injection vulnerabilities
- Check for authentication/authorization gaps
- Verify input validation
- Highlight cryptographic weaknesses
```

**Applying to agent**:
- Select template in agent configuration
- Template content is appended to agent's system prompt
- Multiple agents can share same template

**Use cases**:
- Consistent behavior across multiple agents (all engineers use "code-style" template)
- Domain-specific expertise injection (add "healthcare-compliance" template to reviewers)
- Experimentation (A/B test "verbose" vs. "concise" templates)

### Notification Providers

Agent Academy supports pluggable notification providers for sending agent messages externally.

#### Console Provider

**Configuration**: None required (always available)

**Behavior**: Logs all agent messages to server console output

**Use case**: Development, debugging, server log aggregation

**Format**:
```
[INFO] [aristotle @ main-room] Agent message: Let me analyze the requirements...
```

#### Discord Provider

**Configuration** (in `appsettings.json`):
```json
{
  "Notifications": {
    "Discord": {
      "Enabled": true,
      "WebhookUrl": "https://discord.com/api/webhooks/...",
      "PerAgentAvatars": true
    }
  }
}
```

**Fields**:
- `Enabled`: Set to `true` to activate
- `WebhookUrl`: Discord webhook URL (create via Discord channel settings)
- `PerAgentAvatars`: If `true`, each agent gets unique avatar/username in Discord

**Features**:
- Rich embeds with agent name, room context
- Code blocks preserved with syntax highlighting
- Threaded replies for breakout rooms

**Use case**: Team collaboration, async monitoring, mobile notifications

#### Slack Provider

**Configuration** (in `appsettings.json`):
```json
{
  "Notifications": {
    "Slack": {
      "Enabled": true,
      "WebhookUrl": "https://hooks.slack.com/services/...",
      "Channel": "#agent-academy",
      "BlockKit": true
    }
  }
}
```

**Fields**:
- `Enabled`: Set to `true` to activate
- `WebhookUrl`: Slack webhook URL (create via Slack app settings)
- `Channel`: Target channel (optional, defaults to webhook's default)
- `BlockKit`: Use Slack Block Kit for rich formatting

**Features**:
- Block Kit formatted messages (if enabled)
- Agent name as username, role as emoji
- Threaded messages for context

**Use case**: Enterprise team collaboration, integration with existing Slack workflows

> ⚠️ **GAP**: No MS Teams, email, or custom webhook providers

### System Settings

**Location**: **Settings > Advanced** or `PUT /api/settings`

**Available settings**:

#### Conversation Epoch Thresholds

```json
{
  "epochSize": {
    "default": 50,
    "planning": 40,
    "breakout": 30,
    "review": 35
  }
}
```

**Controls**: Message count before session rotation (see Session Management section).

#### Stuck Detection Thresholds

```json
{
  "stuckDetection": {
    "enabled": true,
    "unproductiveRounds": 5,
    "timeoutMinutes": 30
  }
}
```

**Controls**: Breakout room stuck detection behavior.

#### LLM Rate Limiting

```json
{
  "rateLimits": {
    "requestsPerMinute": 60,
    "tokensPerHour": 100000
  }
}
```

**Controls**: Self-imposed rate limits to prevent runaway costs.

> ⚠️ **GAP**: No config validation — invalid settings silently fail or use defaults

### Consultant API Configuration

**Purpose**: External API for programmatic workspace/task management (see section 04).

**Configuration** (in `appsettings.json`):
```json
{
  "ConsultantApi": {
    "Enabled": true,
    "SharedSecret": "your-secret-key-here",
    "AllowedOrigins": ["https://your-external-service.com"]
  }
}
```

**Fields**:
- `Enabled`: Set to `true` to activate Consultant API endpoints
- `SharedSecret`: Shared secret for HMAC-based authentication
- `AllowedOrigins`: CORS origins allowed to call Consultant API

**Authentication header**:
```http
X-Consultant-Key: your-secret-key-here
```

**Security**: Keep `SharedSecret` in environment variables, not committed to source:
```bash
export CONSULTANT_API_SECRET="your-secret-key-here"
```

Reference in `appsettings.json`:
```json
{
  "ConsultantApi": {
    "SharedSecret": "${CONSULTANT_API_SECRET}"
  }
}
```

### Server Configuration

**Primary config file**: `appsettings.json` in server root.

**Key sections**:

#### Database

```json
{
  "Database": {
    "Provider": "sqlite",
    "ConnectionString": "Data Source=agent-academy.db"
  }
}
```

**Note**: Database is always at workspace root; `ConnectionString` is relative to workspace path.

#### CORS

```json
{
  "Cors": {
    "AllowedOrigins": [
      "http://localhost:3000",
      "https://agent-academy.yourcompany.com"
    ]
  }
}
```

**Required**: Frontend origin must be in list for UI to work.

#### Copilot SDK

```json
{
  "CopilotSdk": {
    "ClientId": "your-github-app-client-id",
    "Authority": "https://github.com/login/oauth/authorize",
    "TokenEndpoint": "https://github.com/login/oauth/access_token",
    "Scopes": ["user", "repo"]
  }
}
```

**Required**: For Copilot SDK authentication (agents use GitHub Copilot).

#### Supervised Restart

**Wrapper script** (for production deployments):

```bash
#!/bin/bash
# run-agent-academy.sh

while true; do
    dotnet AgentAcademy.Server.dll
    EXIT_CODE=$?
    
    if [ $EXIT_CODE -eq 75 ]; then
        echo "Supervised restart requested, restarting..."
        sleep 2
    else
        echo "Server exited with code $EXIT_CODE, stopping."
        exit $EXIT_CODE
    fi
done
```

**How it works**:
- `RESTART_SERVER` command makes server exit with code `75`
- Wrapper script detects code `75` and restarts
- Any other exit code stops the loop (prevents crash loops)

**Use case**: In-place updates, configuration reloads, recovery from certain error states.

---

## Command Reference (Human-Executable)

These commands are available from the **Commands** tab in the UI. Humans can execute them manually to perform administrative actions or assist agents.

### Code/Workspace Commands

#### READ_FILE

**Description**: Read a file from the workspace.

**Syntax**: `READ_FILE <file-path>`

**Example**: `READ_FILE src/AgentAcademy.Core/Models/Task.cs`

**Returns**: File contents (up to max size limit)

**Permissions**: All agents, all humans

**Use case**: Inspecting files without using an agent, sharing code context.

---

#### SEARCH_CODE

**Description**: Search code with pattern matching (regex or literal).

**Syntax**: `SEARCH_CODE pattern:<pattern> [scope:<directory>]`

**Example**: `SEARCH_CODE pattern:ITaskService scope:src/`

**Returns**: List of files and line numbers where pattern appears.

**Permissions**: All agents, all humans

**Use case**: Finding symbol usages, identifying where to make changes.

---

#### LIST_FILES

**Description**: List directory contents.

**Syntax**: `LIST_FILES <directory-path>`

**Example**: `LIST_FILES src/AgentAcademy.Core/Services`

**Returns**: Files and subdirectories in directory.

**Permissions**: All agents, all humans

**Use case**: Exploring project structure, verifying files exist.

---

#### SHELL

**Description**: Execute shell command (allowlisted commands only).

**Syntax**: `SHELL <command> [args...]`

**Example**: `SHELL dotnet build`

**Returns**: Command output (stdout + stderr).

**Permissions**: Engineers, QA agents; humans with admin role

**Security**: Only allowlisted commands (e.g., `dotnet`, `git`, `npm`, `ls`) are permitted. Arbitrary shell execution is blocked.

**Use case**: Running builds, tests, git operations manually.

---

### Task Management Commands

#### CREATE_TASK

**Description**: Create a new task (restricted to planner role).

**Syntax**: `CREATE_TASK title:"<title>" description:"<description>" [specSections:<sections>]`

**Example**: `CREATE_TASK title:"Implement user authentication" description:"Add JWT-based auth to API" specSections:3.2,3.3`

**Returns**: Task ID and initial state.

**Permissions**: Planner agents (Aristotle), humans with admin role

**Effect**: Creates task in `PLANNING` phase, assigns to engineer.

**Use case**: Manually creating tasks when planner isn't available or for testing.

---

#### APPROVE_TASK

**Description**: Approve a task in review phase.

**Syntax**: `APPROVE_TASK <task-id>`

**Example**: `APPROVE_TASK task_20240315_143022`

**Returns**: Confirmation message.

**Permissions**: Reviewer agents (Socrates), humans with reviewer role

**Effect**: Transitions task to `APPROVED`, triggers merge to `develop`.

**Use case**: Manual approval when reviewer is stuck or for urgent merges.

---

#### REQUEST_CHANGES

**Description**: Request changes on a task in review.

**Syntax**: `REQUEST_CHANGES <task-id> reason:"<reason>"`

**Example**: `REQUEST_CHANGES task_20240315_143022 reason:"Missing unit tests for new service"`

**Returns**: Confirmation message.

**Permissions**: Reviewer agents (Socrates), humans with reviewer role

**Effect**: Transitions task back to `IMPLEMENTATION`, notifies engineer.

**Use case**: Manual review when reviewer is unavailable or for specific feedback.

---

#### MERGE_TASK

**Description**: Merge approved task to develop branch.

**Syntax**: `MERGE_TASK <task-id>`

**Example**: `MERGE_TASK task_20240315_143022`

**Returns**: Merge commit SHA and confirmation.

**Permissions**: Reviewer agents (Socrates), humans with admin role

**Effect**: Executes `git merge task/{taskId}` into `develop`.

**Use case**: Manual merge when automatic merge fails or for emergency fixes.

---

#### CANCEL_TASK

**Description**: Cancel a task (any phase).

**Syntax**: `CANCEL_TASK <task-id> [reason:"<reason>"]`

**Example**: `CANCEL_TASK task_20240315_143022 reason:"Requirements changed, no longer needed"`

**Returns**: Confirmation message.

**Permissions**: Planner agents, humans with admin role

**Effect**: Transitions task to `CANCELLED`, closes breakout rooms, deletes task branch.

**Use case**: Aborting stuck tasks, handling requirement changes.

---

### Git/GitHub Commands

#### COMMIT_CHANGES

**Description**: Commit staged changes to current task branch.

**Syntax**: `COMMIT_CHANGES message:"<commit-message>"`

**Example**: `COMMIT_CHANGES message:"feat: Add JWT authentication service"`

**Returns**: Commit SHA and summary.

**Permissions**: Engineer agents, humans with developer role

**Effect**: Executes `git commit -m "<message>"` on task branch.

**Use case**: Manual commits when agent is stuck or for quick fixes.

---

#### CREATE_PR

**Description**: Create GitHub pull request for task branch.

**Syntax**: `CREATE_PR <task-id> [title:"<title>"] [body:"<description>"]`

**Example**: `CREATE_PR task_20240315_143022 title:"Add user authentication"`

**Returns**: PR number and URL.

**Permissions**: Engineer agents, humans with developer role

**Effect**: Creates GitHub PR from task branch to `develop`.

**Use case**: Manual PR creation when task workflow requires it.

> ⚠️ **GAP**: Requires GitHub integration configured (token, repo access)

---

#### MERGE_PR

**Description**: Merge GitHub pull request.

**Syntax**: `MERGE_PR <pr-number> [method:merge|squash|rebase]`

**Example**: `MERGE_PR 42 method:squash`

**Returns**: Merge commit SHA and confirmation.

**Permissions**: Reviewer agents, humans with admin role

**Effect**: Merges PR on GitHub, updates local `develop` branch.

**Use case**: Manual merge when automated workflow fails.

> ⚠️ **GAP**: Requires GitHub integration configured

---

### Memory Commands

#### REMEMBER

**Description**: Store a memory for an agent.

**Syntax**: `REMEMBER <key>: <value>`

**Example**: `REMEMBER architectural_decision: We chose microservices over monolith for team autonomy`

**Returns**: Memory ID and confirmation.

**Permissions**: All agents (for their own memories), humans with admin role

**Effect**: Stores memory in agent's memory store; recalled on future turns.

**Use case**: Persisting critical context across session rotations.

---

#### RECALL

**Description**: Search agent memories by keyword or pattern.

**Syntax**: `RECALL <query>`

**Example**: `RECALL architectural`

**Returns**: List of matching memories with keys and values.

**Permissions**: All agents (their own memories), humans (all memories)

**Use case**: Retrieving previously stored decisions or facts.

---

#### LIST_MEMORIES

**Description**: List all memories for an agent.

**Syntax**: `LIST_MEMORIES [agent-id]`

**Example**: `LIST_MEMORIES aristotle`

**Returns**: All memories for specified agent (or current agent if omitted).

**Permissions**: All agents (their own memories), humans (any agent)

**Use case**: Auditing what an agent remembers, debugging memory issues.

---

#### FORGET

**Description**: Remove a memory.

**Syntax**: `FORGET <memory-id>`

**Example**: `FORGET mem_abc123`

**Returns**: Confirmation message.

**Permissions**: Agent who created memory, humans with admin role

**Effect**: Deletes memory; no longer recalled.

**Use case**: Removing outdated or incorrect memories.

---

### System Commands

#### RESTART_SERVER

**Description**: Trigger supervised restart of server.

**Syntax**: `RESTART_SERVER [reason:"<reason>"]`

**Example**: `RESTART_SERVER reason:"Apply configuration changes"`

**Returns**: Confirmation message (server will disconnect shortly).

**Permissions**: Humans with admin role only

**Effect**: Server exits with code `75`, wrapper script restarts (if configured).

**Use case**: Applying config changes, recovering from errors, forcing crash recovery.

**Warning**: All active connections (websockets) will disconnect. Clients must reconnect.

---

#### RECORD_EVIDENCE

**Description**: Record evidence for task (for auditing/compliance).

**Syntax**: `RECORD_EVIDENCE <task-id> type:<type> data:"<data>"`

**Example**: `RECORD_EVIDENCE task_20240315_143022 type:test_output data:"All 47 tests passed"`

**Returns**: Evidence ID and confirmation.

**Permissions**: All agents, humans with developer role

**Effect**: Stores evidence in task record; visible in task details.

**Use case**: Recording test results, build outputs, manual verification steps.

---

#### CHECK_GATES

**Description**: Check if task phase gates are satisfied.

**Syntax**: `CHECK_GATES <task-id>`

**Example**: `CHECK_GATES task_20240315_143022`

**Returns**: Gate statuses (passed/failed) with reasons.

**Permissions**: All agents, all humans

**Effect**: No state change; informational only.

**Use case**: Debugging why task won't transition phases, verifying requirements met.

---

#### LINK_TASK_TO_SPEC

**Description**: Link a task to specification section(s).

**Syntax**: `LINK_TASK_TO_SPEC <task-id> sections:<section-ids>`

**Example**: `LINK_TASK_TO_SPEC task_20240315_143022 sections:3.2,3.3,4.1`

**Returns**: Confirmation message.

**Permissions**: Planner agents, humans with admin role

**Effect**: Stores spec linkage; used for spec injection and verification.

**Use case**: Manually linking tasks when planner didn't do it, correcting links.

---

## Gap Analysis for Operations

This section compiles functionality gaps identified throughout the operations domain. These represent areas where Agent Academy currently lacks features that would improve operational maturity.

### Monitoring & Observability Gaps

> ⚠️ **GAP**: **No log streaming or live tail** — Server logs are only accessible via server filesystem or console output. No UI-based log viewer, no real-time streaming, no filtering by severity or agent.

> ⚠️ **GAP**: **No resource usage alerts or budgets** — LLM usage is tracked but there are no alerts when costs exceed thresholds, no per-room or per-agent budgets, no cost forecasting.

> ⚠️ **GAP**: **No agent performance benchmarking** — No metrics on average response time, success rate, task completion rate per agent. No A/B testing framework for agent configurations.

> ⚠️ **GAP**: **No Prometheus/OpenTelemetry export** — Health endpoint exists but no standardized metrics export for integration with monitoring systems like Grafana, Datadog, etc.

> ⚠️ **GAP**: **No crash alerting** — Server crashes are recorded in Instance History but there's no proactive alerting (email, Slack, PagerDuty). Must check dashboard manually.

> ⚠️ **GAP**: **No error aggregation or pattern detection** — Errors are logged individually; no grouping by type, no automatic pattern detection (e.g., "10 similar errors in last hour").

### Session & Context Management Gaps

> ⚠️ **GAP**: **No UI button for manual compaction** — Must use API directly via Tools or Consultant API. No one-click "refresh context" button in UI.

> ⚠️ **GAP**: **No session comparison or diff** — Can't compare two sessions to see what changed, can't visualize conversation flow across rotations.

> ⚠️ **GAP**: **No session export/import** — Can't export session history for external analysis, can't import historical sessions from other workspaces.

### Task & Workflow Gaps

> ⚠️ **GAP**: **No manual breakout termination endpoint** — If stuck detection fails, must restart entire server to close orphaned breakout rooms. No targeted intervention.

> ⚠️ **GAP**: **No command history search UI** — Audit log exists in database but no search/filter UI. Must query SQL directly.

> ⚠️ **GAP**: **No in-UI git conflict resolution** — Conflicts require server filesystem access and manual git commands. No conflict visualization or merge tool.

### Configuration & Administration Gaps

> ⚠️ **GAP**: **No config validation** — Invalid settings in `appsettings.json` or via Settings API silently fail or use defaults. No validation errors surfaced to user.

> ⚠️ **GAP**: **No backup/restore workflow** — No automated database backups, no UI for backup/restore, no disaster recovery documentation. Manual file copy only.

> ⚠️ **GAP**: **No workspace migration between hosts** — Can't export workspace (DB + config + git state) and import on different server. Manual file transfer only.

> ⚠️ **GAP**: **No multi-workspace comparison** — Can't view stats across multiple workspaces side-by-side, can't aggregate usage, can't compare configurations.

### Automation & Scheduling Gaps

> ⚠️ **GAP**: **No scheduled operations** — No cron-like scheduling for auto-compaction, auto-cleanup of old sessions, periodic health checks, automated backups.

> ⚠️ **GAP**: **No auto-scaling or load management** — No throttling when system is under heavy load, no queuing for LLM requests, no graceful degradation.

### Integration Gaps

> ⚠️ **GAP**: **No MS Teams, email, or custom webhook notification providers** — Only Console, Discord, Slack supported. No pluggable provider interface for custom integrations.

> ⚠️ **GAP**: **No GitHub integration for PR workflow** — `CREATE_PR` and `MERGE_PR` commands exist but require manual GitHub token configuration. No OAuth flow, no automatic PR status sync.

> ⚠️ **GAP**: **No external issue tracker integration** — No Jira, Linear, GitHub Issues sync. Tasks are isolated within Agent Academy.

### Documentation Gaps

> ⚠️ **GAP**: **No disaster recovery runbook** — No documented procedures for database corruption, catastrophic failures, data loss scenarios.

> ⚠️ **GAP**: **No performance tuning guide** — No recommendations for optimal epoch sizes, rate limits, or agent configurations based on workload characteristics.

> ⚠️ **GAP**: **No security hardening checklist** — No documented best practices for securing Consultant API, protecting secrets, network isolation, etc.

---

**Summary**: Agent Academy provides strong operational foundations but lacks enterprise-grade features in monitoring, automation, and disaster recovery. Most gaps are addressable through planned feature development or documented manual procedures.

