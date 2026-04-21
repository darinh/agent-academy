# Agent Orchestrator

## Purpose

The orchestrator subsystem drives the multi-agent conversation lifecycle — from receiving a human message through planner-led rounds, breakout room work, and review cycles. It determines which agents speak, when, and in what order.

The subsystem is decomposed into four collaborating services:

| Service | Lifetime | Responsibility |
|---------|----------|----------------|
| `AgentOrchestrator` | Singleton | Queue management, message dispatch, startup recovery |
| `ConversationRoundRunner` | Singleton | Planner-led conversation rounds, agent selection, sprint filtering |
| `DirectMessageRouter` | Singleton | DM routing — breakout forwarding or targeted room turns |
| `RoundContextLoader` | Scoped | Loads shared per-round context (spec, session, sprint) |

Ported from v1 TypeScript `CollaborationOrchestrator` to C# with async/await patterns and scoped domain service access. The monolithic orchestrator was decomposed during a 2026-04-14 refactor to isolate queue management from conversation logic and DM routing.

## Current Behavior

**Status: Implemented**

### Queue-Based Processing

> **Source**: `src/AgentAcademy.Server/Services/AgentOrchestrator.cs`

`AgentOrchestrator` is a pure queue manager. It accepts human messages and DM triggers, enqueues them as `QueueItem` records, and dispatches to `ConversationRoundRunner` or `DirectMessageRouter` for execution. All conversation logic and DM routing have been extracted out.

- Entry point: `HandleHumanMessage(roomId)` — enqueues `{RoomId}` and kicks off `ProcessQueueAsync()`
- Entry point: `HandleDirectMessage(recipientAgentId)` — enqueues with dedupe (skips if a DM trigger for the same agent is already queued)
- Processing is serialized via `_processing` flag + `_lock` — only one item is processed at a time
- Dispatch: items with `TargetAgentId` go to `DirectMessageRouter.RouteAsync()`; room items go to `ConversationRoundRunner.RunRoundsAsync()`
- The orchestrator can be stopped via `Stop()`, which cancels the `CancellationTokenSource` and halts queue processing
- **Queue reconstruction on startup**: `ReconstructQueueAsync()` runs on every server startup (crash or clean). It queries `RoomService.GetRoomsWithPendingHumanMessagesAsync()` for rooms whose most recent message has `SenderKind = User`, re-enqueues them, and kicks off processing. This prevents message loss when the server restarts while human messages are pending.
- **Conversation kickoff on fresh start**: During `WebApplicationExtensions.InitializeAsync()` (step 7), if the main room has no `User` or `Agent` messages and crash recovery did not run, a system kickoff message is posted and the room is enqueued. This bootstraps agent collaboration on a fresh workspace without requiring a manual human message. The check is idempotent — once agents have spoken, subsequent restarts skip the kickoff.

### Conversation Rounds

> **Source**: `src/AgentAcademy.Server/Services/ConversationRoundRunner.cs`

`ConversationRoundRunner.RunRoundsAsync(roomId, cancellationToken)` executes up to `MaxRoundsPerTrigger` (3) conversation rounds. Each round gets a fresh DI scope to ensure clean `DbContext` state.

Each round in the main collaboration room follows this sequence:

1. **Session rotation**: Round 1 runs `ConversationSessionService.CheckAndRotateAsync(roomId)` to check epoch thresholds
2. **Context loading**: Spec context, session summary, and active sprint context are loaded once per round
3. **Agent config overrides**: Each agent is resolved through `AgentConfigService.GetEffectiveAgentAsync()` to apply runtime overrides (model, prompt, templates) from the database
4. **Sprint stage filtering**: When an active sprint exists, agents are filtered by `SprintPreambles.FilterByStageRoster()` — only roles allowed in the current stage participate
5. **Planner first**: The agent with role `"Planner"` runs first, with instructions to tag other agents or create TASK ASSIGNMENT blocks. Planner is skipped if excluded by sprint stage.
6. **Tagged agents**: Agents @-mentioned in the planner's response run next (up to `AgentResponseParser.MaxTaggedAgents = 6`)
7. **Fallback to idle**: If no agents were tagged, up to 3 idle agents in the room run (excluding the planner). Candidates are ordered by `AgentLocation.UpdatedAt` ascending (least-recently-active first) so the fallback rotates through the full set rather than always picking the first three in catalog order. `OrderBy` is stable, so ties preserve catalog order — keeping behaviour deterministic before any activity bumps timestamps.
8. **Sequential execution**: Agents run one at a time so each sees prior responses. Agents in `Working` state (in breakout) are skipped.
9. **PASS detection**: Short responses matching PASS/N/A/No comment/Nothing to add are suppressed (via `AgentResponseParser.IsPassResponse`)
10. **Multi-round continuation**: After a round completes, if non-PASS responses were produced and the room has an active task, another round starts automatically. This repeats up to `MaxRoundsPerTrigger = 3` rounds per human message trigger, preventing infinite loops while allowing multi-step conversations to progress without manual re-prompting.

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

`TaskAssignmentHandler` (invoked by `AgentTurnRunner` when it detects `TASK ASSIGNMENT:` blocks in an agent response):

> **Source**: `src/AgentAcademy.Server/Services/TaskAssignmentHandler.cs`

1. Finds the named agent in the catalog
2. **Task gating**: Non-planner agents can only create `Bug` tasks. Other task types from non-planners become a proposal message posted to the main room instead of an actual task.
3. Creates a breakout room for the assigned agent
4. Creates a task item linked to the breakout room
5. Ensures the breakout room has a persisted linked `TaskEntity` via `BreakoutRoomEntity.TaskId`
6. Creates a dedicated task branch (`task/{slug}-{suffix}`), records it once on that task, and sets the breakout plan
7. Creates a worktree for isolated work (via `WorktreeService`) when a workspace is available
8. Returns to `develop` after branch setup
9. Posts a system status message to the main room
10. Launches the breakout lifecycle asynchronously via `Task.Run(() => _breakoutLifecycle.RunBreakoutLifecycleAsync(...))`

On setup failure, cleanup runs independently for each resource: breakout room is closed, orphan task is cancelled, orphan task item is rejected, worktree is removed, orphan git branch is deleted. Each cleanup step is independent — one failure does not prevent the others.

### Breakout Room Workflow

> **Delegated to**: `BreakoutLifecycleService` (loop and coordination) + `BreakoutCompletionService` (post-loop completion, review cycle, agent execution helpers)
> **Source**: `src/AgentAcademy.Server/Services/BreakoutLifecycleService.cs`, `src/AgentAcademy.Server/Services/BreakoutCompletionService.cs`

Inside a breakout room, the assigned agent works in a loop capped at `BreakoutLifecycleService.MaxBreakoutRounds = 200`. Stuck detection closes after `BreakoutLifecycleService.MaxConsecutiveIdleRounds = 5` idle rounds:

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
- **Legacy path**: A reviewer agent runs in the main room and produces a `REVIEW:` block with verdict `APPROVED` or `NEEDS FIX`. If rejected, the agent returns to breakout for a fix loop (same caps as the main breakout loop).

The breakout room is closed after review completes.

### Direct Message Handling

> **Source**: `src/AgentAcademy.Server/Services/DirectMessageRouter.cs`

`DirectMessageRouter.RouteAsync(recipientAgentId)` handles DM-triggered agent turns. Creates a fresh DI scope per invocation.

- Resolves the recipient agent from the catalog (logs warning and returns if not found)
- Applies runtime config overrides via `AgentConfigService.GetEffectiveAgentAsync()`
- If the target agent is in a breakout room (`AgentState.Working` with `BreakoutRoomId`), unread DMs are posted into the breakout room as system messages and acknowledged — the agent processes them in its next breakout round
- Otherwise, loads round context via `RoundContextLoader` and runs a targeted turn in the agent's current room (falls back to the first available room or `"main"` if location is unknown)
- DMs use per-recipient `AcknowledgedAt` tracking to prevent duplication

### Prompt Building

> **Delegated to**: `PromptBuilder` static class (extracted from `AgentOrchestrator`)
> **Source**: `src/AgentAcademy.Server/Services/PromptBuilder.cs`

Five prompt builders construct context for agent invocations:

- **`PromptBuilder.BuildConversationPrompt`**: Session summary (if available) + agent memories (via `AgentMemoryLoader`) + room context + spec context + direct messages + sprint preamble + recent messages (last 20 from active session). **Note**: Agent startup prompt is NOT included — it's sent only during SDK session priming to avoid redundant context accumulation.
- **`PromptBuilder.BuildBreakoutPrompt`**: Session summary (if available) + agent memories + breakout room name + tasks + work log (last 10 from active session) + unread DMs. Same startup prompt deduplication.
- **`PromptBuilder.BuildReviewPrompt`**: Reviewer startup prompt + work report + spec context for accuracy verification
- **`PromptBuilder.BuildAssignmentPlanContent`**: Generates initial plan content from a parsed task assignment
- **`PromptBuilder.BuildTaskBrief`**: Summarizes active tasks and branch context for breakout prompts

### Epoch-Aware Round Logic

Before the first conversation round (main room) or before each round (breakout rooms), `ConversationRoundRunner` (main room) or `BreakoutLifecycleService` (breakout rooms) checks if the active conversation session has exceeded the configured message threshold:

- Main rooms: default 50 messages (`conversation.mainRoomEpochSize`)
- Breakout rooms: default 30 messages (`conversation.breakoutEpochSize`)

If exceeded:
1. `ConversationSessionService.CheckAndRotateAsync()` summarizes the current session via LLM
2. The session is archived with the summary
3. A new active session is created
4. All SDK sessions for the room are invalidated (forcing fresh context)

### Spec Context Loading

`SpecManager.LoadSpecContextAsync()` reads the `specs/` directory, extracting the first heading and purpose paragraph from each `spec.md` file to provide agents with project context.

> **Source**: `src/AgentAcademy.Server/Services/SpecManager.cs`

### Round Context Loading

> **Source**: `src/AgentAcademy.Server/Services/RoundContextLoader.cs`

`RoundContextLoader.LoadAsync(roomId)` consolidates all per-round context loading into an immutable `RoundContext` record. Both `ConversationRoundRunner` and `DirectMessageRouter` resolve this service from their scoped container.

Each field is loaded independently with soft-fail to null and a logged warning, ensuring one failure cannot cascade:

| Field | Source | Description |
|-------|--------|-------------|
| `SpecContext` | `SpecManager.LoadSpecContextAsync()` | Aggregated spec headings/purpose paragraphs |
| `SpecVersion` | `SpecManager.GetSpecVersionAsync()` | Current spec version hash |
| `SessionSummary` | `ConversationSessionQueryService.GetSessionContextAsync()` | Archived session summary for the room |
| `SprintPreamble` | `SprintPreambles.BuildPreamble()` | Sprint stage context, prior context, overflow requirements |
| `ActiveSprintStage` | `SprintService.GetActiveSprintAsync()` | Current sprint stage (controls agent role filtering) |

Sprint context loading (`LoadSprintContextAsync`, private) handles stage detection, prior context, overflow requirements from previous sprints, and preamble building. Sprint stage also controls which agent roles participate via `SprintPreambles.IsRoleAllowedInStage()` and `SprintPreambles.FilterByStageRoster()` (applied in `ConversationRoundRunner`).

### Response Parsing

> **Delegated to**: `AgentResponseParser` static class (extracted from `AgentOrchestrator`)
> **Source**: `src/AgentAcademy.Server/Services/AgentResponseParser.cs`

`AgentResponseParser` handles all response parsing and classification:
- `ParseWorkReport(content)` — extracts `WORK REPORT:` blocks
- `ParseReviewVerdict(content)` — extracts `REVIEW:` blocks
- `ParseTaskAssignments(content)` — extracts `TASK ASSIGNMENT:` blocks
- `ParseTaggedAgents(agents, content)` — finds @-mentioned agents (up to `MaxTaggedAgents = 6`)
- `IsPassResponse(response)` — detects PASS/N/A/No comment/Nothing to add
- `IsStubOfflineResponse(response)` — detects stub/offline markers
- `InferMessageKind(role)` — maps agent roles to `MessageKind` values

### Agent Memory Loading

> **Delegated to**: `AgentMemoryLoader` (extracted from `AgentOrchestrator`)
> **Source**: `src/AgentAcademy.Server/Services/AgentMemoryLoader.cs`

`AgentMemoryLoader.LoadAsync(agentId)` retrieves agent memories from the database, including shared memories. Memories are injected into both conversation and breakout prompts.

### Message Kind Inference

Agent roles map to `MessageKind` values (via `AgentResponseParser.InferMessageKind`):

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
// AgentPipelineExtensions.cs
builder.Services.AddSingleton<AgentMemoryLoader>();
builder.Services.AddSingleton<BreakoutLifecycleService>();
builder.Services.AddSingleton<BreakoutCompletionService>();
builder.Services.AddSingleton<IBreakoutCompletionService>(sp => sp.GetRequiredService<BreakoutCompletionService>());
builder.Services.AddSingleton<AgentTurnRunner>();
builder.Services.AddSingleton<IAgentTurnRunner>(sp => sp.GetRequiredService<AgentTurnRunner>());
builder.Services.AddSingleton<TaskAssignmentHandler>();
builder.Services.AddSingleton<ITaskAssignmentHandler>(sp => sp.GetRequiredService<TaskAssignmentHandler>());
builder.Services.AddSingleton<ConversationRoundRunner>();
builder.Services.AddSingleton<DirectMessageRouter>();
builder.Services.AddSingleton<AgentOrchestrator>();

// ServiceRegistrationExtensions.cs
builder.Services.AddScoped<RoundContextLoader>();
```

`AgentOrchestrator` and its extracted singleton services are registered in `AgentPipelineExtensions`. `RoundContextLoader` is scoped (resolved per-round via `IServiceScopeFactory`). Per-turn agent execution is delegated to `AgentTurnRunner` (consumed via `IAgentTurnRunner`). Post-loop breakout completion (presenting results, review cycle, fix loops) is handled by `BreakoutCompletionService`, consumed through `IBreakoutCompletionService`. `PromptBuilder` and `AgentResponseParser` are static classes — no DI registration needed.

### Dependencies

**AgentOrchestrator (singleton)**:

| Dependency | Lifetime | Purpose |
|------------|----------|---------|
| `IServiceScopeFactory` | Singleton | Creates scoped DB contexts for startup recovery |
| `ConversationRoundRunner` | Singleton | Executes planner-led conversation rounds |
| `DirectMessageRouter` | Singleton | Routes DM-triggered agent turns |
| `BreakoutLifecycleService` | Singleton | Manages breakout room loop (stopped on orchestrator Stop) |
| `ILogger<AgentOrchestrator>` | Singleton | Structured logging |

**ConversationRoundRunner (singleton)**:

| Dependency | Lifetime | Purpose |
|------------|----------|---------|
| `IServiceScopeFactory` | Singleton | Creates fresh scoped container per round |
| `IAgentCatalog` | Singleton | Agent definitions (planner lookup, idle agent enumeration) |
| `IAgentTurnRunner` | Singleton | Per-turn agent execution (config, memory, prompt, LLM, commands) |
| `ILogger<ConversationRoundRunner>` | Singleton | Structured logging |

**DirectMessageRouter (singleton)**:

| Dependency | Lifetime | Purpose |
|------------|----------|---------|
| `IServiceScopeFactory` | Singleton | Creates scoped container per DM routing |
| `IAgentCatalog` | Singleton | Catalog lookup for recipient agent |
| `IAgentTurnRunner` | Singleton | Runs targeted agent turn |
| `ILogger<DirectMessageRouter>` | Singleton | Structured logging |

**RoundContextLoader (scoped)**:

| Dependency | Lifetime | Purpose |
|------------|----------|---------|
| `SpecManager` | Singleton | Loads spec context for prompts |
| `ConversationSessionQueryService` | Scoped | Session summary and sprint context |
| `RoomService` | Scoped | Active workspace path resolution |
| `SprintService` | Scoped | Active sprint and stage loading |
| `SprintArtifactService` | Scoped | Overflow requirements from prior sprints |
| `ILogger<RoundContextLoader>` | Scoped | Structured logging |

**Scoped dependencies resolved per round in `ConversationRoundRunner`**:

| Dependency | Purpose |
|------------|---------|
| `RoomService` | Room state and active task queries |
| `MessageService` | Message posting and retrieval |
| `AgentLocationService` | Agent state and location tracking (Working/Idle/Presenting) |
| `ITaskItemService` | Active task items for planner context |
| `ActivityPublisher` | Publishes thinking/finished events |
| `AgentConfigService` | Runtime agent config overrides (model, prompt, templates) |
| `RoundContextLoader` | Loads spec, session, and sprint context |
| `ConversationSessionService` | Epoch threshold checks and rotation (round 1 only) |

**Scoped dependencies resolved per invocation in `DirectMessageRouter`**:

| Dependency | Purpose |
|------------|---------|
| `RoomService` | Room lookup and fallback resolution |
| `MessageService` | DM retrieval, breakout forwarding, acknowledgment |
| `AgentLocationService` | Agent location and breakout room detection |
| `ActivityPublisher` | Publishes thinking/finished events |
| `AgentConfigService` | Runtime agent config overrides |
| `RoundContextLoader` | Loads spec, session, and sprint context |

**BreakoutLifecycleService (singleton)**:

| Dependency | Lifetime | Purpose |
|------------|----------|---------|
| `IServiceScopeFactory` | Singleton | Creates scoped DB contexts per round |
| `IBreakoutCompletionService` | Singleton | Post-loop completion and agent execution |
| `GitService` | Singleton | Manages task branch checkout per round |
| `WorktreeService` | Singleton | Manages worktree lifecycle |
| `ILogger<BreakoutLifecycleService>` | Singleton | Structured logging |

**BreakoutCompletionService (singleton)**:

| Dependency | Lifetime | Purpose |
|------------|----------|---------|
| `IServiceScopeFactory` | Singleton | Creates scoped DB contexts |
| `IAgentExecutor` | Singleton | Runs agent in breakout/review loops |
| `SpecManager` | Singleton | Loads spec context for breakout prompts |
| `CommandPipeline` | Singleton | Processes commands from agent responses |
| `AgentMemoryLoader` | Singleton | Loads agent memories for breakout prompts |
| `ILogger<BreakoutCompletionService>` | Singleton | Structured logging |

### Constants

**ConversationRoundRunner**:

| Name | Value | Description |
|------|-------|-------------|
| `MaxRoundsPerTrigger` | 3 | Max conversation rounds per human message |

**AgentResponseParser**:

| Name | Value | Description |
|------|-------|-------------|
| `MaxTaggedAgents` | 6 | Cap on tagged agents per round |

**BreakoutLifecycleService**:

| Name | Value | Description |
|------|-------|-------------|
| `MaxBreakoutRounds` | 200 | Absolute cap on breakout loop iterations |
| `MaxConsecutiveIdleRounds` | 5 | Stuck detection threshold (idle rounds) |

Session epoch sizes are configured via `SystemSettingsService`.

### Parsing Records

Defined in `AgentResponseParser` (`src/AgentAcademy.Server/Services/AgentResponseParser.cs`):

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

- ~~No persistence of queue state — pending messages are lost on restart~~ — **resolved**: `ReconstructQueueAsync()` runs on every startup and re-enqueues rooms with pending human messages. Uses `RoomService.GetRoomsWithPendingHumanMessagesAsync()` to find rooms where the latest message has `SenderKind = User`.
- ~~Breakout rooms use fire-and-forget (`Task.Run`) — unobserved exceptions are logged but not surfaced to the caller~~ — **resolved**: `HandleBreakoutFailureAsync` catches unhandled exceptions, closes the breakout room with `Failed` reason, marks linked task as `Blocked`, and posts a failure notification to the parent room.
- ~~No concurrency control on simultaneous breakout rooms for the same agent~~ — **resolved**: `HandleTaskAssignmentAsync` checks `AgentState.Working` before creating a breakout room. If the agent is already working, the assignment is skipped with a status message posted to the room.
- ~~`LoadSpecContext` reads from the file system synchronously~~ — **resolved**: all `SpecManager` methods converted to async (`LoadSpecContextAsync`, `GetSpecSectionsAsync`, `GetSpecContentAsync`) using `File.ReadAllTextAsync`.
- ~~Open-ended breakout and fix loops have no timeout or round cap~~ — **resolved**: stuck-detection tracks consecutive idle rounds (`MaxConsecutiveIdleRounds=5`) and enforces absolute cap (`MaxBreakoutRounds=200`). See spec 011.

## Revision History

| Date | Change | Task |
|------|--------|------|
| 2026-04-18 | Spec sync — documented LRU ordering for the idle-agent fallback in the conversation round (`GetIdleAgentsInRoomAsync` orders candidates by `AgentLocation.UpdatedAt` ascending; ties preserve catalog order via stable sort). Reflects audit fix #105 preventing starvation when more than 3 idle agents share a room. | Anvil |
| 2026-04-15 | Interface extraction sprint: extracted 16 new interface contracts across sprint services (`ISprintArtifactService`, `ISprintMetricsCalculator`, `ISprintScheduleService`), conversation services (`IConversationKickoffService`, `IConversationSessionQueryService`, `IConversationExportService`), utility services (`IRoomArtifactTracker`, `IArtifactEvaluatorService`, `IPlanService`), and infrastructure services (`ISearchService`, `ISystemSettingsService`, `IWorkspaceService`, `IInitializationService`, `IPhaseTransitionValidator`). All consumers migrated to interfaces. | interface-extraction-sprint |
| 2026-04-15 | Extracted `IAgentTurnRunner` interface contract. Updated DI registration (concrete + forwarding), dependency tables for `ConversationRoundRunner` and `DirectMessageRouter` to show `IAgentTurnRunner`. | refactor/agent-turn-runner-contract |
| 2026-04-15 | Extracted `IBreakoutCompletionService` contract from `BreakoutCompletionService`. Updated DI registration and dependency table to show interface-forwarded consumption in `BreakoutLifecycleService`. | service-contract-stabilization |
| 2026-04-15 | Orchestrator decomposition: documented `ConversationRoundRunner` (conversation round execution), `DirectMessageRouter` (DM routing), and `RoundContextLoader` (shared per-round context). Updated `AgentOrchestrator` to reflect its reduced role as a pure queue manager. Updated DI registration, dependency tables, constants ownership, and source references. | spec-006-decomposition-update |
| 2026-04-14 | Conversation kickoff on fresh start: `WebApplicationExtensions.InitializeAsync()` step 7 posts a system kickoff message and triggers orchestration when the main room has no prior user/agent messages. Idempotent — skips on restart if agents have already spoken. Skips during crash recovery. | platform-review |
| 2026-04-13 | Spec sync — documented `BreakoutCompletionService` (post-loop completion, review cycle) and `AgentTurnRunner` (per-turn execution) extracted from orchestrator services. Updated DI registration, dependency tables, and source references. | spec-sync-decomposition |
| 2026-04-11 | Service extraction reconciliation: updated dependencies, constants, and code references to reflect PromptBuilder, AgentResponseParser, AgentMemoryLoader, and BreakoutLifecycleService extractions. Added sprint context, agent config overrides, worktree creation, and response parsing documentation. | spec-006-extraction-reconciliation |
| 2026-04-05 | Spec accuracy audit: fixed HandleDirectMessage signature (takes recipientAgentId only), corrected breakout loop caps (MaxBreakoutRounds=200, MaxConsecutiveIdleRounds=5), fixed DM handling in breakouts (posted as messages, not injected into prompt), added constants to table | spec-accuracy-audit |
| 2026-04-04 | Queue reconstruction on startup: `ReconstructQueueAsync` re-enqueues rooms with unanswered human messages on every server startup. 8 new tests. Resolved queue persistence known gap. | queue-reconstruction |
| 2026-04-04 | Breakout failure surfacing: `HandleBreakoutFailureAsync` catches unhandled exceptions from fire-and-forget breakout loops, closes breakout with `Failed` reason, marks task as `Blocked`, and notifies parent room. Added `Failed` to `BreakoutRoomCloseReason`. | breakout-failure-handling |
| 2026-04-04 | Full reconciliation with code: open-ended breakout/fix loops, DM handling, task gating, branch-based review split, removed stale constants | spec-006-reconciliation |
| 2026-03-30 | Marked section `Outdated` pending reconciliation with open-ended breakout lifecycle and timeout removal | spec-doc-gap-fix |
| 2026-03-28 | Multi-round continuation loop (up to 3 rounds per trigger) | fix-orchestrator-stall |
| 2025-07-21 | Initial implementation — ported from v1 TypeScript | Port orchestrator |
