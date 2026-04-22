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
| [014](./014-database-schema/spec.md) | Database Schema | Implemented |
| [015](./015-security-model/spec.md) | Security Model | Implemented |
| [016](./016-api-reference/spec.md) | API Reference | Implemented |
| [017](./017-deployment/spec.md) | Deployment & Operations | Implemented |
| [018](./018-testing-strategy/spec.md) | Testing Strategy | Implemented |
| [019](./019-forge-engine/spec.md) | Forge Pipeline Engine | Implemented |
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

**Last Survey**: 2026-04-22

### Summary
- **Total Sections**: 21 specification documents (~14,000+ lines)
- **Coverage Score**: 95/100 — Excellent
- **All Features**: Documented with implementation status
- **Backend Coverage**: 90% (domain model, commands, agents, tasks exhaustive)
- **Frontend Coverage**: 75% (component structure documented, interaction flows lighter)
- **Testing**: Documented (test pyramid, conventions, coverage tooling)

### Strengths
- ✅ Backend domain model and command system exhaustively documented
- ✅ Agent execution flow thoroughly specified end-to-end
- ✅ Sprint and task management fully documented with lifecycle diagrams
- ✅ Spec-first culture evident in every section

### Known Documentation Gaps
1. ~~**Database Schema**~~ — ✅ Resolved in [014 — Database Schema](./014-database-schema/spec.md)
2. ~~**API Reference**~~ — ✅ Resolved in [016 — API Reference](./016-api-reference/spec.md)
3. ~~**Security Model**~~ — ✅ Resolved in [015 — Security Model](./015-security-model/spec.md)
4. ~~**Deployment**~~ — ✅ Resolved in [017 — Deployment & Operations](./017-deployment/spec.md)
### Planned Sections (Future)
- ~~`014-database-schema`~~ — ✅ Written
- ~~`015-security-model`~~ — ✅ Written
- ~~`016-api-reference`~~ — ✅ Written
- ~~`017-deployment`~~ — ✅ Written
