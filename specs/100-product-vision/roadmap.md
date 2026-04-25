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

### P1.2 — Orchestrator Tick / Self-Drive [DRAFT — design needed before implementation]

**Closes gap**: G1.
**Risk**: 🔴 (concurrency, scheduler logic — central to product behavior).
**Estimated effort**: ~1–2 days.

**What**: The orchestrator gains the ability to advance a conversation **without** a human message arriving. After an agent responds, if the sprint is in a phase that expects continued work and there is no pending human input, the orchestrator schedules the next round automatically.

**Design decisions still open**:
- Round-based loop vs. true tick. Probably round-based: after each agent message, decide whether to enqueue the next round, with a max-rounds-per-sprint cap to prevent runaway.
- How is "we're done with this round, ready for next" signaled? End of agent turn? Specific command? Stage advance?
- Backpressure: if the human posts mid-round, does the autonomous loop pause until the human's message is processed?
- Idle state: when does the orchestrator decide "no more work to do, sleep"?

**Critical safety requirement**: This loop MUST have:
- A max-rounds-per-sprint cap (default: 50, configurable per sprint).
- A max-cost-per-sprint cap if API costs are tracked (TBD — investigate).
- An emergency stop that the human can trigger from the UI or API.

**Recommend**: write a brief design doc (1–2 pages) and get a human review before implementing.

### P1.3 — Stage Advancement Triggers Next Round [DRAFT]

**Closes gap**: G2 partial, supports G4.
**Risk**: 🟡.
**Estimated effort**: ~0.5 day.

**What**: When a sprint advances stages (e.g., Planning → Implementation), automatically enqueue a new round so agents pick up the new stage's preamble and act on it. Today the stage transitions are silent.

**Acceptance test**:
- Trigger a stage advance via the API.
- Observe an agent message appear in the room reflecting the new stage's intent.

### P1.4 — Self-Evaluation Ceremony at End of Implementation [DRAFT — design needed]

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

### P1.7 — Discord Notification on Idle / Blocked / Sprint Complete [DRAFT]

**Closes gap**: G7.
**Risk**: 🟡.
**Estimated effort**: ~0.5 day.

**What**: Wire orchestrator state changes to NotificationProvider:
- Sprint completes → notify "Sprint X completed, report attached".
- Sprint blocks (self-eval failed N times, or explicit blocker) → notify "Sprint X needs attention".
- All sprints idle, no work queued → notify "Team is idle, awaiting instructions" (debounce so this fires at most once per idle period).

### P1.8 — Room Lifecycle: Read-Only When Sprint Completes [DRAFT — investigation first]

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

- (none yet)

---

## Status Tracking

| Item | Status | Started | Completed | Notes |
|------|--------|---------|-----------|-------|
| P1.1 | done | 2026-04-25 | 2026-04-25 | Kickoff posts a system message in every active workspace room and wakes the orchestrator on `CreateSprintAsync` (manual / scheduled / auto-start paths). Live-verified: Sprint #2 created → kickoff message at +107ms, Aristotle responded at +44s. |
| P1.2 | pending | — | — | Design doc required first |
| P1.3 | done | anvil | feat/p1-3-stage-advance-announce | Live-verified 2026-04-25: Sprint #2 Intake→Planning via /approve-advance posted "➡️ Sprint #2 advanced (user-approved)" to 6 active rooms; Aristotle responded in +35s. Includes regression test + targetRoomIds snapshot for FinalSynthesis room-completion edge case (caught by adversarial review). |
| P1.4 | pending | — | — | Design doc required first |
| P1.5 | done | 2026-04-25 | 2026-04-25 | **Already implemented** prior to roadmap authoring. `SprintStageService.RequiredArtifactByStage["Planning"] = "SprintPlan"` (SprintStageService.cs:44) is enforced in `AdvanceStageAsync` (SprintStageService.cs:112-123), even when `force=true`. SprintPlanDocument (Sprints.cs:63-66) IS the tracking artifact: Summary + Phases (Name/Description/Deliverables) + OverflowRequirements. Covered by `SprintServiceTests.AdvanceStage_ThrowsWithoutRequiredArtifact` and the multi-stage `AdvanceStage_ThrowsAtFinalStage` flow. Gap analysis G6 was stale at authoring. |
| P1.6 | done | 2026-04-25 | 2026-04-25 | **Already implemented** prior to roadmap authoring. `SprintService.CompleteSprintAsync` (SprintService.cs:229-241) refuses to mark a sprint Completed unless a `SprintReport` artifact exists at FinalSynthesis. The `force=true` override is intentional (humans can override; agents cannot). SprintReport (Sprints.cs:78-82) carries Summary + Delivered + Learnings + OverflowRequirements — i.e., the work-report contract. Gap analysis G6 was stale at authoring. |
| P1.7 | pending | — | — | |
| P1.8 | pending | — | — | Investigation first |
| P1.9 | pending | — | — | Acceptance run, after P1.1–P1.8 |
| P2.1 | pending | — | — | After Phase 1 |
| P2.2 | pending | — | — | After Phase 1 |
| P3.1 | pending | — | — | After Phase 1 |
| P3.2 | pending | — | — | After P3.1 |

When a future session works on an item, update its status here in the same commit. The status table is the at-a-glance truth.

## Revision History

- **2026-04-24**: Initial roadmap captured from product-design conversation. Phase 1 defined as the autonomous loop end-to-end. Acceptance test set as the 10-step observable run from spec.md §10. — agent: anvil (operator: agent-academy)
