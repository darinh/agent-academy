# Forge Spike — Critical Gaps and Post-Spike Scope Additions

**Date**: 2026-04-19  
**Authors**: Team consensus (Aristotle, Archimedes, Hephaestus, Athena, Socrates, Thucydides)  
**Context**: Post-design, pre-implementation findings from collaborative spike planning session  
**Status**: Committed for external review

---

## Falsifiability Statement

**If seeded-defect benchmark shows <80% detection per blocking drift code (or <60% per advisory) on runs with modelProvenance.model != stub, the intent-fidelity hypothesis is falsified and the design must be revisited.**

---

## What This Document Is NOT

This is a **findings record**, documenting team consensus on gaps and proposed additions identified during spike design. It is:

- ❌ **NOT a design spec** — schemas shown are illustrative, not frozen
- ❌ **NOT a build plan** — sequencing and implementation details are TBD
- ❌ **NOT a scope commitment** — post-spike additions require separate approval

This document captures **what the spike will and won't test**, and **what gaps external reviewers should expect to address** if adopting the methodology.

---

## Executive Summary

The Forge Pipeline Engine spike (commit `9746d0e`) freezes a minimal 5-phase methodology pipeline testing **context isolation by construction** as a defense against premature descent and context pollution. While the frozen design is internally consistent and executable, the team identified **four critical gaps** that limit the spike's ability to deliver on its core thesis:

1. **Intent Fidelity Gap**: The current spike tests structural correctness but not fidelity to *source intent*. We can detect "the schema was satisfied" but not "the implementation matches what the human actually wanted."
2. **Missing Phases**: The 5-layer methodology (`requirements → contract → function_design → implement → verify`) omits two critical activities: creative ideation (exploring solution space before committing to a design) and final fidelity checking (verifying the end artifact against original intent).
3. **Incomplete Acceptance Infrastructure**: Socrates identified several engineering constraints required for trustworthy evaluation but not yet encoded in the frozen spec.
4. **LLM-Judge Risks**: Semantic validators rely on LLM-judging-LLM, creating circular dependency and blind spots that could mask failures.

This document records these findings for the external team and proposes **five unanimous post-spike additions** to address them.

---

## 1. Intent Fidelity Gap

### The Problem

The current validator pipeline (structural → semantic → cross-artifact) can verify:
- JSON schema compliance
- Internal consistency (e.g., "every FR is referenced by an interface")
- Absence of structural defects (e.g., no dependency cycles in components)

It **cannot** verify:
- **Did the implementation solve the task the human actually cared about?**
- **Did the design explore alternatives before settling on the first viable solution?**
- **Does the final artifact match the source intent, or did meaning drift across 5 layers?**

### Why It Matters

Without intent fidelity checks, the pipeline can pass a Run where:
- Every phase validates internally
- Every artifact satisfies its schema
- The final implementation is *correct according to requirements*
- **But the requirements themselves misunderstood the task brief**

### Worked Example (Rate Limiting)

**Source Intent**: "Add per-user rate limiting to the API (100 requests/hour per authenticated user)"

**What happens**:
1. **Requirements phase** produces: "Implement rate limiting on API endpoints (100 req/hr)"  
   ❌ Drops the "per user" constraint
2. **Contract phase** faithfully implements the requirements artifact: `RateLimiter.CheckLimit(endpoint, hourlyWindow)` — **no user parameter**
3. **Function design** phase: data structure stores `Dictionary<string endpoint, int count>` — **no per-user tracking**
4. **Implementation** phase: global counter incremented on every request, returns 429 when `count > 100` for the endpoint
5. **Verification** phase: all adjacent validators pass ✅
   - Structural: schema valid, no syntax errors
   - Semantic: rate limiter correctly rejects when threshold exceeded
   - Cross-artifact: implementation matches contract, contract matches requirements

**Result**: Pipeline **PASSES** ✅ — but the implementation rate-limits **globally**, not per-user. First 100 requests from any user exhaust the quota for everyone. The system is technically correct per the requirements artifact, but useless for the actual use case.

**Without intent fidelity checking**, this drift is invisible. All validators see consistency across artifacts. The gap between source intent ("per-user") and derived requirements ("global") was never tested.

### Evidence in Current Design

The prompt envelope (§6.2, `prompt-envelope.md`) includes:
- Task summary (≤500 chars) in the `=== TASK ===` section
- Phase goal and schema contract
- Inputs from prior phases (treated as "ground truth")

**What's missing**: Instructions to validate against *original source intent*. Once the `requirements` phase produces its artifact, all downstream phases treat it as authoritative. There is no back-pressure from "wait, does this still match what the human asked for?"

---

## 2. Post-Spike Required Additions

The team reached unanimous agreement on **five scope additions** for post-spike work.

**Two hard architectural assertions** (Archimedes):
1. **Intent fidelity is a PHASE, not a validator.** It must run as a separate `PhaseRun` with zero access to intermediate artifacts (requirements, contract, function_design). Granting access to intermediates re-introduces the context pollution this phase exists to detect.
2. **The drift taxonomy is CLOSED.** The five codes below are exhaustive. Adding a 6th code requires a methodology version bump, not a runtime config change.

### 2.1 Source-Intent Schema

**What**: A structured representation of the human's original ask, separate from the derived `requirements` artifact.

**Why**: Prevents "telephone game" drift where each phase faithfully implements the prior phase's output but collectively diverges from the human's actual need.

**Schema sketch**:
```json
{
  "sourceIntent": {
    "taskBrief": "string (verbatim from human, not paraphrased)",
    "acceptanceCriteria": ["string"],
    "examples": [
      { "scenario": "string", "expectedBehavior": "string" }
    ],
    "counterExamples": [
      { "scenario": "string", "unacceptableBehavior": "string" }
    ],
    "explicitConstraints": ["string"],
    "preferredApproach": "string or null"
  }
}
```

**Integration**: The `source-intent` artifact is:
- Created **once** at Run start (never amended)
- Passed as a **read-only input** to the `requirements` phase for grounding
- Passed to the **final fidelity phase** (see §2.3) for end-to-end validation

### 2.2 Ideation Phase

**What**: A pre-contract phase that explores multiple solution approaches before committing to a design.

**Why**: The current pipeline assumes the "right" design emerges linearly from requirements → contract. In practice, many tasks have multiple valid approaches with different trade-offs. Skipping ideation biases toward the first viable solution.

**Schema sketch**:
```json
{
  "ideation": {
    "approaches": [
      {
        "approachId": "string",
        "summary": "string",
        "pros": ["string"],
        "cons": ["string"],
        "estimatedComplexity": "LOW | MEDIUM | HIGH",
        "risksAndMitigations": [
          { "risk": "string", "mitigation": "string" }
        ]
      }
    ],
    "recommendedApproach": "string (approachId)",
    "rationale": "string"
  }
}
```

**Validator constraint** (Socrates): Ideation phase must include **adversarial review** — a second-pass validator that probes for ambiguity in requirements and asks clarifying questions. If the requirements artifact contains "user," "fast," "secure," or other vague terms without quantification, the validator must flag them as blocking issues.

### 2.3 Final Fidelity Phase

**What**: A terminal phase that compares the final output (implementation artifact) directly against source intent, with **zero access to intermediate artifacts**.

**Why**: Detects semantic drift that survived the per-phase validation pipeline. Answers: "Does the final deliverable solve the original task?"

**Inputs** (Archimedes' runtime invariant):
```json
"inputs": {
  "sourceIntentHash": "<hash>",
  "finalOutputHash": "<hash>"
}
```

**Runtime Constraint**: The fidelity-phase executor **MUST reject** any `PhaseRun` whose `inputArtifactHashes` set is not exactly size 2 and not exactly `{sourceIntent, finalOutput}` by artifactType lookup. This constraint is **enforceable in code** (see Acceptance Hooks, item 6).

**Validator responsibilities**:
- Read `sourceIntent.taskBrief` and `sourceIntent.acceptanceCriteria`
- Read `implementation.code` (or whichever artifact is the terminal output)
- Check: Does the code satisfy the acceptance criteria?
- Check: Are any `explicitConstraints` violated?
- Check: Do the `examples` from source intent behave as expected?

**Output**: A `fidelity` artifact with:
```json
{
  "fidelity": {
    "overallMatch": "PASS | FAIL | PARTIAL",
    "acceptanceCriteriaResults": [
      { "criterion": "string", "satisfied": "boolean", "evidence": "string" }
    ],
    "driftDetected": [ /* see §2.4 */ ]
  }
}
```

### 2.4 Drift Taxonomy (CLOSED)

**The taxonomy is CLOSED.** The 5 codes below are exhaustive and versioned with the methodology. Adding a 6th code requires a methodology version bump, not a runtime config change.

**Drift Codes** (copy-pasteable enum):
```
OMITTED_CONSTRAINT
INVENTED_REQUIREMENT
SCOPE_BROADENED
SCOPE_NARROWED
CONSTRAINT_WEAKENED
```

**Emission Schema** (Archimedes' field types):
```json
{
  "code": "enum (one of the 5 strings above, no free text)",
  "sourceIntentRef": "JSON pointer into source-intent payload (e.g., /constraints/3)",
  "evidenceArtifactHash": "string (must resolve in artifact store)",
  "evidenceLocator": "JSON pointer or line range into evidence artifact"
}
```

**Severity Classification**:
- **Blocking**: `OMITTED_CONSTRAINT`, `CONSTRAINT_WEAKENED`
- **Advisory**: `INVENTED_REQUIREMENT`, `SCOPE_BROADENED`, `SCOPE_NARROWED`

**Example Detection** (from rate-limit scenario):
```json
{
  "code": "OMITTED_CONSTRAINT",
  "sourceIntentRef": "/taskBrief",
  "evidenceArtifactHash": "<requirements.hash>",
  "evidenceLocator": "/functionalRequirements/0/text"
}
```
Interpretation: The source intent (`/taskBrief`) contained "per-user" rate limiting, but the requirements artifact (`<requirements.hash>`) at `/functionalRequirements/0/text` omits the "per-user" part.

### 2.5 Seeded-Defect Benchmarks

**What**: Controlled test cases where a known defect (one of the 5 drift codes) is **intentionally injected** into an intermediate artifact, and the final fidelity phase is expected to detect it.

**Why**: Without seeded defects, we cannot distinguish "the fidelity validator correctly passed a clean run" from "the fidelity validator is blind to this class of error." LLM-judging-LLM creates a risk that the validator hallucinates pass/fail verdicts.

**Example Benchmark** (T1 with scope-shrink defect):
- **Source intent**: "Implement TodoList with add, remove, complete, and list operations"
- **Injected defect**: Requirements phase output omits "list" operation (scope narrowed)
- **Expected fidelity result**: `FAIL` with `driftDetected = [{ code: "SCOPE_NARROWED", sourceIntentRef: "/taskBrief", ... }]`
- **Pass condition**: Fidelity phase emits the drift code within 1 retry attempt

**Benchmark Coverage**:
- At least **1 seeded defect per drift code** (5 minimum)
- At least **1 clean run** (no defects) that should pass
- Runs must use **`modelProvenance.model != "stub"`** (real LLM judgment, not mocked)

**Failure Mode**: If seeded-defect detection falls below the falsifiability thresholds (80% blocking, 60% advisory), the methodology hypothesis is **falsified**.

---

## 3. Socrates' Acceptance Hooks

The following checklist defines **engineering constraints** for spike acceptance. These are testable, enforceable gates (not aspirational goals).

- [ ] `run.json` includes `modelProvenance` field with `{provider, model, version}`; runs with `model: "stub"` are excluded from spike pass/fail evaluation
- [ ] `CanonicalJson.Serialize` has a **stability unit test**: serialize object → deserialize → serialize again → byte-identical output
- [ ] `forge-spike/control/prompt.md` is committed **before the first pipeline run** (prompt envelope must be versioned, not inlined in code)
- [ ] `forge-spike/config/predeclared-blocking-codes.json` is committed with the first engine commit (empty `[]` is acceptable; file must exist)
- [ ] Each `Attempt` record in `phase-runs.json` includes the exact `amendPayload` received from the amend API call (verbatim, not summarized)
- [ ] **Fidelity-phase input violation test**: Unit test that the fidelity-phase executor rejects a `PhaseRun` whose `inputArtifactHashes` is not exactly `{sourceIntent, finalOutput}` by artifactType (size != 2, or wrong types, or contains any intermediate artifact type → `Errored` with `error_kind: "fidelity_input_violation"`)

---

## 4. Operator-Observable Signals

For the spike to be externally evaluable, the following signals must be **visible** to an operator inspecting a Run (not buried in logs or intermediate state):

| Signal | Why It Matters |
|--------|----------------|
| **`source-intent` artifact shown alongside final output** | Proves the run can be inspected against original intent. Without this, an operator cannot manually verify fidelity. |
| **Separate `final-fidelity` PhaseRun** | Proves intent checking is isolated from construction validation. If fidelity is a sub-validator within the `verify` phase, context pollution risk remains. |
| **Drift codes rendered as closed-code counts** | Makes regressions measurable across runs. "Run 1: 0 drift codes, Run 2: 2× OMITTED_CONSTRAINT" is actionable. Prose descriptions ("some requirements were missed") are not. |
| **`modelProvenance.model == "stub"` visibly excluded from pass/fail** | Prevents fake green runs. If a Run passes because validators were stubbed, that should be obvious in the UI/report, not hidden in metadata. |

**Implementation Note**: These signals are not necessarily exposed via a web UI (out of scope for the spike). They must be **present in the artifact store and phase-runs.json** such that an external reviewer can reconstruct the run's behavior.

---

## 5. Identified Risks and Measurement

### 5.1 LLM-Judging-LLM Circular Dependency

**Risk**: Semantic validators ask an LLM to judge another LLM's output. If both models share the same failure modes (e.g., both hallucinate that vague requirements are "clear enough"), the validator will pass garbage.

**Evidence**: The `verify` phase uses an LLM to check "does the implementation match the contract?" Both the `implement` phase and the `verify` phase may use the same model family (e.g., GPT-4). If GPT-4 implementing misreads a contract ambiguity, GPT-4 verifying may make the same misreading and approve the wrong code.

**Mitigation**:
1. **Seeded-defect benchmarks** (§2.5) — if the validator consistently misses injected defects, we detect the blind spot
2. **Multi-model validation** (post-spike) — use a different model family for verification than for construction (e.g., GPT-4 builds, Claude verifies)
3. **Structural validators as gates** — never allow semantic-only validation; always combine with schema/type checks that are deterministic

**Measurement**: Track **false-pass rate** on seeded defects. If validator passes >20% of runs with injected blocking drift, the LLM-judge hypothesis is falsified.

### 5.2 Semantic Validator Blind Spots

**Risk**: Validators may pass outputs that "look right" syntactically but are semantically wrong in subtle ways (e.g., off-by-one errors, inverted conditions, missing edge cases).

**Evidence**: Current validators check "does every FR have a test case?" but not "does the test case actually cover the FR's behavior?" A test that asserts `assertTrue(true)` would pass structural validation but provides zero coverage.

**Mitigation**:
1. **Example-driven validation** — require each FR to include concrete input/output examples; validator must check that code produces the expected output for those examples
2. **Adversarial review in ideation phase** — force validators to ask "what could go wrong?" before accepting a design
3. **Seeded-defect benchmarks** with subtle bugs (not just missing features)

**Measurement**: Track **semantic defect escape rate** — bugs that passed all validators but failed when code was actually run.

### 5.3 Context Window Blowup

**Risk**: Passing all intermediate artifacts as inputs to every phase can exhaust the context window for complex tasks, forcing truncation or summarization (which reintroduces information loss).

**Evidence**: The T3 benchmark (microservice with 5+ interfaces) could generate:
- 200-line requirements artifact
- 400-line contract artifact
- 600-line function design artifact
- Total input to `implement` phase: 1200+ lines before the actual task

**Mitigation**:
1. **Artifact compression** — pass artifact **hashes** in phase-runs.json, not full payloads; phase executor fetches only the artifacts it needs
2. **Phase-scoped inputs** — `implement` phase only receives `contract` and `function_design`, not `requirements` (reduces redundancy)
3. **Hierarchical tasks** (post-spike) — split large tasks into sub-runs with separate artifact stores

**Measurement**: Log **token usage per phase**. If any phase exceeds 75% of model's context limit, flag as near-overflow.

### 5.4 Cost Runaway on Retry Loops

**Risk**: If validators are expensive (multi-step reasoning, long prompts) and phases require multiple retries, cost scales as `O(phases × retries × validator_cost)`. A 5-phase run with 3 retries per phase and $0.50/validator = $7.50 per run.

**Evidence**: The `retry_policy` in methodology.json allows up to 3 retries per phase. If all 5 phases max out retries, that's 15 LLM calls per run (5 phases × 3 retries), not counting the initial attempt.

**Mitigation**:
1. **Cached validation** — if artifact hash hasn't changed, reuse prior validator result
2. **Cheap structural validators first** — run deterministic checks before expensive LLM validators; fail fast
3. **Retry budget** — cap total retries per run (not per phase) to prevent exponential blowup

**Measurement**: Track **cost per run** and **retry distribution**. If median run exceeds $5, revisit validator design.

### 5.5 Artifact Deduplication Handling

**Risk**: If two phases independently produce the same artifact (same payload, same hash), the artifact store deduplicates storage but phase-runs.json may show them as separate outputs. Operators may miscount "artifacts produced."

**Evidence**: The T2 benchmark (CLI tool, 9 methods) could have `verify` phase emit the same schema validation artifact that `implement` phase already produced (both check "does the function signature match the contract?").

**Mitigation**:
1. **Artifact references are by hash** — phase-runs.json records the hash, not a copy of the payload; deduplication is transparent
2. **Metadata separation** — store `<hash>.meta.json` separately from `<hash>.json` so different phases can attach different metadata to the same artifact

**Measurement**: Track **deduplication rate** — percentage of artifact hashes that appear in >1 phase. If >50%, consider whether phases are redundant.

---

## 6. Recommendations for External Team

### What the Spike WILL Test
- ✅ Context isolation by construction (phases only see declared inputs)
- ✅ Artifact immutability and hash-based addressing
- ✅ Retry/amend flow with validator feedback
- ✅ Structural and cross-artifact validators
- ✅ Trace reconstruction from phase-runs.json

### What the Spike WON'T Test
- ❌ Intent fidelity (no source-intent schema, no final fidelity phase)
- ❌ Ideation / solution-space exploration
- ❌ Multi-model validation (all phases use same model family)
- ❌ Cost optimization / retry budgets
- ❌ Hierarchical tasks or sub-runs

### Prioritized Follow-On Work (if spike passes)
1. **Implement seeded-defect benchmarks** — required to validate the LLM-judge hypothesis
2. **Add source-intent schema + final fidelity phase** — closes the intent gap
3. **Implement CanonicalJson stability test** — prerequisite for artifact hash trust
4. **Add modelProvenance tracking** — prerequisite for separating stub runs from real runs
5. **Design ideation phase** — improves solution quality but not strictly required for fidelity

### Open Questions for Decision-Makers
- **Model budget**: Is the team approved to use cheap-tier LLMs (e.g., GPT-4o-mini) for the spike, or does each validator call require approval?
- **Benchmark coverage**: Are 3 tasks (T1, T2, T3) sufficient, or should the spike include 5+ benchmarks before external review?
- **Failure tolerance**: If the spike detects 1 false-pass on seeded defects (out of 5), is that acceptable or disqualifying?

---

## Appendix: Conversation Provenance

This document synthesizes findings from a multi-agent planning session (Aristotle, Archimedes, Hephaestus, Athena, Socrates, Thucydides) across 50+ messages. Specific decisions and rationales were captured in the following stored memories:

- `forge-design-intent-fidelity-gap` (category: finding)
- `forge-post-spike-required-additions` (category: spec-drift)
- `forge-spike-coverage-gaps` (category: gap-pattern)

Frozen design commits:
- `9746d0e` — State machine and 5-phase methodology
- `5ca7199` — Final artifact schemas and prompt envelope (Archimedes)
- `be049ec` — Acceptance criteria with corrected counts (Thucydides)

---

**End of Document**
