# Artifact Schemas

All artifacts are **JSON only** in the spike. No Markdown, no code blocks, no prose preambles. The LLM is instructed to emit a single JSON object matching the named schema; anything else is a structural validator failure.

Every artifact carries a thin envelope so the store doesn't need to consult the methodology to know what it's looking at.

## Common envelope

Every artifact file (`<sha256>.json` in the store) has this top-level shape:

```json
{
  "schema": "<schema_id>",
  "schema_version": "1",
  "produced_by_phase": "<phase_id>",
  "task_id": "<benchmark_task_id>",
  "input_hashes": ["<sha256>", "..."],
  "body": { /* schema-specific payload — see below */ }
}
```

`body` is the only field the LLM produces. The executor wraps it with the envelope before hashing and storing.

> **Hashing rule:** the artifact hash is `sha256` of the canonicalized full envelope (sorted keys, no whitespace, UTF-8). Hashing the envelope (not just `body`) means input lineage participates in identity — two phase runs that produce identical bodies from different inputs will (correctly) produce different artifacts.

---

## 1. `requirements/v1`

Output of the `requirements` phase. Captures what the task demands, decomposed.

```json
{
  "task_summary": "string, ≤ 200 chars",
  "user_outcomes": [
    { "id": "U1", "outcome": "string", "priority": "must|should|could" }
  ],
  "functional_requirements": [
    { "id": "FR1", "statement": "string", "outcome_ids": ["U1"] }
  ],
  "non_functional_requirements": [
    { "id": "NFR1", "category": "performance|security|reliability|usability|other", "statement": "string" }
  ],
  "out_of_scope": ["string"],
  "open_questions": [
    { "id": "Q1", "question": "string", "assumed_answer": "string" }
  ]
}
```

**Validators:**
- Structural: schema match; ≥1 `user_outcomes`; ≥1 `functional_requirements`; every `outcome_ids` reference resolves; IDs unique within their list.
- Semantic (LLM-judge): each FR is testable (binary true/false determinable from observation); no FR is a solution-in-disguise (e.g., "use Redis"); `out_of_scope` is non-trivial (≥1 entry, scope discipline).
- Cross-artifact: N/A (first phase).

---

## 2. `contract/v1`

Output of the `contract` phase. The external interface — what the implementation must satisfy.

```json
{
  "interfaces": [
    {
      "name": "string",
      "kind": "function|class|module|http_endpoint|cli|mcp_tool|file",
      "signature": "string (language-agnostic; e.g. 'fn search(query: string) -> Result[]')",
      "description": "string",
      "preconditions": ["string"],
      "postconditions": ["string"],
      "errors": [
        { "condition": "string", "behavior": "string" }
      ],
      "satisfies_fr_ids": ["FR1"]
    }
  ],
  "data_shapes": [
    {
      "name": "string",
      "fields": [
        { "name": "string", "type": "string", "required": true, "notes": "string" }
      ]
    }
  ],
  "invariants": ["string"],
  "examples": [
    { "scenario": "string", "input": {}, "output": {}, "fr_id": "FR1" }
  ]
}
```

**Validators:**
- Structural: schema match; ≥1 interface; every `satisfies_fr_ids` resolves to an FR in input requirements.
- Semantic (LLM-judge): every FR (must|should) is satisfied by ≥1 interface; signatures are concrete (no `???`, no TODO); examples agree with signatures and postconditions.
- Cross-artifact: every `must`-priority FR has at least one example in `examples[]`.

---

## 3. `function_design/v1`

Output of the `function_design` phase. The internal decomposition — how the contract will be realized.

```json
{
  "components": [
    {
      "id": "C1",
      "name": "string",
      "responsibility": "string (one sentence)",
      "depends_on": ["C2"],
      "implements": ["<interface_name from contract>"]
    }
  ],
  "data_flow": [
    { "from": "C1", "to": "C2", "carries": "string", "trigger": "string" }
  ],
  "error_handling": [
    { "component_id": "C1", "failure": "string", "response": "string" }
  ],
  "deferred_decisions": [
    { "id": "D1", "decision": "string", "rationale_for_deferring": "string" }
  ]
}
```

**Validators:**
- Structural: schema match; component IDs unique; every `depends_on` resolves; every `implements` references an interface from input contract; `data_flow` endpoints resolve to component IDs; no dependency cycles (DAG).
- Semantic (LLM-judge): every contract interface is `implements`-d by ≥1 component; every component has a single, narrow responsibility (no "manages everything" descriptions); error_handling covers every interface error condition.
- Cross-artifact: union of `implements` across all components ⊇ set of interface names from input contract.

---

## 4. `implementation/v1`

Output of the `implement` phase. Code, as text.

```json
{
  "files": [
    {
      "path": "string (repo-relative)",
      "language": "typescript|python|csharp|go|rust|markdown|other",
      "content": "string (full file contents)",
      "implements_component_ids": ["C1"]
    }
  ],
  "build_command": "string (e.g. 'npm run build')",
  "test_command": "string or null",
  "notes": "string"
}
```

**Validators:**
- Structural: schema match; ≥1 file; paths are relative, no `..`, no absolute; languages from enum; `implements_component_ids` resolve to design components.
- Semantic (LLM-judge): each file's content is parseable as the declared language (best-effort syntactic check via heuristic; no compiler invoked in spike); no obvious placeholders (`TODO`, `// implement me`, `pass # implement`).
- Cross-artifact: union of `implements_component_ids` across all files ⊇ set of component IDs in input function_design.

> Note: the spike does NOT execute build/test commands. They are recorded for the human reviewer and Socrates' rubric.

---

## 5. `review/v1`

Output of the `verify` phase. Self-review of the implementation against the full upstream chain.

```json
{
  "verdict": "pass|fail|needs_revision",
  "summary": "string (≤ 500 chars)",
  "checks": [
    {
      "id": "CHK1",
      "kind": "fr_satisfied|contract_satisfied|design_satisfied|invariant_held|example_matches",
      "target_id": "FR1 | <interface_name> | C1 | <invariant text> | <example scenario>",
      "result": "pass|fail|inconclusive",
      "evidence": "string (point to file path + line range or example I/O)"
    }
  ],
  "defects": [
    { "id": "D1", "severity": "critical|major|minor", "description": "string", "location": "string" }
  ],
  "improvements_for_next_iteration": ["string"]
}
```

**Validators:**
- Structural: schema match; `verdict` consistent with `defects` (any `critical` ⇒ verdict ≠ pass).
- Semantic (LLM-judge — adversarial framing): for each `must`-priority FR, ≥1 check exists and references concrete evidence in the implementation files. Reviewer's verdict is justifiable from the listed checks.
- Cross-artifact: every `target_id` of the appropriate `kind` resolves to an entity in the corresponding upstream artifact.

---

## Validator pipeline contract (applies to every artifact)

The validator pipeline is executed in this fixed order:

1. **Structural** — pure code, no LLM. JSON parse, schema match, ID uniqueness, reference resolution. Cheap and deterministic.
2. **Semantic** — LLM-judge call with a frozen rubric per schema. Produces `{passed: bool, failures: [{rule, detail}]}`. Uses **a different model tier** from the generator when feasible (cheap-tier judge by default; configurable).
3. **Cross-artifact** — pure code, runs only after structural+semantic pass. Verifies the new artifact's references against the immutable upstream artifacts loaded from the store.

A failure at any stage halts the pipeline for that attempt. Failure messages are concatenated and passed as `amendment_notes` to the next attempt's prompt.

## Schema implementation note for Hephaestus
Place these as JSON Schema files under `Forge/Schemas/` (`requirements.v1.json`, `contract.v1.json`, ...). Use `System.Text.Json.JsonDocument` + a schema validator nuget (e.g. `JsonSchema.Net`) for the structural step. The semantic-step rubrics are prose embedded in the validator class for each schema; do not load them from disk during the spike (frozen-by-deployment).
