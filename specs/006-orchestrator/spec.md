# Agent Orchestrator

## Purpose

The `AgentOrchestrator` drives the multi-agent conversation lifecycle — from receiving a human message through planner-led rounds, breakout room work, and review cycles. It is the central coordination service that determines which agents speak, when, and in what order.

Ported from v1 TypeScript `CollaborationOrchestrator` to C# with async/await patterns and scoped `WorkspaceRuntime` access.

## Current Behavior

### Queue-Based Processing

Human messages are enqueued by room ID. A single processing loop drains the queue, running one conversation round per room. If the orchestrator is already processing, new messages wait in the FIFO queue.

- Entry point: `HandleHumanMessage(roomId)` — enqueues and kicks off processing
- Processing is serialized — only one room is handled at a time
- The orchestrator can be stopped via `Stop()`, which halts queue processing and in-flight rounds

### Conversation Rounds

Each round in the main collaboration room follows this sequence:

1. **Planner first**: The agent with role `"Planner"` runs first, with instructions to tag other agents or create TASK ASSIGNMENT blocks
2. **Tagged agents**: Agents @-mentioned in the planner's response run next (up to `MAX_TAGGED_AGENTS = 6`)
3. **Fallback to idle**: If no agents were tagged, up to 3 idle agents in the room run
4. **Sequential execution**: Agents run one at a time so each sees prior responses
5. **PASS detection**: Short responses matching PASS/N/A/No comment/Nothing to add are suppressed

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
```

The orchestrator:
1. Finds the named agent in the catalog
2. Creates a breakout room (`BR: {title}`)
3. Creates a task item linked to the breakout room
4. Posts a system status message to the main room
5. Launches the breakout loop asynchronously

### Breakout Room Workflow

Inside a breakout room, the assigned agent works for up to `MAX_BREAKOUT_ROUNDS = 5` iterations:

1. A task brief is posted as a system message
2. Each round builds a prompt with tasks and work log
3. The agent's response is posted to the breakout room's message log
4. If the response contains a `WORK REPORT:` block with status "COMPLETE", the breakout completes early
5. After max rounds, the breakout completes regardless

### Review Cycle

When a breakout completes:

1. The agent moves to "presenting" state in the main room
2. The last agent message (work report) is posted to the main room
3. A reviewer agent (role `"Reviewer"`) evaluates the work
4. The reviewer produces a `REVIEW:` block with verdict `APPROVED` or `NEEDS FIX`
5. If rejected, the agent returns to the breakout room for up to `MAX_FIX_ROUNDS = 2` additional rounds
6. The breakout room is closed after review completes

### Prompt Building

Three prompt builders construct context for agent invocations:

- **`BuildConversationPrompt`**: Agent startup prompt + room context + spec context + recent messages (last 20)
- **`BuildBreakoutPrompt`**: Agent startup prompt + breakout room name + tasks + work log (last 10 messages)
- **`BuildReviewPrompt`**: Reviewer startup prompt + work report + spec context for accuracy verification

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

### Constants

| Name | Value | Description |
|------|-------|-------------|
| `McTimeout` | 120s | Main conversation agent timeout |
| `BreakoutTimeout` | 300s | Breakout room agent timeout |
| `MaxBreakoutRounds` | 5 | Max iterations per breakout |
| `MaxFixRounds` | 2 | Extra rounds after review rejection |
| `MaxTaggedAgents` | 6 | Cap on tagged agents per round |

### Parsing Records

```csharp
internal record ParsedTaskAssignment(string Agent, string Title, string Description, List<string> Criteria);
internal record ParsedWorkReport(string Status, List<string> Files, string Evidence);
internal record ParsedReviewVerdict(string Verdict, List<string> Findings);
```

## Invariants

1. Queue processing is serialized — at most one room is being processed at any time
2. Agents run sequentially within a round so each sees prior responses
3. Breakout loops run asynchronously (fire-and-forget) to not block the main conversation
4. The planner always runs first if one exists
5. PASS responses are never posted to the room
6. Task assignment parsing requires both Agent and Title fields
7. The orchestrator tolerates individual agent failures without aborting the round

## Known Gaps

- No persistence of queue state — pending messages are lost on restart
- Breakout rooms use fire-and-forget (`Task.Run`) — unobserved exceptions are logged but not surfaced to the caller
- No concurrency control on simultaneous breakout rooms for the same agent
- `LoadSpecContext` reads from the file system synchronously

## Revision History

| Date | Change | Task |
|------|--------|------|
| 2025-07-21 | Initial implementation — ported from v1 TypeScript | Port orchestrator |
