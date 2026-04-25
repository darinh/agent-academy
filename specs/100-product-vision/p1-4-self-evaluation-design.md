# P1.4 — Self-Evaluation Ceremony at End of Implementation: Design Doc

**Status**: DRAFT — pending human review before implementation.
**Roadmap item**: P1.4 (Self-Evaluation Ceremony at End of Implementation).
**Closes gap**: G4 — "Agents do not self-evaluate before declaring a sprint done."
**Risk**: 🔴 (state-machine wiring; gates sprint completion; halt path; preamble surface).

This doc is the design preamble the roadmap explicitly required (`roadmap.md §P1.4` open design questions). Read this first; do not start coding the auto-block heuristic, the new `SelfEvaluationReport` artifact, or the `ImplementationSelfEval` substate until it is approved or amended.

---

## 1. Problem statement

Today, when the Planner runs `ADVANCE_STAGE` from `Implementation`, `SprintStageService.AdvanceStageAsync` does the following (`SprintStageService.cs:87–168`):

1. Validates that all sprint tasks are completed or cancelled (`CheckImplementationPrerequisitesAsync`).
2. Advances `CurrentStage` from `Implementation` → `FinalSynthesis`.
3. Announces the advance to active rooms (P1.3).

There is no checkpoint that asks **"did the implementation actually satisfy the requirements?"**. The task-completion check is purely a structural gate (status fields on `TaskEntity`), not a behavioural one. A sprint can leave Implementation with every task marked complete and still have shipped code that does not do what `RequirementsDocument.AcceptanceCriteria` asked for.

This is the gap §10 step 8 of `spec.md` exposes: the acceptance test specifies that *before FinalSynthesis, the team must self-evaluate against acceptance criteria and only proceed if all criteria pass.* P1.4's narrow-scope subset (already shipped, see `roadmap.md §P1.4 partial`) gave us the **mechanism** to halt a sprint (`MarkSprintBlockedAsync` + `BlockedAt` + `SprintBlocked` notification). What it did **not** give us is the **trigger** that decides when to halt automatically. That trigger is this design doc.

The orchestrator must intercept the `Implementation → FinalSynthesis` transition, run a structured self-evaluation ceremony, parse the result, and either advance (all PASS) or loop back (any FAIL) or halt (cap exceeded).

---

## 2. Design principles (informed by what's already in the codebase)

These constrain every design decision below. Most are reuse opportunities flagged during the survey:

1. **Reuse the artifact gate, don't invent a new gate type.** `SprintStageService.RequiredArtifactByStage` (line 40–47) already enforces "you cannot leave stage X without artifact of type Y." Self-eval becomes a new entry: `["Implementation"] = "SelfEvaluationReport"`. This matches the existing pattern (`Intake → RequirementsDocument`, `Planning → SprintPlan`, `Validation → ValidationReport`, `FinalSynthesis → SprintReport`) and means `AdvanceStageAsync` already enforces it without code changes to the gate logic itself.
2. **Reuse the artifact validation surface.** `SprintArtifactService.ValidateContent` (`SprintArtifactService.cs:177`) already does typed JSON validation per artifact type. `SelfEvaluationReport` validation goes here. No new validator class.
3. **Reuse `MarkSprintBlockedAsync` for the halt path.** P1.4-narrow already shipped the atomic block primitive. The "N consecutive self-eval failures" halt is the same operation with a different `BlockReason`. No new halt mechanism. This closes the *auto-blocking heuristic* that `roadmap.md §P1.4 partial` and §P1.7 left explicitly pending.
4. **Reuse `SprintBlocked` → `NeedsInput` notification.** Already wired through `ActivityNotificationBroadcaster` to Discord. The human gets the same "Sprint X needs attention" surface whether the block was external (P1.4-narrow) or self-eval-cap-triggered (this doc). Zero new notification plumbing.
5. **Reuse the preamble surface.** `SprintPreambles.StagePreambles` (`SprintPreambles.cs:32–139`) is the existing C# constant dictionary for stage instructions. The self-eval preamble belongs here, keyed by a new substate identifier (§4.1). It does **not** belong in `Config/sprint-stages.json` or in agent-level config — preambles for sprint stages are already centralised in this one file and adding a parallel surface fragments the source of truth.
6. **Counters live on the sprint, not in memory.** P1.2's design established this principle (`SprintEntity` carries `RoundsThisSprint` etc.). Self-eval attempts get a peer column `SelfEvalAttempts` so the counter survives restarts and is observable through the existing sprint API.
7. **Don't add a 7th stage to `StagesArray`.** The full stage array (`SprintStageService.cs:21–29`) is referenced in dozens of places (rosters, preambles, prerequisite checks, advance announcements, frontend stage display, gap analysis spec). Adding `ImplementationSelfEval` as a real stage cascades across the codebase. Instead, model self-eval as a **substate of Implementation** signalled by a flag (§3.1). The Implementation→FinalSynthesis transition is the only place the substate matters; everywhere else the stage stays "Implementation".
8. **Acceptance criteria come from `RequirementsDocument`, not `SprintPlan`.** `RequirementsDocument.AcceptanceCriteria: List<string>` (`Sprints.cs:63`) is the authoritative source. `SprintPlan` documents the *plan* to satisfy them; the *criteria themselves* live one stage earlier. Self-eval reads from the stored RequirementsDocument artifact for this sprint. (The roadmap text says "tracking artifact" — that wording is loose; the actual list lives in RequirementsDocument.)
9. **The orchestrator parses structured artifact JSON, never free-form chat.** Self-eval verdict parsing is a `JsonSerializer.Deserialize<SelfEvaluationReport>` against the stored artifact's `Content` column, not a regex against the agent's conversation message. Free-form parsing is the failure mode that makes self-eval theatre instead of a gate.

---

## 3. State model additions

### 3.1 `SprintEntity` columns (new)

```csharp
// Self-evaluation accounting (P1.4 full scope). Reset on stage transition INTO Implementation.
public bool SelfEvaluationInFlight { get; set; }    // True between RUN_SELF_EVAL and either advance or new attempt.
public int SelfEvalAttempts { get; set; }           // Number of self-eval reports submitted at Implementation. ≤ MaxSelfEvalAttempts.
public DateTime? LastSelfEvalAt { get; set; }       // Audit + ops visibility.
public string? LastSelfEvalVerdict { get; set; }    // "AllPass" | "AnyFail" | "Unverified" | null. Cached from latest report.
```

Migration is additive (🟢) — no backfill required; defaults are `false`/`0`/`null`.

### 3.2 New artifact type: `SelfEvaluationReport`

Stored via the existing `SPRINT_ARTIFACT` pipeline (`SprintArtifactService`) under `Stage = "Implementation"`, `Type = "SelfEvaluationReport"`. Multiple reports per sprint are allowed (each attempt creates a new row); the orchestrator reads the most recent.

JSON shape:

```json
{
  "Attempt": 1,
  "Items": [
    { "Criterion": "<verbatim from RequirementsDocument.AcceptanceCriteria>",
      "Verdict": "PASS" | "FAIL" | "UNVERIFIED",
      "Evidence": "<text — file path, test name, PR #, log line>",
      "FixPlan": "<required iff Verdict in (FAIL, UNVERIFIED), else null>"
    }
  ],
  "OverallVerdict": "AllPass" | "AnyFail" | "Unverified",
  "Notes": "<optional free text>"
}
```

`SprintArtifactService.ValidateContent` enforces:

- `Items.Count == RequirementsDocument.AcceptanceCriteria.Count` (one entry per criterion — no skipping).
- Each `Items[i].Criterion` matches the corresponding criterion exactly (string-equal, case-sensitive). This prevents the agent from silently rewording a criterion to make it easier to claim PASS.
- `OverallVerdict == "AllPass"` iff every `Items[*].Verdict == "PASS"`. Otherwise `AnyFail` (if any FAIL exists) else `Unverified` (only PASS+UNVERIFIED). Mismatch is a validation error — the agent cannot lie about the rollup.
- Every non-PASS item has a non-empty `FixPlan`.

If the RequirementsDocument is missing or malformed at the moment of validation, validation fails with a clear error — the sprint is structurally invalid for self-eval and the human must intervene (this is correct behaviour; we should not silently advance).

### 3.3 Caps (configurable, with safe defaults)

Read from `appsettings.json` under a new `Orchestrator:SelfEval` section, with hardcoded fallbacks if missing:

| Cap                       | Default | Rationale |
|---------------------------|---------|-----------|
| `MaxSelfEvalAttempts`     | 3       | After 3 failed attempts the team is going in circles; humans must intervene. |
| `MinIntervalBetweenAttemptsMinutes` | 0 | No throttle by default — self-eval is rare relative to round budget. |

`MaxSelfEvalAttempts = 3` is deliberately conservative. Two attempts is too few (one bad fix burns half the budget); five+ attempts encourage the team to keep papering over root issues. **Reviewer ask (§9 q1).**

---

## 4. Control-flow design

### 4.1 The substate model

`Implementation` stage has two substates, distinguished by `SprintEntity.SelfEvaluationInFlight`:

| Substate                        | `CurrentStage`   | `SelfEvaluationInFlight` | What's allowed |
|---------------------------------|------------------|--------------------------|----------------|
| Implementation (normal)         | "Implementation" | `false`                  | Tasks, PRs, code, the existing Implementation preamble. |
| Implementation (self-eval open) | "Implementation" | `true`                   | Only `STORE_ARTIFACT(Type=SelfEvaluationReport, …)` and `ADVANCE_STAGE`. The self-eval preamble is injected. Code-touching commands (`CREATE_PR`, `MERGE_PR`, etc.) are NOT blocked at the command layer (out of scope) — they're discouraged by the preamble. The agent is told: "your only job right now is to evaluate, not to write more code."

**The substate is entered, not advanced into.** There is no separate stage row in `StagesArray`. `CurrentStage` stays `"Implementation"` for the entirety of self-eval, including across multiple FAIL→re-eval loops. The frontend can reflect the substate by reading `SelfEvaluationInFlight` and labelling it ("Implementation — Self-Eval"). No frontend-required change to ship; nice-to-have.

### 4.2 The trigger

A new agent command, `RUN_SELF_EVAL`, opens the substate. It is the **only** way to enter self-eval. We deliberately do NOT auto-trigger on "all tasks completed" — premature self-eval (e.g., trivial sprints with one merged PR but no tests) wastes a turn and inflates `SelfEvalAttempts`. The Planner decides when implementation feels done and runs:

```
RUN_SELF_EVAL:
```

Server effect:

1. Validate sprint is `Active`, `CurrentStage == "Implementation"`, `BlockedAt == null`, and at least one task exists with status `Completed` or `Cancelled` (otherwise this is theatre — refuse with clear error).
2. Validate `RequirementsDocument` artifact exists (otherwise refuse — see §3.2).
3. Set `SelfEvaluationInFlight = true`, save.
4. Wake the orchestrator for this sprint's room (existing `OrchestratorDispatchService.WakeForRoomAsync` path) — the next round will inject the self-eval preamble.

**`ADVANCE_STAGE` from Implementation when `SelfEvaluationInFlight == false` is rejected.** Today there is nothing forcing the Planner to run self-eval before advancing. The artifact gate added in §2 principle 1 (`["Implementation"] = "SelfEvaluationReport"`) does this work: with no SelfEvaluationReport artifact stored, `AdvanceStageAsync` already throws on the gate check. The Planner's only path to FinalSynthesis is: `RUN_SELF_EVAL` → submit report → (if AllPass) `ADVANCE_STAGE`.

### 4.3 The verdict path (after a `SelfEvaluationReport` is stored)

`SprintArtifactService.StoreArtifactAsync` is the choke point. After validating and persisting the artifact, if `Stage == "Implementation"` and `Type == "SelfEvaluationReport"`:

```
1. Increment SprintEntity.SelfEvalAttempts.
2. Set LastSelfEvalAt = now, LastSelfEvalVerdict = report.OverallVerdict.
3. SAVE.
4. Emit ActivityEventType.SelfEvalCompleted (new event type, payload: { sprintId, verdict, attempt }).
5. Decision tree:
   a. report.OverallVerdict == "AllPass":
        — Leave SelfEvaluationInFlight = true (Planner now runs ADVANCE_STAGE; gate passes; advance).
   b. report.OverallVerdict in ("AnyFail", "Unverified") AND SelfEvalAttempts < MaxSelfEvalAttempts:
        — Set SelfEvaluationInFlight = false (re-open Implementation; team fixes; new RUN_SELF_EVAL later).
        — Wake orchestrator with the standard Implementation preamble; team resumes coding.
   c. report.OverallVerdict in ("AnyFail", "Unverified") AND SelfEvalAttempts >= MaxSelfEvalAttempts:
        — Call SprintService.MarkSprintBlockedAsync(sprintId,
            $"Self-eval failed {SelfEvalAttempts} attempt(s). Latest verdict: {OverallVerdict}.").
        — This emits ActivityEventType.SprintBlocked → NeedsInput notification (existing wiring).
        — Sprint is now paused; human unblocks via POST /api/sprints/{id}/unblock (existing endpoint).
        — On unblock, P1.4 partial already resets BlockedAt/BlockReason; we additionally reset
          SelfEvalAttempts = 0 so the team gets a fresh budget after human intervention.
```

Step 5c is the auto-block heuristic the roadmap reserves for "full P1.4". It reuses `MarkSprintBlockedAsync` end-to-end — this is the design's payoff against principles 3 and 4.

### 4.4 The advance path (`ADVANCE_STAGE` from Implementation)

`SprintStageService.AdvanceStageAsync` already enforces the artifact gate (§2 principle 1, achieved purely by the new `RequiredArtifactByStage` entry). After it advances `CurrentStage` to `FinalSynthesis`, we additionally:

```
On successful advance from Implementation:
  - Set SelfEvaluationInFlight = false.
  - Reset SelfEvalAttempts = 0 (next sprint starts clean if archetype reused; defensive even though SprintEntity is per-sprint).
  - Save in the same transaction as the stage advance.
```

Note that the **gate only checks artifact existence**, not verdict. A team could in theory store a `SelfEvaluationReport` with `OverallVerdict = "AnyFail"` and then call `ADVANCE_STAGE`. We block this in `AdvanceStageAsync` with one additional check: when leaving Implementation, fetch the most recent `SelfEvaluationReport` artifact and require `OverallVerdict == "AllPass"`. This is the second half of the gate; without it the artifact gate is checkbox theatre.

### 4.5 The unblock path

When a human runs `POST /api/sprints/{id}/unblock` on a self-eval-blocked sprint, the existing `SprintService.UnblockSprintAsync` (P1.4 partial) clears `BlockedAt` + `BlockReason`. We extend it minimally:

```csharp
// In UnblockSprintAsync, after the existing atomic ExecuteUpdateAsync clears BlockedAt:
if (preState.BlockReason?.StartsWith("Self-eval failed") == true) {
    // Give the team a fresh self-eval budget after human intervention.
    await _db.Sprints
        .Where(s => s.Id == sprintId && s.BlockedAt == null)  // re-check, defensive
        .ExecuteUpdateAsync(setters => setters
            .SetProperty(s => s.SelfEvalAttempts, 0)
            .SetProperty(s => s.SelfEvaluationInFlight, false));
}
```

Idempotent: if the human unblocks twice, the second call is a no-op.

### 4.6 Failure-mode taxonomy

| Failure                                              | Detection                                       | Behaviour |
|------------------------------------------------------|-------------------------------------------------|-----------|
| Agent submits malformed `SelfEvaluationReport` JSON  | `SprintArtifactService.ValidateContent` throws  | Validation error returns to agent; counter not incremented; no state change. |
| Agent submits AnyFail/Unverified report              | Verdict path §4.3.b                             | Re-open Implementation, team fixes. |
| Agent submits report at attempt 3 with AnyFail       | Verdict path §4.3.c                             | Auto-block; human notified. |
| Agent calls `ADVANCE_STAGE` without `RUN_SELF_EVAL`  | Artifact gate (no SelfEvaluationReport exists)  | `InvalidOperationException` from `AdvanceStageAsync`. |
| Agent calls `ADVANCE_STAGE` with stored AnyFail report | Verdict gate (§4.4 second half)               | `InvalidOperationException` from `AdvanceStageAsync`. |
| Server crash mid-self-eval (`SelfEvaluationInFlight = true`) | Restart                                | No special recovery. The flag persists; preamble continues to inject self-eval instructions when the next round runs. The team can either submit a report or (if stuck) the human can unblock manually. |

---

## 5. The self-eval preamble

A new entry in `SprintPreambles.StagePreambles`, keyed by a virtual stage identifier `"ImplementationSelfEval"` and selected by the preamble builder when `CurrentStage == "Implementation"` AND `SelfEvaluationInFlight == true`. (`BuildPreamble` signature is extended to accept the sprint entity, or just `SelfEvaluationInFlight: bool`. The latter is less invasive; recommend it.)

Hardcoded preamble (revise during implementation; this is the shape):

```
=== SPRINT STAGE: IMPLEMENTATION — SELF-EVALUATION ===

The Planner has called RUN_SELF_EVAL. Implementation is paused. Your only
task this round is to evaluate the work against the sprint's acceptance
criteria and produce a SelfEvaluationReport artifact. Do not write more
code, do not open new PRs, do not refactor — that work waits for the
verdict.

**The acceptance criteria** for this sprint are stored in the
RequirementsDocument artifact (Stage=Intake). Read them with
GET_ARTIFACT and copy each one VERBATIM into your report. Do not reword
them. Reworded criteria fail validation.

**For each criterion, return one of three verdicts:**
  PASS       — There is concrete evidence the criterion is met.
               Cite the evidence (file path + line, test name, PR #,
               log output). "We implemented it" is not evidence.
  FAIL       — The criterion is not met. Provide a FixPlan describing
               what work would close the gap.
  UNVERIFIED — The criterion is plausibly met but you cannot produce
               concrete evidence in this session. Provide a FixPlan
               describing what verification step is missing.

**Be honest about UNVERIFIED.** A FAIL or UNVERIFIED is not a failure
of the team — it's the system working. Lying or claiming PASS without
evidence is the failure mode this ceremony exists to prevent.

**HOW TO COMPLETE (Planner only — Aristotle):**
1. Synthesize verdicts from the team's review.
2. Run: `STORE_ARTIFACT: Type=SelfEvaluationReport Content=<JSON per
   the schema in spec 100-product-vision/p1-4-self-evaluation-design.md §3.2>`
3. The server computes OverallVerdict and decides:
     - AllPass     → you may now run ADVANCE_STAGE.
     - AnyFail     → Implementation re-opens; address the FixPlans.
     - Unverified  → same as AnyFail; verify the gaps before re-running.
4. After {MaxSelfEvalAttempts} attempts without AllPass, the sprint is
   automatically blocked and a human is notified. There is no way to
   bypass this — the cap exists to stop infinite loops.
```

The `{MaxSelfEvalAttempts}` token is interpolated by `BuildPreamble` from config so the preamble stays in sync with the cap.

---

## 6. New API surface

| Method | Path                                          | Body                       | Purpose |
|--------|-----------------------------------------------|----------------------------|---------|
| POST   | `/api/sprints/{id}/self-eval/start`           | (empty)                    | Server-side equivalent of `RUN_SELF_EVAL` for human/operator override. Sets `SelfEvaluationInFlight = true` after the same validations §4.2 enforces. Useful when the Planner is stuck. |
| GET    | `/api/sprints/{id}/self-eval/latest`          | —                          | Returns the most recent `SelfEvaluationReport` artifact + verdict + attempts/cap, for the frontend timeline view. |

Both endpoints are thin wrappers over existing service methods (no new service class). `RUN_SELF_EVAL` agent-command handler calls the same `SprintService.StartSelfEvalAsync` internal method that the POST endpoint routes to.

The frontend timeline can render the latest report as a checklist (one row per criterion, coloured by verdict). Out of scope for this design but the GET endpoint is shaped to support it.

---

## 7. Notification surface

One new `ActivityEventType`:

```csharp
SelfEvalCompleted   // payload: { sprintId, verdict, attempt, attemptCap }
```

Mapped in `ActivityNotificationBroadcaster`:

| Verdict      | NotificationType  | Title                              |
|--------------|-------------------|------------------------------------|
| `AllPass`    | `Progress`        | "Sprint X passed self-evaluation"  |
| `AnyFail`    | `Progress`        | "Sprint X self-eval found issues (attempt N/cap)" |
| `Unverified` | `Progress`        | "Sprint X self-eval has unverified items (attempt N/cap)" |

The auto-block at attempt-cap reuses the existing `SprintBlocked` event (§4.3.c) and its `NeedsInput` mapping. No new notification surface for the halt.

`Progress` is appropriate (low urgency); the `NeedsInput` escalation only fires on the auto-block, which is what humans actually need to act on.

---

## 8. Implementation order (when this doc is approved)

1. **Schema & artifact** (additive). Migration for the four new `SprintEntity` columns. New `SelfEvaluationReport` record in `Sprints.cs`. Validation in `SprintArtifactService.ValidateContent`. Tests around validation rules (criterion-count match, verbatim match, rollup consistency, FixPlan-required-on-non-PASS).
2. **Artifact gate addition.** One-line change to `SprintStageService.RequiredArtifactByStage`. Tests for the gate firing.
3. **Verdict path & counters.** `SprintArtifactService.StoreArtifactAsync` extension to bump counter + emit event + decide. New `ActivityEventType.SelfEvalCompleted`. Tests for each branch of §4.3 (AllPass / AnyFail / Unverified / cap-exceeded).
4. **`RUN_SELF_EVAL` command + handler.** Validation per §4.2. Wake orchestrator. Tests.
5. **Verdict gate.** `AdvanceStageAsync` second-half check (§4.4) — fetch latest report, require AllPass. Tests for the rejection path.
6. **Unblock recovery.** `UnblockSprintAsync` extension for self-eval block reasons (§4.5). Test for budget reset.
7. **Preamble.** New entry in `SprintPreambles.StagePreambles`. `BuildPreamble` signature extension. Tests that the right preamble is selected based on `SelfEvaluationInFlight`.
8. **API surface.** Two endpoints (§6). Controller tests.
9. **Notification mapping.** Three Progress notifications + reuse SprintBlocked. Broadcaster tests.
10. **Acceptance test thread.** Live-run a sprint through Implementation, call `RUN_SELF_EVAL`, submit AnyFail → re-eval → AllPass → advance. Then a separate run that hits the cap and gets blocked. Capture observed behaviour in roadmap status note.

Order matters: each step depends only on prior steps. Tests must be written alongside, not after. **Risk classification per step**: 1🟢, 2🟡, 3🔴, 4🟡, 5🔴, 6🟡, 7🟢, 8🟢, 9🟢, 10🟡. The 🔴 steps (verdict path and verdict gate) get full Anvil Large treatment with three reviewers (gpt-5.5 + claude-opus-4.6 + gemini-3-pro-preview).

---

## 9. Open design questions for the human reviewer

**q1. `MaxSelfEvalAttempts` default — 3 or 5?** This doc proposes 3. Three says "we don't trust circular fix-attempts past round 3"; five gives more rope before notifying a human. There is no way to tell which is right without running real sprints. Recommend ship at 3, raise to 5 if the auto-block fires on healthy work.

**q2. Should `MERGE_PR` / `CREATE_PR` be hard-blocked at the command layer when `SelfEvaluationInFlight == true`?** This doc deliberately does NOT block them — the preamble discourages them and that's enough for cooperative agents. Hard-blocking adds command-layer plumbing and a new failure mode (PR-in-flight when self-eval starts). Recommend: do not hard-block; revisit if observed misbehaviour.

**q3. Where do acceptance criteria come from when `RequirementsDocument` is the wrong source?** A future sprint archetype might not produce a RequirementsDocument (e.g., a pure refactor sprint with no Intake). For now, refuse self-eval in that case (§3.2). Long-term, the criteria source could be configurable per archetype. Out of scope for P1.4 full; flagging here.

**q4. Should `SelfEvaluationReport` content be append-only or last-write-wins?** This doc treats each report as a new row (multiple per sprint). The orchestrator reads "the most recent." Alternative: keep one row and overwrite. Append-only gives a clean audit trail (frontend can show "Attempt 1 found X, Attempt 2 fixed it") and matches `SprintArtifactEntity`'s natural shape. Recommend append-only.

**q5. Should the verdict gate (§4.4) require AllPass, or accept "AllPass-or-human-override"?** A human running `ADVANCE_STAGE` with `force=true` today bypasses prerequisites but NOT artifact gates. Self-eval verdict is a soft gate (we said "all PASS"); should `force=true` skip it? Recommend: no — the whole point is that the auto-advance path requires PASS. A human who wants to advance despite a FAIL can call `POST /api/sprints/{id}/unblock` after a manual `MarkSprintBlocked` followed by storing an override report; the friction is intentional.

---

## 10. What this doc does NOT design

- **Cost tracking / token caps tied to self-eval.** Same hook point as P1.2 §4.6; deferred to a separate item.
- **A persona-level "self-evaluator" agent role.** The current design uses the existing roster; the Planner synthesizes verdicts from team input. A dedicated `Evaluator` role might raise quality but adds roster + permissions surface. Defer until we see whether the existing roster can self-evaluate competently.
- **Frontend self-eval timeline UI.** The GET endpoint (§6) is shaped for it; the UI is a follow-up task.
- **Self-eval at stages other than Implementation.** Validation already has its own report, Planning has its own gate. Self-eval is specifically about *whether the code does what was promised* — that question is only meaningful at the boundary between Implementation and FinalSynthesis.

---

## 11. Summary for the impatient

- Add `["Implementation"] = "SelfEvaluationReport"` to `RequiredArtifactByStage`. Existing artifact gate now blocks Implementation→FinalSynthesis without a stored report.
- New `SelfEvaluationReport` artifact type with strict JSON validation: one entry per acceptance criterion (verbatim, no skipping), evidence required, rollup verdict computed and cross-checked.
- New `RUN_SELF_EVAL` command opens the substate; agent-side preamble tells the team to evaluate, not code.
- Verdict path runs server-side from `StoreArtifactAsync`: AllPass keeps the substate open for the Planner's `ADVANCE_STAGE`; AnyFail/Unverified re-opens Implementation; cap-exceeded calls existing `MarkSprintBlockedAsync` (the auto-block heuristic the roadmap reserved).
- `AdvanceStageAsync` adds a second check requiring the latest report's `OverallVerdict == "AllPass"` to actually transition.
- Reuses every halt + notification primitive P1.4-narrow already shipped. Net new code is concentrated in artifact validation, the verdict path, and one preamble.
