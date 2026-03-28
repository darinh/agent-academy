# 008 — Agent Memory System

## Purpose
Defines a persistent per-agent knowledge store that survives across sessions. Agents can record lessons, decisions, patterns, and risks — then recall them when working on related tasks.

> **Status: Planned** — Design specification. No implementation exists.

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
- Max 50 memories per agent (oldest auto-evicted when exceeded, with warning)
- Max 500 characters per value
- Categories are validated against the allowed set

## Isolation

- Agents can only read/write their own memories
- No cross-agent memory access (prevents information leakage between roles)
- Human can view all agent memories via API for debugging
- `FORGET` requires a confirmation step (logged as audit event)

## Invariants

- Memory keys are unique per agent
- REMEMBER with an existing key updates the value (upsert semantics)
- All memory operations are audit-logged
- Memory content is never included in other agents' prompts

## Known Gaps

- **Memory search**: `RECALL` with `query` implies full-text search. Need to decide: exact key match only, LIKE patterns, or FTS5?
- **Memory import/export**: No bulk operations defined. Should agents be able to seed memories from a file?
- **Memory decay**: No TTL or staleness detection. Old memories may become incorrect as the codebase evolves.
- **Cross-agent knowledge sharing**: Intentionally prohibited for isolation. But some knowledge (like "the build command is X") is universal. Consider a `shared` category visible to all?

## Revision History

| Date | Change | Task |
|------|--------|------|
| 2026-03-28 | Initial spec from agent team feature request v3 | agent-command-system |
