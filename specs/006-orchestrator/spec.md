# Agent Orchestrator

## Purpose

The `AgentOrchestrator` drives the multi-agent conversation lifecycle — from receiving a human message through planner-led rounds, breakout room work, and review cycles. It is the central coordination service that determines which agents speak, when, and in what order.

Ported from v1 TypeScript `CollaborationOrchestrator` to C# with async/await patterns and scoped `WorkspaceRuntime` access.

## Current Behavior

**Status: Implemented**

### Queue-Based Processing

Human messages and DM triggers are enqueued by room ID. A single processing loop drains the queue, running one conversation round per room. If the orchestrator is already processing, new messages wait in the FIFO queue.

- Entry point: `HandleHumanMessage(roomId)` — enqueues `{RoomId}` and kicks off `ProcessQueueAsync()`
- Entry point: `HandleDirectMessage(agentId, roomId)` — enqueues with dedupe for the same agent/room
- Processing is serialized via `_processing` flag + `_lock` — only one room is processed at a time
- The orchestrator can be stopped via `Stop()`, which flips `_stopped` and halts queue processing

### Conversation Rounds

Each round in the main collaboration room follows this sequence:

1. **Session rotation**: Round 1 runs `ConversationSessionService.CheckAndRotateAsync(roomId)` to check epoch thresholds
2. **Planner first**: The agent with role `"Planner"` runs first, with instructions to tag other agents or create TASK ASSIGNMENT blocks
3. **Tagged agents**: Agents @-mentioned in the planner's response run next (up to `MaxTaggedAgents = 6`)
4. **Fallback to idle**: If no agents were tagged, up to 3 idle agents in the room run
5. **Sequential execution**: Agents run one at a time so each sees prior responses. Agents in `Working` state (in breakout) are skipped.
6. **PASS detection**: Short responses matching PASS/N/A/No comment/Nothing to add are suppressed
7. **Multi-round continuation**: After a round completes, if non-PASS responses were produced and the room has an active task, another round starts automatically. This repeats up to `MaxRoundsPerTrigger = 3` rounds per human message trigger, preventing infinite loops while allowing multi-step conversations to progress without manual re-prompting.

### Task Assignment Workflow

When an agent's response contains `TASK ASSIGNMENT:` blocks:

```
TASK ASSIGNMENT:
Agent: @AgentName
Title: Short title
Description: What to do
Acceptance Criteria:
- Criterion 1
- Criterion 2
Type: Feature|Bug|Chore|Spike
```

The orchestrator:
1. Finds the named agent in the catalog
2. **Task gating**: Non-planner agents can only create `Bug` tasks. Other task types from non-planners become a proposal message posted to the main room instead of an actual task.
3. Creates a breakout room for the assigned agent
4. Creates a task item linked to the breakout room
5. Ensures the breakout room has a persisted linked `TaskEntity` via `BreakoutRoomEntity.TaskId`
6. Creates a dedicated task branch (`task/{slug}-{suffix}`), records it once on that task, and sets the breakout plan
7. Returns to `develop` after branch setup
8. Posts a system status message to the main room
9. Launches the breakout loop asynchronously via `Task.Run` (fire-and-forget)

On setup failure, the breakout room is closed and any orphan task is cancelled.

### Breakout Room Workflow

Inside a breakout room, the assigned agent works in an **open-ended loop** (`for (round = 1; ; round++)`) — there is no round cap:

1. Session rotation check runs before each round
2. Git round lock is acquired and the task branch is ensured
3. A prompt is built with: task brief + work log + unread DMs + session summary
4. The agent runs and its response is posted to the breakout room
5. Commands in the response are processed while still on the task branch
6. If the response contains a `WORK REPORT:` block with `Status: COMPLETE`, the breakout completes early
7. Without a work report, the loop continues indefinitely until the agent signals completion

### Review Cycle

When a breakout completes:

1. The agent moves to `Presenting` state in the main room
2. The work report is posted to the main room
3. The task is transitioned to `InReview`

Two review paths exist:

- **Branch-backed tasks** (current default): Auto-review is skipped. The task awaits manual `APPROVE_TASK` → `MERGE_TASK` commands from a reviewer or planner.
- **Legacy path**: A reviewer agent runs in the main room and produces a `REVIEW:` block with verdict `APPROVED` or `NEEDS FIX`. If rejected, the agent returns to breakout for an **open-ended fix loop** (`for (round = 1; ; round++)`) — there is no fix-round cap.

The breakout room is closed after review completes.

### Direct Message Handling

The orchestrator has full DM integration:

- `HandleDirectMessage(agentId, roomId)` provides a dedicated queue path for DM-triggered processing
- If the target agent is in a breakout room, unread DMs are posted into the breakout room as system messages
- Otherwise, DMs are injected into the agent's prompt in both main-room and breakout contexts
- DMs use per-recipient `AcknowledgedAt` tracking to prevent duplication

### Prompt Building

Three prompt builders construct context for agent invocations:

- **`BuildConversationPrompt`**: Session summary (if available) + agent memories + room context + spec context + recent messages (last 20 from active session). **Note**: Agent startup prompt is NOT included — it's sent only during SDK session priming to avoid redundant context accumulation.
- **`BuildBreakoutPrompt`**: Session summary (if available) + agent memories + breakout room name + tasks + work log (last 10 from active session) + unread DMs. Same startup prompt deduplication.
- **`BuildReviewPrompt`**: Reviewer startup prompt + work report + spec context for accuracy verification

### Epoch-Aware Round Logic

Before the first conversation round (main room) or before each round (breakout rooms), the orchestrator checks if the active conversation session has exceeded the configured message threshold:

- Main rooms: default 50 messages (`conversation.mainRoomEpochSize`)
- Breakout rooms: default 30 messages (`conversation.breakoutEpochSize`)

If exceeded:
1. `ConversationSessionService.CheckAndRotateAsync()` summarizes the current session via LLM
2. The session is archived with the summary
3. A new active session is created
4. All SDK sessions for the room are invalidated (forcing fresh context)

### Spec Context Loading

`LoadSpecContext()` reads the `specs/` directory, extracting the first heading and purpose paragraph from each `spec.md` file to provide agents with project context.

### Message Kind Inference

Agent roles map to `MessageKind` values:

| Role | MessageKind |
|------|-------------|
| Planner | Coordination |
| Architect | Decision |
| SoftwareEngineer | Response |
| Reviewer | Review |
| Validator | Validation |
| TechnicalWriter | SpecChangeProposal |
| (other) | Response |

## Interfaces & Contracts

### Service Registration

```csharp
// Program.cs
builder.Services.AddSingleton<AgentOrchestrator>();
```

Registered as a singleton. Uses `IServiceScopeFactory` to create scoped `WorkspaceRuntime` instances for each conversation round and breakout loop.

### Dependencies

| Dependency | Lifetime | Purpose |
|------------|----------|---------|
| `IServiceScopeFactory` | Singleton | Creates scoped DB contexts |
| `IAgentExecutor` | Singleton | Runs agents against prompts |
| `ActivityBroadcaster` | Singleton | Publishes thinking/finished events |
| `ILogger<AgentOrchestrator>` | Singleton | Structured logging |
| `WorkspaceRuntime` | Scoped (per round) | Room/message/agent state management |
| `ConversationSessionService` | Scoped (per round) | Epoch threshold checks and rotation |

### Constants

| Name | Value | Description |
|------|-------|-------------|
| `MaxRoundsPerTrigger` | 3 | Max conversation rounds per human message |
| `MaxTaggedAgents` | 6 | Cap on tagged agents per round |

Note: Per-turn and breakout timeouts (`McTimeout`, `BreakoutTimeout`) were removed. Breakout rounds (`MaxBreakoutRounds`) and fix rounds (`MaxFixRounds`) are now open-ended. Session epoch sizes are configured via `SystemSettingsService`.

### Parsing Records

```csharp
internal record ParsedTaskAssignment(string Agent, string Title, string Description, List<string> Criteria, TaskType Type);
internal record ParsedWorkReport(string Status, List<string> Files, string Evidence);
internal record ParsedReviewVerdict(string Verdict, List<string> Findings);
```

## Invariants

1. Queue processing is serialized — at most one room is being processed at any time
2. Agents run sequentially within a round so each sees prior responses
3. Breakout loops run asynchronously (fire-and-forget via `Task.Run`) to not block the main conversation
4. The planner always runs first if one exists
5. PASS responses are never posted to the room
6. Task assignment parsing requires both Agent and Title fields
7. The orchestrator tolerates individual agent failures without aborting the round
8. Non-planner agents can only create Bug tasks; other types become proposals
9. Branch-backed tasks skip auto-review in favor of manual APPROVE_TASK/MERGE_TASK

## Known Gaps

- No persistence of queue state — pending messages are lost on restart
- Breakout rooms use fire-and-forget (`Task.Run`) — unobserved exceptions are logged but not surfaced to the caller
- ~~No concurrency control on simultaneous breakout rooms for the same agent~~ — **resolved**: `HandleTaskAssignmentAsync` checks `AgentState.Working` before creating a breakout room. If the agent is already working, the assignment is skipped with a status message posted to the room.
- `LoadSpecContext` reads from the file system synchronously
- ~~Open-ended breakout and fix loops have no timeout or round cap~~ — **resolved**: stuck-detection tracks consecutive idle rounds (`MaxConsecutiveIdleRounds=5`) and enforces absolute cap (`MaxBreakoutRounds=200`). See spec 011.

## Revision History

| Date | Change | Task |
|------|--------|------|
| 2026-04-04 | Full reconciliation with code: open-ended breakout/fix loops, DM handling, task gating, branch-based review split, removed stale constants | spec-006-reconciliation |
| 2026-03-30 | Marked section `Outdated` pending reconciliation with open-ended breakout lifecycle and timeout removal | spec-doc-gap-fix |
| 2026-03-28 | Multi-round continuation loop (up to 3 rounds per trigger) | fix-orchestrator-stall |
| 2025-07-21 | Initial implementation — ported from v1 TypeScript | Port orchestrator |
