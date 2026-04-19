# Forge Pipeline Engine — Spike Design

**Status:** Design frozen for spike (do not edit during execution — that's the experiment).
**Owners:** Archimedes (architecture), Hephaestus (implementation).
**Goal:** Empirically test that **context isolation by construction** prevents premature descent and context pollution, vs. a single strong-tier agent control.

## What this spike is
A minimal pipeline that runs a hardcoded 5-layer methodology
(`requirements → contract → function_design → implement → verify`) end-to-end,
producing immutable, content-addressed artifacts at each layer, with a fresh
LLM session per phase and a validator pipeline gating each artifact.

## What this spike is NOT
- No methodology generator (methodology is hardcoded in JSON).
- No human gates / no UI / no SignalR integration / no DB.
- No fan-out, no parallel reviews, single attempt per phase by default
  (with retry/amend on validation failure, capped).
- No external code execution (validators are static + LLM-judge only).
- No `extract` phase primitive (deferred — would confound the experiment).

## Document index
| Doc | What it locks down |
|---|---|
| [state-machine.md](state-machine.md) | Run / PhaseRun / Attempt lifecycle, transitions, retry/amend rules, terminal states |
| [artifact-schemas.md](artifact-schemas.md) | JSON Schema for every artifact type the spike emits |
| [prompt-envelope.md](prompt-envelope.md) | The exact §6.2 prompt envelope template, frozen for the spike |
| [storage-layout.md](storage-layout.md) | On-disk directory structure, file naming, hashing rules |
| [methodology.json](methodology.json) | The single hardcoded 5-layer methodology used for all 3 benchmark tasks |

## Success criterion
Per agreed scope (memory `forge-spike-scope-2026-04-19`):
> Pipeline beats single strong-tier agent on at least one task it fails,
> OR produces higher-quality output via blind adversarial review,
> at acceptable cost multiplier (≤ 5×).

Decision authority on WIN/LOSS: Socrates, against rubric pre-committed by Thucydides.
