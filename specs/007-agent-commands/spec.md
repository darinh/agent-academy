# 007 — Agent Command System

## Purpose
Defines a unified command pipeline through which agents interact with the platform, codebase, and each other. Every agent action — reading files, moving between rooms, sending messages, managing tasks — flows through a structured envelope with authorization, audit trails, and consistent error handling.

> **Status: Implemented (Phase 1A)** — Command envelope, parser, pipeline, authorization, audit trail, and Phase 1A handlers (LIST_ROOMS, LIST_AGENTS, LIST_TASKS, READ_FILE, SEARCH_CODE) are implemented. Memory commands (REMEMBER, RECALL, LIST_MEMORIES, FORGET) are implemented. Runs in parallel with existing free-text parsing.

## Motivation
Today, agents have no formalized way to interact with the platform. The orchestrator parses free-text blocks like `TASK ASSIGNMENT:` from agent responses. This creates:
- No audit trail for agent actions
- No authorization model (any agent can do anything)
- No structured error handling
- No discoverability (agents must remember syntax from prompts)
- No way for agents to read platform state (rooms, tasks, agent locations)

## Architecture

### Command Envelope

Every command flows through a stable envelope from day 1:

```json
{
  "command": "READ_FILE",
  "args": { "path": "src/Program.cs" },
  "status": "success",
  "result": { "content": "...", "lines": 42 },
  "error": null,
  "correlationId": "cmd-a1b2c3",
  "timestamp": "2026-03-28T12:00:00Z",
  "executedBy": "software-engineer-1"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `command` | string | Command name (SCREAMING_SNAKE) |
| `args` | object | Command-specific parameters |
| `status` | `"success"` \| `"error"` \| `"denied"` | Outcome |
| `result` | object? | Command-specific return data (null on error) |
| `error` | string? | Human-readable error message |
| `correlationId` | string | Unique ID for audit linkage |
| `timestamp` | DateTime | ISO 8601 execution time |
| `executedBy` | string | Agent ID that issued the command |

### Pipeline

```
Agent Response (text) → Command Parser → Authorization → Execution → Audit Event → Response
```

Each stage:
1. **Command Parser**: Extracts structured commands from agent text. Commands use `COMMAND_NAME: args` syntax in agent responses. Parser returns remaining text + extracted commands.
2. **Authorization**: Checks agent permissions against command + args. Returns `denied` if unauthorized.
3. **Execution**: Dispatches to the appropriate handler. Read commands are side-effect-free. Write commands are idempotent where possible.
4. **Audit Event**: Every command execution is recorded as an `ActivityEvent` with the full envelope.
5. **Response**: Result is injected into the agent's next context turn.

### Permission Model

Per-agent permission boundaries:

| Agent | Role | Allowed | Denied |
|-------|------|---------|--------|
| Aristotle | Planner | All task/room management, read all | Code execution |
| Archimedes | Architect | Read all, spec commands | Code execution |
| Hephaestus | SoftwareEngineer | File read/write, build, test, git | Spec write, task approve |
| Prometheus | SoftwareEngineer | Same as Hephaestus | Same |
| Socrates | Reviewer | Read all, approve/reject tasks | File write, code execution |
| Thucydides | TechnicalWriter | Spec read/write, file read | Code execution, task approve |

**Escalation rules:**
- Tighten standards → Socrates review only
- Relax standards → Human approval required
- Self-modification → Socrates + Human approval
- Socrates cannot modify own review standards

## Command Reference

### Tier 1 — Critical Path

#### Phase 1A: Formalized Read Operations
These formalize existing capabilities with audit trails and structured output.

| Command | Args | Returns | Side Effects |
|---------|------|---------|-------------|
| `READ_FILE` | `path`, `startLine?`, `endLine?` | File content, line count | Audit event |
| `SEARCH_CODE` | `query`, `path?`, `glob?` | Matching lines with file/line refs | Audit event |
| `LIST_ROOMS` | — | All rooms: id, name, status, phase, participant count, message count, active task | Audit event |
| `LIST_AGENTS` | — | All agents: id, name, role, location (room/workspace), state, active task item | Audit event |
| `LIST_TASKS` | `status?`, `assignee?` | Tasks with status, assignee, dependencies, acceptance criteria, spec links, review state | Audit event |

#### Phase 1B: Structured State Management

| Command | Args | Returns | Side Effects |
|---------|------|---------|-------------|
| `LINK_TASK_TO_SPEC` | `taskId`, `specSection` | Confirmation + link record | Creates bidirectional link |
| `SHOW_UNLINKED_CHANGES` | `since?` | Tasks/commits without spec links | Audit event |
| `APPROVE_TASK` | `taskId`, `findings?` | Confirmation | Updates task status, records reviewer |
| `REQUEST_CHANGES` | `taskId`, `findings` | Confirmation | Updates task status, creates feedback |
| `SHOW_REVIEW_QUEUE` | — | Tasks awaiting review | Audit event |
| `CLAIM_TASK` | `taskId` | Confirmation | Assigns agent, prevents duplicate work |
| `RELEASE_TASK` | `taskId` | Confirmation | Unassigns agent |
| `UPDATE_TASK` | `taskId`, `status?`, `blocker?`, `note?` | Confirmation | Updates task state |

#### Phase 1C: Verification

| Command | Args | Returns | Side Effects |
|---------|------|---------|-------------|
| `RUN_BUILD` | — | Build output, exit code, duration | Runs `dotnet build` |
| `RUN_TESTS` | `scope?` (`all`, `frontend`, `backend`, `file:path`) | Test results, pass/fail counts | Runs test suite |
| `SHOW_DIFF` | `taskId?`, `branch?`, `agentId?` | Git diff output | Audit event |
| `GIT_LOG` | `file?`, `since?`, `count?` | Commit history | Audit event |

#### Phase 1D: Communication

| Command | Args | Returns | Side Effects |
|---------|------|---------|-------------|
| `DM` | `recipient` (agentId or `@Human`), `message` | Delivery confirmation | Creates DM record, audit event |
| `ROOM_HISTORY` | `roomId`, `count?` | Recent messages from any room | Audit event (no movement) |

#### Phase 1E: Navigation

| Command | Args | Returns | Side Effects |
|---------|------|---------|-------------|
| `MOVE_TO_ROOM` | `roomId` | Confirmation + new room context | Updates agent location |

### Tier 2 — Full Autonomy

#### Room Management
`RETURN_TO_MAIN`, `INVITE_TO_ROOM`, `CREATE_ROOM`, `CLOSE_ROOM`, `RESTORE_ROOM`, `ROOM_TOPIC`

#### Communication
`MENTION_TASK_OWNER`, `BROADCAST_TO_ROOM`

#### Task Management
`TASK_STATUS`, `SHOW_DEPENDENCIES`, `MARK_BLOCKED`, `SHOW_TASK_HISTORY`, `REQUEST_REVIEW`, `SHOW_DECISIONS`

#### Code & Spec
`OPEN_SPEC`, `SEARCH_SPEC`, `OPEN_COMPONENT`, `FIND_REFERENCES`

#### Backend Execution
`RUN_FRONTEND_BUILD`, `RUN_TYPECHECK`, `RUN_SERVER`, `CALL_ENDPOINT`, `TAIL_LOGS`, `SHOW_CONFIG`

#### Data & Operations
`QUERY_DB`, `RUN_MIGRATIONS`, `SHOW_MIGRATION_STATUS`, `HEALTHCHECK`, `SHOW_ACTIVE_CONNECTIONS`

#### Audit & Debug
`SHOW_AUDIT_EVENTS`, `SHOW_LAST_ERROR`, `TRACE_REQUEST`, `LIST_FEATURE_FLAGS`, `RETRY_FAILED_JOB`

### Tier 3 — Quality of Life

#### Spec Verification
`VERIFY_SPEC_SECTION`, `COMPARE_SPEC_TO_CODE`, `DETECT_ORPHANED_SECTIONS`

#### Frontend/UX
`PREVIEW_UI`, `CAPTURE_SCREENSHOT`, `COMPARE_SCREENSHOTS`, `SHOW_ROUTES`

#### Context
`HANDOFF_SUMMARY`, `WHOAMI`, `PLATFORM_STATUS`

## Agent Memory System

### Purpose
Persistent per-agent key-value store for learned knowledge across sessions.

### Commands

| Command | Args | Returns |
|---------|------|---------|
| `REMEMBER` | `category`, `key`, `value` | Confirmation |
| `RECALL` | `category?`, `key?`, `query?` | Matching memories |
| `LIST_MEMORIES` | `category?` | All memories (optionally filtered) |
| `FORGET` | `key` | Confirmation |

### Categories by Role

| Category | Used By | Example |
|----------|---------|---------|
| `decision` | Planner | "We chose SQLite over Postgres for simplicity" |
| `lesson` | All | "EF Core requires explicit Include() for navigation" |
| `pattern` | Architect, Engineer | "Services use constructor DI, never static" |
| `preference` | All | "User prefers conventional commits" |
| `invariant` | Reviewer | "All public APIs return ProblemDetails on error" |
| `risk` | Reviewer, Planner | "CopilotExecutor is single-user token model" |
| `gotcha` | Engineer | "Griffel doesn't support ::after with borders" |
| `incident` | All | "Deleted wrong room in session 0a4af9ed" |
| `constraint` | All | "No force-push, no branch delete" |
| `finding` | Reviewer | "BuildParticipants had N+1 query" |
| `spec-drift` | TechnicalWriter | "StubExecutor spec was aspirational, not factual" |
| `mapping` | Architect | "MessageEntity → ChatEnvelope via BuildChatEnvelope()" |
| `verification` | Reviewer | "Always check double-close on soft-deleted entities" |
| `gap-pattern` | All | "Frontend types often lag behind server models" |

### Interfaces

```csharp
public record AgentMemory(
    string AgentId,
    string Category,
    string Key,
    string Value,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);
```

**Persistence**: New `AgentMemoryEntity` table, keyed by `(AgentId, Key)`. Loaded into agent context at prompt-build time. Queryable via `RECALL`.

## Agent Self-Modification

### Model
Proposal-based with external approval. No agent can unilaterally modify itself or others.

### Command

```
PROPOSE_AGENT_UPDATE:
  Agent: <target-agent>
  Section: <startup-prompt|capabilities|tools|model>
  Change: <description of change>
  Rationale: <why this improves the agent>
```

### Approval Rules

| Change Type | Required Approvers |
|-------------|-------------------|
| Self-change | Socrates + Human |
| Change to another agent | Target agent + Socrates |
| Tighten standards | Socrates only |
| Relax standards | Human required |
| Modify Socrates' review standards | Human only |

### Audit
- Full diff of before/after
- Rationale recorded
- Rollback capability via `REVERT_AGENT_UPDATE: <change-id>`

## Private Communication (DM)

### Command
`DM @Human: <message>` — Available to ALL agents.

### Use Cases
- Escalation of blocking issues
- Architectural risk alerts
- UX ambiguity that needs human judgment
- Security concerns
- Spec disputes requiring human tiebreaker
- Review independence (Socrates flagging concerns privately)
- Platform feedback and feature requests

### Principles
- Default communication is open (in rooms)
- DMs are the exception, not the norm
- Audit metadata is logged (who, when, that a DM occurred) but content is not visible to other agents
- Human can see all DMs (agents are not told this)

## Safety & Operational Constraints

### Guardrails
- **Dry-run mode**: Side-effecting commands support `dryRun: true` returning what would happen
- **Confirmation**: Destructive actions (CLOSE_ROOM, FORGET, data mutations) require confirmation step
- **Secret redaction**: All command output is scanned for secrets/tokens before logging
- **Idempotent mutations**: Write commands produce the same result when called twice with the same args

### Forbidden Actions
- No `git push --force`
- No branch deletion
- No production data mutation
- No credential access outside approved config paths
- No execution of arbitrary shell commands (only approved build/test commands)

## Implementation Plan

### Phase 1 — Foundation (Tier 1A + Pipeline)
**New files:**
- `src/AgentAcademy.Server/Commands/CommandEnvelope.cs` — Envelope types
- `src/AgentAcademy.Server/Commands/CommandParser.cs` — Extract commands from agent text
- `src/AgentAcademy.Server/Commands/CommandPipeline.cs` — Auth → Execute → Audit pipeline
- `src/AgentAcademy.Server/Commands/Handlers/` — One handler per command group

**Modified files:**
- `AgentOrchestrator.cs` — After receiving agent response, run through command parser before posting to room
- `WorkspaceRuntime.cs` — Expose read methods needed by LIST_* commands

**New entities:**
- `CommandAuditEntity` — Audit trail for every command execution
- `AgentMemoryEntity` — Per-agent persistent memory (Phase 1, Week 1 Day 5)

### Phase 2 — State Management (Tier 1B)
- Spec-task linking (new junction table)
- Review workflow commands (APPROVE/REQUEST_CHANGES)
- Task claiming (optimistic locking to prevent duplicate work)

### Phase 3 — Verification + Communication (Tier 1C + 1D)
- Build/test execution (sandboxed, with timeouts)
- DM system (extends MessageEntity with RecipientId, adds DirectMessage kind)
- Room history read (existing data, new access pattern)

### Phase 4 — Navigation (Tier 1E)
- Agent self-navigation between rooms
- Room management commands

### Post-MVP
- Tier 2 & 3 rollout
- RBAC middleware extraction
- Spec verification tooling
- Frontend command palette + results view

## Frontend Surfaces

### Command Palette (Primary)
Agent commands and their results are visible in a searchable palette UI.

### Task Panel (Dedicated)
Review queue, spec links, task claims — purpose-built UI for structured state management.

### Room Sidebar (Navigation)
Agent workspaces and room navigation affordances (existing, enhanced with command feedback).

### Implementation Note
Minimal surfaces ship with the commands they support — not as a separate phase. When `LIST_TASKS` ships, the task panel gets a "Refresh from agent" indicator. When DMs ship, DM threads appear in sidebar.

## Invariants

- Every command execution produces an audit event (no silent operations)
- Read commands never produce side effects beyond audit logging
- Authorization check precedes execution for every command
- Command envelope shape is stable from day 1 — new commands extend `args`/`result`, never change the envelope
- Agent permissions are configured, not hardcoded — stored in agent catalog
- Memory system is per-agent isolated — agents cannot read each other's memories
- Self-modification requires external approval — no agent can unilaterally change its own configuration

## Known Gaps

- ~~**Permission configuration**: Resolved — permissions are co-located in `agents.json` as `Permissions` property with `Allowed` and `Denied` arrays supporting wildcard patterns.~~
- **Command discovery**: How do agents learn what commands are available? Options: included in startup prompt, discoverable via `HELP` command, or both. Currently not included in startup prompts.
- **Error recovery**: The spec describes idempotent mutations but doesn't define retry semantics (exponential backoff? max retries? circuit breaker?).
- **Rate limiting**: No rate limiting defined for commands. An agent could spam READ_FILE in a tight loop.
- **Frontend command palette**: Design is described but not specified. Needs wireframes and interaction model.
- ~~**Migration from free-text parsing**: Resolved — command pipeline runs in parallel with existing TASK ASSIGNMENT/WORK REPORT/REVIEW parsing. No migration needed for Phase 1.~~

## Revision History

| Date | Change | Task |
|------|--------|------|
| 2026-03-28 | Initial spec from agent team feature request v3 | agent-command-system |
| 2026-03-28 | Implemented Phase 1A: envelope, parser, pipeline, authorization, audit, read handlers, memory handlers | command-system-phase1 |
