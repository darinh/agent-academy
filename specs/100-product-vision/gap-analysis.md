# 100 — Gap Analysis: Vision vs. Reality

**Companion to**: [spec.md](./spec.md)
**Established**: 2026-04-24
**Status**: Living document. Update whenever a gap closes or a new gap is discovered.

This document is brutally honest. Where the spec elsewhere in this repo says "Implemented" but the behavior does not match the vision, that is recorded here as a gap, not glossed over.

---

## Summary

| Vision capability | Status | Severity |
|-------------------|--------|----------|
| Sprint as a six-phase lifecycle with phases that *advance autonomously* | ❌ Missing autonomous advancement | 🔴 Blocks vision |
| Agents push back on incoherent scope at Intake | ❌ Not in agent preambles | 🔴 Blocks vision |
| Tracking artifact produced at Planning | ✅ Enforced (SprintStageService gate) | ✅ Done |
| Agents work autonomously without human poking | ❌ Orchestrator is reactive only | 🔴 Blocks vision |
| Self-evaluation ceremony at end of Implementation | ❌ No ceremony, no gate | 🔴 Blocks vision |
| Final work report artifact at Final Synthesis | ✅ Enforced (CompleteSprintAsync gate) | ✅ Done |
| Discord notification on idle / blocked | 🟡 Notification system exists, not wired to autonomy state | 🟡 Important |
| Rooms become read-only with agents offline when sprint completes | 🟡 Status enum may exist; agent-offline wiring unverified | 🟡 Important |
| Cross-project background work | ❌ Orchestrator processes one workspace's queue at a time | 🟡 Important |
| Forge as the sprint Planning entry point | 🟡 Forge exists; not invoked from sprint flow | 🟡 Important |
| Visibility surface answers "what did they do while I was away?" | 🟡 Data is there; surface is split across 18 nav items | 🟢 Defer |

## Detailed Gaps

### G1 — No Autonomous Wake-Up (🔴 Blocks vision)

**Vision**: Agents continue working when the human is away.
**Code**: `AgentOrchestrator.ProcessQueueAsync` only fires when a message-receive code path explicitly enqueues. There is no tick loop, no cron-driven wake-up, no "sprint advanced → next round" trigger.
**Evidence**: `src/AgentAcademy.Server/Services/AgentOrchestrator.cs` line 124.
**Impact**: This is *the* gap. Without it, every other sprint feature is theatre.

### G2 — Sprint Creation Does Not Dispatch (🔴 Blocks vision)

**Vision**: Starting a sprint kicks off the team.
**Code**: `SprintService.CreateSprintAsync` (lines 47–145) inserts the entity, fires an activity event, syncs room phases. **Does not enqueue any agent message.**
**Evidence**: Read end-to-end in prior session.
**Impact**: A sprint exists in the DB and nothing acts on it. Combined with G1, scheduled sprints are no-ops.

### G3 — No Intake Pushback (🔴 Blocks vision)

**Vision**: Agents read a vague or incoherent request and push back to scope it down.
**Code**: Agent preambles do not instruct this behavior at Intake. The agents will dive in.
**Evidence**: Agent persona files / orchestrator preambles do not include scope-pushback prompting (needs verification — call this out in roadmap).
**Impact**: First user impression is "agents went off and did weird stuff" instead of "agents helped me think."

### G4 — No Self-Evaluation Ceremony (🔴 Blocks vision)

**Vision**: End of Implementation triggers a forced self-check on coverage, completeness, acceptance. Loops back if gaps found.
**Code**: No such ceremony exists. Sprint stages can advance to FinalSynthesis without any gate.
**Evidence**: SprintService stage transitions are unguarded.
**Impact**: Sprints "complete" without delivering the actual work; the user has to manually catch this every time.

### G5 — Cross-Project Autonomy Missing (🟡 Important)

**Vision**: Agents work on Project B in the background while the UI is showing Project A.
**Code**: The orchestrator queue is per-workspace, but the scheduler/dispatch focuses on whichever workspace is loaded.
**Evidence**: Needs a focused investigation — call out in roadmap.
**Impact**: User cannot leave the UI on one project while expecting other projects to progress.

### G6 — Artifact Production Not Enforced (🟡 Important) [RESOLVED 2026-04-25]

**Vision**: Tracking artifact at Planning, work report at Final Synthesis. Sprints without these are incomplete.
**Code (re-checked 2026-04-25)**: Stage gates ARE enforced. `SprintStageService.RequiredArtifactByStage` requires `RequirementsDocument` to leave Intake, `SprintPlan` to leave Planning, `ValidationReport` to leave Validation, and `SprintReport` to complete (in `SprintService.CompleteSprintAsync`). The `force=true` override exists for humans only; agents cannot bypass via the orchestration path.
**Original assessment was wrong**: This gap was closed before the spec was authored. P1.5 / P1.6 in the roadmap are marked done with code references.
**Residual risk**: None for the enforcement contract. Future work could harden artifact *quality* (validation rules already exist in `SprintArtifactService.ValidateArtifactAsync`).

### G7 — Idle/Blocked Notifications (🟡 Important)

**Vision**: Discord ping when the team is blocked or out of work.
**Code**: NotificationProvider exists. There is no signal that says "team is idle" to feed into it.
**Evidence**: Needs verification of orchestrator state-emit hooks.
**Impact**: Human has no out-of-band signal that their attention is needed.

### G8 — Room Lifecycle Incomplete (✅ CLOSED by P1.8)

**Vision**: Completed sprint → room becomes read-only, agents go offline, room remains searchable.
**Status**: Closed 2026-04-25. `MessageService.AddAgentMessageAsync` / `AddHumanMessageAsync` reject writes to rooms in Completed or Archived status with `RoomReadOnlyException` → HTTP 409 Conflict. `SprintService.CompleteSprintAsync` / `CancelSprintAsync` / `TimeOutSprintAsync` invoke `RoomLifecycleService.MarkSprintRoomsCompletedAsync` **in the same transaction as the sprint state change**, evacuating agents from the frozen rooms, archiving descendant breakout rooms (including those whose parent was already flipped Completed at FinalSynthesis), and evacuating breakout occupants. Auto-start provisions a fresh default room before the next sprint is created so it isn't inert. Rooms remain readable — only writes are locked.
**Evidence**: `SprintService.cs` (PersistTerminalSprintWithRoomFreezeAsync), `RoomLifecycleService.cs` (MarkSprintRoomsCompletedAsync), `MessageService.cs` (IsTerminalRoomStatus guards), `RoomReadOnlyException.cs`, tests in `RoomLifecycleServiceTests.cs` + `SprintServiceTests.cs` + `MessageServiceTests.cs`.
**Review**: Two adversarial rounds (gpt-5.3-codex + gpt-5.5 + claude-opus-4.6). Round-1 caught stranded breakouts + race via fire-and-forget hosted service. Round-2 caught atomicity race (sprint visible Completed before rooms frozen), stranded breakouts under already-Completed parents (SprintStageService FinalSynthesis), inert auto-start (all rooms frozen before next sprint creation), PK collision on `{slug}-main`. All addressed.

### G9 — Forge Not Wired to Sprint Planning (🟡 Important)

**Vision**: Forge is the tool for converting Intake → atomic work items at Planning.
**Code**: Forge exists as a separate engine (spec 019). The Planning stage does not currently invoke it.
**Evidence**: SprintService.PlanningStage prompt does not reference Forge.
**Impact**: Planning is ad-hoc; Forge is decorative.

### G10 — Spec Status Inflation (🟢 Lower priority but real)

**Vision**: Specs reflect what the code actually does.
**Code**: Many specs marked "Implemented" describe behavior that is partially built or not wired together (e.g., Sprint System spec describes the lifecycle ceremonially without acknowledging that nothing autonomously advances stages).
**Impact**: Future agents trust the spec, do not investigate the code, and propagate the misalignment.
**Mitigation**: The `audit-request-history` skill exists for this. Run it before major roadmap revisions.

### G11 — Navigation Surface Bloat (🟢 Defer)

**Vision**: Small number of high-signal panels for audit/spot-check.
**Code**: 18 top-level nav items, 14 of them agent-introspection surfaces with overlapping concerns.
**Impact**: New users (the human, even) cannot find things; panel value is ambiguous.
**Why deferred**: Cannot prune well without seeing which panels matter once sprints actually run. Pruning before the autonomy loop ships would be premature.

---

## Investigation Required (before final roadmap commits)

Items in this list are gaps where the code state is *suspected* but not yet verified. Each one should be confirmed by reading the code before the corresponding roadmap item is implemented.

- [ ] **G3 verification**: Read agent persona files and orchestrator preambles to confirm absence of scope-pushback prompting.
- [ ] **G5 verification**: Trace how `SprintSchedulerService` selects which workspace to evaluate; confirm whether multiple workspaces' queues are processed in parallel.
- [ ] **G7 verification**: Identify whether any orchestrator state event ("queue empty", "sprint complete") is emitted today that could feed a Discord notifier.
- [ ] **G8 verification**: Read `RoomService` and SignalR hub for room status enforcement on chat send and on agent presence.
- [ ] **G9 verification**: Read `SprintService` Planning stage prompt construction; confirm Forge is or isn't invoked.

Future agent: do NOT skip the verification step. Do NOT take this gap analysis as ground truth and start coding. Verify, then implement.
