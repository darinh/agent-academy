---
name: code-intelligence
description: Use when exploring codebase structure, finding callers/callees, understanding relationships between files or classes, checking what code depends on what, or assessing the blast radius of a change. Prefer this over grep, glob, or file reads for structural questions. Use before planning or implementing any Medium or Large task.
allowed-tools: mcp__codebase-memory-mcp__search_graph, mcp__codebase-memory-mcp__trace_call_path, mcp__codebase-memory-mcp__get_architecture, mcp__codebase-memory-mcp__detect_changes, mcp__codebase-memory-mcp__query_graph, mcp__codebase-memory-mcp__search_code, mcp__codebase-memory-mcp__get_code_snippet
---

# Code Intelligence Tools

The `codebase-memory-mcp` server provides structural code intelligence via a
knowledge graph built from the codebase. The project is indexed as
`home-darin-projects-agent-academy`.

**Never use grep/glob/file reads for structural questions** when graph queries can
answer it — graph queries return precise results in a single tool call (~500 tokens)
vs file-by-file exploration (~80K tokens).

## Available Tools

| Tool | Use For |
|------|---------|
| `search_graph` | Find functions, classes, routes, variables by name/pattern |
| `trace_call_path` | Who calls a function and what it calls (inbound/outbound) |
| `get_architecture` | High-level package/service/dependency overview |
| `detect_changes` | Map git diff to affected symbols and blast radius |
| `query_graph` | Custom Cypher queries for complex multi-hop patterns |
| `search_code` | Graph-augmented grep — finds text, ranks by structural importance |
| `get_code_snippet` | Read source for a specific symbol (use after search_graph) |

## Common Workflows

**Before planning a Medium/Large task (Anvil integration):**
1. `detect_changes` — what's already in flight in the git diff
2. `trace_call_path` on symbols you plan to change — blast radius
3. `search_graph` with `label: "Interface"` if changing an interface — find implementations
4. Include results in the evidence bundle before the Plan step

**"What calls X?"**
→ `trace_call_path(function_name="X", direction="inbound")`

**"What does X call?"**
→ `trace_call_path(function_name="X", direction="outbound")`

**"What does this git diff affect?"**
→ `detect_changes(project="home-darin-projects-agent-academy")`

**"Find all classes/functions matching a pattern"**
→ `search_graph(project="home-darin-projects-agent-academy", name_pattern=".*Handler.*")`

**"What's the overall architecture?"**
→ `get_architecture(project="home-darin-projects-agent-academy")`

**"Which files tend to change together?"**
→ `query_graph` with `FILE_CHANGES_WITH` edges

## Key Constraint

All calls require `project: "home-darin-projects-agent-academy"`.
