# P1.2 — Orchestrator Self-Drive: Design Doc

**Status**: RESOLVED — design questions in §12 answered 2026-04-25; ready for implementation per §13.
**Roadmap item**: P1.2 (Orchestrator Tick / Self-Drive).
**Closes gap**: G1 — "Agents do not continue without a human pressing return."
**Risk**: 🔴 (concurrency, scheduler logic, runaway-cost potential).

This doc is the design preamble the roadmap explicitly required before P1.2 implementation. Read this first; do not start coding P1.2 until it is approved or amended.

---

## 1. Problem statement

Today the orchestrator is **trigger-driven**. Every conversation round is initiated by:

1. A human message (`AgentOrchestrator.HandleHumanMessage`), or
2. A system kickoff (`SprintKickoffService.PostKickoffAsync` → wakes orchestrator), or
3. A stage-advance announcement (P1.3, same wake-up pattern), or
4. A queue reconstruction at startup (`AgentOrchestrator.ReconstructQueueAsync`).

Inside a single trigger, `ConversationRoundRunner` already runs **up to 3 rounds** (`MaxRoundsPerTrigger = 3`) as long as agents produce non-PASS responses and the room has an active task (`ConversationRoundRunner.cs:41–151`). That inner loop is the closest thing we have to autonomy.

The gap is at the **outer** boundary. After those 3 rounds, the orchestrator goes idle and waits for a fresh external trigger. If the work isn't done, nothing happens until a human types something. §10 of `spec.md` step 5 ("the agents continue the conversation autonomously, advancing through Planning → Implementation") therefore fails.

P1.2's job is to give the orchestrator the ability to **enqueue its own next trigger** under bounded, observable, halt-able conditions.

---

## 2. Design principles (informed by what's already in the codebase)

These constrain every design decision below. Most are reuse opportunities flagged during the survey:

1. **Reuse the queue, don't add a parallel scheduler.** `AgentOrchestrator` already serializes work via `_processing` + `_lock` + `QueueItem`. Self-drive must enqueue through the same `TryEnqueueRoom` path as humans, so dedupe, ordering, and idempotency come for free.
2. **A self-drive enqueue is a `SystemContinuation` queue item, not a fake human message.** Treating it as a human message would corrupt the existing `GetRoomsWithPendingHumanMessagesAsync` reconstruction logic and pollute message history. We add a new `QueueItemKind` (or equivalent flag) so reconstruction and dedupe can distinguish it.
3. **Decisions are made at round-loop exit, not on a wall-clock tick.** A true tick would race with `ProcessQueueAsync` and double the surface area. Round-based decisioning is post-condition: when `ConversationRoundRunner.RunRoundsAsync` finishes, evaluate whether to enqueue another trigger for the same room.
4. **Counters live on the sprint, not in memory.** `SprintEntity` already carries lifecycle state (`Status`, `BlockedAt`, `AwaitingSignOff`, `CompletedAt`). The new round/cost counters belong here so they survive restarts and are visible to the existing `SprintTimeoutService` / `MarkSprintBlockedAsync` / `CancelSprintAsync` paths.
5. **Halt mechanisms must reuse `MarkSprintBlockedAsync`.** P1.4 already shipped the atomic `ExecuteUpdateAsync` block primitive (`SprintService.MarkSprintBlockedAsync`). The cap-exceeded halt is the same operation with a different `BlockReason`. No new halt primitive should be introduced.
6. **Notification on halt reuses `ActivityEventType.SprintBlocked`.** Already wired through `ActivityNotificationBroadcaster` to Discord NeedsInput as of P1.4. The human gets the same "Sprint needs attention" surface whether the block was external (P1.4) or self-cap-triggered (P1.2).
7. **No round may run while `BlockedAt != null`.** This is non-negotiable — P1.4 established that blocked sprints are paused. Self-drive must read the current `BlockedAt` on every decision, not cache it.
8. **The human always wins.** A human-posted message at any moment immediately preempts self-drive: it lands in the queue ahead of the next system continuation that would have been enqueued, and a `BackpressurePause` rule (see §4.4) prevents self-drive from racing against an in-flight human dispatch.

---

## 3. State model additions

### 3.1 `SprintEntity` columns (new)

```csharp
// Self-drive accounting (P1.2). All three reset to 0 / null on stage transition.
public int RoundsThisSprint { get; set; }      // Total agent-turn rounds executed for this sprint.
public int RoundsThisStage { get; set; }       // Rounds since entering current stage. Reset on stage advance.
public int SelfDriveContinuations { get; set; } // How many times self-drive enqueued a continuation. ≤ RoundsThisSprint.
public DateTime? LastRoundCompletedAt { get; set; } // For idle decisioning + ops visibility.
```

Migration is additive (🟢) — no backfill required; defaults are zero/null.

### 3.2 Caps (configurable, with safe defaults)

Read from `appsettings.json` under a new `Orchestrator:SelfDrive` section, with hardcoded fallbacks if missing:

| Cap                                  | Default | Per-sprint override field          | Rationale |
|--------------------------------------|---------|------------------------------------|-----------|
| `MaxRoundsPerSprint`                 | 50      | `SprintEntity.MaxRoundsOverride?`  | Roadmap explicitly mandated. |
| `MaxConsecutiveSelfDriveContinuations` | 8     | —                                  | Bounds runaway loops independent of round count. |
| `MaxRoundsPerStage`                  | 20      | —                                  | A sprint stuck spinning in Planning should halt before burning the whole sprint cap. |
| `MinIntervalBetweenContinuationsMs`  | 2000    | —                                  | Backstop against a bug enqueueing in tight loop. |

Cost caps are **out of scope for this doc** — see `specs/100-product-vision/cost-tracking-design.md` for the cost-anomaly-detection design that plugs into the §4.6 hook. The cost design intentionally does **not** add a hard `MaxCostUsdPerSprint` cap to this section's table; instead it tracks every sprint's cost (always-on), compares the projected end-of-sprint cost to a baseline learned from clean completed sprints, and triggers a configurable `BreachAction` (Notify | Warn | Block) when a sprint runs anomalously hot. Round-count caps in this section remain the only hard ceilings; cost is observed continuously and acted on relative to history.

### 3.3 Per-sprint kill switch (no schema change)

The "emergency stop" the roadmap calls for is **already expressible**: `POST /api/sprints/{id}/block` with reason `"Operator emergency stop"` halts self-drive immediately because of principle 7 (`BlockedAt != null` ⇒ no rounds). UI work to surface a button on top of this endpoint is a separate ergonomic task; the runtime contract is already complete.

---

## 4. Control-flow design

### 4.1 The decision point

A new component, `SelfDriveDecisionService` (singleton), is invoked at exactly **one** place: end of `ConversationRoundRunner.RunRoundsAsync`, after session rotation, before the method returns. Adding it anywhere else creates parallel decisioning paths.

```
RunRoundsAsync(roomId)
  └─ runs N inner rounds (existing, unchanged)
  └─ session rotation (existing)
  └─ SelfDriveDecisionService.DecideAndMaybeEnqueueAsync(roomId)   ← NEW
```

`DecideAndMaybeEnqueueAsync` returns `void` and never throws — it logs and continues on any failure. Self-drive failing must never break the trigger run that actually completed.

### 4.2 The continue/halt/idle decision tree

```
1. Resolve sprint for room. If no active sprint → IDLE (return).
2. If sprint.BlockedAt != null            → IDLE.
3. If sprint.AwaitingSignOff              → IDLE (waiting on human).
4. If sprint.Status != "Active"           → IDLE.
5. If room.IsCompleted/Archived           → IDLE.
6. If RoundsThisSprint >= MaxRoundsPerSprint
                                          → HALT (block sprint, reason: "Round cap reached: {n}/{cap}").
7. If RoundsThisStage >= MaxRoundsPerStage
                                          → HALT (block sprint, reason: "Stage round cap reached for {stage}: {n}/{cap}").
8. If SelfDriveContinuations >= MaxConsecutiveSelfDriveContinuations
                                          → HALT (block sprint, reason: "Continuation cap reached without human checkpoint: {n}/{cap}").
9. If lastTriggerProducedNoNonPassResponses
                                          → IDLE (the inner loop already detected "nothing to add").
10. If room has no ActiveTask AND sprint.CurrentStage in {Intake, Discussion}
                                          → IDLE (waiting on human steering, do not loop).
11. If now < LastRoundCompletedAt + MinIntervalBetweenContinuationsMs
                                          → IDLE (anti-runaway).
12. Otherwise                              → CONTINUE: enqueue a SystemContinuation queue item for this roomId.
```

**Decision-input contract.** `RunRoundsAsync` returns a small struct (`RoundRunOutcome { bool HadNonPassResponse, int InnerRoundsExecuted }`) which is passed to the decision service. This avoids the decision service re-querying state that the round runner already knows.

**Inner-loop counter increments.** `ConversationRoundRunner` increments `RoundsThisSprint` and `RoundsThisStage` by `InnerRoundsExecuted` and writes `LastRoundCompletedAt` in the same `SaveChanges` as session rotation. (One write per trigger run, not per inner round — keeps DB load proportional to triggers, not turns.)

### 4.3 The HALT path (cap-exceeded)

When any of decisions 6–8 fire:

1. Call `SprintService.MarkSprintBlockedAsync(sprintId, reason)` — the existing P1.4 atomic primitive.
2. The existing P1.4 wiring already emits `ActivityEventType.SprintBlocked` → Discord "Sprint needs attention".
3. Self-drive enqueues nothing further. Existing inbound human-message path remains the unblocking trigger; once a human posts, they implicitly choose to continue (presumably after also calling `/unblock`).

We get the entire halt-and-notify chain for free. **No new wiring needed.**

### 4.4 Backpressure: human posts mid-loop

The orchestrator's queue already serializes (`_processing` + `_lock`). The risk is a new race where:

- Self-drive decides "CONTINUE" and is about to enqueue.
- A human posts simultaneously.
- Two queue items end up enqueued; the human's runs first; then the now-stale system-continuation runs immediately after.

Mitigation, kept minimal:

- `TryEnqueueRoom` already dedupes by room. We extend the dedupe rule: **if the existing queued item for this room is `HumanMessage`, a `SystemContinuation` enqueue is dropped** (the human's trigger will run another conversation, and the post-round decision will run again afterwards — no work lost).
- If the existing item is `SystemContinuation` and the new arrival is `HumanMessage`, the system-continuation is **upgraded in place** to `HumanMessage` (same room, real human input takes precedence). No second slot is created.

This rule is implemented in `TryEnqueueRoom` (or a small helper) and unit-tested with all four combinations.

### 4.5 Continuation message body

The continuation enqueue does **not** post a visible system message. `SprintKickoffService` and `SprintStageAdvanceAnnouncer` post visible messages because the human and the audit log need to see "kickoff happened" / "stage advanced". A self-drive continuation is the **absence of a stop**; surfacing it as a chat line each time would flood the room.

Instead:

- A structured `ActivityEvent` of type `SprintRoundContinuationScheduled` is emitted (no Discord notification — `Internal` severity). This gives ops a paper trail without UI noise.
- The chat is silent. From the human's perspective, the agents simply keep talking until a halt or idle condition fires.

### 4.6 Cost-cap insertion point (deferred)

In step 12 (CONTINUE), before enqueueing, a single hook is reserved:

```csharp
if (await _costGuard.ShouldHaltAsync(sprint, cancellationToken)) {
    await _sprintService.MarkSprintBlockedAsync(sprintId, "Cost cap reached");
    return;
}
```

`_costGuard` is `ICostGuard` (singleton). The default implementation is `NoOpCostGuard` returning `false`. The real cost-tracking impl becomes its own roadmap item once token-counting infrastructure exists. This shape lets the future change be additive — DI swap, no decision-tree restructuring.

---

## 5. Startup and crash recovery

`AgentOrchestrator.ReconstructQueueAsync` currently reconstructs only **rooms with pending human messages**. After a crash mid-self-drive, we have two choices:

**Option A (chosen): rely on the next decisioning pass.** Reconstruction does NOT replay pending self-drive continuations. If the sprint is still Active, has rounds remaining, and is not blocked, the *first* trigger after restart (whether reconstructed human message, kickoff on fresh start, or human posting fresh) will produce a `RunRoundsAsync` call whose post-condition decision will re-enter the self-drive loop naturally.

**Option B (rejected): persist pending continuations.** Adds a new persisted-queue table, reconstruction logic, and dedupe-on-startup logic. High cost for the failure mode of "we lose at most one continuation between crash and the next live trigger."

If reality reveals Option A is too lossy (e.g., a sprint that crashed mid-Implementation with no human attention for hours never resumes), we revisit. For now: simpler, less surface, no new schema.

**Open question (review me):** is there a daily/hourly sweep that nudges Active-but-quiescent sprints? Looking at `SprintTimeoutService`, the answer is "yes — for overdue/timeout, but those *cancel* sprints, not nudge them." A periodic nudge for self-drive-quiescent sprints is a candidate for a follow-up roadmap item, not P1.2.

---

## 6. Configuration surface

```jsonc
// appsettings.json
{
  "Orchestrator": {
    "SelfDrive": {
      "Enabled": true,
      "MaxRoundsPerSprint": 50,
      "MaxRoundsPerStage": 20,
      "MaxConsecutiveSelfDriveContinuations": 8,
      "MinIntervalBetweenContinuationsMs": 2000
    }
  }
}
```

`Enabled: false` is a global kill switch — useful in dev, in tests, and if production self-drive misbehaves and a hot-config flip is faster than a deploy. When `Enabled: false`, `SelfDriveDecisionService.DecideAndMaybeEnqueueAsync` is a logged no-op; the rest of the system is unaffected.

`SprintEntity.MaxRoundsOverride` (nullable int) lets a human raise/lower the per-sprint cap via API for a known-large sprint without reconfiguring globally.

---

## 7. Acceptance criteria for the P1.2 implementation task

When the implementation PR lands, these must be observably true (not "data shape is correct" — actual behavior). The implementation task may not declare done until each is demonstrated.

1. **No-input continuation, observable.** Create a sprint, post one human message, walk away. Within 3 minutes, at least 5 distinct agent rounds occur with no further human input, and `SprintEntity.RoundsThisSprint >= 5`, `SelfDriveContinuations >= 1`.
2. **Round cap halts.** Set `MaxRoundsPerSprint` to 5 in test config. Same scenario. After round 5, `BlockedAt != null`, `BlockReason` contains "Round cap", and a `SprintBlocked` ActivityEvent was emitted.
3. **Stage cap halts.** Set `MaxRoundsPerStage` to 3. Sprint reaches 3 rounds in Planning. Halt, with reason mentioning "Planning". Stage advance after `/unblock` resets `RoundsThisStage` to 0.
4. **Continuation-cap halts.** Set `MaxConsecutiveSelfDriveContinuations` to 2. After 2 self-drive continuations without a human message, sprint blocks with the corresponding reason.
5. **Block pauses immediately.** Mid-loop, call `POST /api/sprints/{id}/block`. The next decision call returns IDLE; no new continuation is enqueued. After `/unblock`, the next human message resumes; self-drive then resumes too.
6. **Human preempts cleanly.** Mid-loop, post a human message. The human's message is processed in the next dispatched item; no duplicate work occurs; the post-round decision afterwards re-evaluates from a fresh state.
7. **Idle stages do not loop.** A sprint in Intake stage with no ActiveTask does **not** trigger continuations purely because an agent gave a non-PASS reply. (Decision step 10.)
8. **Crash safety.** Kill the server mid-loop. Restart. The reconstruction does not enqueue a phantom continuation. A subsequent human message resumes the loop normally and counters continue from where they left off (because they're persisted).
9. **`Enabled: false` is a true kill switch.** With the flag off, no self-drive continuation occurs even after humans trigger rounds. Existing trigger-driven behavior is unchanged.
10. **No new visible system message per continuation.** The room transcript shows only agent messages and the existing kickoff/stage-advance system messages. No `"Sprint #N continuing autonomously"` chat line appears.

Each criterion maps to at least one xUnit test in the implementation PR.

---

## 8. Test plan (preview, for reviewer scrutiny)

Unit:
- `SelfDriveDecisionServiceTests` — every branch of the decision tree (12 branches × pass/fail = ~24 tests).
- `TryEnqueueRoomDedupeTests` — all four (HumanMessage, SystemContinuation) × (HumanMessage, SystemContinuation) combinations.
- `SprintServiceTests.IncrementRoundCountersAsync_*` — counter math, especially stage-advance reset.
- Migration tests for the new columns (default zero; null `LastRoundCompletedAt`).

Integration:
- A multi-round end-to-end test using existing `WebApplicationFactory` infra + a mock chat provider that replies "PASS"/"non-PASS" deterministically. Drive a sprint to round 5 with `MaxRoundsPerSprint=5`, assert HALT.
- Existing `ConversationRoundRunnerTests` must not regress — self-drive is invoked from the *end* of `RunRoundsAsync`; tests that don't supply a sprint context still pass (decision returns IDLE on no sprint).

Live-verification (tier 3, the §10-style proof):
- Spin up the dev server. Create a sprint with `MaxRoundsPerSprint=8`. Post one message. Watch the timeline. Confirm rounds advance without further human input until cap → block → Discord notification.
- This is the only verification that actually proves the feature works at the §10 level. It is REQUIRED before P1.2 is marked done.

---

## 9. Spec Change Proposal

When P1.2 implementation lands, these spec sections must be updated **in the same PR**:

- **`specs/006-orchestrator/spec.md`**: new subsection "Self-Drive Continuation" under Current Behavior, describing `SelfDriveDecisionService`, the decision tree, and the new `RoundRunOutcome` flow at end of `RunRoundsAsync`.
- **`specs/013-sprint-system/spec.md`**: new fields on `SprintEntity` (`RoundsThisSprint`, `RoundsThisStage`, `SelfDriveContinuations`, `LastRoundCompletedAt`, `MaxRoundsOverride`), and the cap semantics.
- **`specs/014-database-schema/spec.md`**: schema delta for the new columns.
- **`specs/004-notification-system/spec.md`**: add `SprintRoundContinuationScheduled` ActivityEventType (Internal severity, no Discord routing).
- **`specs/100-product-vision/gap-analysis.md`**: G1 → 🟢 (or 🟢 partial if integration tests are deferred); G2 strengthened.
- **`specs/100-product-vision/roadmap.md`**: P1.2 status table → `done` with implementation PR reference.
- **`specs/CHANGELOG.md`**: entry noting the new behavior, the configuration surface, and the new caps with defaults.

Type: NEW_CAPABILITY. No existing behavior is removed; the trigger-driven path is preserved and self-drive layers on top of it.

---

## 10. Risks and what could go wrong

- **Runaway loop bug** (highest risk by impact). Mitigated by: per-sprint round cap, per-stage round cap, per-continuation-streak cap, min-interval cap, global `Enabled:false` kill switch, and the existing `MarkSprintBlockedAsync` halt primitive. Five layers of brakes.
- **Interaction with breakouts.** The decision service runs at the **main-room** trigger level. Breakout rooms have their own task/round dynamics (`BreakoutLifecycleService`). For P1.2 v1, self-drive does NOT enqueue continuations against breakout rooms — only rooms that map to a sprint. Breakout-driven autonomy is a future item.
- **Counter drift on partial failures.** If `IncrementRoundCountersAsync` succeeds but the continuation-enqueue throws, counters may be high-by-one. Acceptable: it conservatively halts sooner. The reverse (continuation enqueued, counter not bumped) would be dangerous and is prevented by writing the counter in the same `SaveChangesAsync` as session rotation, before the decision call.
- **Test flakiness from real LLM calls.** The integration test must use the mock chat provider already used by `ConversationRoundRunnerTests`. Real-LLM tests stay manual.
- **Spec-says-Implemented-but-isn't risk** (the §10 meta-rule). The acceptance criteria in §7 above are the proof. Until *test 1* passes against a running system, P1.2 is NOT done — regardless of how clean the unit tests look.

---

## 11. What this design intentionally does not do

To keep P1.2 reviewable and shippable:

- **No cost tracking.** Hook reserved (§4.6). Real impl is its own item.
- **No cross-workspace scheduling.** That's P2.1. Self-drive operates only on the room/sprint that just produced a round.
- **No self-evaluation ceremony.** That's P1.4-full. Self-drive will happily run agents past the Implementation→FinalSynthesis boundary today; once P1.4 lands, the ceremony's preamble + verdict logic will gate that transition. The two features are independent and ship separately.
- **No UI changes.** Surfacing self-drive state in the UI (round counter, "agents are continuing autonomously" indicator) is an ergonomic follow-up, not a P1.2 requirement.

---

## 12. Resolved decisions

Resolved 2026-04-25 by the human reviewer. The questions in the prior revision are kept here as decisions so future readers see the rationale.

1. **Per-stage cap default of 20 — confirmed.** A stage that legitimately needs 20+ rounds is a yellow flag worth a human review checkpoint. Revisit after the first real sprints run if 25+ in Implementation turns out to be normal.
2. **Ship with `Enabled: true` in production from day one — confirmed.** Shipping disabled defeats the purpose of P1.2 (which exists to fix §10 step 5). A documented hot-flip procedure (`appsettings.{Environment}.json` override → restart) is the operator's escape hatch if telemetry looks bad.
3. **No visible per-continuation system message — confirmed.** Silent operation with the ActivityEvent paper trail is enough; chat noise from per-Nth heartbeats is rejected.
4. **Round cap defaults live in `appsettings.json` — confirmed.** They are orchestrator-level, not per-agent, so `Config/agents.json` is the wrong home.

---

## 13. Implementation order (suggested for the future P1.2 task)

1. EF migration for the new `SprintEntity` columns. Verify with a schema test.
2. `RoundRunOutcome` return value from `ConversationRoundRunner.RunRoundsAsync`. Update existing tests.
3. Counter bump in `RunRoundsAsync` (single SaveChanges with session rotation). Test.
4. `ICostGuard` + `NoOpCostGuard`. DI registration.
5. `SelfDriveDecisionService` skeleton. Unit-test all 12 branches against fakes.
6. Wire the decision service into `RunRoundsAsync` at the very end. Existing tests must still pass.
7. `TryEnqueueRoom` dedupe extension for `SystemContinuation`. Test all four combinations.
8. Configuration plumbing (`Orchestrator:SelfDrive` section + binding).
9. Spec updates (§9 above) in the same commit/PR.
10. Live verification (§8 tier 3) — REQUIRED to mark P1.2 done.

Items 1–4 are independent and can be split into a precursor PR if reviewers prefer smaller chunks.

---

## 14. Approval

P1.2 implementation must not begin until this design is reviewed by the human operator and either approved as-is or amended with explicit feedback. The roadmap's "design doc required first" gate is binding.

Reviewer, please respond with one of:

- ✅ **Approved**, proceed with implementation.
- ✏️ **Approved with amendments** — list the changes needed.
- ❌ **Rejected** — explain the disagreement and propose an alternative.

— anvil (operator: agent-academy), 2026-04-25
