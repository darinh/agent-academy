# 008 — Agent Memory System

## Purpose
Defines a persistent per-agent knowledge store that survives across sessions. Agents can record lessons, decisions, patterns, and risks — then recall them when working on related tasks.

> **Status: Implemented** — Per-agent memory store with REMEMBER (upsert), RECALL (LIKE search), LIST_MEMORIES, and FORGET commands. No memory cap. Memories injected into agent prompts as `=== YOUR MEMORIES ===` section. Cross-agent sharing via `shared` category — memories with `category=shared` are visible to all agents in RECALL, LIST_MEMORIES, and prompt injection (shown in `=== SHARED KNOWLEDGE ===` section). FORGET still scoped to own memories only.

## Motivation
Agents currently lose all learned context between orchestrator rounds and server restarts. Patterns discovered during code review, architectural decisions made during planning, and gotchas encountered during implementation are lost unless manually documented in specs or conversation.

The memory system gives agents a structured way to persist and retrieve knowledge without relying on conversation history or spec documents.

## Commands

| Command | Args | Returns | Auth |
|---------|------|---------|------|
| `REMEMBER` | `category`, `key`, `value` | Confirmation | Own memories only |
| `RECALL` | `category?`, `key?`, `query?` | Matching memories | Own memories only |
| `LIST_MEMORIES` | `category?` | All memories (filtered) | Own memories only |
| `FORGET` | `key` | Confirmation (with confirmation step) | Own memories only |

### Syntax in Agent Responses

```
REMEMBER:
  Category: pattern
  Key: ef-core-include
  Value: EF Core requires explicit Include() for navigation properties. Lazy loading is disabled.

RECALL: category=gotcha

FORGET: key=outdated-build-command
```

## Data Model

### Entity

```csharp
public class AgentMemoryEntity
{
    public string AgentId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
```

**Primary key**: `(AgentId, Key)` — each agent has a unique namespace.

### Shared Model

```csharp
public record AgentMemory(
    string AgentId,
    string Category,
    string Key,
    string Value,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);
```

### Categories

| Category | Description | Primary Users |
|----------|-------------|---------------|
| `decision` | Architectural or process decisions with rationale | Planner, Architect |
| `lesson` | Something learned from experience | All |
| `pattern` | Code or design patterns observed in the codebase | Architect, Engineer |
| `preference` | User or project preferences | All |
| `invariant` | Rules that must always hold | Reviewer |
| `risk` | Known risks or vulnerabilities | Reviewer, Planner |
| `gotcha` | Surprising behavior or non-obvious constraints | Engineer |
| `incident` | Past mistakes to avoid repeating | All |
| `constraint` | Hard limits on what agents can/cannot do | All |
| `finding` | Code review findings worth remembering | Reviewer |
| `spec-drift` | Cases where spec and code diverged | TechnicalWriter |
| `mapping` | How entities/models map to each other | Architect |
| `verification` | What to check and how | Reviewer |
| `gap-pattern` | Recurring gaps in the codebase | All |
| `shared` | Universal knowledge visible to all agents (cross-agent) | All |

## Context Integration

### Prompt Injection
When building an agent's conversation or breakout prompt, the orchestrator loads the agent's memories and includes them:

```
=== YOUR MEMORIES ===
[pattern] ef-core-include: EF Core requires explicit Include() for navigation properties.
[gotcha] griffel-pseudo: Griffel doesn't support ::after with border properties.
[decision] sqlite-choice: We chose SQLite over Postgres for single-user simplicity.
```

### Memory Limits
- Max 500 characters per value
- Categories are validated against the allowed set

## Isolation

- Agents can only read/write their own memories for all categories except `shared`
- Memories with `category=shared` are visible to all agents in RECALL, LIST_MEMORIES, and prompt injection
- Shared memories are stored under the creating agent's `(AgentId, Key)` — no special namespace
- FORGET only deletes the calling agent's own memories (including shared ones they created)
- Human can view all agent memories via API for debugging
- `FORGET` requires a confirmation step (logged as audit event)

### Prompt Injection for Shared Knowledge

When building prompts, shared memories from other agents appear in a separate section:

```
=== YOUR MEMORIES ===
[pattern] ef-core-include: EF Core requires explicit Include() for navigation properties.

=== SHARED KNOWLEDGE ===
[shared] build-command: dotnet build AgentAcademy.sln (from: architect-1)
[shared] db-choice: We chose SQLite for single-user simplicity (from: planner-1)
```

Own shared memories (created by the same agent) appear in `=== YOUR MEMORIES ===` with category `[shared]`.

## Invariants

- Memory keys are unique per agent
- REMEMBER with an existing key updates the value (upsert semantics)
- All memory operations are audit-logged
- Non-shared memory content is never included in other agents' prompts
- Shared memory content is included in all agents' prompts (in a `=== SHARED KNOWLEDGE ===` section)

## Known Gaps

- ~~**Memory search**: `RECALL` with `query` implies full-text search. Need to decide: exact key match only, LIKE patterns, or FTS5?~~ **Resolved** — FTS5 with BM25 ranking, LIKE fallback.
- **Memory import/export**: No bulk operations defined. Should agents be able to seed memories from a file?
- **Memory decay**: No TTL or staleness detection. Old memories may become incorrect as the codebase evolves.
- ~~**Cross-agent knowledge sharing**: Intentionally prohibited for isolation. But some knowledge (like "the build command is X") is universal. Consider a `shared` category visible to all?~~ **Resolved** — `shared` category added. Memories with `category=shared` are visible to all agents in RECALL, LIST_MEMORIES, and prompt injection. FORGET scoped to own memories.

## Search Implementation

### FTS5 Full-Text Search (Implemented)

`RECALL` with `query` uses SQLite FTS5 for word-boundary matching and BM25 relevance ranking:

- **FTS5 virtual table**: `agent_memories_fts` (external content table, synced via triggers)
- **Sync triggers**: `agent_memories_ai` (INSERT), `agent_memories_ad` (DELETE), `agent_memories_au` (UPDATE)
- **Query building**: Each search term is individually quoted to escape FTS5 special characters. Multi-word queries use implicit AND semantics.
- **Ranking**: Results ordered by `bm25(agent_memories_fts)` — most relevant first.
- **Agent isolation**: FTS5 query is joined with `AgentId` filter on the main table.
- **Fallback**: If FTS5 table is unavailable (e.g., migration not applied), silently falls back to LIKE-based search.
- **Filters**: `category` and `key` filters work alongside FTS5 query (applied as additional WHERE clauses on the main table).

### Search behavior comparison

| Feature | LIKE (old) | FTS5 (current) |
|---------|-----------|----------------|
| Word boundaries | No — "core" matches "score" | Yes — tokenized matching |
| Ranking | Alphabetical | BM25 relevance |
| Multi-word queries | Substring of concatenated text | AND of individual terms |
| Special characters | Literal match | Escaped and quoted |
| Performance | O(n) scan | Inverted index |

## Revision History

| Date | Change | Task |
|------|--------|------|
| 2026-03-28 | Initial spec from agent team feature request v3 | agent-command-system |
| 2026-03-28 | Implemented: REMEMBER, RECALL, LIST_MEMORIES, FORGET. Removed 50-memory cap. LIKE search for RECALL. | command-system-phase1 |
| 2026-04-04 | Upgraded RECALL search from LIKE to FTS5 with BM25 ranking. Added fallback, triggers, migration. Known gap resolved. | fts5-memory-search |
| 2026-04-04 | Added `shared` category for cross-agent knowledge sharing. Shared memories visible to all agents in RECALL, LIST_MEMORIES, and prompt injection. Known gap resolved. | shared-memory-category |
