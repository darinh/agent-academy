# Agent Academy — Specifications

This directory is the **single source of truth** for Agent Academy. Every claim here must be verifiable against actual code.

## Spec Index

| Section | Title | Status |
|---------|-------|--------|
| [000](./000-system-overview/spec.md) | System Overview | Implemented |
| [001](./001-domain-model/spec.md) | Domain Model | Implemented |
| [002](./002-development-workflow/spec.md) | Development Workflow | Implemented |
| [003](./003-agent-system/spec.md) | Agent Execution System | Implemented |
| [004](./004-notification-system/spec.md) | Notification System | Implemented |
| [005](./005-workspace-runtime/spec.md) | Domain Services Layer | Implemented |
| [006](./006-orchestrator/spec.md) | Agent Orchestrator | Implemented |
| [007](./007-agent-commands/spec.md) | Agent Command System | Implemented |
| [008](./008-agent-memory/spec.md) | Agent Memory System | Implemented |
| [009](./009-spec-management/spec.md) | Spec Management | Implemented |
| [010](./010-task-management/spec.md) | Task Management & Git Workflow | Implemented |
| [011](./011-state-recovery/spec.md) | State Recovery and Supervised Restart | Implemented |
| [012](./012-consultant-api/spec.md) | Consultant API | Implemented |
| [013](./013-sprint-system/spec.md) | Sprint System | Implemented |
| [300](./300-frontend-ui/spec.md) | Frontend UI | Implemented |

## Conventions

### Document Structure
Each spec section lives in its own numbered directory and follows a standard template:

- **Purpose**: What this section covers
- **Current Behavior**: Verified description (or "Planned" for unimplemented features)
- **Interfaces & Contracts**: Types, APIs, data shapes
- **Invariants**: Rules that must always hold
- **Known Gaps**: Where implementation is incomplete
- **Revision History**: Changes linked to tasks

### Numbering
- `000–099`: Architecture and system-level concerns
- `100–199`: Domain model and business logic
- `200–299`: API and integration layer
- `300–399`: Frontend and UI
- `400–499`: Infrastructure and DevOps

### Status Values
- **Planned**: Spec written, code not yet implemented
- **Implemented**: Code exists and matches the spec
- **Outdated**: Code has diverged from the spec — needs reconciliation

### Verification
After every code change:
1. Run the affected spec sections against the codebase
2. Verify file paths, type names, function signatures
3. Update status from "Planned" to "Implemented" when code lands
4. Log changes in [CHANGELOG.md](./CHANGELOG.md)

## Coverage Status

**Last Survey**: 2026-04-14

### Summary
- **Total Sections**: 15 specification documents (~7,100+ lines)
- **Coverage Score**: 85/100 — Excellent
- **All Features**: Documented with implementation status
- **Backend Coverage**: 90% (domain model, commands, agents, tasks exhaustive)
- **Frontend Coverage**: 75% (component structure documented, interaction flows lighter)

### Strengths
- ✅ Backend domain model and command system exhaustively documented
- ✅ Agent execution flow thoroughly specified end-to-end
- ✅ Sprint and task management fully documented with lifecycle diagrams
- ✅ Spec-first culture evident in every section

### Known Documentation Gaps
1. **Database Schema** — No consolidated entity relationship diagram (scattered across 39+ entity files)
2. **API Reference** — 80+ REST endpoints documented per-feature but no unified catalog
3. **Security Model** — Threat model and permission system implicit, not consolidated
4. **Deployment** — Production deployment process is tribal knowledge
5. **Testing Strategy** — 4,400+ tests exist but conventions undocumented

### Planned Sections (Future)
- `014-database-schema` — Entity relationships, migrations, index strategy
- `015-security-model` — Threat model, permission consolidation, OAuth security
- `016-api-reference` — Unified REST endpoint catalog
- `017-deployment` — Production deployment guide
- `018-testing-strategy` — Test pyramid, coverage targets, conventions
