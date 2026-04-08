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
| [005](./005-workspace-runtime/spec.md) | Workspace Runtime | Implemented |
| [006](./006-orchestrator/spec.md) | Agent Orchestrator | Implemented |
| [007](./007-agent-commands/spec.md) | Agent Command System | Implemented |
| [008](./008-agent-memory/spec.md) | Agent Memory System | Implemented |
| [009](./009-spec-management/spec.md) | Spec Management | Implemented |
| [010](./010-task-management/spec.md) | Task Management & Git Workflow | Implemented |
| [011](./011-state-recovery/spec.md) | State Recovery and Supervised Restart | Implemented |
| [012](./012-consultant-api/spec.md) | Consultant API | Implemented |
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
