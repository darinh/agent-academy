# Prompt Envelope §6.2 — Frozen for Spike

Every phase attempt is rendered through **this exact template**, in this exact order, with no additions. Freezing the envelope is a precondition of the experiment: if we change envelopes mid-spike, we cannot attribute outcomes to the architecture vs. prompt tweaks.

The envelope produces a single string sent as the **user message** of a fresh LLM session. There is one constant **system message** (below) and zero prior turns.

## System message (constant, all phases)

```
You are a phase executor in a software engineering pipeline.
You execute exactly one phase per session. You have no memory of prior sessions.
You produce a single JSON object matching the schema declared in the user message.
You do not produce prose, markdown, code fences, or commentary.
If the inputs are insufficient, produce the JSON with `open_questions` populated where the schema permits, never refuse.
You will be evaluated by automated validators. Failed validations will cause your output to be discarded; a fresh agent will be asked to retry with your validator failures as guidance.
```

## User message template

The template uses `{{...}}` placeholders. The executor substitutes them from the PhaseRun context. **Section order is fixed.** Empty sections are still emitted with their header so the model sees a consistent structure.

```
=== TASK ===
{{task_summary}}

=== PHASE ===
phase_id: {{phase_id}}
goal: {{phase_goal}}

=== OUTPUT CONTRACT ===
You must produce a JSON object whose root field `body` matches schema `{{schema_id}}`.
The schema body is:
{{schema_body_json}}

The schema's semantic rules (you will be judged on these):
{{schema_semantic_rules}}

=== INPUTS ===
The following artifacts from prior phases are provided verbatim. Do not summarize them; treat them as ground truth.

{{#each inputs}}
--- input[{{@index}}]: {{this.schema_id}} (from phase `{{this.phase_id}}`) ---
{{this.body_json}}

{{/each}}

=== INSTRUCTIONS ===
{{phase_instructions}}

=== AMENDMENT NOTES ===
{{#if amendment_notes}}
Your previous attempt was rejected by validators. Their messages, in full:
{{#each amendment_notes}}
- [{{this.validator}}] {{this.message}}
{{/each}}

You are NOT being shown your previous output. Produce a NEW response from scratch that satisfies the schema AND addresses every failure above.
{{else}}
(none — this is the first attempt)
{{/if}}

=== RESPONSE FORMAT ===
Respond with a single JSON object on one line or pretty-printed, no surrounding text:
{ "body": { ...matches schema {{schema_id}}... } }
```

## Substitution rules

| Placeholder | Source |
|---|---|
| `{{task_summary}}` | `Run.task.summary` (the benchmark task brief, ≤500 chars) |
| `{{phase_id}}` | `methodology.phases[i].id` |
| `{{phase_goal}}` | `methodology.phases[i].goal` |
| `{{schema_id}}` | `methodology.phases[i].output_schema` (e.g. `requirements/v1`) |
| `{{schema_body_json}}` | The schema's `body` definition, pretty-printed JSON Schema |
| `{{schema_semantic_rules}}` | The bullet list from the corresponding section of `artifact-schemas.md` |
| `{{inputs}}` | Resolved input artifacts, in the order declared in `methodology.phases[i].inputs` |
| `{{phase_instructions}}` | `methodology.phases[i].instructions` (verbatim, no preprocessing) |
| `{{amendment_notes}}` | Concatenation of all `validator_failures` from the previous Attempt; empty on attempt 1 |

## Frozen LLM call parameters
| Param | Value | Rationale |
|---|---|---|
| Model (generator) | `claude-sonnet-4.5` | Strong-tier baseline; same model used for control. |
| Model (semantic judge) | `claude-haiku-4.5` | Cheap-tier judge keeps validator cost ≤ generator cost. |
| Temperature | `0.2` | Lower than default; we want consistency, not creativity, for an architecture experiment. |
| Max tokens | `8192` | Enough for the largest expected artifact (implementation files). |
| `response_format` | JSON object | If supported by SDK; else rely on system message contract + structural validator. |
| Timeout | `120s` per call | |
| Retries on infra error | `2` (HTTP-level) | Distinct from Attempt-level retries. Network blips don't count against retry budget. |

## What the envelope deliberately does NOT include
- **No "you are an expert at X" persona priming.** The phase is the persona.
- **No few-shot examples.** Examples would leak shape information that the schema already declares; a few-shot approach is a separate experiment.
- **No prior attempt output.** Only validator failures. (See state-machine.md §"Amendment vs. retry".)
- **No methodology overview.** The phase only knows its own goal and inputs. This is the load-bearing claim of the architecture; we test it by enforcing it.
- **No tool definitions.** No tool use in the spike.

## Reproducibility note
Render the final user-message string verbatim into `attempt.prompt.txt` in the storage layout. This file is the audit record. Reviewers (and future us) can replay the exact LLM call.
