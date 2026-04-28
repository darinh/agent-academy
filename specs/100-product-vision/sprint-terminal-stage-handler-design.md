# Sprint Terminal-Stage Transition Handler — Design Doc

**Status**: PROPOSED — design only; no code change in this PR. Implementation follows in a separate PR after review.
**Roadmap entry**: Proposed Addition "Sprint terminal-stage transition handler" (`specs/100-product-vision/roadmap.md:255–266`); blocks P1.9 closure.
**Closes**: §10 acceptance steps 6 (SelfEvaluationReport exists) and 7 (SprintReport exists), and the state-machine inconsistency where `status=Completed` while `currentStage=Implementation`.
**Risk**: 🔴 (sprint lifecycle wiring, terminal-stage atomicity, drives auto-block on failure path; touches P1.4 ceremony invocation, P1.6 artifact gate, stage advancement).
**Author**: anvil (operator: agent-academy), 2026-04-28.

This doc is the design preamble the roadmap entry explicitly asks for ("scope is large… needs a design doc before implementation"). Read this first; do not start coding the driver, the new round-runner hook, or the `BlockReason="Terminal-stage ceremony failed: …"` strings until it is approved or amended.

---

## 1. Problem statement

The system has every piece needed to terminate a sprint cleanly:

- P1.4 self-evaluation ceremony — `RunSelfEvalHandler` (`Commands/Handlers/RunSelfEvalHandler.cs:25–199`), `POST /api/sprints/{id}/self-eval/start` (`Controllers/SprintController.cs`), `SelfEvaluationReport` artifact validation in `SprintArtifactService.ValidateContent` (`Services/SprintArtifactService.cs:161–320`).
- The artifact gate at Implementation→FinalSynthesis — `SprintStageService.RequiredArtifactByStage["Implementation"] = "SelfEvaluationReport"` (`Services/SprintStageService.cs:41–48`) plus the verdict gate (`SprintStageService.cs:143–179`) which requires `OverallVerdict == AllPass`.
- The artifact gate at FinalSynthesis→Completed — `SprintService.CompleteSprintAsync` requires `SprintReport` at FinalSynthesis (`Services/SprintService.cs:236–247`) unless `force=true`.
- The auto-block primitive — `SprintService.MarkSprintBlockedAsync` (`Services/SprintService.cs:442–521`) wired through `ActivityNotificationBroadcaster` to `NotificationType.NeedsInput`.

What does **not** exist is the **driver** that fires those gates on natural sprint progression. Empirically (per Sprint #15 supervised acceptance run on 2026-04-28 and a full audit of Sprint #11):

1. **`selfEvalAttempts=0` on every Completed sprint in the database (15/15)** — the P1.4 mechanism shipped in PRs #143/#144/PR3 has never auto-fired in production. It only runs when an operator manually invokes `RUN_SELF_EVAL` or `POST /api/sprints/{id}/self-eval/start`.
2. **No `SprintReport` artifact exists for any Completed sprint** — completion notification fires (per finding-9 `idle notification` PASS), but no artifact is stored.
3. **State-machine inconsistency** — Sprint #11 is `status=Completed` with `currentStage=Implementation`. The only way this is reachable is `CompleteSprintAsync(force=true)`, which the roadmap row confirms operators have been using to release the Implementation-stage round cap (20/20).

Per the gap analysis and §10 acceptance-test rubric, the product is supposed to terminate sprints via a ceremony chain that fires automatically when implementation work is done:

```
[Implementation, all tasks terminal]
        │
        ▼  (1) RUN_SELF_EVAL — agents produce SelfEvaluationReport
        │
        ▼  (2) on AllPass artifact stored: AdvanceStageAsync(Implementation→FinalSynthesis)
        │
        ▼  (3) FinalSynthesis preamble drives agents to produce SprintReport
        │
        ▼  (4) on SprintReport artifact stored: CompleteSprintAsync(force: false)
        │
        ▼
[status=Completed, currentStage=FinalSynthesis]
```

Today, only step (1) is reachable, and only via explicit operator invocation. Steps (2)–(4) require manual orchestration that has never happened on a real sprint. The result is the symptom in the roadmap: §10 step 6 and step 7 cannot pass on any sprint regardless of how cleanly the implementation work was done. **P1.9 cannot close until this ships.** This is a single coherent fix; per architect-1's FinalSynthesis recommendation it is filed as one Proposed Addition rather than three.

---

## 2. Design principles (informed by what's already in the codebase)

These are reuse opportunities surfaced by the survey. Most of the design below is "wire existing pieces together"; deliberately little new mechanism is introduced.

1. **Reuse the artifact + verdict gates, don't add a new gate.** `SprintStageService.AdvanceStageAsync` already enforces the artifact gate AND the `AllPass` verdict gate at Implementation→FinalSynthesis (`SprintStageService.cs:129–179`). `SprintService.CompleteSprintAsync` already enforces the `SprintReport` artifact gate at FinalSynthesis→Completed (`SprintService.cs:236–247`). The driver merely **calls these methods at the right time** with `force: false`. No new gate logic is added; the existing gates become observable rather than skipped.
2. **Reuse `MarkSprintBlockedAsync` for the failure path.** P1.4-narrow already shipped the atomic block primitive. A ceremony-step failure becomes the same operation with `BlockReason="Terminal-stage ceremony failed: <step>: <reason>"`. No new halt mechanism. The human gets the existing `SprintBlocked → NeedsInput` notification on Discord — zero new notification plumbing.
3. **Reuse the per-round hook in `ConversationRoundRunner`.** After every agent round, `ConversationRoundRunner` already calls `IncrementRoundCountersAsync` and then `InvokeSelfDriveDecisionAsync` (`Services/ConversationRoundRunner.cs:299–335`). The driver invocation slots in next to the self-drive decision — same scope, same fail-open semantics, same captured `sprintIdAtRunStart`. No new background timer, no new event bus, no per-sprint scheduler.
4. **State-machine driven, not event driven.** The codebase has no pre-commit listener pattern: `SprintService.QueueEvent + FlushEvents` is post-persist broadcast (`SprintService.cs:113–148, 257–274`); `ActivityPublisher` (`Services/ActivityPublisher.cs:24–64`) and `IActivityBroadcaster` (`Services/Contracts/IActivityBroadcaster.cs:10–26`) are simple pub/sub. **The roadmap entry's suggestion to subscribe to a `SprintCompleting` event is not implementable as written** — that event does not exist, and adding a generic pre-commit event-bus is a much larger change than this design needs. Instead, the driver is a stateless service that reads sprint state and fires the correct next transition; it is invoked from the per-round hook.
5. **Per-step atomicity, not whole-chain atomicity.** The roadmap entry calls for "ceremony chain inside a single transaction." That phrasing is misleading: steps (1) and (3) of the chain depend on **agent rounds** that produce artifacts, which cannot live inside a database transaction (they take seconds-to-minutes and involve multiple HTTP round-trips to Copilot SDK). The correct framing is: **each transition is its own atomic operation with the same gate semantics it already has, and the driver advances the cursor exactly one step per invocation.** §3 spells out the state machine; §4 spells out the per-step atomicity.
6. **Don't auto-trigger on cap-trip.** `SelfDriveDecisionService` already calls `MarkSprintBlockedAsync` on round-cap, stage-cap, continuation-cap, and cost-cap (`Services/SelfDriveDecisionService.cs:121–162`). Cap-trip means the team is going in circles; firing self-eval on a stuck sprint produces a noisy `AnyFail` report and burns the (already small) `MaxSelfEvalAttempts` budget. The driver fires on **natural task completion** (every sprint task is Approved/Completed/Cancelled), not on cap-trip.
7. **`force=true` remains the human escape valve.** The driver does not fire on `force=true` paths and does not interfere with operator overrides via `POST /api/sprints/{id}/complete?force=true`. But §6.2 calls out a small additional hardening: when `force=true` is used at non-terminal `currentStage`, the operation should also advance `currentStage` to `FinalSynthesis` so the snapshot is internally consistent. (This addresses finding 3 — the `Completed` while `currentStage=Implementation` state.)
8. **Fail open at the driver layer.** The driver runs AFTER the round has succeeded; a driver crash MUST NOT propagate into the round-runner. Mirror the existing `InvokeSelfDriveDecisionAsync` fail-open pattern (`ConversationRoundRunner.cs:340–360`). A driver failure is logged as a warning; the next round's driver invocation re-evaluates state and either advances or fires `SprintBlocked` if the failure is structural.

---

## 3. State model — the driver's view of a sprint

The driver computes one of six states from `(SprintEntity, SprintArtifacts, TaskEntities)` and acts on each. These are derived states; **no new columns are added to `SprintEntity`** (the existing `Status`, `CurrentStage`, `SelfEvaluationInFlight`, `SelfEvalAttempts`, `LastSelfEvalVerdict`, `BlockedAt` columns plus the artifact and task tables are sufficient).

| State name                       | Predicate (all conditions ANDed)                                                                                                                                 | Action                                                                                                          |
|----------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------|
| `NotApplicable`                  | sprint.Status ≠ "Active" OR sprint.BlockedAt ≠ null OR sprint.AwaitingSignOff                                                                                    | No-op. Driver returns silently. Covers paused, blocked, sign-off-pending (including FinalSynthesis sign-off mid-ceremony), and already-terminal sprints. When the human approves sign-off, `AwaitingSignOff` flips back to false and the next round's driver invocation re-evaluates. |
| `ImplementationInProgress`       | sprint.CurrentStage == "Implementation" AND ∃ task with `Status ∈ {Queued, Active, Blocked, AwaitingValidation, InReview, ChangesRequested, Approved, Merging}` | No-op. Implementation is not done yet; let normal self-drive continue. **Note**: `Approved` and `Merging` are intentionally non-terminal here — they match `RoomLifecycleService.TerminalTaskStatuses = {Completed, Cancelled}` (`RoomLifecycleService.cs:17–21`), which is the set `CheckImplementationPrerequisitesAsync` (`SprintStageService.cs:451–476`) actually enforces. A predicate that included `Approved` would cause the driver to fire self-eval on a sprint that would then fail `AdvanceStageAsync` because PR-merge work is still pending. |
| `ReadyForSelfEval`               | sprint.CurrentStage == "Implementation" AND every task has `Status ∈ {Completed, Cancelled}` AND task count ≥ 1 with at least one non-Cancelled AND `SelfEvaluationInFlight == false` AND no `SelfEvaluationReport` artifact exists with `OverallVerdict == AllPass` for this sprint | **Action 1**: invoke server-side self-eval start AND wake the orchestrator for every active room in the sprint's workspace (see §4.1 wake path). |
| `SelfEvalInFlight`               | sprint.CurrentStage == "Implementation" AND `SelfEvaluationInFlight == true` AND no AllPass artifact stored yet                                                  | No-op (modulo watchdog — see §4.3). Wait for the agent round to produce the `SelfEvaluationReport`. The verdict path in `SprintArtifactService.StoreArtifactAsync` (P1.4 design §4.3) already updates `SelfEvalAttempts`/`LastSelfEvalVerdict` when the artifact lands. |
| `ReadyForStageAdvance`           | sprint.CurrentStage == "Implementation" AND latest `SelfEvaluationReport` artifact has `OverallVerdict == AllPass`                                               | **Action 2**: invoke `SprintStageService.AdvanceStageAsync(sprintId, force: false)` AND wake the orchestrator. The existing artifact + verdict gates (`SprintStageService.cs:130–179`) will permit this transition (provided `_signOffRequiredStages` does not include "Implementation" — see §3.3). |
| `FinalSynthesisInProgress`       | sprint.CurrentStage == "FinalSynthesis" AND no `SprintReport` artifact at FinalSynthesis                                                                          | **Action 3**: ensure the FinalSynthesis preamble (existing in `SprintPreambles.StagePreambles`) is being injected by waking the orchestrator. The driver itself does not call an agent; it advances the cursor and lets the next round's Planner produce the artifact. The watchdog (§4.3) bounds how long this state can persist. |
| `ReadyForCompletion`             | sprint.CurrentStage == "FinalSynthesis" AND `SprintReport` artifact at FinalSynthesis exists                                                                      | **Action 4**: invoke `SprintService.CompleteSprintAsync(sprintId, force: false)`. The existing artifact gate (`SprintService.cs:236–247`) will permit this transition. No orchestrator wake needed — the sprint is now terminal. |

The driver's job per invocation is: classify into one of these six states, perform the action if any, and return. **It only ever fires one action per invocation**; transitions cascade across rounds, not within a single invocation. This keeps every transition observable in `ActivityEventEntity` and prevents the "ceremony silently bulldozed past a failure" failure mode.

### 3.1 Predicate details

**"Every task has terminal status"**: scope is the set of `TaskEntity` rows where `SprintId == sprintId`; terminal means `Status ∈ {Completed, Cancelled}`. This **must** match `RoomLifecycleService.TerminalTaskStatuses` (`RoomLifecycleService.cs:17–21`), which is the set `CheckImplementationPrerequisitesAsync` enforces (`SprintStageService.cs:451–476`). The `task count ≥ 1 with at least one non-Cancelled task` check excludes the empty-sprint case and the all-cancelled case to mirror P1.4 design §3.2's "self-eval on zero non-cancelled tasks is structurally meaningless." A sprint where every task was cancelled is treated as `NotApplicable` and must be terminated by the operator with `force=true` (this is correct behaviour: zero work to evaluate ≠ ceremony-eligible; the operator has explicit awareness via the §6.4 force-complete log line).

**"Latest `SelfEvaluationReport` artifact has `OverallVerdict == AllPass`"**: same query as `SprintStageService.cs:147–178` — order by `CreatedAt desc, Id desc`, deserialize `Content` as `SelfEvaluationReport`, check `OverallVerdict`. The driver does not duplicate parsing logic; it shares the same query helper extracted to a new private method on `SprintArtifactService` (or reads through a new `ISprintArtifactService.GetLatestSelfEvalVerdictAsync(sprintId, ct)` accessor — implementer's choice).

**"No `SprintReport` artifact at FinalSynthesis"**: `SELECT 1 FROM SprintArtifacts WHERE SprintId = @id AND Stage = 'FinalSynthesis' AND Type = 'SprintReport' LIMIT 1`. Same shape as `SprintService.cs:236–247`.

### 3.2 Why the predicates use existing columns only

`SelfEvaluationInFlight` is read but not written by the driver — it is set/cleared by the existing P1.4 verdict path (design doc §4.3, in `SprintArtifactService.StoreArtifactAsync`). This means:

- The driver does not race the verdict path: the verdict path is the writer; the driver reads after the verdict path commits.
- The classification predicates need no new columns: every column referenced in the §3 table already exists per `SprintEntity.cs:12–71`. The watchdog (§4.3) and the `force=true` hardening (§6.4) do introduce one new nullable column (`FinalSynthesisEnteredAt`); see §6.2 for the migration shape.
- The driver is **safe under concurrent invocation** by construction:
  - On the same sprint, `MarkSprintBlockedAsync` uses a conditional `ExecuteUpdateAsync` (`SprintService.cs:451–455`) — the loser of a block race is a clean no-op.
  - `AdvanceStageAsync` and `CompleteSprintAsync` use standard EF tracking (load → validate → mutate → save), NOT `ExecuteUpdateAsync`. Two concurrent driver invocations that both pass the gates will both write the same target value (both advance to the same next stage; both flip Status to "Completed"). The DB writes converge; the duplicate `SprintStageAdvanced` / `SprintCompleted` events and duplicate `SyncWorkspaceRoomsToStageAsync` calls are noisy-but-idempotent (room transitions are idempotent because the freeze pass guards on existing room state). For v1, this convergent-write behaviour is acceptable; if it becomes problematic, a follow-up adds a sprint-scoped advisory lock or converts these to conditional updates. The driver MUST NOT rely on either method being atomically conditional — that's a §8 implementation note.
  - Stale-state `InvalidOperationException`s from `AdvanceStageAsync`/`CompleteSprintAsync` (e.g., "status is not Active", "already at final stage", "sprint is awaiting sign-off") MUST be classified as **NoOp**, not Block — see §4.2 stale-exception classifier. A loser of a benign race must never auto-block a healthy sprint.

### 3.3 Sign-off-configured environments

`SprintStageService` reads `SprintStageOptions.SignOffRequiredStages` from `appsettings.json` (`SprintStageService.cs:88–89`). The default is `[]` (no sign-off gating; `appsettings.json:62`). When an operator configures `SignOffRequiredStages` to include "Implementation" (or any stage prior to FinalSynthesis), `AdvanceStageAsync` does NOT change `CurrentStage` — it sets `AwaitingSignOff = true` and stores `PendingStage` (`SprintStageService.cs:189–214`). The driver MUST handle this explicitly:

- After the driver invokes `AdvanceStageAsync` from `ReadyForStageAdvance`, it re-reads the sprint. If `AwaitingSignOff == true`, the action emitted is `RequestedSignOff` (a new `TerminalStageAction` value), not `AdvancedToFinalSynthesis`. The next driver invocation lands in `NotApplicable` (per `AwaitingSignOff` check in `NotApplicable` predicate) and waits for human approval. When the operator approves sign-off via the existing endpoint, `AwaitingSignOff` flips back to false and the next round's driver re-evaluates and either fires `AdvancedToFinalSynthesis` or recognises that the stage already advanced (no-op).
- This behaviour preserves the operator's explicit choice to gate the transition. The driver does not invent its own bypass.

---

## 4. Control-flow design

### 4.1 New service: `ISprintTerminalStageHandler`

```csharp
namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// After every agent round, classifies the captured sprint's terminal-stage state
/// and fires at most one ceremony-chain transition per invocation. Stateless;
/// idempotent on retry; fails open (logs and returns on any internal error).
///
/// Wired into <see cref="ConversationRoundRunner"/> immediately after
/// IncrementRoundCountersAsync, alongside (and BEFORE) the SelfDriveDecisionService
/// invocation — the driver's job is to short-circuit further self-drive work
/// when the team is actually done, so it must run first.
/// </summary>
public interface ISprintTerminalStageHandler
{
    /// <summary>
    /// Returns the action taken (or NoOp) for observability/test assertions.
    /// Never throws — internal exceptions are logged and converted to
    /// <see cref="TerminalStageAction.NoOp"/>.
    /// </summary>
    Task<TerminalStageAction> AdvanceIfReadyAsync(string sprintId, CancellationToken ct = default);
}

public enum TerminalStageAction
{
    NoOp,
    StartedSelfEval,
    AdvancedToFinalSynthesis,
    RequestedSignOff,          // AwaitingSignOff was set; operator must approve
    SteeredToFinalSynthesis,   // FinalSynthesisInProgress → woke orchestrator; no DB change
    CompletedSprint,
    Blocked,                   // ceremony step failed → MarkSprintBlockedAsync called
}
```

**Caller contract**: when `AdvanceIfReadyAsync` returns any value other than `NoOp`, the caller MUST skip the subsequent `SelfDriveDecisionService.DecideAsync` call for this trigger. The driver has either advanced the chain (in which case scheduling another self-drive continuation is wasted work or, worse, can trip a stage-cap immediately after `StartedSelfEval` and block the sprint — see §4.4 and §4.5) or has already blocked the sprint (in which case the self-drive decision would no-op anyway, but skipping makes the intent explicit). See §4.4 for the wiring.

### 4.2 Implementation outline (`SprintTerminalStageHandler`)

```csharp
public async Task<TerminalStageAction> AdvanceIfReadyAsync(string sprintId, CancellationToken ct)
{
    try
    {
        var sprint = await _sprintService.GetSprintByIdAsync(sprintId, ct);
        if (sprint is null) return TerminalStageAction.NoOp;

        // State: NotApplicable
        if (sprint.Status != "Active" || sprint.BlockedAt is not null || sprint.AwaitingSignOff)
            return TerminalStageAction.NoOp;

        if (sprint.CurrentStage == "Implementation")
        {
            // Predicate: every task in {Completed, Cancelled}, ≥1 non-cancelled task
            var snapshot = await _taskQueryService.GetSprintTaskStatusSnapshotAsync(sprintId, ct);
            if (!snapshot.AllTerminal || snapshot.NonCancelledCount == 0)
                return TerminalStageAction.NoOp;  // ImplementationInProgress

            // State: ReadyForSelfEval / SelfEvalInFlight / ReadyForStageAdvance
            var verdict = await _artifactService.GetLatestSelfEvalVerdictAsync(sprintId, ct);
            if (verdict == SelfEvaluationOverallVerdict.AllPass)
            {
                // Action 2: ReadyForStageAdvance
                return await TryAdvanceStageAsync(sprintId, ct);
            }
            if (sprint.SelfEvaluationInFlight)
                return await TickSelfEvalWatchdogAsync(sprintId, ct);  // §4.3

            // Action 1: ReadyForSelfEval
            return await TryStartSelfEvalAsync(sprintId, ct);
        }

        if (sprint.CurrentStage == "FinalSynthesis")
        {
            var hasReport = await _artifactService.HasArtifactAsync(
                sprintId, stage: "FinalSynthesis", type: "SprintReport", ct);
            if (hasReport)
            {
                // Action 4: ReadyForCompletion
                return await TryCompleteSprintAsync(sprintId, ct);
            }
            // Action 3: FinalSynthesisInProgress — wake orchestrator AND tick watchdog (§4.3)
            return await SteerToFinalSynthesisAsync(sprintId, ct);
        }

        return TerminalStageAction.NoOp;
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex,
            "SprintTerminalStageHandler.AdvanceIfReadyAsync failed for sprint {SprintId}; " +
            "ceremony chain will retry on next round (fail-open)", sprintId);
        return TerminalStageAction.NoOp;
    }
}
```

#### 4.2.1 The wake mechanism

After every state-changing action — `StartedSelfEval`, `AdvancedToFinalSynthesis`, `SteeredToFinalSynthesis` — the handler **must** wake the orchestrator for every active room in the sprint's workspace. Without the wake, the next agent round may never fire (the existing `SelfDriveDecisionService` returns early on PASS-only outcomes — `SelfDriveDecisionService.cs:145–147` — and `RunSelfEvalHandler` itself does not wake — `RunSelfEvalHandler.cs:19–23`).

PR3 of P1.4 already established the wake pattern in `SprintController.TryWakeOrchestratorForSprintAsync` (`SprintController.cs:613–650`): query the `Rooms` table for non-Archived, non-Completed rooms in the sprint's `WorkspacePath`, then call `IAgentOrchestrator.HandleHumanMessage(roomId)` for each, with per-room try/catch and warning logs. The driver mirrors this **exactly**. The implementation PR should extract that helper into a shared service (e.g., `IOrchestratorWakeService.WakeWorkspaceRoomsForSprintAsync(sprintId, ct)`) so both the controller endpoint AND the driver share one path; without that extraction the driver duplicates the query.

`CompletedSprint` and `Blocked` do NOT wake — the sprint is terminal/paused and further rounds are unwanted.

#### 4.2.2 The stale-state exception classifier

Each `Try*` helper wraps its single action in a try/catch with **explicit classification** of the resulting exception. Stale-state losses must NEVER block:

```csharp
private async Task<TerminalStageAction> TryAdvanceStageAsync(string sprintId, CancellationToken ct)
{
    try
    {
        await _stageService.AdvanceStageAsync(sprintId, force: false);
        var sprint = await _sprintService.GetSprintByIdAsync(sprintId, ct);
        if (sprint?.AwaitingSignOff == true)
            return TerminalStageAction.RequestedSignOff;
        await WakeWorkspaceRoomsAsync(sprintId, ct);
        return TerminalStageAction.AdvancedToFinalSynthesis;
    }
    catch (InvalidOperationException ex) when (IsStaleStateException(ex))
    {
        // Benign race: another invocation already advanced (or sprint moved out of
        // Implementation under us). Re-read and let the next round re-classify.
        _logger.LogInformation(
            "AdvanceStageAsync stale-state for sprint {SprintId}: {Message} — treating as NoOp",
            sprintId, ex.Message);
        return TerminalStageAction.NoOp;
    }
    catch (Exception ex)
    {
        await BlockWithReasonAsync(sprintId, $"advance-stage: {ex.Message}", ct);
        return TerminalStageAction.Blocked;
    }
}

// Stale-state classifier: matches the message prefixes the existing services throw
// when their preconditions don't hold. Keep this list narrow and explicit; new
// stale-state messages from those services must be added here in lockstep.
private static bool IsStaleStateException(InvalidOperationException ex) =>
    ex.Message.Contains("status is", StringComparison.Ordinal)            // "status is {Cancelled|Completed}"
    || ex.Message.Contains("already at the final stage", StringComparison.Ordinal)
    || ex.Message.Contains("awaiting user sign-off", StringComparison.Ordinal)
    || ex.Message.Contains("is already", StringComparison.Ordinal);       // "Sprint X is already Completed"
```

The same shape applies to `TryStartSelfEvalAsync` (catch "Sprint must be in Implementation stage" / "Sprint is already blocked" / "Sprint is in self-eval already" → NoOp) and `TryCompleteSprintAsync` (catch "Sprint X is already Completed/Cancelled" → NoOp). Implementer expands the classifier as needed; the rule is: **a benign race ⇒ NoOp; a structural failure ⇒ Block**.

### 4.3 Stall watchdogs (FinalSynthesis AND self-eval)

Two sprint states can stall indefinitely without code defect: `SelfEvalInFlight` (agents may not produce the `SelfEvaluationReport`) and `FinalSynthesisInProgress` (agents may not produce the `SprintReport`). Both need a watchdog. The watchdogs only fire from per-round driver invocations — but because the driver's wake mechanism (§4.2.1) wakes the orchestrator on every state-changing action, the room receives a fresh round shortly after each transition, and the next driver invocation tick catches the watchdog. The combination is:

> Driver acts → wakes rooms → next round runs → driver re-invoked → watchdog evaluated.

So the watchdog has a heartbeat as long as the wake mechanism succeeds. If wakes fail repeatedly (logged warning per §4.2.1), the watchdog can stall — operators will see a sprint stuck in self-eval / FinalSynthesis indefinitely with watchdog-warning logs at orchestration, and can intervene manually. (A future enhancement could add a low-frequency timer/scheduler as a backstop; out of scope for v1 per §9.)

#### 4.3.1 FinalSynthesis stall watchdog

A new column `SprintEntity.FinalSynthesisEnteredAt : DateTime?` records when a sprint first entered FinalSynthesis. It is **set on entry** by `SprintStageService.AdvanceStageAsync` (when transitioning into FinalSynthesis) and by `SprintService.CompleteSprintAsync` when `force=true` is used to leap from a non-terminal stage (§6.4). It is **never cleared** — the value is preserved on the row for audit purposes (operators can query "how long did sprint X spend in FinalSynthesis").

Each `SteerToFinalSynthesisAsync` invocation computes `now - FinalSynthesisEnteredAt`; if it exceeds `Orchestrator:TerminalStage:FinalSynthesisStallMinutes` (default **30**), the driver blocks the sprint:

```
MarkSprintBlockedAsync(sprintId,
    "Terminal-stage ceremony failed: SprintReport not produced within 30 minutes " +
    "of entering FinalSynthesis.");
```

Otherwise it wakes the workspace rooms and returns `SteeredToFinalSynthesis`.

#### 4.3.2 Self-eval stall watchdog

The existing `SprintEntity.LastSelfEvalAt` column (`SprintEntity.cs:54`) is set to `now` by P1.4's verdict path when a `SelfEvaluationReport` lands. We reuse it as the watchdog timestamp: if the driver fired `StartedSelfEval` and `LastSelfEvalAt` has not advanced after `Orchestrator:TerminalStage:SelfEvalStallMinutes` (default **15**) **AND** `SelfEvaluationInFlight == true`, the sprint is blocked:

```
MarkSprintBlockedAsync(sprintId,
    "Terminal-stage ceremony failed: SelfEvaluationReport not produced within 15 minutes " +
    "of self-eval start.");
```

There is one bootstrap problem: the very first self-eval has `LastSelfEvalAt == null`, so we can't compute "time since". The driver records a separate per-invocation marker — when transitioning `ReadyForSelfEval → SelfEvalInFlight`, it stamps `SprintEntity.SelfEvalStartedAt : DateTime?` (a second new nullable column; same migration shape as `FinalSynthesisEnteredAt`). The watchdog computes `now - max(SelfEvalStartedAt, LastSelfEvalAt)`. `SelfEvalStartedAt` is cleared by the verdict path when a report lands AND `OverallVerdict == AllPass` (the chain has progressed); for AnyFail/Unverified verdicts it is RE-stamped on the next `StartedSelfEval` (giving the team a fresh budget after the verdict path re-opens Implementation per P1.4 §4.3.b).

The **15-minute** default is shorter than FinalSynthesis's 30 because the self-eval prompt is mechanical (per-task PASS/FAIL/UNVERIFIED with evidence) — half an hour without a report indicates the team is genuinely stuck, not just deliberating.

#### 4.3.3 Why these are watchdogs, not timers

The watchdogs are evaluated **inline during the per-round driver invocation**, not on a background timer. This matches the no-scheduler principle (§9) and keeps the design surface small. The wake mechanism (§4.2.1) ensures rounds happen frequently enough during ceremony to evaluate the watchdog. If the orchestrator queue is genuinely stuck (no rooms producing rounds at all), the watchdog will not fire — but in that case the entire system is stuck, which is a higher-priority incident than a stalled sprint.

### 4.4 Wiring into `ConversationRoundRunner`

In `Services/ConversationRoundRunner.cs`, immediately AFTER the existing `IncrementRoundCountersAsync` block (`ConversationRoundRunner.cs:299–325`) and BEFORE `InvokeSelfDriveDecisionAsync` (line 334–335), the driver runs and the self-drive decision is **conditionally skipped** based on the result:

```csharp
// Terminal-stage check: if the team has finished implementation work and
// the ceremony chain can advance, fire the next transition. Runs BEFORE
// self-drive decision; if the driver took ANY action (including Block),
// self-drive is skipped because (a) StartedSelfEval/AdvancedToFinal/
// SteeredToFinal already woke the rooms, so a continuation enqueue is
// redundant; (b) more importantly, scheduling a continuation could trip
// the stage round cap (Implementation: 20/20) immediately after
// StartedSelfEval, calling MarkSprintBlockedAsync and aborting the
// ceremony before the agent gets a chance to produce the report. Fail-open
// inside the helper.
var terminalAction = TerminalStageAction.NoOp;
if (sprintIdAtRunStart is not null)
{
    terminalAction = await InvokeTerminalStageHandlerAsync(
        sprintIdAtRunStart, CancellationToken.None);
}

// Skip self-drive decision when the terminal handler already steered the
// sprint. It would either no-op (sprint now BlockedAt or Status=Completed)
// or actively cause harm (cap-trip blocking the just-started ceremony).
if (terminalAction == TerminalStageAction.NoOp)
{
    await InvokeSelfDriveDecisionAsync(
        roomId, sprintIdAtRunStart, roundOutcome, CancellationToken.None);
}
```

`InvokeTerminalStageHandlerAsync` is a 10-line private helper modeled on `InvokeSelfDriveDecisionAsync` (line 346–360): create scope, resolve `ISprintTerminalStageHandler`, call `AdvanceIfReadyAsync`, swallow any exception with a warning log and return `TerminalStageAction.NoOp` to keep self-drive flow on the safe (skip) side when the driver itself crashed.

**The conditional skip is critical, not optional**: without it, a sprint that just transitioned `Implementation → SelfEvalInFlight` via `StartedSelfEval` would have its self-drive cap re-checked against the freshly-bumped `RoundsThisStage` counter; if the team was already at 19/20, the next continuation would push 20/20 and `SelfDriveDecisionService` would auto-block the sprint with `"Stage round cap reached for Implementation: 20/20"` — interrupting the ceremony chain at exactly the wrong moment. The skip preserves the rounds budget for the agent to actually produce the report.

### 4.5 The cap-trip interaction

When `SelfDriveDecisionService` trips a cap and calls `MarkSprintBlockedAsync` (`SelfDriveDecisionService.cs:121–162`), the sprint enters `BlockedAt != null`. The driver then short-circuits to `NotApplicable` on subsequent rounds. This is correct: a cap-tripped sprint is going in circles, and the human must decide whether to (a) raise the cap and resume, (b) fix something and unblock, or (c) `force=true` cancel/complete. Auto-firing self-eval on a cap-tripped sprint would produce noise, not signal.

When the human unblocks via `POST /api/sprints/{id}/unblock`, the driver re-evaluates next round. If the team has actually finished work (all tasks terminal), it fires the chain; if not, normal self-drive resumes. No special unblock-side coordination is required.

### 4.6 Failure-mode taxonomy

| Failure                                                                           | Detection                                                                                | Behaviour                                                                                                                                                                                  |
|-----------------------------------------------------------------------------------|------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Self-eval handler throws (e.g., genuine internal error)                           | `TryStartSelfEvalAsync` catch (non-stale)                                                | `MarkSprintBlockedAsync(…, "Terminal-stage ceremony failed: start-self-eval: <message>")`; `TerminalStageAction.Blocked`.                                                                  |
| **Stale-state race** (another invocation already advanced/completed/started)      | `IsStaleStateException` classifier in `Try*` helpers (§4.2.2)                            | **`TerminalStageAction.NoOp`**. Loser of the race is silent; next round re-classifies. Never auto-blocks a healthy sprint.                                                                 |
| **Self-eval stalled** (in-flight, no report after 15 min)                         | Self-eval watchdog (§4.3.2)                                                              | Block with "Terminal-stage ceremony failed: SelfEvaluationReport not produced within 15 minutes of self-eval start.". Operator decides to extend, prompt the team, or `force=true`.        |
| Self-eval cap exceeded (3 attempts, all AnyFail/Unverified)                       | P1.4 verdict path already auto-blocks; driver sees `BlockedAt != null` → `NotApplicable` | Existing P1.4 behaviour. Driver does nothing. Human unblocks → driver resumes.                                                                                                              |
| `AdvanceStageAsync` throws non-stale (artifact gate or verdict gate missed unexpectedly) | `TryAdvanceStageAsync` catch (non-stale)                                            | Block with "Terminal-stage ceremony failed: advance-stage: <message>". Operator inspects artifacts + verdict.                                                                              |
| `AdvanceStageAsync` requested sign-off (operator-configured)                      | `sprint.AwaitingSignOff == true` after the call (§3.3)                                   | `TerminalStageAction.RequestedSignOff`. Driver waits in `NotApplicable` until human approves.                                                                                              |
| FinalSynthesis stall (no `SprintReport` after 30 min)                             | FinalSynthesis watchdog (§4.3.1)                                                         | Block with "Terminal-stage ceremony failed: SprintReport not produced within 30 minutes of entering FinalSynthesis.". Operator decides whether to extend, prompt, or `force=true` complete. |
| `CompleteSprintAsync` throws non-stale (e.g., artifact deleted mid-ceremony)     | `TryCompleteSprintAsync` catch (non-stale)                                               | Block with "Terminal-stage ceremony failed: complete-sprint: <message>".                                                                                                                   |
| Driver itself crashes (DI failure, unexpected null, etc.)                         | Outer try/catch in `AdvanceIfReadyAsync` and in the round-runner wiring                  | Logged warning; driver returns `NoOp`; next round retries. No state change. Round runner unaffected; self-drive decision skipped (safe-side default).                                       |
| Operator force-completes via API                                                  | Operator's call sets `force=true`; driver reads `Status="Completed"` → `NotApplicable`   | No interference. Existing escape valve preserved. §6.4 hardening additionally advances `currentStage` so the snapshot is internally consistent.                                            |
| Wake mechanism fails (orchestrator dispatch error)                                | `WakeWorkspaceRoomsAsync` per-room try/catch (§4.2.1)                                    | Logged warning per failed room; other rooms still woken. If ALL wakes fail, the sprint can stall — operator sees the warning and intervenes. Watchdog still ticks on the next round if any. |

---

## 5. New events and surfaces

### 5.1 No new `ActivityEventType` values needed

Each ceremony step already emits its own event:

- Self-eval start → `ActivityEventType.SelfEvalCompleted` already fires from the verdict path when the report lands.
- Stage advance → `SprintStageAdvanced` already fires from `AdvanceStageAsync`.
- Sprint completion → `SprintCompleted` already fires from `CompleteSprintAsync`.
- Ceremony failure → `SprintBlocked` already fires from `MarkSprintBlockedAsync`.

The driver does not introduce a parallel "TerminalStageDriverFired" event. The downstream Discord/notification surface is unchanged.

### 5.2 Observability — log lines

Every driver invocation that does anything other than `NoOp` logs at `Information`:

```
SprintTerminalStageHandler advanced sprint {SprintId} #{Number}: {Action} ({CurrentStage} → {NextStage}; selfEvalAttempts={N}; verdict={V})
```

`NoOp` returns at `Trace` level only (do not flood `Information` — every round produces one of these on a healthy sprint that's not yet ready). `Blocked` logs at `Warning` with the full `BlockReason`.

### 5.3 Configuration

Under a new `Orchestrator:TerminalStage` section in `appsettings.json`:

```json
{
  "Orchestrator": {
    "TerminalStage": {
      "FinalSynthesisStallMinutes": 30,
      "SelfEvalStallMinutes": 15
    }
  }
}
```

No other knobs. Self-eval cap counts (`MaxSelfEvalAttempts` etc.) are owned by P1.4 (`Orchestrator:SelfEval`); driver respects them transparently by reading the verdict path's outputs.

---

## 6. Required peripheral changes

These are NOT the driver itself but are required for the design to land cleanly. Each is small.

### 6.1 New `ISprintArtifactService` accessors

```csharp
Task<SelfEvaluationOverallVerdict?> GetLatestSelfEvalVerdictAsync(
    string sprintId, CancellationToken ct = default);

Task<bool> HasArtifactAsync(
    string sprintId, string stage, string type, CancellationToken ct = default);
```

`GetLatestSelfEvalVerdictAsync` extracts the query already inlined in `SprintStageService.cs:147–178` so both call sites use one implementation. `HasArtifactAsync` is a 3-line `_db.SprintArtifacts.AnyAsync(…)` wrapper. Both are 🟢 additive.

### 6.2 New `SprintEntity` columns

```csharp
public DateTime? FinalSynthesisEnteredAt { get; set; }   // Set on FIRST entry to FinalSynthesis; never cleared (audit signal).
public DateTime? SelfEvalStartedAt { get; set; }         // Set when driver fires StartedSelfEval; cleared by P1.4 verdict path on AllPass.
```

**Invariant**: `FinalSynthesisEnteredAt` is **set on entry, never cleared**. Set by:

- `SprintStageService.AdvanceStageAsync` when transitioning *into* FinalSynthesis (one new line).
- `SprintService.CompleteSprintAsync` when `force=true` is used to leap from a non-terminal stage (§6.4 — same line that advances `currentStage`).

It is **NOT cleared** by `CompleteSprintAsync` or `CancelSprintAsync`. The value persists on the row for audit ("how long was sprint X in FinalSynthesis"). This keeps invariants single-writer (set-once) and lets §6.5 tests assert non-null after force-complete without contradiction.

**Invariant**: `SelfEvalStartedAt` is set by the driver when transitioning `ReadyForSelfEval → SelfEvalInFlight` (action `StartedSelfEval`). It is cleared by the P1.4 verdict path in `SprintArtifactService.StoreArtifactAsync` ONLY when `OverallVerdict == AllPass` (the chain has progressed); on AnyFail/Unverified it is left set, then RE-stamped on the next `StartedSelfEval` after the team re-attempts (giving each attempt a fresh stall window).

Both columns are additive 🟢 — defaults are null; existing rows are unaffected; existing in-flight FinalSynthesis sprints (e.g., the broken Sprint #11) will simply not be watchdog-eligible until they re-enter, which is acceptable (those sprints already need operator attention; the §10 backfill in §6.6 handles them).

### 6.3 New `ITaskQueryService` accessor for the task-status snapshot

```csharp
record SprintTaskStatusSnapshot(int TotalCount, int NonCancelledCount, bool AllTerminal);

Task<SprintTaskStatusSnapshot> GetSprintTaskStatusSnapshotAsync(
    string sprintId, CancellationToken ct = default);
```

`ITaskQueryService` (`Services/Contracts/ITaskQueryService.cs`) is the established read-side surface for tasks; this accessor belongs there alongside the existing query methods. Implementation is a single `GROUP BY Status` query against `_db.Tasks` (or two narrow `CountAsync` calls if the implementer prefers to keep it framework-agnostic). Returns the three values the driver needs in one DB round-trip.

### 6.4 `CompleteSprintAsync(force=true)` consistency hardening (finding 3 fix)

In `SprintService.CompleteSprintAsync` (`SprintService.cs:221–283`), when `force=true` AND `sprint.CurrentStage != "FinalSynthesis"`, set `sprint.CurrentStage = "FinalSynthesis"` and `sprint.FinalSynthesisEnteredAt = DateTime.UtcNow` (only if currently null — preserves the set-once invariant if the sprint had legitimately entered FinalSynthesis previously) in the same transaction as the status flip. This addresses finding 3 directly: the `Completed`/`currentStage=Implementation` inconsistency cannot recur because `force=true` now produces a coherent terminal snapshot. Log a warning when this hardening fires:

```
Sprint #{Number} force-completed at non-terminal stage {OldStage}; advancing currentStage to FinalSynthesis to preserve invariant.
```

This is a small behavior change (one extra column write + one log line) scoped to the `force=true` path so non-force completions are unaffected. Existing tests that call `CompleteSprintAsync(force: true)` and assert `Status == "Completed"` are unaffected; tests that assert `currentStage` after a `force=true` call from non-terminal stages will need to expect `"FinalSynthesis"` (a small migration documented in the implementation PR).

**Caller-impact check** (verify in implementation PR):

- `Controllers/SprintController.cs:271` — bare API call; no assertion on `currentStage` post-call. Unaffected.
- `Commands/Handlers/CompleteSprintHandler.cs:71` — bare command handler call; no assertion. Unaffected.
- `tests/AgentAcademy.Server.Tests/SprintServiceTests.cs` — multiple `CompleteSprintAsync(…, force: true)` callers. Audit each: tests asserting `Status == "Completed"` are fine; tests asserting `currentStage == "Implementation"` (or any non-`FinalSynthesis` value) post-call must be updated. The implementation PR's Spec Change Proposal will list the affected test names explicitly.

### 6.5 Test infrastructure additions

A new test class `SprintTerminalCeremonyTests` (in `tests/AgentAcademy.Server.Tests/`) drives the end-to-end chain:

1. **Happy path**: Create sprint, advance to Implementation, create N tasks all in `Completed` status → invoke driver → assert `StartedSelfEval`, `SelfEvaluationInFlight == true`, and that wake was called for each active room.
2. **AnyFail loop**: Continue from (1): store `SelfEvaluationReport` with `OverallVerdict=AnyFail` → invoke driver → assert `NoOp` (re-open Implementation per P1.4 §4.3.b is automatic; driver waits) → store `OverallVerdict=AllPass` → invoke driver → assert `AdvancedToFinalSynthesis`, `currentStage == "FinalSynthesis"`, and `FinalSynthesisEnteredAt != null`.
3. **Completion**: Continue from (2): invoke driver immediately → assert `SteeredToFinalSynthesis` (no `SprintReport` yet) and that wake was called → store `SprintReport` artifact → invoke driver → assert `CompletedSprint`, `Status == "Completed"`, `currentStage == "FinalSynthesis"`.
4. **Cap-trip path**: Drive sprint to Implementation cap → assert driver returns `NoOp` (because `BlockedAt != null`) → operator unblocks → tasks all marked Completed → assert driver fires `StartedSelfEval`.
5. **FinalSynthesis stall**: Advance into FinalSynthesis, fast-forward `FinalSynthesisEnteredAt - 31 minutes`, invoke driver → assert `Blocked` with the 30-min stall message.
6. **Self-eval stall**: Fire `StartedSelfEval` (sets `SelfEvalStartedAt`), fast-forward `SelfEvalStartedAt - 16 minutes` with `SelfEvaluationInFlight==true` and no report stored, invoke driver → assert `Blocked` with the 15-min stall message.
7. **`force=true` consistency**: Complete sprint with `force=true` while at Implementation → assert `currentStage == "FinalSynthesis"`, `FinalSynthesisEnteredAt != null`, and the warning log was emitted.
8. **`Approved` is non-terminal**: Create sprint, advance to Implementation, create N tasks all in `Approved` status (PR approved but not merged) → invoke driver → assert `NoOp` (`ImplementationInProgress`). This locks the predicate alignment with `RoomLifecycleService.TerminalTaskStatuses`.
9. **All-cancelled sprint**: Create sprint with N tasks all `Cancelled` → invoke driver → assert `NoOp` (`NonCancelledCount == 0`). Confirms operator-only force-completion is required.
10. **Stale-state race**: Mock concurrent `AdvanceStageAsync` calls; assert one returns `AdvancedToFinalSynthesis` and the loser returns `NoOp` (NOT `Blocked`). Confirms the stale-state classifier behaves correctly.
11. **Sign-off-configured environment**: Configure `SignOffRequiredStages = ["Implementation"]`, advance to `ReadyForStageAdvance` → invoke driver → assert `RequestedSignOff` and `AwaitingSignOff == true` → invoke driver again → assert `NoOp` (per `NotApplicable` predicate) → operator approves sign-off → invoke driver → assert `AdvancedToFinalSynthesis`.
12. **Self-drive skip**: Wire the round-runner with mock `ISelfDriveDecisionService`, simulate driver returning `StartedSelfEval` → assert `SelfDriveDecisionService.DecideAsync` is **not** called this trigger. Locks the §4.4 conditional skip.

The existing `SprintServiceEventTests`, `SprintServiceTests`, and `SprintArtifactServiceTests` cover the underlying primitives; `SprintTerminalCeremonyTests` covers the orchestration behaviour the driver introduces.

### 6.6 One-shot historical backfill (for §7 criterion 4 and #1 finding-3 closure)

The product currently has historical Completed sprints with `currentStage != "FinalSynthesis"` (Sprint #11 documented in §1; possibly others — implementer should run a count query as the first step of the implementation PR). These rows cannot be repaired by code change alone — the new `force=true` hardening (§6.4) only affects future completions. A one-shot SQL repair runs in the implementation PR's migration:

```sql
-- Backfill historical Completed sprints to satisfy invariant Status='Completed' ⇒ CurrentStage='FinalSynthesis'.
-- Stamps FinalSynthesisEnteredAt = CompletedAt as a best-effort audit value (we don't
-- know when those sprints actually entered FinalSynthesis; CompletedAt is the most
-- defensible proxy). Logs a warning per row repaired.
UPDATE Sprints
SET CurrentStage = 'FinalSynthesis',
    FinalSynthesisEnteredAt = COALESCE(FinalSynthesisEnteredAt, CompletedAt)
WHERE Status = 'Completed'
  AND CurrentStage <> 'FinalSynthesis';
```

The migration logs `[Sprints backfill] Repaired N historical Completed sprints with non-terminal currentStage.` for operator visibility. After this runs once, criterion 4 holds for the entire database (historical + post-deploy).

If preferred, the implementer may instead ship the repair as a separate operator-runnable script in `scripts/` and narrow criterion 4 to "for every Completed sprint with `CompletedAt > <deploy_date>`". Either approach satisfies the criterion; the in-migration repair is recommended because it's automatic and the audit signal is preserved.

---

## 7. Acceptance criteria (covers the three sub-findings from the roadmap entry)

This design is "done" when ALL of the following hold on a real sprint that completes naturally (no `force=true`):

| # | Criterion | Verification | Maps to finding |
|---|-----------|--------------|-----------------|
| 1 | A sprint that drives to "all tasks `Completed` or `Cancelled`" auto-fires self-eval within one agent round of the predicate becoming true. | `SELECT SelfEvalAttempts FROM Sprints WHERE Id = @id` returns ≥ 1; `SELECT 1 FROM SprintArtifacts WHERE SprintId = @id AND Type = 'SelfEvaluationReport'` returns at least one row. `SprintTerminalCeremonyTests.SelfEvalAutoFires_OnAllTasksTerminal`. | Finding 1 (selfEvalAttempts=0 on every sprint) |
| 2 | A sprint with a stored `SelfEvaluationReport` of `OverallVerdict=AllPass` auto-advances Implementation → FinalSynthesis within one round (assuming `SignOffRequiredStages` does not gate Implementation; otherwise sign-off is requested). | `SELECT CurrentStage FROM Sprints WHERE Id = @id` returns `FinalSynthesis`; `ActivityEventEntity` row of type `SprintStageAdvanced` exists. `SprintTerminalCeremonyTests.AdvancesToFinalSynthesis_OnAllPass`. | Sub-finding (auto-advance enabling) |
| 3 | A sprint at FinalSynthesis with a stored `SprintReport` artifact auto-completes (no `force=true` required) within one round. | `SELECT Status, CurrentStage FROM Sprints WHERE Id = @id` returns `(Completed, FinalSynthesis)`; `ActivityEventEntity` row of type `SprintCompleted` exists. `SprintTerminalCeremonyTests.AutoCompletes_OnSprintReport`. | Finding 2 (no SprintReport produced) |
| 4 | After the §6.6 backfill runs, the state-machine invariant `Status="Completed" ⇒ CurrentStage="FinalSynthesis"` holds **for every Completed sprint in the database**, on both natural and `force=true` paths going forward. | `SELECT COUNT(*) FROM Sprints WHERE Status='Completed' AND CurrentStage <> 'FinalSynthesis'` returns 0 immediately after the migration runs AND remains 0 after a representative sample of post-deploy completions (natural + force). `SprintServiceTests.ForceComplete_AdvancesCurrentStage_ToFinalSynthesis` plus the migration's logged repair count. | Finding 3 (status=Completed, currentStage=Implementation) |
| 5 | A sprint where self-eval fails the cap (3 attempts, all AnyFail) ends in `BlockedAt != null` with `BlockReason` containing "Self-eval failed", **with no driver interference** (P1.4 verdict path owns this; driver must not double-block or override). | `SprintTerminalCeremonyTests.DriverDefersTo_P14_OnSelfEvalCapExceeded`. | Failure-mode coverage |
| 6 | A sprint stalled at FinalSynthesis without producing `SprintReport` is auto-blocked after the configured stall window with the exact message in §4.3.1. | `SprintTerminalCeremonyTests.AutoBlocks_OnFinalSynthesisStall`. | Failure-mode coverage |
| 7 | A sprint stalled at self-eval (in-flight, no report) is auto-blocked after the configured stall window with the exact message in §4.3.2. | `SprintTerminalCeremonyTests.AutoBlocks_OnSelfEvalStall`. | Failure-mode coverage |
| 8 | A sprint where all tasks are `Approved` (PR approved but merge pending) does NOT trigger `ReadyForSelfEval` — the driver returns `NoOp` (`ImplementationInProgress`). | `SprintTerminalCeremonyTests.DoesNotFireSelfEval_OnApprovedNonTerminal`. **This locks the driver predicate to the actual `CheckImplementationPrerequisitesAsync` gate** — without this test, an implementation that incorrectly treats `Approved` as terminal would pass criteria 1–3 on happy-path data but break on real sprints with PR-merge work in flight. | Predicate-alignment correctness |
| 9 | When the driver returns any non-`NoOp` action, `SelfDriveDecisionService.DecideAsync` is **not** invoked for that round. | `SprintTerminalCeremonyTests.SelfDriveDecisionSkipped_WhenDriverActed`. Locks the §4.4 conditional skip. | Cap-trip race prevention |
| 10 | The §10 acceptance script (`scripts/p1-9-acceptance-check.sh`) PASSES on a freshly-completed sprint: step 6 (SelfEvaluationReport exists), step 7 (SprintReport exists), step 8 (room status terminal — assuming the parallel script-side enum-gap fix has shipped). | Live supervised acceptance run after merge. Capture exit 0 + step-by-step PASS log. **This is the actual P1.9 closure gate.** | All three findings end-to-end |

Criteria 1–9 are unit/integration testable in CI; criterion 10 requires a live supervised run.

---

## 8. Open questions reserved for implementation PR review

These are NOT blocking the design; surfacing them so the implementer can address them in the implementation PR's Spec Change Proposal section. None of them require human triage at design time.

1. **Watchdog default — 30 minutes ok, or should we observe a few real sprints first?** Initial default is a guess; real-sprint observation may want it longer (e.g., 60 minutes) before the first stabilization. Implementer ships at 30 with a tunable knob; we adjust based on observed data.
2. **Should the driver also fire on `SignOffRequested → SignOffApproved`?** When `AwaitingSignOff` flips back to false (operator approves the FinalSynthesis advance), should the driver immediately re-evaluate? Today the next round will call the driver and reach `ReadyForCompletion` if `SprintReport` exists; the latency is at most one round (~30s). I think this is fine — calling it from the sign-off path adds coupling for negligible gain. Implementer's call.
3. **Order of driver vs. self-drive decision** — §4.4 prescribes terminal-first, self-drive-second. An alternative is parallel: self-drive enqueues a continuation, driver fires, continuation becomes a no-op when it dispatches and finds `Status="Completed"`. Ordering as designed is simpler and avoids the wasted enqueue; I recommend keeping it as specified.

---

## 9. What this design intentionally does NOT do

To keep the PR small and reviewable, the following are out of scope. Each is a separate concern that may become its own roadmap entry later.

- **Generic event bus / pre-commit listener pattern.** The roadmap entry hinted at subscribing to a `SprintCompleting` event. We're explicitly not building that. The codebase has no such pattern today, and adding one for a single subscriber is over-engineering. If 3+ subscribers materialize later, file a separate Proposed Addition for the bus.
- **Frontend changes.** The driver produces existing `ActivityEventType` values that the frontend already renders. No new UI required.
- **Discord/notification changes.** Existing `SprintCompleted` and `SprintBlocked` notifications cover the new failure cases.
- **`OverflowFromSprintId` / sprint chaining behavior changes.** The driver completes sprints; what comes next (auto-start, overflow handoff) is unchanged from `SprintService.TryAutoStartNextSprintAsync`'s existing logic.
- **Self-eval cap tuning.** P1.4 owns these; if `MaxSelfEvalAttempts=3` proves wrong on real sprints, P1.4 design doc §3.3 has the discussion.
- **Forced-completion telemetry.** The §6.4 hardening adds a warning log when `force=true` advances `currentStage`; we could also emit a dedicated `ActivityEventType` value so the historical force-complete pattern (Sprint #11, #15) becomes queryable. Defer until a second use case appears.

---

## 10. Spec Change Proposal (for the implementation PR)

When the implementation PR ships, that PR's Spec Change Proposal should:

- **Section affected**: `specs/100-product-vision/roadmap.md` Proposed Additions (line 255–266) — moved to Status Tracking as a new completed row; the Proposed-Additions entry is removed.
- **Sections affected**: P1.9 Status row — updated from `blocked` to `done` once the live supervised run (criterion 7) passes; otherwise reflects the current closure status of this driver only.
- **Change type**: NEW_CAPABILITY (driver) + BUG_FIX_CODE (finding 3 hardening).
- **Spec text to add**: a new section in `specs/100-product-vision/spec.md` under sprint lifecycle: "Sprint terminal-stage ceremony auto-fires on natural task completion; sprints reach `Status=Completed` only via the ceremony chain or explicit `force=true` operator override." Include the four-step diagram from §1.
- **Verification**: criteria 1–7 in §7 above.

---

## 11. Rollback

Rollback for the implementation PR is `git revert <commit>` plus the migration rollback for `SprintEntity.FinalSynthesisEnteredAt` (single `DROP COLUMN`). The system reverts to today's behavior: terminal stage requires manual operator orchestration. No data loss; no in-flight sprints corrupted (the column is nullable; existing rows are already null).

Rollback for THIS doc PR is plain `git revert` — no code, no migration, no behavior change.
