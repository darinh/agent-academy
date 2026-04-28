# 100 — Product Vision

**Status**: Authoritative — supersedes implicit assumptions in any other spec.
**Owner**: The human (darin). Agents propose changes via PR; the human accepts.
**Established**: 2026-04-24
**Audience**: Every agent that opens this repo.

> ⚠️ **Read this before doing any work.** If a task you've been handed contradicts this document, STOP and ask the human. Do not silently work around the vision.

---

## 1. The Product in One Paragraph

Agent Academy is a **dev team you manage** — not a chat app, not a research dashboard, not a collection of agent introspection panels. The human is the manager. Agents are the team (with a lead). Work is organized into **sprints**: bounded, scoped work sessions that begin with a goal, end with a shippable artifact and a written report, and run **autonomously** in between. The human steps away, returns, and audits — they do not babysit the conversation.

## 2. Audience and Scope

- **Primary user**: the human who built this (darin) and a small handful of Microsoft teammates.
- **Out of scope**: making this work for strangers, public productization, multi-tenant SaaS shape. If those become real goals later, that is a different project and likely a rewrite.
- **Implication**: optimize for *usability for one informed user*, not for onboarding ramps, telemetry-driven product decisions, or polish for cold prospects.

## 3. The Sprint — Unit of Work

A sprint is the central unit of work in Agent Academy. Everything else (rooms, artifacts, tasks, the orchestrator) exists to serve the sprint lifecycle.

### 3.1 Sprint Lifecycle (the canonical six phases)

1. **Intake** — Human posts a goal in a room. Agents *push back* on cohesion ("the bosses seem unrelated to the weapons request — move them to a follow-up sprint?"). Agents ask clarifying questions. The output of Intake is an agreed scope.
2. **Planning** — Agents convert the agreed scope into concrete work items (this is where the **Forge** is the right tool). Output: a tracking artifact that records what was ordered.
3. **Implementation** — Agents work. The orchestrator drives the conversation forward without the human posting again. The human can spot-check, override, or step away.
4. **Self-Evaluation Ceremony** — A preamble-driven phase that forces agents to evaluate test coverage, completeness, and acceptance against the original scope. If gaps are found, the sprint loops back to Implementation. This is non-optional and non-skippable.
5. **Final Synthesis** — Agents package the change with documentation, version bumps, and a final **work report artifact**. The artifact is the proof of work.
6. **Handoff** — Either the next queued sprint begins, or the team notifies the human (Discord) that they are awaiting instructions.

### 3.2 The Forcing Function

Artifacts are not optional. They are the **forcing function** that makes agents follow the process. A sprint without a tracking artifact at Intake/Planning, and without a work report artifact at Final Synthesis, is not a completed sprint — it is an abandoned conversation.

### 3.3 Pushback at Intake is Required

The agent that fails to push back on an incoherent request and just starts working is **operating incorrectly**. The expected behavior is: read the request, identify scope-cohesion problems, propose a tighter scope, get human agreement, *then* proceed. This is a load-bearing behavior of the product, not a polish item.

### 3.4 Terminal-Stage Ceremony Chain (auto-firing)

Phases 4 (Self-Evaluation Ceremony) and 5 (Final Synthesis) above are **driven by the system, not by the human or the agents**. When implementation work reaches a natural completion point, the platform fires the ceremony chain on its own and walks the sprint to terminal state without operator intervention. This is what makes "the human steps away" (§5) work for the end of a sprint, not just the middle.

Concretely, after each agent round in a sprint at `currentStage=Implementation`, the platform classifies sprint state and fires **at most one transition per round**:

```
[Implementation, every task is Completed or Cancelled, with at least one Completed]
        │
        ▼  (1) auto-fire RUN_SELF_EVAL — agents produce a SelfEvaluationReport
        │
        ▼  (2) on AllPass artifact stored: AdvanceStage(Implementation → FinalSynthesis)
        │
        ▼  (3) FinalSynthesis preamble drives agents to produce a SprintReport
        │
        ▼  (4) on SprintReport artifact stored: CompleteSprint(force=false)
        │
        ▼
[status=Completed, currentStage=FinalSynthesis]
```

A sprint where **every task is Cancelled** (no work was completed) does not trigger the chain — it stays in the driver's `NotApplicable` state and requires operator `force=true` completion via the existing escape valve. There is nothing to self-evaluate; the team did not produce work.

Each step is its own atomic transition guarded by the existing artifact + verdict gates. The driver does not bypass any gate; it makes the gates **observable on natural sprint progression** rather than only when an operator manually invokes them. Failure at any step (e.g., self-eval cap exceeded, watchdog timeout, missing artifact) routes through `MarkSprintBlocked` → `NeedsInput` notification — the same human-attention surface the rest of the system already uses.

Two invariants govern the chain:

- **One step per round.** Transitions cascade across rounds, not within a single round runner invocation. Steps (2) and (4) — the gate-passing transitions — each emit their own `SprintStageAdvanced` / `SprintCompleted` `ActivityEventEntity` row through the existing service methods, so the audit trail (§6) shows the ceremony walking forward stage-by-stage rather than as a single opaque "completed" event. Steps (1) and (3) are observable via the existing self-evaluation audit surface and the per-round agent activity, respectively.
- **`force=true` remains the human escape valve.** Operator overrides via `POST /api/sprints/{id}/complete?force=true` bypass the driver. When `force=true` is used at a non-terminal `currentStage`, the operation also advances `currentStage` to `FinalSynthesis` so the snapshot is internally consistent (no `status=Completed` while `currentStage=Implementation`).

Without this chain, P1.4's self-evaluation ceremony and P1.6's `SprintReport` artifact gate never fired on a real sprint — every Completed sprint in the database had `selfEvalAttempts=0` and zero `SprintReport` artifacts. The chain is the trigger; P1.4 + P1.6 are the gates it walks the sprint through.

> Implementation: see [`sprint-terminal-stage-handler-design.md`](./sprint-terminal-stage-handler-design.md). Shipped 2026-04-28 in PR #192.

## 4. Rooms — Scope Container for a Sprint

A room is the scoped chat record for one sprint.

- Created by the **human** to kick off a new sprint, OR by the **team** when a completed sprint produced more work that warrants a follow-up.
- While the sprint is **active**, the room allows messages and agents are online in it.
- When the sprint **completes**, the room becomes **read-only**, agents go **offline** in it, and it remains **discoverable and searchable** as historical record.
- Inactive rooms are part of the audit trail. The human navigates back to them to see what was decided, what was built, and why.

> Today: rooms exist but the active/inactive lifecycle (read-only + agents-offline) is partially implemented at best. See gap-analysis.md.

## 5. Autonomy — The Core Promise

> The single most important capability. If this does not work, nothing else matters.

The product's promise is that the human **steps away** and the team **continues working**. Concretely:

- Closing the UI does not stop work.
- Loading a different project in the UI does not stop work on other projects.
- A scheduled sprint kicks off and runs to completion (or to a real blocker) without the human posting in chat.
- The orchestrator drives conversation rounds forward on its own; agents are not purely reactive to human messages.

> Today: agents are **purely reactive**. There is no autonomous wake-up loop. This is the gap that defines the next phase of work.

## 6. Visibility — Audit, Not Surveillance

The human's relationship to the running team is **audit and spot-check**, not real-time observation. The visibility surface should answer:

- "What did the team do while I was away?" — recent artifacts, chat history of recent sprints.
- "Are they blocked?" — Discord notification surfaces this.
- "Is this work any good?" — the human opens the room, reads the report, scans the diff.

The current 18-tab navigation is over-built for this. Visibility for one user does not require 18 distinct introspection panels; it requires a small number of high-signal surfaces.

## 7. Cross-Project Background Work

The team works on **multiple projects** even if only one is loaded in the UI at any given time. The orchestrator and scheduler are project-aware and process work for any workspace whose schedule allows it. The UI shows the project the human is currently inspecting; the agents work on whatever has work to do.

## 8. Components and Their Roles

| Component | Role in the vision | Reality check |
|-----------|-------------------|---------------|
| **Forge** | Idea → atomic work items at Planning. | Built. Used? Unclear. Needs to be the sprint planning entry point. |
| **Rooms** | Scoped chat record for one sprint. Read-only when sprint ends. | Partially implemented; lifecycle wiring incomplete. |
| **Sprints** | The unit of work. Six-phase lifecycle. | Schema and stages exist; autonomous execution does not. |
| **Artifacts** | Forcing function for process compliance. Tracking artifact + work report. | Concept exists; the *required-for-sprint-completion* contract is not enforced. |
| **Tasks** | Atomic work items inside a sprint. | Implemented. Sprint-task association added in prior session. |
| **Orchestrator** | Drives conversations forward without human poking. | Reactive only. Missing: tick / scheduled wake-up. |
| **Notifications (Discord)** | Tell the human "we're blocked" or "we're done, awaiting instructions." | Notification system exists; tying it to autonomy state is not wired. |
| **Consultant API** | Programmatic interface for the human to direct/inspect the team. | Built. |
| **Memory / Knowledge / Digests / Goals / Plan / Activity / Timeline panels** | Various agent-introspection surfaces. | Mostly built, mostly unused, mostly redundant. Defer pruning until autonomy works. |

## 9. Non-Goals (right now)

These are valid concerns that are **explicitly out of scope** for the current phase. Any agent that wanders into one of these without explicit human direction is off-task.

- **Rewriting the command system to MCP.** The current text-parsed command system is the wrong shape long-term — commands should be MCP tool invocations made directly by the SDK. This is a real ~2-week effort and a separate decision. Defer until the autonomy loop works end-to-end. The current commands work, just inelegantly.
- **Public productization / strangers as users.** See §2.
- **Onboarding flows, marketing pages, analytics dashboards.** See §2.
- **Pruning / restructuring the navigation.** Defer until panels have real usage to inform the prune.
- **New peripheral features** (Memory v2, Goals revamp, Forge UI polish, etc.). The autonomy loop is the gating dependency.

## 10. Definition of Done for the Vision

The vision is delivered when **all** of the following are observably true, demonstrable in a single end-to-end session:

1. The human posts a goal in a new room.
2. Agents push back on scope cohesion (if applicable) before agreeing.
3. Agents produce a tracking artifact recording the agreed scope.
4. The human walks away. Closes the browser.
5. The agents continue the conversation autonomously, advancing through Planning → Implementation.
6. At end of Implementation, the self-evaluation ceremony runs and either passes or loops back.
7. On pass, agents produce a final work report artifact and notify the human via Discord.
8. The room transitions to read-only with agents offline; it remains searchable.
9. If a follow-up sprint is queued, it starts. If not, the team is idle and the Discord notification reflects that.
10. The human returns hours later, sees the artifacts, reads the report, audits the diff, and either accepts or sends feedback.

When all 10 are true in one observable run, the vision is delivered. Until then, it is not.

## 11. The Meta-Rule

Every agent that works in this repo MUST read this document at session start, and MUST treat anything that contradicts it as a bug.

- Spec sections that say "Implemented" but contradict §10 are **wrong** and should be flagged or corrected.
- Tasks that don't move the system measurably closer to §10 are **lower priority** than tasks that do.
- "I built the data model" is not "I implemented the feature." The feature is the observable behavior in §10.

See `roadmap.md` for the prioritized work items, and `gap-analysis.md` for the current vision-vs-reality delta.

---

## Revision History

- **2026-04-24**: Initial vision document captured from product-design conversation. Establishes sprints as the unit of work, autonomy as the core promise, and the meta-rule that all agents read this first. — agent: anvil (operator: agent-academy)
