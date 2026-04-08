# Anvil Architecture Pattern Analysis

**Author:** Archimedes (Architect)  
**Date:** 2026-03-30  
**Source:** `~/.copilot/installed-plugins/_direct/burkeholland--anvil/agents/anvil.agent.md`  
**Task:** analyze-anvil-architecture-patterns-f41270

---

## Overview

Anvil is an evidence-first coding agent that verifies code before presenting it. It uses adversarial multi-model review, SQL-tracked verification, and a gated workflow to ensure code quality. This analysis extracts its architectural patterns and maps each to Agent Academy's current capabilities.

---

## Extracted Patterns (13 total)

### P1. Evidence Ledger (SQL-based verification tracking)

**What it does:** Every verification step (build, test, lint, diagnostics, review) is INSERT'd into an `anvil_checks` SQL table with task_id, phase, check_name, tool, exit_code, output_snippet, and pass/fail. The final "Evidence Bundle" is a SELECT, not prose — if the INSERT didn't happen, the verification didn't happen.

**Why it matters:** Prevents hallucinated verification. An agent can't claim "build passed ✅" without a corresponding SQL row backed by an actual tool call.

**Classification: Platform-level.** Agent Academy agents have no SQL tool access. The platform would need to provide either: (a) a new SQL command (QUERY/INSERT), or (b) a structured RECORD_CHECK command that the orchestrator stores on the agent's behalf. Option (b) is simpler and maintains the command envelope pattern.

---

### P2. Adversarial Review (multi-model cross-examination)

**What it does:** After self-verification, the agent launches 1 (Medium) or 3 (Large) code-review sub-agents using *different models* (GPT, Gemini, Claude). Each reviewer independently examines staged diffs. Results are recorded in the ledger.

**Why it matters:** Different models catch different things. Single-model self-review has systematic blind spots.

**Classification: Platform-level.** Agent Academy agents cannot spawn sub-agents. The simplest path is modifying the orchestrator's existing review cycle to run 1-3 reviewers with different models based on task sizing. The `CopilotExecutor` already supports per-agent model selection via `AgentDefinition.Model`.

---

### P3. Baseline Snapshots (capture state before changes)

**What it does:** Before any code change, run the verification cascade and record results with `phase = 'baseline'`. After changes, compare baseline vs. after to detect regressions introduced by the agent vs. pre-existing failures.

**Why it matters:** Without baselines, you can't distinguish "I broke it" from "it was already broken."

**Classification: Hybrid.** Agents can run `RUN_BUILD` and `RUN_TESTS` today. But recording the results requires P1 (Evidence Ledger), which is platform-level. Without P1, baseline capture degrades to prompt-only ("note the build output") which is unreliable.

---

### P4. Gate System (verification checkpoints)

**What it does:** Hard gates at critical workflow points:
- Gate 1: Cannot start implementation until baseline records exist
- Gate 2: Cannot present until adversarial review verdicts are recorded
- Gate 3: Cannot present until minimum verification signals exist (2 for Medium, 3 for Large)

Each gate is a SQL query against the ledger.

**Why it matters:** Prevents agents from skipping steps under context pressure.

**Classification: Hybrid.** Prompt-level gates are unenforceable — the agent can ignore them. Platform-enforced gates (orchestrator checks ledger before allowing WORK REPORT submission) would be robust but require P1 first. **Without P1, gates are aspirational only.**

---

### P5. Task Sizing (S/M/L with proportional rigor)

**What it does:** Tasks classified as Small (typo, one-liner), Medium (bug fix, feature), or Large (new feature, multi-file, auth/crypto). Each size triggers different verification depth. Also classifies per-file risk: 🟢 additive, 🟡 modifying logic, 🔴 auth/crypto/payments/schema.

**Why it matters:** Right-sizes verification overhead. Simple changes don't need 3 adversarial reviewers.

**Classification: Agent-level.** The agent assesses size from the task description and applies the appropriate workflow. Pure prompt engineering. **Can be adopted immediately.**

---

### P6. Pushback (requirement/implementation challenge)

**What it does:** Before executing any request, the agent evaluates whether it's a good idea. If concerned, surfaces a `⚠️ Pushback` callout and asks for confirmation before proceeding.

Implementation concerns: tech debt, duplication, simpler approach exists.  
Requirements concerns: conflicts with existing behavior, solving symptom not cause, dangerous edge cases.

**Why it matters:** Catches expensive mistakes before they're built.

**Classification: Agent-level (partial).** Agent Academy agents can surface concerns in responses. However, Anvil uses `ask_user` with structured choices — Agent Academy's equivalent is `ASK_HUMAN` which goes through the notification system. The pattern works but with higher latency. **Can be adopted immediately** with ASK_HUMAN as the confirmation mechanism.

---

### P7. Verification Cascade (tiered verification)

**What it does:** Three tiers, all mandatory if applicable:
- **Tier 1 (always):** IDE diagnostics + syntax/parse check
- **Tier 2 (if tooling exists):** Build, type checker, linter, tests
- **Tier 3 (if Tiers 1-2 give no runtime signal):** Import/load test, smoke execution

**Why it matters:** Defense in depth. Static checks alone miss runtime errors.

**Classification: Hybrid.**
- Tier 1 IDE diagnostics: **Not available.** Agent Academy agents run server-side, not in an IDE. Use `dotnet build` output as proxy (catches same errors for C#).
- Tier 2: **Available.** Agents have `RUN_BUILD` and `RUN_TESTS`.
- Tier 3 smoke execution: **Not available.** Agents lack bash/shell access. Would require a new `RUN_COMMAND` or `EXECUTE_SCRIPT` command.

**Practical scope today:** Tiers 1-2 via RUN_BUILD + RUN_TESTS only.

---

### P8. Boosted Prompts (intent clarification)

**What it does:** Agent rewrites the user's prompt into a precise specification — fixing typos, inferring target files, expanding shorthand. Only shown if intent materially changed.

**Why it matters:** Reduces ambiguity before work starts.

**Classification: Agent-level.** Pure prompt instruction. **Can be adopted immediately.**

---

### P9. Session Recall (learning from history)

**What it does:** Before planning, query session history for files about to be changed. Look for past sessions that touched those files, especially ones with failures/regressions.

**Why it matters:** Prevents "same bug twice" across sessions.

**Classification: Hybrid.** Agent Academy has REMEMBER/RECALL for persistent memory. The session_store file-level history query is a Copilot platform feature not available here. **Workaround:** Agents use REMEMBER more aggressively to record file-specific lessons. The Learn step (P11) feeds this.

---

### P10. Git Hygiene (pre-work state checks)

**What it does:** Before starting work: check for uncommitted changes, check if on correct branch, detect worktrees.

**Why it matters:** Prevents messy git state from contaminating new work.

**Classification: Partially implemented — pending bug fixes.** The branch-per-breakout workflow handles branch creation and switching automatically. However, three critical integration bugs are currently being fixed (InReview state not set, branch metadata fragile, merge SHA missing). Not safe to mark as solved until those land.

---

### P11. Learn Step (post-verification knowledge capture)

**What it does:** After verification succeeds, store confirmed facts: working build commands, codebase patterns, reviewer findings that verification missed, regressions introduced and fixed.

**Why it matters:** Builds institutional memory across sessions.

**Classification: Agent-level.** Agent Academy already has REMEMBER with rich categories (pattern, gotcha, incident, lesson, etc.). The Learn step is a formalized prompt instruction for *when* and *what* to remember. **Can be adopted immediately.**

---

### P12. Auto-Commit (commit after presenting)

**What it does:** After presenting verified work, automatically commit with conventional commit message and Co-authored-by trailer. Capture pre-SHA for rollback.

**Why it matters:** Reduces friction between verified work and persistence.

**Classification: Platform-level.** In Agent Academy, git operations in breakout rooms are managed by the platform (the branch-per-breakout workflow handles checkout/switching). Agents don't have direct git commit commands. Implementing this requires either: (a) a new `GIT_COMMIT` command for agents, or (b) the orchestrator auto-commits after WORK REPORT with status COMPLETE. Option (b) aligns better with the existing architecture — the orchestrator already controls the breakout lifecycle.

---

### P13. Operational Readiness (Large tasks)

**What it does:** Before presenting Large work, check: error logging with context, graceful degradation on dependency failure, no hardcoded secrets/config.

**Why it matters:** Catches production gaps that automated tests miss.

**Classification: Agent-level.** A review checklist the agent runs against its own output. Can use READ_FILE (once fixed) to inspect code. **Can be adopted once READ_FILE is operational.**

---

## Corrected Classification Summary

| # | Pattern | Classification | Platform Work Needed |
|---|---------|---------------|---------------------|
| P1 | Evidence Ledger | **Platform** | New RECORD_CHECK command or SQL access |
| P2 | Adversarial Review | **Platform** | Multi-model review in orchestrator |
| P3 | Baseline Snapshots | **Hybrid** | Depends on P1 for recording |
| P4 | Gate System | **Hybrid** | Depends on P1; prompt-only is unenforceable |
| P5 | Task Sizing | **Agent** | None |
| P6 | Pushback | **Agent** | None (uses ASK_HUMAN) |
| P7 | Verification Cascade | **Hybrid** | Tier 3 needs shell access; Tiers 1-2 work today |
| P8 | Boosted Prompts | **Agent** | None |
| P9 | Session Recall | **Hybrid** | Enhanced via REMEMBER; full version needs platform |
| P10 | Git Hygiene | **Partial** | Bug fixes in progress |
| P11 | Learn Step | **Agent** | None (uses REMEMBER) |
| P12 | Auto-Commit | **Platform** | GIT_COMMIT command or orchestrator auto-commit |
| P13 | Operational Readiness | **Agent** | None (needs READ_FILE fix) |

**Corrected counts:**
- Agent-level (adopt now): **5** — P5, P6, P8, P11, P13
- Hybrid (agent.md + platform work): **4** — P3, P4, P7, P9
- Platform-level (requires new features): **3** — P1, P2, P12
- Partially implemented: **1** — P10

---

## Dependency Map

```
P5 (Task Sizing) ← foundation, everything references this
  │
  ├── P6 (Pushback) — independent, runs at task start
  ├── P8 (Boosted Prompts) — independent, runs at task start
  │
  ▼
P3 (Baseline Snapshots) ← must happen BEFORE implementation
  │  requires P7 (what to capture) and P1 (where to record)
  ▼
P7 (Verification Cascade) ← defines what checks to run
  │  Tiers 1-2 available now; Tier 3 needs shell access
  ▼
P1 (Evidence Ledger) ← records P7 and P3 results
  │  PLATFORM DEPENDENCY: needs RECORD_CHECK command
  ▼
P4 (Gate System) ← queries P1 to enforce checkpoints
  │  without P1, gates are prompt-only (unenforceable)
  │
  ▼
[IMPLEMENTATION HAPPENS HERE]
  │
  ▼
P7 again (post-change verification) → P1 again (record)
  ▼
P2 (Adversarial Review) ← PLATFORM DEPENDENCY
  │  needs orchestrator multi-model review
  ▼
P13 (Operational Readiness) ← Large tasks only
  ▼
P12 (Auto-Commit) ← PLATFORM DEPENDENCY
  │  needs GIT_COMMIT or orchestrator auto-commit
  ▼
P11 (Learn Step) ← uses existing REMEMBER
```

**Corrected ordering note:** Baseline Snapshots (P3) now precedes Verification Cascade (P7) in the workflow — baselines are captured BEFORE implementation using the same checks that P7 defines. The dependency is: P3 *uses* P7's check definitions, and P3 *records via* P1.

---

## Recommended Implementation Order

### Phase 1: Agent Prompt Enrichment (no platform changes)

These patterns can be added to engineer agent system prompts today:

1. **P5 — Task Sizing** — Add S/M/L rubric and per-file risk classification
2. **P6 — Pushback** — Add implementation/requirements challenge protocol with ASK_HUMAN
3. **P8 — Boosted Prompts** — Add intent clarification step
4. **P11 — Learn Step** — Formalize REMEMBER triggers (build commands found, patterns discovered, regressions fixed)
5. **P7 (Tiers 1-2) — Verification Cascade** — Instruct agents to run RUN_BUILD + RUN_TESTS before and after changes

**What this delivers:** Agents classify work, push back on bad ideas, clarify intent, verify builds, and accumulate institutional memory. No Evidence Ledger means verification is prompt-enforced (not SQL-enforced), which is weaker but still a significant improvement.

### Phase 2: Evidence Infrastructure (platform features)

6. **P1 — Evidence Ledger** — New RECORD_CHECK command. The orchestrator stores check records per task. Evidence Bundle becomes a formatted view of stored records.
7. **P3 — Baseline Snapshots** — Now enforceable: agent runs checks before implementation, records via RECORD_CHECK.
8. **P4 — Gate System** — Orchestrator checks ledger before accepting WORK REPORT. Platform-enforced, not prompt-enforced.
9. **P12 — Auto-Commit** — Orchestrator auto-commits on WORK REPORT COMPLETE with conventional commit message.

**What this delivers:** Full verified workflow — baselines, gated progression, evidence bundles, auto-commit. The core Anvil value proposition.

### Phase 3: Advanced Features (larger platform changes)

10. **P2 — Adversarial Review** — Modify orchestrator review cycle to spawn 1-3 reviewers with different models. Leverage existing `AgentDefinition.Model` support in `CopilotExecutor`.
11. **P9 — Session Recall** — New SEARCH_HISTORY command for file-level session queries. Until then, P11 (Learn Step) provides a degraded version via REMEMBER/RECALL.
12. **P7 Tier 3 — Smoke Execution** — New RUN_SCRIPT or EXECUTE command. Needs security sandboxing design.
13. **P10 — Git Hygiene** — Completes once branch-per-breakout bugs are fixed.
14. **P13 — Operational Readiness** — Completes once READ_FILE is fixed.

---

## Key Trade-offs

| Decision | Why | Alternative Rejected | Risk Accepted |
|----------|-----|---------------------|---------------|
| RECORD_CHECK command over raw SQL | Maintains command envelope pattern; SQL access is a security surface | Give agents direct SQL | Less flexible than raw SQL; may need schema evolution |
| Orchestrator auto-commit over agent GIT_COMMIT | Agents shouldn't manage git state; platform owns the branch lifecycle | Agent-controlled git | Agents can't do partial commits or custom messages |
| Build output over IDE diagnostics | Agents run server-side, not in an IDE; `dotnet build` catches the same C# errors | Add IDE diagnostic proxy | Lose per-file diagnostic granularity |
| Prompt-enforced gates in Phase 1 | Delivers value without platform changes | Wait for platform gates | Agents may skip gates; acceptable for initial adoption |
| Multi-model review via orchestrator | Reuses existing review cycle; ~50 lines of change | Agent-spawned sub-agents | Limited to review-time model diversity; agents can't use sub-agents for other purposes |
