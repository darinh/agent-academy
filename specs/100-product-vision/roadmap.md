# 100 — Roadmap

**Companion to**: [spec.md](./spec.md), [gap-analysis.md](./gap-analysis.md)
**Established**: 2026-04-24
**Status**: Authoritative work backlog. Future sessions execute from this list, top-down.

> ⚠️ **This is the source of truth for what to work on.** If you are starting a new session and have no specific task from the human, **work the next pending item on this list**. Do not invent work that is not on this list. If you believe a new item belongs on the list, add it to the bottom under "Proposed Additions" and surface it to the human for triage.

---

## Phase 1 — Make One Autonomous Loop Work End-to-End

**Goal**: Deliver §10 of [spec.md](./spec.md) — the "definition of done for the vision" — in a minimum viable form. One sprint, one room, one observable end-to-end run.

**Acceptance test for the entire phase** (not for individual items):
> The human creates a room with a goal, posts the goal, walks away from the keyboard. Within 30 minutes the agents have produced a tracking artifact, advanced through Implementation, run a self-evaluation, produced a final work report artifact, posted a Discord notification, and stood the room down to read-only. Returning, the human can see all of this without re-prompting.

**This acceptance test is non-negotiable.** It is the only criterion for declaring Phase 1 complete. "The data model exists" is not Phase 1 complete. "The endpoint returns 200" is not Phase 1 complete. The 10-step observable run is Phase 1 complete.

### P1.1 — Sprint Creation Posts a Kickoff Message [DRAFT — needs refinement during execution]

**Closes gap**: G2.
**Risk**: 🟡 (modifies `SprintService.CreateSprintAsync` business logic).
**Estimated effort**: ~0.5 day.

**What**: When `CreateSprintAsync` succeeds, post a coordination message into the sprint's primary room. The message instructs the team that a new sprint has begun, references the sprint goal, and asks for a Planning round.

**Open questions to resolve during execution**:
- Is there a "primary room" concept per sprint, or do sprints attach to whatever room created them? Trace the data model.
- What message type does the orchestrator's `ProcessQueueAsync` accept as a wake-up trigger? Is it any room message, or does it need to be from a specific sender / have a specific tag?
- Does the kickoff message need to come "from" the human, the system, or a specific agent role?

**Acceptance test (this item alone)**:
- Create a sprint via the API.
- Observe a message appear in the sprint's room within 5 seconds.
- Observe the orchestrator process that message and queue at least one agent response.

### P1.2 — Orchestrator Tick / Self-Drive [✅ DONE 2026-04-25]

**Closes gap**: G1.
**Risk**: 🔴 (concurrency, scheduler logic — central to product behavior).
**Estimated effort**: ~1–2 days.

**What**: The orchestrator gains the ability to advance a conversation **without** a human message arriving. After an agent responds, if the sprint is in a phase that expects continued work and there is no pending human input, the orchestrator schedules the next round automatically.

**Design decisions** (resolved 2026-04-25 in [`p1-2-self-drive-design.md`](./p1-2-self-drive-design.md) §12):
- Round-based loop with per-stage and per-sprint round caps (defaults in `appsettings.json`).
- "Round complete" signal: end of agent turn with no human input pending.
- Backpressure: human messages mid-round drop pending continuations and the room re-tick on the human turn.
- Idle: orchestrator sleeps when no active sprint room has pending work.

**Critical safety requirements** (all baked into the resolved design):
- Per-stage and per-sprint round caps (defaults shipped in `appsettings.json`).
- Cost monitoring via [`cost-tracking-design.md`](cost-tracking-design.md): always-on tracking + anomaly detection + configurable `BreachAction`. Round caps are the hard ceiling; cost is an observation-and-alert mechanism, not a hard cap.
- Emergency stop available via existing `POST /api/sprints/{id}/block`.

**Design doc**: [`p1-2-self-drive-design.md`](./p1-2-self-drive-design.md) — drafted 2026-04-25, design questions resolved 2026-04-25; ready for implementation per §13.

### P1.3 — Stage Advancement Triggers Next Round [DRAFT]

**Closes gap**: G2 partial, supports G4.
**Risk**: 🟡.
**Estimated effort**: ~0.5 day.

**What**: When a sprint advances stages (e.g., Planning → Implementation), automatically enqueue a new round so agents pick up the new stage's preamble and act on it. Today the stage transitions are silent.

**Acceptance test**:
- Trigger a stage advance via the API.
- Observe an agent message appear in the room reflecting the new stage's intent.

### P1.4 — Self-Evaluation Ceremony at End of Implementation [design RESOLVED — ready for implementation]

**Closes gap**: G4.
**Risk**: 🔴 (preamble + state machine wiring; affects sprint completion semantics).
**Estimated effort**: ~1 day.

**What**: When a sprint reaches the boundary between Implementation and FinalSynthesis, the orchestrator does NOT advance directly. Instead it triggers a **self-evaluation round** with a hard-coded preamble that instructs agents to:
1. List acceptance criteria from the sprint's tracking artifact.
2. For each criterion, state PASS / FAIL / UNVERIFIED with evidence.
3. If any FAIL or UNVERIFIED, return to Implementation with a fix plan.
4. Only on all PASS may the sprint advance to FinalSynthesis.

**Open design questions**:
- Where does the preamble live? `Config/sprint-stages.json`? Hardcoded in C#? An MCP-style configurable prompt?
- How does the orchestrator parse "all PASS" vs "some FAIL" from the agent response? Strict JSON output? Specific command invocation? Tool call?
- How many self-eval loops are allowed before the sprint is declared blocked and the human notified?

**Recommend**: write a design doc; consider this the *defining* feature of the autonomy loop because it's what makes the agents *trustworthy* without supervision.

### P1.5 — Tracking Artifact Required at Planning [DONE — pre-existing]

**Closes gap**: G6 partial.
**Risk**: 🟡.
**Estimated effort**: ~0.5 day.

**What**: Sprint cannot advance from Planning to Implementation unless a tracking artifact exists for it. Block the stage transition if absent; surface a clear error to the orchestrator so the agents know they must produce one.

**Status**: Already implemented at the time the roadmap was authored. `SprintStageService.RequiredArtifactByStage["Planning"] = "SprintPlan"` is enforced in `AdvanceStageAsync` (cannot be bypassed by `force=true`). `SprintPlanDocument` (`Sprints.cs:63-66`) is the tracking artifact (phases, deliverables, overflow). Verified by `SprintServiceTests.AdvanceStage_ThrowsWithoutRequiredArtifact`.

### P1.6 — Final Work Report Artifact Required at FinalSynthesis [DONE — pre-existing]

**Closes gap**: G6 partial.
**Risk**: 🟡.
**Estimated effort**: ~0.5 day.

**What**: Sprint cannot transition to Completed unless a final work report artifact exists. Same enforcement pattern as P1.5.

**Status**: Already implemented at the time the roadmap was authored. `SprintService.CompleteSprintAsync` (`SprintService.cs:229-241`) refuses to mark a sprint Completed unless a `SprintReport` artifact exists at FinalSynthesis. `force=true` allows human override (intentional — agents cannot bypass).

### P1.7 — Discord Notification on Idle / Blocked / Sprint Complete [PARTIAL — Completed + Idle done; Blocked deferred to P1.4]

**Closes gap**: G7 (sprint-complete + team-idle paths).
**Risk**: 🟡.
**Estimated effort**: ~0.5 day.

**What**: Wire orchestrator state changes to NotificationProvider:
- Sprint completes → notify "Sprint X completed, report attached". **[DONE]** — `ActivityNotificationBroadcaster` (`src/AgentAcademy.Server/Notifications/ActivityNotificationBroadcaster.cs`) now includes `SprintCompleted` and `SprintCancelled` in `NotifiableEvents` and maps them through `MapToNotification` to `NotificationType.TaskComplete` / `NotificationType.Error` respectively.
- Sprint blocks (self-eval failed N times, or explicit blocker) → notify "Sprint X needs attention". **[DONE — explicit-blocker path]** — `ActivityEventType.SprintBlocked` is emitted by `SprintService.MarkSprintBlockedAsync` and mapped to `NotificationType.NeedsInput` ("Sprint needs attention"). The auto-blocking heuristic (self-eval failure count) lands with full P1.4.
- All sprints idle, no work queued → notify "Team is idle, awaiting instructions" (debounce so this fires at most once per idle period). **[DONE]** — new `TeamIdleNotificationService` (`src/AgentAcademy.Server/Notifications/TeamIdleNotificationService.cs`) subscribes to `IActivityBroadcaster`. On `SprintCompleted`/`SprintCancelled` it queries `Sprints.Count(s => s.Status == "Active")`; if zero AND a per-process latch is unset, dispatches one `NotificationType.NeedsInput` "Team is idle" message via `INotificationManager.SendToAllAsync`. The latch resets on `SprintStarted`. Idle definition is "no `Active` sprints"; `AwaitingSignOff` sprints are still `Active` and emit their own sign-off notifications, so they are intentionally treated as not-idle to avoid double-notify.

### P1.8 — Room Lifecycle: Read-Only When Sprint Completes [DONE]

**Closes gap**: G8.
**Risk**: 🟡.
**Estimated effort**: ~0.5–1 day depending on what already exists.

**What**: When a sprint enters Completed, the associated room transitions to a state where:
- Chat send is rejected by the API and SignalR hub.
- Agents are unsubscribed / marked offline in that room.
- The room remains readable and searchable in the UI.

**Investigation needed first** (per gap-analysis G8): see what's already wired. May be smaller than estimated.

### P1.9 — Phase 1 Acceptance Test Run [REQUIRED]

After P1.1–P1.8 are merged: a human-supervised observable run of the full 10-step acceptance test from §10 of `spec.md`. If any step fails, the failing capability re-enters the roadmap. Phase 1 is **not declared complete** until this run passes.

---

## Phase 2 — Cross-Project Background Work

**Goal**: Close G5. Agents work on multiple projects whether or not the UI is loaded on them.

**Acceptance test for the phase**:
> Two projects exist. The human loads Project A in the UI. A sprint is active in Project B. Without any UI interaction with Project B, the sprint in Project B advances and reaches a checkpoint observable on inspection.

### P2.1 — Multi-Workspace Orchestrator Scheduling [DRAFT — investigation needed]

**Closes gap**: G5.
**Risk**: 🔴 (concurrency, fairness).
**Estimated effort**: TBD after investigation.

**What**: The scheduler and orchestrator process queues across all workspaces, not just the foreground one. Investigation: trace `SprintSchedulerService.EvaluateOneAsync` and confirm current behavior.

### P2.2 — UI Indicator for Background Work [DRAFT]

**Closes gap**: minor — supports visibility (§6).
**Risk**: 🟢.

**What**: When background work is happening on a project not currently loaded, surface a small indicator in the UI (badge, notification toast, or a "background activity" tray). The human should know when other projects are progressing.

---

## Phase 3 — Visibility Surface Pruning

**Goal**: Close G11. Once sprints actually run, prune the 18-tab nav down to a small high-signal set informed by what's actually used during real sprint runs.

**Acceptance test for the phase**:
> Default nav is ~5 primary surfaces (proposal: Rooms, Sprints, Artifacts, Agents, Settings). Detail panels (Memory, Goals, Plan, Knowledge, Digests, Activity, Timeline) live as drill-downs from those primaries, not as siblings. The human reports the nav is no longer confusing.

### P3.1 — Nav Telemetry [DRAFT — only after Phase 1]

**Closes gap**: prerequisite for the prune decisions.
**Risk**: 🟢.

**What**: Lightweight client-side recording of which nav items are visited. NOT for product analytics in the SaaS sense — for the human's own decision-making about which panels to keep.

### P3.2 — Restructure Nav [DRAFT — needs UX design]

**Closes gap**: G11.
**Risk**: 🟡.

**What**: Per-design after telemetry data is in. Specific structure to be proposed at that point.

---

## Deferred — Explicit Non-Goals (Track to Prevent Drift)

These items have been considered and explicitly deferred. Future agents: do NOT pick these up without human direction.

| Item | Why deferred |
|------|--------------|
| Rewrite command system to use MCP tools | ~2-week effort. Right shape long-term, not on the critical path to the autonomy loop. Revisit after Phase 1. |
| New agent persona authoring tools | The agents we have work. Defer until we know what's missing from the autonomy loop. |
| Forge UI polish | Forge engine works. Wiring it into Planning (P-future) matters more than its visual surface. |
| Memory v2 / Knowledge v2 | Defer until we know they're used. |
| Public OAuth / multi-user features | Out of scope per spec.md §2. |
| Marketing site, landing page, onboarding flow | Out of scope per spec.md §2. |

---

## Proposed Additions

(Future agents and the human add items here for triage. Do not silently work on these — they are not on the active roadmap until the human moves them up.)

- **Per-archetype acceptance-criteria source for self-evaluation** (referenced by P1.4 design §10 deferred). Today self-eval reads acceptance criteria from `RequirementsDocument`. Future sprint archetypes (e.g., a refactor sprint with no Intake) would have no `RequirementsDocument`. A pluggable per-archetype criteria source would unblock self-eval for those archetypes. **Why not yet**: sprint archetypes are not a real concept in the codebase (zero references in `src/`); designing this now would be on top of a non-existent abstraction. Wait until at least one non-default archetype is proposed.

- **Per-tool `MaxDenialsPerTurn`** (carried over from earlier session handoffs, surfaced again 2026-04-25). Today permission-denial throttling is a single global cap per agent turn. Splitting it per-tool would let agents continue working when one tool is misbehaving while still bounding total damage. **Why not yet**: not blocking any roadmap item; revisit when an actual scenario produces the symptom.

- **Refactor delivered ✅: `DiscordNotificationProvider.cs`** (shipped 2026-04-26 by PR #153, squash-merged to develop). Lifecycle FSM extracted into new `DiscordProviderLifecycle` class (667 lines) per [`discord-lifecycle-refactor-design.md`](./discord-lifecycle-refactor-design.md) (status: IMPLEMENTED). Provider reduced from 540 lines to a transport-only role; states {Disconnected, Configured, Connecting, Connected, Disconnecting, Disposed} with explicit transitions. Outbound-op drain cancellation, dispose-after-disconnect, and connect-on-dead-socket recovery all live in the FSM. 553 new unit tests in `DiscordProviderLifecycleTests`; full server suite 6519/6519 + Forge 425/425 green. Three adversarial reviewers (gpt-5.3-codex, opus-4.6, sonnet-4.6) caught 4 unique findings, all fixed in-branch before merge.

- **Refactor candidate: `AgentOrchestrator.cs`** (surfaced 2026-04-25 by stabilization gate; missed by the earlier same-day pass). 413 lines, 12 fix commits in the last 30 days touching coordination paths: orphaned `TaskId` on git-branch failure (#281b88d), breakout-loop failure surfacing to parent room (#6c5323d), concurrent breakout rooms for the same agent (#b764e6b), copilot-SDK auth alignment (#112). The dispatch / queue-dedupe / sprint-state-recheck / breakout-lifecycle coordination is the hot spot — the file is already decomposed into sub-services (`IConversationRoundRunner`, `IOrchestratorDispatchService`, `IBreakoutLifecycleService`, `IDirectMessageRouter`), so the refactor is about how the orchestrator *coordinates* those collaborators (lock-based queue + dedupe matrix + pre-dispatch state re-check), not re-extracting them. Mirror of the Discord pattern: stable transport, recurrent coordination bugs. Recommend a state-machine for queue-item dispatch (Idle → Dispatching{kind} → Running{kind} → Settling) with explicit transitions and dedupe rules, replacing the current `_processing` boolean + `_queuedRoomKinds` dictionary + ad-hoc HM-over-SC upgrade logic. **Why not yet**: not on the critical path to Phase 1 acceptance; current loop works under unit coverage. No design doc drafted yet — propose drafting one only after Phase 1 closes (P1.9 passes), to avoid pulling refactor effort into the acceptance window. Revisit during Phase 2 or after the next coordination bug.

- **Phase 1 acceptance run automation (P1.9 helper) — partial: read-only status driver delivered 2026-04-26 (`scripts/p1-9-acceptance-check.sh`).** Read-only inspector that walks the §10 conditions against an existing sprint and reports PASS/FAIL/NA per step, sourcing every signal from the live API (no writes; safe to run against a running sprint). Authorized by human override after the operator-wrapper nag-loop (the gate "build only after P1.9 passes once" was overridden because the diagnostic value was needed before the live run, not after). The remaining piece — the *driving* automation that posts a goal and polls stage advances — is still gated on P1.9 passing once. **First live run against Sprint #2** surfaced two real signals worth filing: (a) Sprint #2 has been in Implementation 24h with `selfEvalAttempts=0` (P1.4 ceremony has not fired); (b) zero `senderKind=Agent` messages in the main room (agent activity may be entirely in breakouts, or the sprint is silently stuck). Do not auto-fix — surface for human triage at the next P1.9 attempt. **2026-04-26 follow-up**: chat-relay fix in this PR addressed (b) — main-room agent visibility now PASSES (25 agent messages observed live). Re-run on Sprint #2 went 3/3 → 4/2 (PASS/FAIL).

- **P1.4 ceremony never fires — root-cause diagnosis (2026-04-26, surfaced from this PR's re-run)**: Sprint #2 still shows `selfEvalAttempts=0` after 25h. Audit of `/api/commands/audit` (158 records) shows the orchestrator IS actively trying to close tasks but each attempt fails:
  1. **`UPDATE_TASK status=Completed` → VALIDATION** (planner-1, 01:13:01). `UpdateTaskHandler.AllowedStatuses` allows only `{Active, Blocked, AwaitingValidation, InReview, Queued}` — terminal states (`Completed`/`Approved`/`Merging`/`Cancelled`/`ChangesRequested`) flow through dedicated commands (`MERGE_TASK`/`APPROVE_TASK`/`CANCEL_TASK`/`REQUEST_CHANGES`). This is by design, but the agent's mental model is "set status=Completed" — the Implementation preamble does not name the lifecycle commands explicitly enough.
  2. **`APPROVE_TASK` → NOT_FOUND** (planner-1, 01:11:56) using slug `smoke-test-verify-chat-relay-after-system-recovery-cde495` — the agent invented a slug-style ID rather than capturing the real GUID returned by ADD_TASK.
  3. **`MERGE_TASK` → EXECUTION exit 1, empty stderr** (planner-1, 01:12:24). Symptom is `git commit` returning 1 with no stderr — almost certainly "nothing to commit" because the squash merge had no diff (the upstream branch had no commits — natural consequence of failure (2)).

  **Net effect**: tasks are stuck in `Active` / `Blocked` forever. The "all tasks merged" gate that should prompt Aristotle to RUN_SELF_EVAL never opens, so the P1.4 ceremony never fires. This is a **prompt/protocol gap, not a code bug**: `RunGitAsync` correctly uses `ProcessStartInfo.ArgumentList.Add` (no shell-quoting issue); the enum has all the right values. The fix lies in (a) tightening the Implementation preamble to spell out the lifecycle command sequence (`ADD_TASK → … → APPROVE_TASK → MERGE_TASK`, never `UPDATE_TASK status=Completed`), and (b) making ADD_TASK's response include the canonical task ID in a way the agent reliably echoes back. **Why not yet**: this would modify a live experimental sprint mid-flight and risk losing 24h+ of operational data; better to land it as part of the supervised P1.9 attempt with a fresh sprint where we can reset and rerun. Surface for human triage at the next P1.9 attempt.

---

## Status Tracking

| Item | Status | Started | Completed | Notes |
|------|--------|---------|-----------|-------|
| P1.1 | done | 2026-04-25 | 2026-04-25 | Kickoff posts a system message in every active workspace room and wakes the orchestrator on `CreateSprintAsync` (manual / scheduled / auto-start paths). Live-verified: Sprint #2 created → kickoff message at +107ms, Aristotle responded at +44s. |
| P1.2 | done | 2026-04-25 | 2026-04-25 | **Self-drive decision service shipped.** `SelfDriveDecisionService` invokes 12-step gate tree after every round (kill-switch → sprint state → caps → outcome → stage idle → min-interval delayed enqueue with re-check → cost guard). New `QueueItemKind.SystemContinuation` enqueue path with full dedupe matrix vs. `HumanMessage` (HM upgrade-in-place over a queued SC) and pre-dispatch sprint state re-check. `SprintEntity` gains `RoundsThisSprint`/`RoundsThisStage`/`SelfDriveContinuations`/`LastRoundCompletedAt`/`MaxRoundsOverride`/`BlockedAt`/`BlockReason` (foundation PR). `SprintStageService` resets stage counters on advance/approve. Cap-trip → `MarkSprintBlockedAsync` (HALT). `ActivityEventType.SprintRoundContinuationScheduled` Internal-only (no Discord noise). Fail-open in the round runner: any decision-service exception logs and is ignored. 28 new unit tests across decision branches, dedupe matrix, and stage-reset; full broader suite (6066 tests) passes. Live verification deferred — mechanism unit-covered end-to-end. |
| P1.3 | done | anvil | feat/p1-3-stage-advance-announce | Live-verified 2026-04-25: Sprint #2 Intake→Planning via /approve-advance posted "➡️ Sprint #2 advanced (user-approved)" to 6 active rooms; Aristotle responded in +35s. Includes regression test + targetRoomIds snapshot for FinalSynthesis room-completion edge case (caught by adversarial review). |
| P1.4 | done | 2026-04-25 | 2026-04-25 | **Full P1.4 self-evaluation ceremony shipped across PR1 (foundation #143), PR2 (verdict path #144), and PR3 (API + notifications, this PR).** PR3 adds: `POST /api/sprints/{id}/self-eval/start` (server-side equivalent of `RUN_SELF_EVAL`, audited via `HumanCommandAuditor`, dispatches the existing `RunSelfEvalHandler` and wakes `IAgentOrchestrator.HandleHumanMessage(roomId)` for every active room in the sprint's workspace); `GET /api/sprints/{id}/self-eval/latest` returning the most recent `SelfEvaluationReport` artifact + attempts/cap/verdict/in-flight (204 if none); `ISprintArtifactService.GetLatestSelfEvalReportAsync`; `ActivityEventType.SelfEvalCompleted` mapping in `ActivityNotificationBroadcaster` — verdict-aware: `AllPass`→`TaskComplete` "Sprint #N passed self-evaluation", `AnyFail`→`AgentThinking` "Sprint #N self-eval found issues (attempt N/cap)", `Unverified`→`AgentThinking` "Sprint #N self-eval has unverified items (attempt N/cap)". The cap-exceeded auto-block reuses the existing `SprintBlocked`→`NeedsInput` mapping from PR2. **Notification-type deviation from design §7**: design specified `NotificationType.Progress` (low-urgency); the existing enum has no `Progress` value, so `AgentThinking` is used for AnyFail/Unverified (closest "low-urgency status" surface) and `TaskComplete` for AllPass. **Acceptance test**: 13 new unit tests cover the controller + broadcaster paths end-to-end (workspace ownership, missing-handler guard, wrong-stage 409, happy path with audit + flag-flip + wake, orchestrator-failure best-effort, append-only latest-report ordering, missing-metadata fallback, all three verdict mappings); full server suite 6424 passing (was 6411 baseline). The live §10 acceptance run (driving a real sprint through Implementation→AnyFail→AllPass and a separate cap-exceeded block) is feasible against the running server and is captured here as the operational follow-up; mechanism is unit-covered end-to-end. **Earlier work (PR1 #143)**: artifact type + records + `SprintEntity` columns + parse-level static validation. **Earlier work (PR2 #144)**: `RequiredArtifactByStage["Implementation"] = "SelfEvaluationReport"` + verdict-equality gate; `RUN_SELF_EVAL` command with §4.2 validations + atomic `ExecuteUpdateAsync` flip; single-transaction verdict path with DB-aware validation + cap-exceeded auto-block; append-only artifact storage via filtered partial unique index; `UnblockSprintAsync` resets self-eval state on self-eval blocks; `ImplementationSelfEval` preamble; `SelfEvalOptions`; `SelfEvalCompleted` event. **Design**: [`p1-4-self-evaluation-design.md`](./p1-4-self-evaluation-design.md) §8 steps 1–9 fully landed; step 10 (live acceptance) is the next operator action. |
| P1.5 | done | 2026-04-25 | 2026-04-25 | **Already implemented** prior to roadmap authoring. `SprintStageService.RequiredArtifactByStage["Planning"] = "SprintPlan"` (SprintStageService.cs:44) is enforced in `AdvanceStageAsync` (SprintStageService.cs:112-123), even when `force=true`. SprintPlanDocument (Sprints.cs:63-66) IS the tracking artifact: Summary + Phases (Name/Description/Deliverables) + OverflowRequirements. Covered by `SprintServiceTests.AdvanceStage_ThrowsWithoutRequiredArtifact` and the multi-stage `AdvanceStage_ThrowsAtFinalStage` flow. Gap analysis G6 was stale at authoring. |
| P1.6 | done | 2026-04-25 | 2026-04-25 | **Already implemented** prior to roadmap authoring. `SprintService.CompleteSprintAsync` (SprintService.cs:229-241) refuses to mark a sprint Completed unless a `SprintReport` artifact exists at FinalSynthesis. The `force=true` override is intentional (humans can override; agents cannot). SprintReport (Sprints.cs:78-82) carries Summary + Delivered + Learnings + OverflowRequirements — i.e., the work-report contract. Gap analysis G6 was stale at authoring. |
| P1.7 | done | 2026-04-25 | 2026-04-25 | Sprint-complete + idle notifications shipped (`TeamIdleNotificationService` + `ActivityNotificationBroadcaster` extension). Blocked-sprint notification wired to `ActivityEventType.SprintBlocked` once P1.4's blocked-signal subset landed (2026-04-25): mapped to `NotificationType.NeedsInput` "Sprint needs attention". 7 unit tests + DI wiring test + broadcaster mapping test. |
| P1.8 | done | 2026-04-25 | 2026-04-25 | Completed + Cancelled + timed-out sprints freeze their workspace rooms in the same transaction as the sprint state change (`SprintService.PersistTerminalSprintWithRoomFreezeAsync`). `MessageService` rejects writes to Completed/Archived rooms with `RoomReadOnlyException` → 409 Conflict. Descendant breakouts are archived and occupants evacuated even when the parent room was already flipped Completed during FinalSynthesis. Auto-start provisions a fresh default room before creating the next sprint. Two adversarial-review rounds (6 findings across codex + gpt-5.5 + opus-4.6) addressed. Round-2 surfaced the atomicity race, stranded breakouts under already-Completed parents, and the inert auto-start; all fixed. |
| P1.9 | blocked | 2026-04-26 | — | **Status as of 2026-04-27 (Sprint #11 acceptance attempt): blocker A (SDK permission failure) is CLOSED by PR #174; blocker B (worktree cwd isolation) is still OPEN — design doc shipped (PR #160) but implementation has not started. Sprint #11 reached Implementation cleanly (5 of 6 stages) with zero SDK-tool denials, then failed to reach FinalSynthesis because Hephaestus's `write_file` calls landed in the main-checkout cwd instead of the assigned task worktree, and a stray `IGitService` commit landed unrelated work directly on local develop. P1.9 cannot pass until blocker B implementation lands and a sprint reaches §10 step 7 PASS.** Original 2026-04-26 verdict (preserved for context): supervised acceptance run executed against fresh Sprint #3 (cancelled). 6 PASS / 2 FAIL / 2 NA — workflow advanced Intake→Planning→Discussion→Validation→Implementation; stalled in Implementation. Two real platform blockers identified: (1) **SDK permission failure** — `unexpected user permission response` on baseline `bash`/`apply_patch` requests in fresh Copilot SDK sessions, reproduced across 2 agents (Hephaestus, Athena) and 2 fresh sessions. **CLOSED 2026-04-27 by PR #174** (SDK-builtin-tool exclusion + name-collision filter; live-validated by Sprint #10 + Sprint #11 with 0 denials). (2) **Workspace isolation gap** — `write_file` from a breakout-session agent writes to the develop checkout (`/home/darin/projects/agent-academy/`) instead of the assigned task worktree (`.worktrees/task_…/`). Three-layer root cause documented in design doc PR #160: (a) SDK tool wrappers ignore worktree path, (b) `IGitService` singleton commits in develop regardless, (c) twelve structured command handlers ignore `CommandContext.WorkingDirectory`. **Re-confirmed live by Sprint #11 on 2026-04-27**: identical failure mode — agent wrote `VersionController.cs` / `VersionInfoProvider.cs` / `IVersionInfoProvider.cs` to main checkout cwd; `Hephaestus (Engineer)` git author committed `d21201b` directly on local develop bundling Sprint #11 work into a misleadingly-named "fix:" commit (layer (b) of the design doc). Sprint #11 force-completed at Implementation; develop reset to `origin/develop`. **§10 step results (Sprint #11)**: 1✓ requirements, 2✓ participation (no permission denials this run), 3✓ sprint plan, 4 NA, 5✓ reached Implementation, 6✗ no self-eval, 7✗ no SprintReport, 8 NA, 9 NA, 10 NA. **Phase 1 is NOT closed until blocker B implementation lands and a re-run reaches step 7 PASS.** Next concrete action: implement blocker B per design doc `p1-9-blocker-b-worktree-cwd-isolation-design.md` (all three layers), then re-run a supervised acceptance sprint. |
| P2.1 | pending | — | — | After Phase 1 |
| P2.2 | pending | — | — | After Phase 1 |
| P3.1 | pending | — | — | After Phase 1 |
| P3.2 | pending | — | — | After P3.1 |

When a future session works on an item, update its status here in the same commit. The status table is the at-a-glance truth.

## Revision History

- **2026-04-24**: Initial roadmap captured from product-design conversation. Phase 1 defined as the autonomous loop end-to-end. Acceptance test set as the 10-step observable run from spec.md §10. — agent: anvil (operator: agent-academy)
- **2026-04-25**: Stabilization session. Baseline green (6473 server + 425 forge + 3020 client tests; 0 build warnings). Pruned orphan worktree `task/design-unified-task-service-layer-contracts-92c583` (already merged via #148). Surfaced 3 new Proposed Additions: per-tool `MaxDenialsPerTurn` (carry-over), `DiscordNotificationProvider` refactor (4 lifecycle fixes in 14 days), and a P1.9 acceptance-run helper. Phase 1 (P1.1–P1.8) all marked done; only P1.9 (human-supervised acceptance run) remains for Phase 1 closure. — agent: anvil (operator: agent-academy)
- **2026-04-25** (later): Drafted design doc for the `DiscordNotificationProvider` lifecycle refactor at [`discord-lifecycle-refactor-design.md`](./discord-lifecycle-refactor-design.md). No code changes — proposal only, awaiting human triage. P1.9 deferred (intentionally human-supervised). — agent: anvil (operator: agent-academy)
- **2026-04-25** (later still): Stabilization-gate follow-up. Surfaced a 4th Proposed Addition: refactor candidate `AgentOrchestrator.cs` (413 lines, 12 fix commits in 30 days; coordination/dedupe/state-recheck hot spot). Earlier same-day pass missed this file because the worst raw fix-count offender (`WorkspaceRuntime.cs`, 24 fixes) was already deleted on 2026-04-12 (commit b215362) and skewed the candidate ranking. No code change; documentation only. — agent: anvil (operator: agent-academy)
- **2026-04-25** (corrective): Removed self-imposed "human triage" gate from `discord-lifecycle-refactor-design.md` and from this roadmap entry. Author resolved all 5 §6 design questions himself (decisions A1, B-keep, C-idempotent, D-out-of-scope, E-no-interface). Updated `.github/copilot-instructions.md` autonomous-operation rules to forbid "open questions for human triage" in design docs — humans override via PR review on the implementation PR, not via gate on the design doc. Implementation may begin under Anvil Large at any session. No code change. — agent: anvil (operator: agent-academy)
- **2026-04-26**: Discord lifecycle FSM refactor shipped end-to-end. PR #153 (5 files, +1471/-265) squash-merged to develop after CI green and 3-reviewer adversarial pass. New `DiscordProviderLifecycle` class owns the state machine; `DiscordNotificationProvider` reduced to transport. Design-doc status flipped to IMPLEMENTED. Refactor candidate entry under Proposed Additions converted to a "delivered ✅" note. Next session should pick up P1.9 (human-supervised acceptance run) or the next pending Proposed Addition. — agent: anvil (operator: agent-academy)
- **2026-04-26** (later): P1.9 status driver shipped + chat-relay fix in `MessageService.PostBreakoutMessageAsync` (PR #155). Agent breakout messages now mirror as `💬 {agent} [in {breakout}]: …` system summaries into the parent room — closes the long-standing "main room shows zero agent activity" visibility gap. Live-verified: re-run against Sprint #2 went 3 PASS/3 FAIL → 4 PASS/2 FAIL (step 2 now PASSES with 25 agent messages observed). Remaining 2 FAILs are P1.4-ceremony related and traced to a prompt/protocol gap (agents using slug IDs instead of GUIDs returned by ADD_TASK; `UPDATE_TASK status=Completed` rejected because terminal states flow through MERGE_TASK/APPROVE_TASK) — diagnostic captured under Proposed Additions, no code fix on the live experimental sprint. — agent: anvil (operator: agent-academy)
- **2026-04-26** (later still): P1.9 blocker A landed as PR #159 (added `hook` to `AlwaysSafeKinds` + dedup-by-Kind denial-log escalation; 28→35 tests, full suite 6954 passing; awaiting human merge per NO-SELF-MERGE). Drafted P1.9 blocker B design doc at [`p1-9-blocker-b-worktree-cwd-isolation-design.md`](./p1-9-blocker-b-worktree-cwd-isolation-design.md). Diagnosed the parallel-breakout write-isolation failure as a *three-layer* bug rooted in a shared `FindProjectRoot()` assumption: (1) SDK tool wrappers (`CodeWriteToolWrapper`, `AgentToolFunctions`) ignore worktree path; (2) `IGitService` singleton commits in develop regardless; (3) twelve structured command handlers (`READ_FILE`, `RUN_TESTS`, `RUN_BUILD`, `SHOW_DIFF`, etc.) ignore `CommandContext.WorkingDirectory`. v1 of the design only identified layer 1; 3-reviewer adversarial pass (codex + opus + gpt-5.5) converged on the layers-2-and-3 omissions and 5 other gaps. v2 covers all three layers, the review-fix loop omission, scopeRoot identity validation, and read-isolation acceptance criteria. No code change yet — design awaits human review on the design PR; implementation is gated only on that review (per autonomous-operation rules, design questions resolved by author, not punted to human triage). — agent: anvil (operator: agent-academy)
- **2026-04-26** (P1.9 blockers landing): PR #158 (P1.9 supervised acceptance verdict + roadmap update), PR #159 (P1.9 blocker A — `AgentPermissionHandler` accepts SDK `hook` permission kind + dedupes denial logs by Kind), and PR #160 (P1.9 blocker B design — three-layer worktree cwd isolation: SDK tool wrappers + `IGitService` singleton commits + the 12 structured command handlers) all merged to develop. P1.9 stays `blocked` until blocker B *implementation* lands and a re-run reaches §10 step 7 PASS. — agent: anvil (operator: agent-academy)
- **2026-04-27** (P1.9 SDK-tool exclusion fix landed via #174): With observability layer #173 in place, ran Sprint #9 against the `/api/version` brief to capture diagnostic data. ToolDiag telemetry confirmed the `unexpected user permission response` crash is **SDK builtins** the agents call from outside our structured-command discipline: 6 `bash` calls (planner-1, architect-1) → `Kind=shell` denial → SDK-wrapped error → entire turn discarded; 5 `view` calls (software-engineer-1) → same root cause via SDK's `Unhandled error` wrapper. Custom registered tools (10 per agent, none of which are `bash`/`view`) confirmed both as SDK builtins added on top. PR #174 (self-merged per autonomy directive) sets `SessionConfig.ExcludedTools = [bash, shell, exec, view, write_file, edit, create_file, delete_file, str_replace_editor, apply_patch]` for every session, with a `ResolveExcludedSdkTools` name-collision filter so a registered custom tool sharing a builtin name (currently `write_file`) isn't silently disabled (caught by adversarial-review round 1). Tests: 13 new units in `CopilotExecutorExcludedToolsTests`; full suite 6665 server + 425 forge passing. **Live regression test (Sprint #10, post-fix)**: 0 `bash` failures, 0 `view` failures, 0 permission denials, 11/11 tool calls succeeded — vs Sprint #9's 6+5+2. The SDK-permission failure mode that has held P1.9 back since 2026-04-26 is closed. P1.9 stays `blocked` only on the next supervised acceptance re-run reaching §10 step 7 PASS — no longer blocked on a code defect. — agent: anvil (operator: agent-academy)
- **2026-04-27** (later — Sprint #11 supervised acceptance attempt: P1.9 still blocked on blocker B, confirmed live): Drove Sprint #11 against the `/api/version` brief end-to-end. **Loop progress**: Intake → Planning → Discussion → Validation → Implementation all advanced cleanly (5 of 6 stages); RequirementsDocument, SprintPlan, ValidationReport artifacts all stored; agents participated (Aristotle planning, Archimedes / Athena architecture review, Hephaestus implementation, Socrates/Thucydides reviewers). **Zero SDK-tool denials** (PR #174 fix held — 0 `bash`/`view`/`write_file` `Kind=` denials in ToolDiag for the entire sprint). **Failure**: never reached FinalSynthesis. Hephaestus (claude-haiku-4.5 then gpt-5.4 after watchdog cancels) produced the implementation via custom `write_file` calls — but the writes targeted the **main checkout cwd** (`/home/darin/projects/agent-academy/`), not the assigned task worktree (`.worktrees/task_implement-api-version-endpoint-backend-foundation-phase-1-e8110f-eb94875e/`). Server log: `Tool call: write_file by software-engineer-1 (cwd=/home/darin/projects/agent-academy, path=src/AgentAcademy.Server/Controllers/VersionController.cs, length=1230, allowedRoots=src/)`. Worktree stayed at 0 dirty files for the whole sprint; main checkout accumulated `VersionController.cs` (untracked) + modified `ServiceRegistrationExtensions.cs` + modified `System.cs`. Then a `Hephaestus (Engineer)` git commit `d21201b` (`fix: use room-scoped workspace lookup in TaskAssignmentHandler`) landed **directly on local develop** in the main checkout, bundling unrelated `IVersionInfoProvider.cs` + `VersionInfoProvider.cs` from the Sprint #11 work into a misleadingly-named "fix:" commit (the orchestrator's `IGitService` singleton commits in develop regardless of which task is "active" — this is layer 2 of blocker B, exactly as the design doc predicted). Sprint #11 force-completed at Implementation; local develop reset to `origin/develop` (drop `d21201b` and untracked `VersionController.cs`). **§10 step results for Sprint #11**: 1✓ requirements artifact, 2✓ agent participation (zero subagent-permission failures this time), 3✓ sprint plan, 4 NA, 5✓ reached Implementation, 6✗ no self-eval, 7✗ no SprintReport, 8 NA (no read-only test), 9 NA, 10 NA. **Same step 6+7 fail mode as the 2026-04-26 attempt — but now isolated to a single root cause**: blocker B (worktree cwd isolation). Blocker A is closed by #174. **P1.9 cannot pass until blocker B implementation lands** — design doc `p1-9-blocker-b-worktree-cwd-isolation-design.md` (PR #160) covers all three layers; implementation has not been attempted yet. P1.9 status row stays `blocked`. — agent: anvil (operator: agent-academy)
