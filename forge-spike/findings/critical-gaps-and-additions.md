# Forge Spike — Critical Gaps and Post-Spike Scope Additions

**Date**: 2026-04-19  
**Authors**: Team consensus (Aristotle, Archimedes, Hephaestus, Athena, Socrates, Thucydides)  
**Context**: Post-design, pre-implementation findings from collaborative spike planning session  
**Status**: Committed for external review

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

Example scenario:
- Task brief: "Build an MCP server with tools for code_search and file_read"
- Requirements phase interprets "code_search" as full-text search only (misses semantic/AST search intent)
- Contract, design, implementation faithfully realize this narrow interpretation
- All validators pass ✅
- **But the resulting tool is useless for the intended use case** ❌

### Evidence in Current Design

The prompt envelope (§6.2, `prompt-envelope.md`) includes:
- Task summary (≤500 chars) in the `=== TASK ===` section
- Phase goal and schema contract
- Inputs from prior phases (treated as "ground truth")

**What's missing**: Instructions to validate against *original source intent*. Once the `requirements` phase produces its artifact, all downstream phases treat it as authoritative. There is no back-pressure from "wait, does this still match what the human asked for?"

### Proposed Solution (Post-Spike)

Add a **source-intent schema** that captures:
- The human's original task brief (verbatim, not summarized)
- Acceptance criteria from the human (if provided)
- Concrete examples or counter-examples from the brief
- Any explicit constraints or preferences

Include this schema as a read-only input to:
1. The **requirements** phase (for grounding)
2. A new **final fidelity** phase (for end-to-end validation against source intent)

---

## 2. Post-Spike Required Additions

The team reached unanimous agreement on **five scope additions** for post-spike work:

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

**Integration**: Provided as a read-only input to all phases. Validators check drift.

---

### 2.2 Ideation Phase

**What**: A new phase **before** `contract`, after `requirements`, that explores multiple solution approaches without committing to one.

**Why**: The current methodology jumps from "what must be done" (requirements) to "the external interface" (contract), implicitly committing to the first viable design. High-quality engineering involves divergent thinking before convergence.

**Phase goal**: 
> "Generate 2-3 distinct solution approaches for satisfying the requirements. For each approach, outline its core strategy, key trade-offs, and which requirements it prioritizes. Do not produce detailed designs — only conceptual sketches."

**Output schema** (`ideation/v1`):
```json
{
  "approaches": [
    {
      "id": "A1",
      "name": "string",
      "coreStrategy": "string",
      "strengths": ["string"],
      "weaknesses": ["string"],
      "prioritizes_fr_ids": ["FR1"],
      "defers_fr_ids": ["FR2"]
    }
  ],
  "recommendedApproach": "A1",
  "rationale": "string"
}
```

**Validators**:
- Structural: ≥2 approaches, distinct strategies (LLM judge for "is this meaningfully different?")
- Semantic: Every must-priority FR appears in `prioritizes_fr_ids` of at least one approach
- Cross-artifact: Recommended approach is justified by requirements coverage + task constraints

**Impact**: Prevents premature descent into the first locally-viable solution. Forces the model to "think in alternatives" before committing.

---

### 2.3 Final Fidelity Phase

**What**: A terminal phase **after** `verify` that checks the full pipeline output against **source intent**, not just internal consistency.

**Why**: The `verify` phase (as currently designed) checks implementation against contract/design/requirements. But it does not check "did we build what the human wanted?" This phase closes the loop.

**Phase goal**:
> "Adversarially evaluate the final implementation against the original task brief and acceptance criteria. Ignore internal artifacts (contract, design) — treat the implementation as a black box and judge only observable behavior against source intent."

**Inputs**: `["sourceIntent", "implementation", "requirements"]`

**Output schema** (`fidelity/v1`):
```json
{
  "intentChecks": [
    {
      "criterionId": "string (from sourceIntent.acceptanceCriteria)",
      "verdict": "pass|fail|partial",
      "evidence": "string (concrete evidence from implementation)",
      "gap": "string or null (what's missing/wrong if fail/partial)"
    }
  ],
  "exampleChecks": [
    {
      "exampleId": "string (from sourceIntent.examples)",
      "verdict": "pass|fail",
      "evidence": "string"
    }
  ],
  "driftAnalysis": {
    "meaningfulDrift": true|false,
    "driftDescription": "string or null",
    "severity": "critical|moderate|minor|none"
  },
  "overallVerdict": "acceptable|needs_revision",
  "revisionNeeded": ["string (concrete changes required)"]
}
```

**Validators**:
- Structural: Every acceptance criterion from sourceIntent has a corresponding check
- Semantic: Evidence references actual file paths, line ranges, or concrete I/O examples
- Cross-artifact: If `overallVerdict=acceptable`, all `criterionId` checks must be `pass` or `partial` with justified trade-offs

---

### 2.4 Drift Taxonomy

**What**: A structured vocabulary for classifying semantic drift across pipeline phases.

**Why**: "The output diverged from intent" is too vague for automated detection or human review. We need categories of drift with severity levels.

**Taxonomy** (preliminary):

| Drift Type | Definition | Severity | Example |
|------------|------------|----------|---------|
| **Scope Creep** | Implementation adds features not in task brief | Minor–Moderate | Task asks for search tool; impl adds caching, rate-limiting |
| **Scope Shrink** | Requirements or implementation silently drops a must-have | Critical | Task requires error handling; impl assumes all inputs valid |
| **Misinterpretation** | Requirements phase misreads task brief | Critical | Task says "search code"; requirements interpret as "grep files" (misses semantic search) |
| **Over-Specification** | Contract or design commits to implementation details task didn't require | Minor | Task says "read files"; contract mandates streaming I/O when simple read would work |
| **Unjustified Trade-off** | Design sacrifices a requirement without explicit rationale | Moderate–Critical | Design drops a should-priority FR to simplify, but doesn't document why |
| **Semantic Weakening** | Downstream phases water down intent (e.g., "must" → "should") | Moderate | Requirements say FR1 is must-priority; design defers it to "future work" |

**Integration**: 
- Validators emit drift classifications when detected
- `fidelity` phase includes drift analysis in its output
- Post-spike: train a classifier or provide LLM judge with this taxonomy

---

### 2.5 Seeded-Defect Benchmarks

**What**: Benchmark tasks with **known, intentional defects** injected at specific phases, used to test validator recall (ability to detect defects).

**Why**: The current benchmarks (T1, T2, T3) test the pipeline on "clean path" tasks. We don't know if validators would catch:
- A requirements phase that silently drops a must-have
- A contract that violates an invariant from requirements
- An implementation that compiles but violates a postcondition

Without adversarial testing, we cannot distinguish "validators are effective" from "the model happened to produce clean outputs."

**Structure**:
Each seeded-defect benchmark includes:
- A base task (e.g., T1: build MCP server)
- A defect injection point (e.g., `requirements` phase)
- The specific defect (e.g., "omit FR3: error handling for invalid paths")
- Expected validator behavior (e.g., "semantic validator on `contract` phase should flag missing error cases")

**Example** (seeded defect for T1):
```json
{
  "baseTask": "T1-mcp-server",
  "defectInjection": {
    "phase": "requirements",
    "defectType": "scope_shrink",
    "description": "Requirements artifact omits error handling for file_read when path does not exist (violates acceptance criterion #3.3)",
    "injectionMethod": "manual_edit_of_artifact"
  },
  "expectedDetection": {
    "detectingPhase": "contract",
    "detectingValidator": "semantic",
    "expectedMessage": "Acceptance criterion T1-AC-3.3 (file_read must handle 'not found' error) has no corresponding interface error case"
  }
}
```

**Metrics**:
- **Recall**: % of seeded defects detected by at least one validator
- **Precision**: % of validator flags that are true defects (not false positives)
- **Detection latency**: How many phases downstream before the defect is caught

**Post-spike deliverable**: 5–10 seeded-defect benchmarks across all drift types, with expected validator behavior pre-declared.

---

## 3. Socrates' Acceptance Hooks

During design review, Socrates (code reviewer agent) imposed several **non-negotiable constraints** on the spike implementation. These are recorded here as commitments Hephaestus must satisfy:

### 3.1 Model Provenance Tracking

**Constraint**: Every artifact's `.meta.json` must include:
```json
{
  "modelProvenance": {
    "provider": "string (e.g., 'openai', 'anthropic')",
    "model": "string (e.g., 'gpt-4', 'claude-sonnet-4')",
    "apiVersion": "string",
    "temperature": "number",
    "topP": "number or null",
    "seed": "number or null (for reproducibility)"
  }
}
```

**Rationale**: Cost analysis and model-behavior attribution require knowing which model produced which artifact. Without provenance, we cannot answer "did the claude run produce better outputs than the gpt run?"

**Acceptance test**: Socrates will reject any PR where `.meta.json` files lack `modelProvenance` or have incomplete fields.

---

### 3.2 Canonical JSON Stability Test

**Constraint**: The hashing logic (`canonical_json()` function) must have a **committed unit test** demonstrating stability under:
- Key reordering
- Whitespace variation
- Unicode normalization (NFC vs. NFD)
- Numeric representation (1.0 vs. 1, scientific notation)

**Rationale**: Artifact identity depends on hash stability. If `canonical_json()` is non-deterministic or locale-dependent, the entire content-addressing system breaks.

**Test requirement** (from Socrates):
```csharp
[Fact]
public void CanonicalJson_IsStableUnderKeyReordering()
{
    var json1 = """{"b": 2, "a": 1}""";
    var json2 = """{"a": 1, "b": 2}""";
    Assert.Equal(CanonicalJson(json1), CanonicalJson(json2));
}

[Fact]
public void CanonicalJson_ProducesSameHashAcrossRuntimes()
{
    var input = """{"foo": "bar", "baz": [1, 2, 3]}""";
    var expectedHash = "sha256:a3c8f8e9d..."; // pre-computed
    Assert.Equal(expectedHash, Sha256Hash(CanonicalJson(input)));
}
```

**Acceptance gate**: Socrates will block merge until these tests exist and pass in CI.

---

### 3.3 Prompt Envelope Committed First

**Constraint**: The file `docs/forge-spike/prompt-envelope.md` (§6.2) must be committed **before** any implementation of prompt rendering logic.

**Rationale**: The spike's validity depends on the prompt template being frozen before execution. If Hephaestus writes the renderer and *then* backfills the spec, there's no evidence the implementation matches the design.

**Process**:
1. Archimedes/Thucydides commit `prompt-envelope.md` with full template
2. Socrates reviews and approves
3. Hephaestus implements renderer as a pure translation of the spec
4. Socrates reviews implementation against committed spec

**Verification**: Git history must show `prompt-envelope.md` committed (and merged to design branch) before any `.cs` files in `src/Forge.PipelineEngine/`.

---

### 3.4 Predeclared Validator Blocking Codes

**Constraint**: Every validator that can reject an artifact must declare **upfront** the specific JSON-RPC-style error codes it emits, in a committed file `docs/forge-spike/validator-error-codes.json`.

**Why**: Prevents ad-hoc error messages. Enables automated parsing of validator failures. Supports future tooling (e.g., "show me all runs that failed with `E_MISSING_FR_COVERAGE`").

**Schema**:
```json
{
  "validatorErrorCodes": [
    {
      "code": "E_SCHEMA_MISMATCH",
      "validator": "structural",
      "description": "Artifact does not match declared JSON schema",
      "severity": "critical",
      "exampleMessage": "Field 'user_outcomes' is required but missing"
    },
    {
      "code": "E_UNTESTABLE_FR",
      "validator": "semantic",
      "phase": "requirements",
      "description": "A functional requirement is not testable (no observable pass/fail condition)",
      "severity": "moderate",
      "exampleMessage": "FR3 'the system should be fast' has no measurable criterion"
    }
  ]
}
```

**Acceptance test**: 
1. File must exist before first validator implementation
2. Every `validatorResult.code` emitted during spike execution must be pre-declared in this file
3. Socrates will fail CI if a novel error code appears at runtime

---

### 3.5 Phase Transition Audit Log

**Constraint**: Every state transition in `phase-runs.json` must include:
```json
{
  "stateTransitions": [
    {
      "from": "Pending",
      "to": "Running",
      "timestamp": "2026-04-19T18:00:00Z",
      "trigger": "executor_start",
      "metadata": {}
    }
  ]
}
```

**Why**: Crash recovery and debugging require a time-series of what happened. Without timestamps, we cannot answer "did this hang or did it just start?"

**Socrates' rule**: Every transition (Run, PhaseRun, Attempt) must append to its `stateTransitions` array. No in-place updates to `status` fields without a corresponding audit entry.

---

### 3.6 No Silent Failures in Validators

**Constraint**: If a validator throws an unhandled exception (e.g., LLM API timeout, JSON parse error, null reference), the attempt **must** transition to `Errored` state with `error_kind` set, **not** silently proceed to next validator.

**Why**: Silent failures create false-pass scenarios ("all validators passed" because one crashed before it could flag a defect).

**Implementation requirement**:
```csharp
try {
    var result = await validator.ValidateAsync(artifact);
    results.Add(result);
} catch (Exception ex) {
    // Do NOT swallow. Transition Attempt to Errored.
    return AttemptResult.Errored(error_kind: "validator_crash", ex.Message);
}
```

**Socrates' test**: Inject a validator that throws on every call. Verify the attempt transitions to `Errored`, not `Accepted`.

---

## 4. Identified Risks

### 4.1 LLM-Judging-LLM (Circular Dependency)

**Risk**: Semantic validators use an LLM to judge whether another LLM's output is "reasonable" (e.g., "is this FR testable?"). This creates circular dependency:
- The generator LLM might produce defective requirements
- The validator LLM might have the same blind spots
- Both pass, defect proceeds to implementation

**Evidence**: 
- The semantic validator for `requirements/v1` asks an LLM judge: "Is FR3 testable?" 
- If the judge model has the same training bias as the generator (e.g., both accept vague FRs like "the system should be user-friendly"), the validator is ineffective.

**Mitigation strategies** (for post-spike):
1. **Model diversity**: Use a different model family for validation than generation (e.g., generate with GPT, validate with Claude)
2. **Adversarial prompting**: Instruct the validator to "adopt a hostile reviewer stance; assume the output is wrong until proven otherwise"
3. **Hybrid validation**: Combine LLM judge with rule-based checks (e.g., regex for "testable FR must include a measurable verb: 'returns', 'throws', 'displays'")
4. **Human-in-the-loop sampling**: Randomly sample 10% of validator passes for human review to measure false-pass rate

**Spike constraint**: Document validator false-pass rate as a metric (% of validator-accepted artifacts that a human reviewer would reject).

---

### 4.2 Semantic Validator Blind Spots

**Risk**: The LLM judge lacks domain context to catch subtle defects.

**Example**:
- Task brief: "Build an MCP server with code_search tool"
- Requirements artifact includes FR1: "code_search accepts a pattern string and returns matches"
- Contract includes interface: `function code_search(pattern: string): Match[]`
- **Defect**: The task brief implied semantic/AST-aware search, but requirements/contract only specify text search
- **Validator blind spot**: Without knowledge of MCP tool conventions or code search best practices, the LLM judge sees no defect — the FR and interface are internally consistent

**Evidence from current design**: 
- Validators have access to prior phase artifacts (requirements → contract → design → implementation)
- Validators do **not** have access to:
  - External knowledge (e.g., "MCP tools should follow protocol spec X")
  - Domain best practices (e.g., "code search tools typically support regex, case sensitivity toggles")
  - Implicit task context (e.g., "this is for a code editor, not a log file viewer")

**Mitigation strategies** (post-spike):
1. **Inject domain knowledge**: Extend validator prompts with relevant documentation (e.g., "Here is the MCP protocol spec. Check if the interface satisfies it.")
2. **Example-driven validation**: Provide the validator with concrete examples from the task brief and ask "does the implementation handle these?"
3. **Negative examples**: Ask the validator to generate *counter-examples* — inputs that should fail — and verify the contract/implementation specifies error behavior for them

**Metric**: False-negative rate (% of actual defects that validators miss, measured via seeded-defect benchmarks).

---

### 4.3 Context Window Blowup in Late Phases

**Risk**: The `verify` phase receives **all prior artifacts** as inputs (requirements, contract, design, implementation). For complex tasks, this could exceed LLM context limits or degrade quality.

**Evidence**:
- Benchmark T3 acceptance criteria includes "≥10 API endpoints, ≥15 methods, ≥2000 LOC"
- If each phase adds 2–5KB of JSON, the `verify` phase prompt could be 20–30KB (comfortable for 128K context models, problematic for 32K models)
- If implementation includes full file contents (per `implementation/v1` schema), a 2000-LOC task → ~100KB artifact → prompt exceeds many models' context

**Mitigation**:
1. **Selective input**: Allow phases to declare `inputSelection: "summary"` to receive abridged versions of prior artifacts (e.g., only FR IDs, not full statements)
2. **Chunk-and-validate**: Split large implementations into files, validate per-file against contract, aggregate results
3. **Model tier gating**: Use larger-context models for late phases (e.g., GPT-4 Turbo for `verify`, GPT-4 for early phases)

**Spike constraint**: If any attempt fails with `error_kind=context_limit_exceeded`, abort the run immediately (don't retry) and flag for post-spike architectural revision.

---

### 4.4 Cost Runaway on Retry Loops

**Risk**: A pathological task could trigger MAX_ATTEMPTS retries on every phase, multiplying cost by 3× per phase (= 3^5 = 243× worst case for 5 phases).

**Example failure mode**:
- Semantic validator for `requirements` is overly strict (rejects any FR phrased with "should" instead of "shall")
- Generator produces 3 attempts, all rejected for phrasing
- MAX_ATTEMPTS exhausted → Run fails
- **But**: 3 LLM calls consumed (1 generation + 1 validation per attempt × 3 attempts = 6 LLM calls for zero output)

**Mitigation**:
1. **Cost cap per run**: Abort if cumulative tokens exceed a threshold (e.g., 500K tokens)
2. **Retry degradation**: Attempt 1 uses expensive model, attempt 2 uses mid-tier, attempt 3 uses cheap (rationale: if two strong attempts failed, a third won't help)
3. **Validator calibration**: Pre-test validators on known-good artifacts to tune strictness (avoid false-reject loops)

**Metric**: Average attempts-per-phase across successful runs. Target: ≤1.5 (i.e., most phases succeed on first attempt, occasional retry).

---

### 4.5 Artifact Duplication (Hash Collisions from Different Inputs)

**Risk**: Two phase runs with **different inputs** produce **identical artifacts** (byte-for-byte), collapsing to the same hash. This is *correct deduplication* per the design, but might confuse lineage tracking.

**Example**:
- Run A: requirements → contract → design → implementation (produces artifact X)
- Run B: *different requirements* → contract → design → implementation (produces *same artifact X*)
- Artifact X exists once in store, with two `.meta.json` files (different lineage)

**Why this could be a problem**:
- Viewer tools assume "one artifact = one lineage"
- Cost attribution: if artifact X is reused, who gets charged for its generation?

**Current design handles this**: 
- `artifacts/<hash>.meta.json` can have multiple instances (or be a JSON array of lineage records)
- Storage layout spec (§5.3) says "meta is advisory only, not identity-defining"

**Risk downgrade**: This is a **documentation/tooling problem**, not a correctness bug. Socrates confirms the design handles it. Post-spike: ensure viewer tools display all lineages for deduplicated artifacts.

---

## 5. Recommendations for External Team

If the external team proceeds with the spike as-is (frozen design from commit `9746d0e`), the following findings should inform their evaluation:

### What the Spike WILL Test
✅ Context isolation prevents catastrophic context pollution  
✅ Immutable artifacts enable reproducibility and auditability  
✅ Validator pipeline catches structural defects (schema violations, broken references)  
✅ Multi-attempt retry improves robustness vs. single-shot generation  
✅ Content-addressed storage enables deduplication and lineage tracking  

### What the Spike WILL NOT Test
❌ Fidelity to human intent (only internal consistency)  
❌ Quality of creative exploration (no ideation phase)  
❌ Drift detection across phase boundaries  
❌ Validator effectiveness (no seeded-defect benchmarks)  
❌ Model-specific behavior differences (no A/B testing infrastructure)  

### Recommended Follow-On Work (Priority Order)
1. **Seeded-defect benchmarks** (highest ROI — tests validator recall, unblocks trust in results)
2. **Source-intent schema + fidelity phase** (closes the "did we build what they wanted?" gap)
3. **Model provenance tracking** (enables cost/quality attribution — required for production)
4. **Drift taxonomy** (makes semantic drift measurable, not just vibes)
5. **Ideation phase** (improves design quality, secondary to validation infrastructure)

### Open Questions for External Team
- Is a spike that validates construction but not intent sufficient for your decision? Or do you need intent-fidelity before proceeding?
- Are you willing to accept LLM-judge validators as-is, or do you require hybrid (LLM + rule-based) validation before production?
- Do you have a target cost ceiling for pipeline runs? (Informs retry budget and model tier selection)

---

## Appendix: Memory References

The task brief references three memory keys that informed this document:
- `forge-design-intent-fidelity-gap` — (not yet stored)
- `forge-post-spike-required-additions` — (not yet stored)
- `forge-spike-coverage-gaps` — (not yet stored)

These memories are expected to be created during or after the planning session that produced this document. If you are reading this and the memories are missing, refer to the session transcript from 2026-04-19 (Aristotle-led planning room).

---

## Document Provenance

- **Initial draft**: Thucydides, 2026-04-19, based on team discussion and frozen spike design
- **Review**: Pending (Socrates, Archimedes)
- **Approval**: Pending (Aristotle)
- **Committed**: 2026-04-19 (this file)

This document is **not part of the frozen spike design** (commit `9746d0e`). It is a post-design findings report intended for human decision-makers evaluating whether to proceed with the spike as-is or incorporate the recommended additions.
