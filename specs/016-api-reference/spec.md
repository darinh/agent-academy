# 016 — API Reference

## Purpose

Unified catalog of all REST endpoints, SSE streams, and the SignalR hub exposed by Agent Academy Server. Organized by functional domain for discoverability. For detailed behavioral contracts, see the domain-specific specs referenced in each section.

## Current Behavior

> **Status: Implemented** — 145 endpoints across 26 controllers, 3 SSE streams, 1 SignalR hub.

## Authentication

All endpoints (except `/`, `/healthz`, and `/api/auth/*`) require one of:

| Method | Header / Mechanism | Identity |
|--------|--------------------|----------|
| GitHub OAuth | Cookie (`aspnetcore.auth`) | Authenticated GitHub user |
| Consultant Key | `X-Consultant-Key: {secret}` | `SenderKind = User`, `SenderId = "consultant"` |

If `ConsultantApi:SharedSecret` is not configured, the consultant auth scheme is not registered. See [012 — Consultant API](../012-consultant-api/spec.md) for details.

## Conventions

- All routes are prefixed with `/api/` unless noted otherwise.
- Route parameters use `{param}` syntax.
- Query parameters are optional unless marked **(required)**.
- Error responses use `ProblemDetails` format (RFC 7807).
- List endpoints support pagination via `limit`/`offset` or cursor-based `after` query parameters — see [Pagination](#pagination) below.
- SSE streams use `text/event-stream` content type with `data:` JSON payloads.

### Error Response Format

All error responses use [RFC 7807 ProblemDetails](https://www.rfc-editor.org/rfc/rfc7807). The response body is:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Bad Request",
  "status": 400,
  "detail": "Human-readable explanation of what went wrong.",
  "code": "machine_readable_code"
}
```

| Field | Type | Always Present | Description |
|-------|------|----------------|-------------|
| `type` | string | Yes | URI identifying the error type |
| `title` | string | Yes | Short HTTP status phrase |
| `status` | integer | Yes | HTTP status code |
| `detail` | string | Yes | Specific error message |
| `code` | string | No | Machine-readable error code (in `extensions`) |

**Backend**: All controllers use the `ApiProblem` factory (`Controllers/ApiProblem.cs`) which produces `ProblemDetails` instances with an optional `code` extension. Controllers return these via `BadRequest(pd)`, `NotFound(pd)`, `Conflict(pd)`, etc.

**Frontend**: The `extractApiError()` helper in `api/core.ts` reads `body.detail ?? body.error ?? body.title` to extract messages, supporting both ProblemDetails and any legacy format.

### Rate Limiting

Three independent rate-limiting mechanisms protect the server. None share state.

#### 1. Consultant API Rate Limiting (HTTP-level)

Applies to all requests authenticated via `X-Consultant-Key`. Non-consultant requests are not rate-limited at the HTTP layer.

| Property | Read (`GET`) | Write (`POST`/`PUT`/`DELETE`/`PATCH`) |
|----------|-------------|---------------------------------------|
| **Permit limit** | 60 | 20 |
| **Window** | 60 seconds (sliding) | 60 seconds (sliding) |
| **Segments per window** | 6 | 6 |
| **Scope** | Global (shared across all consultant requests) | Global |

**Configuration**: `ConsultantApi:RateLimiting` in `appsettings.json`. Set `Enabled: false` to disable.

**When exceeded**: HTTP `429 Too Many Requests` with:
- `Retry-After` header (seconds until next permit)
- `application/problem+json` body:

```json
{
  "type": "https://tools.ietf.org/html/rfc6585#section-4",
  "title": "Rate limit exceeded",
  "status": 429,
  "detail": "Too many requests. Try again in N seconds."
}
```

> **Note**: No `X-RateLimit-Remaining` or `X-RateLimit-Limit` headers are returned. Clients should rely on `Retry-After` for backoff.

**Backend**: `ConsultantRateLimitExtensions.cs` registers a `PartitionedRateLimiter<HttpContext>` with two sliding-window partitions (`consultant-read`, `consultant-write`). Enabled only when consultant auth is configured and `RateLimiting.Enabled` is true.

#### 2. Command Rate Limiting (per-agent, internal)

Applies to agent command execution within the command pipeline. Not an HTTP-level limiter — commands are denied internally before execution.

| Property | Default |
|----------|---------|
| **Max commands** | 30 |
| **Window** | 60 seconds |
| **Scope** | Per-agent (keyed by `agentId`) |

**Configuration**: Adjustable at runtime via system settings:
- `commands.rateLimitMaxCommands` (default: 30)
- `commands.rateLimitWindowSeconds` (default: 60)

Changes take effect immediately — `SettingsController.UpsertSettings` and `WebApplicationExtensions.InitializeAsync` both call `CommandRateLimiter.Configure()` on update.

**When exceeded**: The command pipeline returns a denied `CommandEnvelope` with `Status = Denied`, `ErrorCode = RateLimit`. The denial is audited and injected into the agent's context. No HTTP error is returned to the caller — the agent receives the denial as part of its conversation flow.

**Backend**: `CommandRateLimiter.cs` implements an in-memory sliding-window counter per agent. `CommandPipeline.cs` checks the limiter before executing any command.

#### 3. Agent Quotas (per-agent, hourly)

Per-agent resource quotas that limit requests, tokens, and cost within a rolling hour window.

| Quota | Default | Window |
|-------|---------|--------|
| `MaxRequestsPerHour` | null (unlimited) | 1 hour |
| `MaxTokensPerHour` | null (unlimited) | 1 hour |
| `MaxCostPerHour` | null (unlimited) | 1 hour |

**Configuration**: Per-agent via `PUT /api/agents/{agentId}/quota` (stored in `AgentConfigs` table, cached 30 seconds).

**Enforcement**:
- Request count: authoritative in-memory sliding window
- Tokens and cost: best-effort DB aggregation over the last hour

**When exceeded**: `AgentQuotaExceededException` is thrown with `RetryAfterSeconds`. The executor catches it and transitions the agent to a "temporarily paused" state. No HTTP 429 is returned — the quota violation surfaces as a paused-agent message in the room.

**Backend**: `AgentQuotaService.cs` tracks quotas. `CopilotExecutor.cs`, `AgentTurnRunner.cs`, and `BreakoutCompletionService.cs` enforce them at the execution boundary.

### Pagination

List endpoints use one of three pagination styles. The style is fixed per endpoint.

#### Style 1: Cursor-based (`after`)

Used for chronologically-ordered streams where total count is unknown or expensive.

| Parameter | Type | Default | Max | Description |
|-----------|------|---------|-----|-------------|
| `after` | string | — | — | Message ID cursor; returns items after this ID |
| `limit` | int | varies | varies | Maximum items to return |

Response includes `hasMore: true/false` to indicate additional pages. No total count.

**Endpoints using cursor-based pagination:**

| Endpoint | Default `limit` | Max `limit` |
|----------|-----------------|-------------|
| `GET /api/rooms/{roomId}/messages` | 50 | 200 |
| `GET /api/rooms/{roomId}/messages/stream` (SSE) | 200 (fixed replay cap) | 200 |
| `GET /api/dm/threads/{agentId}/stream` (SSE) | 200 (fixed replay cap) | 200 |

#### Style 2: Limit/offset with total count

Used for bounded collections where clients need page navigation.

| Parameter | Type | Default | Max | Description |
|-----------|------|---------|-----|-------------|
| `limit` | int | varies | varies | Maximum items to return |
| `offset` | int | 0 | — | Number of items to skip |

Response includes `totalCount` (or `total`) for page calculation.

**Endpoints using limit/offset with total count:**

| Endpoint | Default `limit` | Max `limit` |
|----------|-----------------|-------------|
| `GET /api/sessions` | 20 | 100 |
| `GET /api/rooms/{roomId}/sessions` | 20 | 100 |
| `GET /api/commands/audit` | 50 | 200 |
| `GET /api/system/restarts` | 20 | 100 |
| `GET /api/digests` | 20 | 100 |
| `GET /api/retrospectives` | 20 | 100 |
| `GET /api/sprints` | 20 | 100 |
| `GET /api/notifications/deliveries` | 50 | 200 |

> **Note**: `GET /api/notifications/deliveries` uses limit/offset but does not return a total count in the response.

#### Style 3: Limit-only (no offset, no cursor)

Used for "most recent N items" queries where paging through older results is not supported.

| Endpoint | Default `limit` | Max `limit` |
|----------|-----------------|-------------|
| `GET /api/rooms/{roomId}/artifacts` | 100 | 500 |
| `GET /api/rooms/{roomId}/usage/records` | 50 | 200 |
| `GET /api/rooms/{roomId}/errors` | 50 | 200 |
| `GET /api/usage/records` | 50 | 200 |
| `GET /api/errors/records` | 50 | 200 |
| `GET /api/specs/search` | 5 | 20 |
| `GET /api/search` | 25 per category | 100 per category |

#### Unpaginated endpoints

All other list endpoints return the full collection. These are bounded by design (e.g., rooms, agents, workspaces, templates) and are not expected to grow unboundedly.

---

## 1. System & Health

> Controller: `SystemController` — no route prefix

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/` | Service info and endpoint list | — | `200` JSON |
| GET | `/healthz` | Liveness probe (basic) | — | `200 "Healthy"` |
| GET | `/health` | Readiness probe (checks DB + executor) | — | `200` / `503` JSON |
| GET | `/api/health/instance` | Instance-level health and status | — | `InstanceHealth` |
| GET | `/api/overview` | Workspace overview (agents, rooms, tasks, sprint) | — | `WorkspaceOverview` |
| GET | `/api/agents/configured` | All configured agents (catalog + custom) | — | `List<AgentDefinition>` |
| GET | `/api/models` | Available LLM models and executor status | — | model list |
| POST | `/api/system/reload-catalog` | Trigger manual agent catalog reload | — | `200` / error |
| GET | `/api/system/restarts` | Restart history | `?limit` `?offset` | paginated list |
| GET | `/api/system/restarts/stats` | Restart statistics | `?hours` | stats object |

## 2. Authentication

> Controller: `AuthController` — route: `api/auth`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/auth/status` | Auth enabled/authenticated state, current user | — | auth status |
| GET | `/api/auth/login` | Start GitHub OAuth flow (redirect) | — | `302` redirect |
| POST | `/api/auth/logout` | Sign out and clear tokens | — | `200` |

## 3. Workspace Management

> Controller: `WorkspaceController` — route: `api`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/workspace` | Active workspace metadata | — | workspace object |
| GET | `/api/workspaces` | List known workspaces | — | `List<WorkspaceMeta>` |
| PUT | `/api/workspace` | Switch active workspace | body: `{ path }` | workspace object |
| POST | `/api/workspaces/scan` | Scan directory for project metadata | body: `{ path }` | `ProjectScanResult` |
| POST | `/api/workspaces/onboard` | Scan, upsert workspace, ensure room | body: `{ path }` | `OnboardResult` |

See [005 — Domain Services Layer](../005-workspace-runtime/spec.md) for workspace lifecycle.

## 4. Rooms

> Controller: `RoomController` — route: `api/rooms`

### 4.1 Room CRUD

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/rooms` | List rooms | `?includeArchived` | `List<RoomSnapshot>` |
| GET | `/api/rooms/{roomId}` | Get room details | — | `RoomSnapshot` |
| POST | `/api/rooms` | Create a room | body: `{ name, ... }` | `RoomSnapshot` |
| PUT | `/api/rooms/{roomId}/name` | Rename room | body: `{ name }` | `RoomSnapshot` |
| POST | `/api/rooms/cleanup` | Archive stale rooms (all tasks complete) | — | result |

### 4.2 Room Membership

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| POST | `/api/rooms/{roomId}/agents/{agentId}` | Add agent to room | — | `AgentLocation` |
| DELETE | `/api/rooms/{roomId}/agents/{agentId}` | Remove agent from room | — | `AgentLocation` |

### 4.3 Messages

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/rooms/{roomId}/messages` | Get room messages | `?after` `?limit` `?sessionId` | `RoomMessagesResponse` |
| POST | `/api/rooms/{roomId}/messages` | Post agent/system message | body: `PostMessageRequest` | `ChatEnvelope` |
| POST | `/api/rooms/{roomId}/human` | Post human message (triggers orchestrator) | body: `HumanMessageRequest` | `ChatEnvelope` |
| GET | `/api/rooms/{roomId}/messages/stream` | **SSE** — stream room messages | `?after` | `text/event-stream` |

> **SSE stream**: Replays missed messages since `after`, then streams new messages as `data:` JSON events. Connection stays open until client disconnects.

### 4.4 Room Operations

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| POST | `/api/rooms/{roomId}/phase` | Transition room phase | body: `PhaseTransitionRequest` | `RoomSnapshot` |
| POST | `/api/rooms/{roomId}/compact` | Compact room (invalidate agent sessions) | — | `200` |

### 4.5 Room Plan

> Controller: `PlanController`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/rooms/{roomId}/plan` | Get current room plan | — | `PlanContent` |
| PUT | `/api/rooms/{roomId}/plan` | Create or update plan | body: `PlanContent` | `200` |
| DELETE | `/api/rooms/{roomId}/plan` | Delete plan | — | `200` |

### 4.6 Room Analytics

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/rooms/{roomId}/usage` | Room token usage summary | — | `UsageSummary` |
| GET | `/api/rooms/{roomId}/usage/agents` | Per-agent usage breakdown | — | `List<AgentUsageSummary>` |
| GET | `/api/rooms/{roomId}/usage/records` | Recent usage records | `?agentId` `?limit` | `List<LlmUsageRecord>` |
| GET | `/api/rooms/{roomId}/errors` | Room error records | `?limit` | `List<ErrorRecord>` |
| GET | `/api/rooms/{roomId}/artifacts` | Room artifacts (placeholder) | — | `[]` |
| GET | `/api/rooms/{roomId}/evaluations` | Artifact quality evaluations | — | `{ artifacts: EvaluationResult[], aggregateScore: double }` |

### 4.7 Sessions

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/rooms/{roomId}/sessions` | List room sessions | `?status` `?limit` `?offset` | `SessionListResponse` |
| POST | `/api/rooms/{roomId}/sessions` | Create new conversation session | — | `ConversationSessionSnapshot` |

## 5. Direct Messages

> Controller: `DmController` — route: `api/dm`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/dm/threads` | List DM threads | — | `List<DmThreadSummary>` |
| GET | `/api/dm/threads/{agentId}` | Get thread messages | — | `List<DmMessage>` |
| POST | `/api/dm/threads/{agentId}` | Send DM to agent (triggers response) | body: `SendDmRequest` | `DmMessage` |
| GET | `/api/dm/threads/{agentId}/stream` | **SSE** — stream DM messages | `?after` | `text/event-stream` |
| GET | `/api/dm/threads/stream` | **SSE** — stream thread list updates | — | `text/event-stream` |

> **SSE streams**: The per-agent stream replays missed messages since `after`, then pushes new messages. The thread-list stream pushes invalidation events when any thread changes.

## 6. Agents

> Controllers: `AgentController`, `AgentConfigController` — route: `api/agents`

### 6.1 Agent State

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/agents/locations` | Current agent locations across rooms | — | `List<AgentLocation>` |
| PUT | `/api/agents/{agentId}/location` | Move agent to room/state | body: `UpdateLocationRequest` | `AgentLocation` |
| GET | `/api/agents/{agentId}/sessions` | Active and archived breakout sessions | — | `List<BreakoutRoom>` |
| POST | `/api/agents/{agentId}/run` | Execute a prompt against the agent | body: raw text | execution result |

### 6.2 Agent Knowledge

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/agents/{agentId}/knowledge` | Non-expired agent knowledge entries | — | knowledge list |
| POST | `/api/agents/{agentId}/knowledge` | Upsert knowledge entry | body: `AppendKnowledgeRequest` | knowledge entry |
| GET | `/api/knowledge` | Shared knowledge grouped by agent | — | grouped knowledge |

See [008 — Agent Memory System](../008-agent-memory/spec.md) for knowledge lifecycle.

### 6.3 Agent Quota

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/agents/{agentId}/quota` | Current quota status | — | `QuotaStatus` |
| PUT | `/api/agents/{agentId}/quota` | Update quota limits | body: `UpdateQuotaRequest` | `QuotaStatus` |
| DELETE | `/api/agents/{agentId}/quota` | Remove quota (unlimited) | — | `200` |

### 6.4 Agent Configuration

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/agents/{agentId}/config` | Effective config + stored override | — | `AgentConfigResponse` |
| PUT | `/api/agents/{agentId}/config` | Create/update config override | body: `UpsertAgentConfigRequest` | `AgentConfigResponse` |
| POST | `/api/agents/{agentId}/config/reset` | Reset to catalog defaults | — | `AgentConfigResponse` |
| POST | `/api/agents/custom` | Create custom agent from prompt | body: `CreateCustomAgentRequest` | `AgentDefinition` |
| DELETE | `/api/agents/custom/{agentId}` | Delete custom agent | — | `200` |

See [003 — Agent Execution System](../003-agent-system/spec.md) for agent lifecycle.

## 7. Tasks

> Controller: `CollaborationController` — various routes

### 7.1 Task CRUD

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/tasks` | List tasks | `?sprintId` | `List<TaskSnapshot>` |
| GET | `/api/tasks/{taskId}` | Get single task | — | `TaskSnapshot` |
| POST | `/api/tasks` | Submit task (creates + starts orchestration) | body: `TaskAssignmentRequest` | `TaskAssignmentResult` |

### 7.2 Task Lifecycle

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| PUT | `/api/tasks/{taskId}/assign` | Assign agent to task | body: `AssignTaskRequest` | `TaskSnapshot` |
| PUT | `/api/tasks/{taskId}/status` | Update task status | body: `UpdateTaskStatusRequest` | `TaskSnapshot` |
| PUT | `/api/tasks/{taskId}/priority` | Update task priority | body: `UpdateTaskPriorityRequest` | `TaskSnapshot` |
| PUT | `/api/tasks/{taskId}/branch` | Record branch name | body: `UpdateTaskBranchRequest` | `TaskSnapshot` |
| PUT | `/api/tasks/{taskId}/pr` | Record PR info | body: `UpdateTaskPrRequest` | `TaskSnapshot` |
| PUT | `/api/tasks/{taskId}/complete` | Mark task complete | body: `CompleteTaskRequest` | `TaskSnapshot` |

### 7.3 Task Dependencies

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/tasks/{taskId}/dependencies` | Get dependency info | — | `TaskDependencyInfo` |
| POST | `/api/tasks/{taskId}/dependencies` | Add dependency | body: `AddDependencyRequest` | `TaskDependencyInfo` |
| DELETE | `/api/tasks/{taskId}/dependencies/{dependsOnTaskId}` | Remove dependency | — | `TaskDependencyInfo` |

### 7.4 Task Metadata

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/tasks/{taskId}/comments` | Task comments | — | `List<TaskComment>` |
| GET | `/api/tasks/{taskId}/specs` | Spec links for task | — | `List<SpecTaskLink>` |

### 7.5 Bulk Operations

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| POST | `/api/tasks/bulk/status` | Bulk-update task statuses | body: `BulkUpdateStatusRequest` | `BulkOperationResult` |
| POST | `/api/tasks/bulk/assign` | Bulk-assign tasks to agent | body: `BulkAssignRequest` | `BulkOperationResult` |

See [010 — Task Management](../010-task-management/spec.md) for task lifecycle and git workflow.

## 8. Sprints

> Controller: `SprintController` — route: `api/sprints`

### 8.1 Sprint CRUD

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/sprints` | List sprints | `?limit` `?offset` | paginated list |
| GET | `/api/sprints/active` | Get active sprint | — | sprint or `204` |
| GET | `/api/sprints/{id}` | Get sprint with artifacts | — | sprint object |
| POST | `/api/sprints` | Start new sprint | — | sprint object |

### 8.2 Sprint Lifecycle

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| POST | `/api/sprints/{id}/advance` | Advance to next stage | `?force` | sprint object |
| POST | `/api/sprints/{id}/complete` | Complete sprint | `?force` | sprint object |
| POST | `/api/sprints/{id}/cancel` | Cancel sprint | — | sprint object |
| POST | `/api/sprints/{id}/approve-advance` | Approve pending stage advancement | — | sprint object |
| POST | `/api/sprints/{id}/reject-advance` | Reject pending stage advancement | — | sprint object |

### 8.3 Sprint Artifacts & Metrics

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/sprints/{id}/artifacts` | Sprint artifacts | `?stage` | artifact list |
| GET | `/api/sprints/{id}/metrics` | Sprint metrics | — | metrics object |
| GET | `/api/sprints/metrics/summary` | Workspace-level metrics summary | — | summary object |

### 8.4 Sprint Schedule

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/sprints/schedule` | Get sprint schedule | — | schedule object |
| PUT | `/api/sprints/schedule` | Create/update schedule | body: schedule config | schedule object |
| DELETE | `/api/sprints/schedule` | Delete schedule | — | `200` |

See [013 — Sprint System](../013-sprint-system/spec.md) for sprint lifecycle and stage gates.

## 9. Commands

> Controllers: `CommandController`, `CommandAuditController`

### 9.1 Command Execution

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/commands/metadata` | Allowlisted command catalog for human UI | — | command metadata |
| POST | `/api/commands/execute` | Execute a command (sync or async) | body: `ExecuteCommandRequest` | execution result |
| GET | `/api/commands/{correlationId}` | Get async command status | — | command status |

### 9.2 Command Audit

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/commands/audit` | Paginated audit log | `?agentId` `?command` `?status` `?hoursBack` `?limit` `?offset` | audit entries |
| GET | `/api/commands/audit/stats` | Aggregate audit statistics | `?hoursBack` | stats by status/agent/command |

See [007 — Agent Command System](../007-agent-commands/spec.md) for command definitions and permissions.

## 10. Specs

> Controller: `CollaborationController`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/specs/version` | Spec corpus version/hash/section count | — | version info |
| GET | `/api/specs/search` | Search spec sections by keyword | `?q` **(required)** `?limit` (1–20, default 5) | `List<SpecSearchResult>` |
| GET | `/api/specs/{sectionId}/tasks` | Tasks linked to a spec section | — | `List<SpecTaskLink>` |

See [009 — Spec Management](../009-spec-management/spec.md) for the spec system.

## 11. Analytics & Usage

> Controllers: `AnalyticsController`, `SystemController`

### 11.1 Agent Analytics

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/analytics/agents` | Per-agent performance metrics | `?hoursBack` | `AgentAnalyticsSummary` |
| GET | `/api/analytics/agents/{agentId}` | Detailed analytics for one agent | `?hoursBack` `?requestLimit` `?errorLimit` `?taskLimit` | `AgentAnalyticsDetail` |
| GET | `/api/analytics/tasks` | Task-cycle analytics | `?hoursBack` | `TaskCycleAnalytics` |

### 11.2 Global Usage

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/usage` | Global LLM usage summary | `?hoursBack` | `UsageSummary` |
| GET | `/api/usage/records` | Recent global usage records | `?agentId` `?limit` | `List<LlmUsageRecord>` |

### 11.3 Global Errors

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/errors` | Global error summary | `?hoursBack` | `ErrorSummary` |
| GET | `/api/errors/records` | Recent error records | `?agentId` `?hoursBack` `?limit` | `List<ErrorRecord>` |

## 12. Memory System

> Controller: `MemoryController` — route: `api/memories`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/memories/browse` | Browse memories with filters | `?agentId` `?category` `?search` `?includeExpired` | memory list |
| GET | `/api/memories/stats` | Per-category memory stats | `?agentId` | stats object |
| GET | `/api/memories/export` | Export memories as JSON | `?agentId` `?category` | JSON download |
| POST | `/api/memories/import` | Bulk import/upsert memories | body: `MemoryImportRequest` | import result |
| DELETE | `/api/memories` | Delete one memory entry | `?agentId` **(required)** `?key` **(required)** | `200` |
| DELETE | `/api/memories/expired` | Delete expired memories | `?agentId` | deletion count |

See [008 — Agent Memory System](../008-agent-memory/spec.md) for memory categories, expiry, and injection.

## 13. Retrospectives & Digests

> Controllers: `RetrospectiveController`, `DigestController`

### 13.1 Retrospectives

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/retrospectives` | List retrospectives | `?agentId` `?taskId` `?limit` `?offset` | paginated list |
| GET | `/api/retrospectives/{commentId}` | Get one retrospective with task metadata | — | retrospective |
| GET | `/api/retrospectives/stats` | Aggregate retrospective statistics | — | stats object |

### 13.2 Digests

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/digests` | List digests | `?status` `?limit` `?offset` | paginated list |
| GET | `/api/digests/{id}` | Get digest with source retrospectives | — | digest object |
| GET | `/api/digests/stats` | Aggregate digest statistics | — | stats object |

## 14. Sessions

> Controller: `SessionController` — route: `api/sessions`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/sessions` | List conversation sessions | `?status` `?limit` `?offset` `?hoursBack` `?workspace` | `SessionListResponse` |
| GET | `/api/sessions/stats` | Session statistics | `?hoursBack` `?workspace` | `SessionStats` |

## 15. Notifications

> Controller: `NotificationController` — route: `api/notifications`

### 15.1 Provider Management

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/notifications/providers` | List providers and status | — | `List<ProviderStatusDto>` |
| GET | `/api/notifications/providers/{id}/schema` | Provider config schema | — | `ProviderConfigSchema` |
| POST | `/api/notifications/providers/{id}/configure` | Apply provider configuration | body: `Dict<string,string>` | `200` |
| POST | `/api/notifications/providers/{id}/connect` | Connect provider | — | `200` |
| POST | `/api/notifications/providers/{id}/disconnect` | Disconnect provider | — | `200` |
| POST | `/api/notifications/test` | Send test notification to all connected | — | `200` |

### 15.2 Delivery History

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/notifications/deliveries` | Query delivery history | `?channel` `?providerId` `?status` `?roomId` `?limit` `?offset` | `List<NotificationDeliveryDto>` |
| GET | `/api/notifications/deliveries/stats` | Delivery statistics | `?hours` | stats by status |

See [004 — Notification System](../004-notification-system/spec.md) for provider architecture.

## 16. Instruction Templates

> Controller: `InstructionTemplateController` — route: `api/instruction-templates`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/instruction-templates` | List all templates | — | `List<InstructionTemplateResponse>` |
| GET | `/api/instruction-templates/{id}` | Get one template | — | `InstructionTemplateResponse` |
| POST | `/api/instruction-templates` | Create template | body: `InstructionTemplateRequest` | `InstructionTemplateResponse` |
| PUT | `/api/instruction-templates/{id}` | Update template | body: `InstructionTemplateRequest` | `InstructionTemplateResponse` |
| DELETE | `/api/instruction-templates/{id}` | Delete template | — | `200` |

## 17. Data Export

> Controller: `ExportController` — route: `api/export`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/export/agents` | Export agent analytics | `?hoursBack` `?format=csv\|json` | CSV or JSON download |
| GET | `/api/export/usage` | Export LLM usage records | `?hoursBack` `?agentId` `?limit` `?format=csv\|json` | CSV or JSON download |
| GET | `/api/export/rooms/{roomId}/messages` | Export room messages | `?format=json\|md` | JSON or Markdown download |
| GET | `/api/export/dm/{agentId}/messages` | Export DM thread | `?format=json\|md` | JSON or Markdown download |

## 18. Search

> Controller: `SearchController` — route: `api/search`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/search` | Search messages and tasks | `?q` **(required)** `?scope` `?messageLimit` `?taskLimit` | `SearchResults` |

## 19. Filesystem

> Controller: `FilesystemController`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/filesystem/browse` | Browse subdirectories | `?path` `?showHidden` | directory listing |

## 20. GitHub Integration

> Controller: `GitHubController`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/github/status` | GitHub integration/auth status | — | `GitHubStatusResponse` |

## 21. Settings

> Controller: `SettingsController` — route: `api/settings`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/settings` | All settings with defaults | — | `Dict<string,string>` |
| GET | `/api/settings/{key}` | One setting value | — | `SettingResponse` |
| PUT | `/api/settings` | Bulk upsert settings | body: settings map | `200` |

## 22. Worktrees

> Controller: `WorktreeController` — route: `api/worktrees`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/worktrees` | List active worktrees with git/task info | — | `List<WorktreeStatusSnapshot>` |

## 23. Activity & Real-Time

> Controller: `ActivityController` — route: `api/activity`
> Hub: `ActivityHub` — path: `/hubs/activity`

### 23.1 REST + SSE

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/activity/recent` | Recent activity events | — | `List<ActivityEvent>` |
| GET | `/api/activity/stream` | **SSE** — stream activity events | — | `text/event-stream` |

> **SSE stream**: Replays recent events on connect, then pushes new `ActivityEvent` payloads as they occur.

### 23.2 SignalR Hub

**Endpoint**: `ws://localhost:{port}/hubs/activity`

**Server → Client Events**:

| Event | Payload | Description |
|-------|---------|-------------|
| `activityEvent` | `ActivityEvent` | All activity events (messages, task changes, sprint transitions, errors, etc.) |

The hub is thin — broadcasting is handled by `ActivityHubBroadcaster` which wraps `IHubContext<ActivityHub>`. All server-side services emit `ActivityEvent` objects that are pushed to all connected clients.

**Client → Server**: No client-to-server methods. The hub is broadcast-only.

---

## Request/Response Schemas

Full property definitions for every request body and response type referenced in the endpoint tables above. Properties listed in declaration order. Types marked **(required)** have validation attributes enforcing non-null/non-empty values.

> **Serialization**: All enums serialize as `camelCase` strings (via `JsonStringEnumConverter`). All `DateTime` values are UTC ISO 8601.

### Enums Reference

Enums used across multiple schemas are listed here once. Each serializes as its string name.

| Enum | Values |
|------|--------|
| `CollaborationPhase` | `Intake`, `Planning`, `Discussion`, `Validation`, `Implementation`, `FinalSynthesis` |
| `RoomStatus` | `Idle`, `Active`, `AttentionRequired`, `Completed`, `Archived` |
| `TaskStatus` | `Queued`, `Active`, `Blocked`, `AwaitingValidation`, `InReview`, `ChangesRequested`, `Approved`, `Merging`, `Completed`, `Cancelled` |
| `TaskType` | `Feature`, `Bug`, `Chore`, `Spike` |
| `TaskPriority` | `Critical` (0), `High` (1), `Medium` (2), `Low` (3) |
| `TaskSize` | `XS`, `S`, `M`, `L`, `XL` |
| `PullRequestStatus` | `Open`, `ReviewRequested`, `ChangesRequested`, `Approved`, `Merged`, `Closed` |
| `WorkstreamStatus` | `NotStarted`, `Ready`, `InProgress`, `Blocked`, `Completed` |
| `MessageKind` | `System`, `TaskAssignment`, `Coordination`, `Plan`, `Status`, `Review`, `Validation`, `Decision`, `Question`, `Response`, `SpecChangeProposal`, `DirectMessage` |
| `MessageSenderKind` | `System`, `Agent`, `User` |
| `DeliveryPriority` | `Low`, `Normal`, `High`, `Urgent` |
| `AgentAvailability` | `Ready`, `Preferred`, `Active`, `Busy`, `Offline` |
| `AgentState` | `InRoom`, `Working`, `Presenting`, `Idle`, `Offline` |
| `CommandStatus` | `Success`, `Error`, `Denied` |
| `ActivityEventType` | `AgentLoaded`, `AgentCatalogReloaded`, `AgentThinking`, `AgentFinished`, `RoomCreated`, `RoomClosed`, `TaskCreated`, `PhaseChanged`, `MessagePosted`, `MessageSent`, `PresenceUpdated`, `RoomStatusChanged`, `ArtifactEvaluated`, `QualityGateChecked`, `IterationRetried`, `CheckpointCreated`, `AgentErrorOccurred`, `AgentWarningOccurred`, `SubagentStarted`, `SubagentCompleted`, `SubagentFailed`, `AgentPlanChanged`, `AgentSnapshotRewound`, `ToolIntercepted`, `CommandExecuted`, `CommandDenied`, `CommandFailed`, `TaskClaimed`, `TaskReleased`, `TaskApproved`, `TaskRejected`, `TaskChangesRequested`, `TaskStatusUpdated`, `TaskCommentAdded`, `TaskPrStatusChanged`, `AgentRecalled`, `RoomRenamed`, `DirectMessageSent`, `SpecTaskLinked`, `EvidenceRecorded`, `GateChecked`, `SprintStarted`, `SprintStageAdvanced`, `SprintArtifactStored`, `SprintCompleted`, `SprintCancelled`, `TaskUnblocked`, `TaskRetrospectiveCompleted`, `LearningDigestCompleted`, `ContextUsageUpdated` |
| `ActivitySeverity` | `Info`, `Warning`, `Error` |

### Rooms Domain

#### `RoomSnapshot`

Snapshot of a room's current state. Returned by most room mutation endpoints.

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `id` | string | No | Room ID |
| `name` | string | No | Display name |
| `topic` | string | Yes | Room topic/description |
| `status` | `RoomStatus` | No | Current lifecycle state |
| `currentPhase` | `CollaborationPhase` | No | Current collaboration phase |
| `activeTask` | `TaskSnapshot` | Yes | Task currently being worked in this room |
| `participants` | `AgentPresence[]` | No | Agents present in the room |
| `recentMessages` | `ChatEnvelope[]` | No | Recent message history |
| `createdAt` | datetime | No | Room creation timestamp |
| `updatedAt` | datetime | No | Last modification timestamp |
| `phaseGates` | `PhasePrerequisiteStatus` | Yes | Per-phase gate status for UI transition buttons |

#### `PhasePrerequisiteStatus`

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `gates` | `Dictionary<string, PhaseGate>` | No | Map of phase name → gate status |

#### `PhaseGate`

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `allowed` | boolean | No | Whether transition to this phase is permitted |
| `reason` | string | Yes | Human-readable explanation when not allowed |

#### `ChatEnvelope`

A message in a collaboration room.

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `id` | string | No | Message ID |
| `roomId` | string | No | Room the message belongs to |
| `senderId` | string | No | Sender's agent/user ID |
| `senderName` | string | No | Display name of sender |
| `senderRole` | string | Yes | Role of sender (e.g., "architect") |
| `senderKind` | `MessageSenderKind` | No | Origin category: System, Agent, or User |
| `kind` | `MessageKind` | No | Semantic message type |
| `content` | string | No | Message body text |
| `sentAt` | datetime | No | When the message was sent |
| `correlationId` | string | Yes | Links related messages across a conversation turn |
| `replyToMessageId` | string | Yes | ID of the message being replied to |
| `hint` | `DeliveryHint` | Yes | Routing hints for targeted delivery |

#### `DeliveryHint`

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `targetRole` | string | Yes | Route to agents with this role |
| `targetAgentId` | string | Yes | Route to a specific agent |
| `priority` | `DeliveryPriority` | No | Delivery priority level |
| `replyRequested` | boolean | No | Whether a reply is expected |

#### `RoomMessagesResponse`

Paginated room message list (cursor-based).

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `messages` | `ChatEnvelope[]` | No | Messages in the page |
| `hasMore` | boolean | No | Whether more messages exist after this page |

#### `PostMessageRequest`

Post an agent/system message to a room.

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `roomId` | string | Yes | max 100 chars | Target room ID |
| `senderId` | string | Yes | max 100 chars | Sender agent ID |
| `content` | string | Yes | 1–50,000 chars | Message body |
| `kind` | `MessageKind` | No | — | Default: `Response` |
| `correlationId` | string | No | — | Correlation ID for threading |
| `hint` | `DeliveryHint` | No | — | Delivery routing hints |

#### `HumanMessageRequest`

Post a human message to a room (triggers orchestrator).

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `content` | string | Yes | 1–50,000 chars | Message body |

#### `PhaseTransitionRequest`

Transition a room to a new collaboration phase.

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `roomId` | string | Yes | max 100 chars | Target room ID |
| `targetPhase` | `CollaborationPhase` | Yes | valid enum | Phase to transition to |
| `reason` | string | No | max 500 chars | Why the transition is happening |

#### `ConversationSessionSnapshot`

Snapshot of a conversation session (epoch) within a room.

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `id` | string | No | Session ID |
| `roomId` | string | No | Parent room ID |
| `roomType` | string | No | Room type identifier |
| `sequenceNumber` | integer | No | Monotonic session counter within the room |
| `status` | string | No | Session status (e.g., "Active", "Archived") |
| `summary` | string | Yes | LLM-generated summary (populated on archive) |
| `messageCount` | integer | No | Number of messages in this session |
| `createdAt` | datetime | No | Session start time |
| `archivedAt` | datetime | Yes | When the session was archived |
| `workspacePath` | string | Yes | Workspace path if scoped to a project |

#### `SessionListResponse`

Paginated session list with total count.

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `sessions` | `ConversationSessionSnapshot[]` | No | Sessions in the page |
| `totalCount` | integer | No | Total matching sessions for pagination |

### Direct Messages Domain

#### `SendDmRequest`

Send a direct message to an agent.

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `message` | string | Yes | 1–50,000 chars | Message content |

#### `DmMessage`

A direct message in a DM thread.

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `id` | string | No | Message ID |
| `senderId` | string | No | Sender ID |
| `senderName` | string | No | Display name |
| `senderRole` | string | Yes | Agent role |
| `content` | string | No | Message body |
| `sentAt` | datetime | No | When sent |
| `isFromHuman` | boolean | No | `true` if sent by a human, `false` if sent by agent |

### Agents Domain

#### `AgentDefinition`

Full definition of an agent loaded from the catalog.

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `id` | string | No | Unique agent identifier |
| `name` | string | No | Display name |
| `role` | string | No | Agent role (e.g., "architect", "developer") |
| `summary` | string | No | Short description of the agent's purpose |
| `startupPrompt` | string | No | System prompt used when the agent starts |
| `model` | string | Yes | LLM model identifier (null = workspace default) |
| `capabilityTags` | string[] | No | Tags describing agent capabilities |
| `enabledTools` | string[] | No | Tool names the agent can use |
| `autoJoinDefaultRoom` | boolean | No | Whether the agent joins the default room on load |
| `gitIdentity` | `AgentGitIdentity` | Yes | Git author name/email for commits |
| `permissions` | `CommandPermissionSet` | Yes | Allowed/denied command patterns |
| `quota` | `ResourceQuota` | Yes | Per-agent resource limits |

#### `AgentGitIdentity`

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `authorName` | string | No | Git commit author name |
| `authorEmail` | string | No | Git commit author email |

#### `CommandPermissionSet`

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `allowed` | string[] | No | Allowed command patterns (supports wildcards like `READ_*`) |
| `denied` | string[] | No | Denied command patterns (evaluated after allowed) |

#### `ResourceQuota`

Per-agent resource limits. Null values mean unlimited.

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `maxRequestsPerHour` | integer | Yes | Max LLM requests per hour |
| `maxTokensPerHour` | long | Yes | Max tokens per hour |
| `maxCostPerHour` | decimal | Yes | Max cost per hour (USD) |

#### `AgentPresence`

Real-time presence info for an agent in a room.

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `agentId` | string | No | Agent identifier |
| `name` | string | No | Display name |
| `role` | string | No | Agent role |
| `availability` | `AgentAvailability` | No | Current readiness state |
| `isPreferred` | boolean | No | Whether this agent is preferred for the current task |
| `lastActivityAt` | datetime | No | Last activity timestamp |
| `activeCapabilities` | string[] | No | Currently active capability tags |

#### `AgentLocation`

Agent's current position and state within the workspace.

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `agentId` | string | No | Agent identifier |
| `roomId` | string | No | Room the agent is in |
| `state` | `AgentState` | No | Current physical state |
| `breakoutRoomId` | string | Yes | Breakout room ID if in a breakout |
| `updatedAt` | datetime | No | Last state change timestamp |

#### `UpdateLocationRequest`

Move an agent to a room/state.

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `roomId` | string | Yes | max 100 chars | Target room ID |
| `state` | `AgentState` | Yes | valid enum | New agent state |
| `breakoutRoomId` | string | No | max 100 chars | Breakout room ID |

#### `AppendKnowledgeRequest`

Add a knowledge entry for an agent.

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `entry` | string | Yes | 1–10,000 chars | Knowledge text to append |

#### `UpdateQuotaRequest`

Update an agent's resource quotas. Null clears the limit (unlimited).

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `maxRequestsPerHour` | integer | No | 1–100,000 | Max LLM requests per hour |
| `maxTokensPerHour` | long | No | 1–100,000,000 | Max tokens per hour |
| `maxCostPerHour` | decimal | No | 0.01–10,000 | Max cost per hour (USD) |

#### `QuotaStatus`

Result of a quota check.

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `agentId` | string | No | Agent identifier |
| `isAllowed` | boolean | No | Whether the agent can proceed |
| `deniedReason` | string | Yes | Why the agent was denied |
| `retryAfterSeconds` | integer | Yes | Seconds to wait before retrying |
| `configuredQuota` | `ResourceQuota` | Yes | The agent's configured limits |
| `currentUsage` | `AgentUsageWindow` | Yes | Current usage within the quota window |

#### `AgentUsageWindow`

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `requestCount` | integer | No | Requests in current window |
| `totalTokens` | long | No | Tokens consumed in current window |
| `totalCost` | decimal | No | Cost in current window |

### Agent Configuration

#### `AgentConfigResponse`

Effective agent config with override details.

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `agentId` | string | No | Agent identifier |
| `effectiveModel` | string | No | Resolved model ID (override or catalog default) |
| `effectiveStartupPrompt` | string | No | Resolved system prompt |
| `hasOverride` | boolean | No | Whether a config override exists |
| `override` | `AgentConfigOverrideDto` | Yes | Raw override values if present |

#### `AgentConfigOverrideDto`

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `startupPromptOverride` | string | Yes | Custom system prompt |
| `modelOverride` | string | Yes | Custom model ID |
| `customInstructions` | string | Yes | Additional instructions appended to prompt |
| `instructionTemplateId` | string | Yes | Linked template ID |
| `instructionTemplateName` | string | Yes | Linked template display name |
| `updatedAt` | datetime | No | Last modification timestamp |

#### `UpsertAgentConfigRequest`

Create or update an agent config override. All fields nullable — `null` clears that override field.

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `startupPromptOverride` | string | No | max 100,000 chars | Custom system prompt |
| `modelOverride` | string | No | max 100 chars | Custom model ID |
| `customInstructions` | string | No | max 100,000 chars | Additional instructions |
| `instructionTemplateId` | string | No | max 100 chars | Link to instruction template |

#### `CreateCustomAgentRequest`

Create a custom agent from a prompt.

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `name` | string | Yes | max 100 chars | Agent display name |
| `prompt` | string | Yes | 1–100,000 chars | System prompt for the agent |
| `model` | string | No | max 100 chars | LLM model ID |

### Tasks Domain

#### `TaskSnapshot`

Full snapshot of a task's state.

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `id` | string | No | Task ID |
| `title` | string | No | Task title |
| `description` | string | No | Detailed description |
| `successCriteria` | string | No | Acceptance criteria |
| `status` | `TaskStatus` | No | Current lifecycle state |
| `type` | `TaskType` | No | Feature, Bug, Chore, or Spike |
| `currentPhase` | `CollaborationPhase` | No | Current collaboration phase |
| `currentPlan` | string | No | Latest plan text |
| `validationStatus` | `WorkstreamStatus` | No | Validation workstream progress |
| `validationSummary` | string | No | Validation workstream notes |
| `implementationStatus` | `WorkstreamStatus` | No | Implementation workstream progress |
| `implementationSummary` | string | No | Implementation workstream notes |
| `preferredRoles` | string[] | No | Preferred agent roles for this task |
| `createdAt` | datetime | No | Creation timestamp |
| `updatedAt` | datetime | No | Last update timestamp |
| `size` | `TaskSize` | Yes | Estimated effort |
| `startedAt` | datetime | Yes | When work began |
| `completedAt` | datetime | Yes | When task was completed |
| `assignedAgentId` | string | Yes | Assigned agent's ID |
| `assignedAgentName` | string | Yes | Assigned agent's display name |
| `usedFleet` | boolean | No | Whether multi-model fleet was used |
| `fleetModels` | string[] | Yes | Models used if fleet was employed |
| `branchName` | string | Yes | Git branch for this task |
| `pullRequestUrl` | string | Yes | PR URL |
| `pullRequestNumber` | integer | Yes | PR number |
| `pullRequestStatus` | `PullRequestStatus` | Yes | PR review state |
| `reviewerAgentId` | string | Yes | Agent assigned as reviewer |
| `reviewRounds` | integer | No | Number of review iterations |
| `testsCreated` | string[] | Yes | Test file paths created |
| `commitCount` | integer | No | Number of commits |
| `mergeCommitSha` | string | Yes | Merge commit SHA |
| `commentCount` | integer | No | Number of comments |
| `workspacePath` | string | Yes | Workspace path |
| `sprintId` | string | Yes | Sprint this task belongs to |
| `dependsOnTaskIds` | string[] | Yes | IDs of tasks this depends on |
| `blockingTaskIds` | string[] | Yes | IDs of tasks blocked by this |
| `priority` | `TaskPriority` | No | Priority level (default: Medium) |

#### `TaskAssignmentRequest`

Create and assign a new task.

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `title` | string | Yes | max 200 chars | Task title |
| `description` | string | Yes | 1–10,000 chars | Detailed description |
| `successCriteria` | string | Yes | 1–5,000 chars | Acceptance criteria |
| `roomId` | string | No | max 100 chars | Target room (auto-created if omitted) |
| `preferredRoles` | string[] | Yes | — | Preferred agent roles |
| `type` | `TaskType` | No | valid enum | Default: `Feature` |
| `correlationId` | string | No | — | Correlation ID for tracking |
| `currentPlan` | string | No | max 50,000 chars | Initial plan text |
| `priority` | `TaskPriority` | No | valid enum | Default: `Medium` |

#### `TaskAssignmentResult`

Result of creating and assigning a task.

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `correlationId` | string | No | Correlation ID for the operation |
| `room` | `RoomSnapshot` | No | The room where the task was created |
| `task` | `TaskSnapshot` | No | The created task snapshot |
| `activity` | `ActivityEvent` | No | Activity event emitted for the creation |

#### `ActivityEvent`

An audit trail event emitted during collaboration.

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `id` | string | No | Event ID |
| `type` | `ActivityEventType` | No | Event category |
| `severity` | `ActivitySeverity` | No | Info, Warning, or Error |
| `roomId` | string | Yes | Associated room ID |
| `actorId` | string | Yes | Agent/user that triggered the event |
| `taskId` | string | Yes | Associated task ID |
| `message` | string | No | Human-readable event description |
| `correlationId` | string | Yes | Links related events |
| `occurredAt` | datetime | No | Event timestamp |
| `metadata` | `Dictionary<string, object>` | Yes | Arbitrary key-value metadata |

#### `AssignTaskRequest`

Assign an agent to an existing task.

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `agentId` | string | Yes | max 100 chars | Agent to assign |
| `agentName` | string | Yes | max 200 chars | Agent display name |

#### `UpdateTaskStatusRequest`

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `status` | `TaskStatus` | Yes | valid enum | New task status |

#### `UpdateTaskPriorityRequest`

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `priority` | `TaskPriority` | Yes | valid enum | New task priority (`Critical`, `High`, `Medium`, `Low`) |

#### `UpdateTaskBranchRequest`

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `branchName` | string | Yes | max 300 chars | Git branch name |

#### `UpdateTaskPrRequest`

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `url` | string | Yes | valid URL, max 2,000 chars | Pull request URL |
| `number` | integer | Yes | ≥ 1 | Pull request number |
| `status` | `PullRequestStatus` | Yes | valid enum | PR review status |

#### `CompleteTaskRequest`

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `commitCount` | integer | Yes | 0–100,000 | Number of commits |
| `testsCreated` | string[] | No | — | Test file paths created |

#### `AddDependencyRequest`

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `dependsOnTaskId` | string | Yes | max 200 chars | ID of the dependency target task |

#### `TaskDependencyInfo`

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `taskId` | string | No | The task being queried |
| `dependsOn` | `TaskDependencySummary[]` | No | Tasks this one depends on |
| `dependedOnBy` | `TaskDependencySummary[]` | No | Tasks that depend on this one |

#### `TaskDependencySummary`

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `taskId` | string | No | Dependency target task ID |
| `title` | string | No | Task title |
| `status` | `TaskStatus` | No | Current status |
| `isSatisfied` | boolean | No | Whether the dependency is met (status = Completed) |

#### `BulkUpdateStatusRequest`

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `taskIds` | string[] | Yes | ≥ 1 item | Task IDs to update |
| `status` | `TaskStatus` | Yes | safe statuses only | New status |

#### `BulkAssignRequest`

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `taskIds` | string[] | Yes | ≥ 1 item | Task IDs to assign |
| `agentId` | string | Yes | 1–100 chars | Target agent ID |
| `agentName` | string | No | max 200 chars | Agent display name |

#### `BulkOperationResult`

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `requested` | integer | No | Number of items requested |
| `succeeded` | integer | No | Items successfully processed |
| `failed` | integer | No | Items that failed |
| `updated` | `TaskSnapshot[]` | No | Successfully updated task snapshots |
| `errors` | `BulkOperationError[]` | No | Per-item errors |

#### `BulkOperationError`

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `taskId` | string | No | Task that failed |
| `code` | string | No | Machine-readable error code |
| `error` | string | No | Error description |

### Commands Domain

#### `ExecuteCommandRequest`

Execute a command (sync or async).

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `command` | string | Yes | 1–10,000 chars | Command name (e.g., `RUN_BUILD`, `LIST_TASKS`) |
| `args` | `Dictionary<string, JsonElement>` | No | — | Command arguments as JSON key-value pairs |

#### `CommandEnvelope`

Command execution result envelope.

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `command` | string | No | Command name |
| `args` | `Dictionary<string, object>` | No | Arguments passed |
| `status` | `CommandStatus` | No | Success, Error, or Denied |
| `result` | `Dictionary<string, object>` | Yes | Command result payload |
| `error` | string | Yes | Error message if failed |
| `correlationId` | string | No | Unique execution ID |
| `timestamp` | datetime | No | Execution timestamp |
| `executedBy` | string | No | Agent/user that executed |
| `errorCode` | string | Yes | Machine-readable error code (see `CommandErrorCode`) |
| `retryCount` | integer | No | Automatic retry attempts before this result |

### Memory System

#### `MemoryImportRequest`

Bulk import/upsert memories.

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `memories` | `MemoryImportEntry[]` | Yes | max 500 items | Memories to import |

#### `MemoryImportEntry`

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `agentId` | string | Yes | max 100 chars | Agent the memory belongs to |
| `category` | string | Yes | max 200 chars | Memory category |
| `key` | string | Yes | max 200 chars | Unique key within category |
| `value` | string | Yes | max 500 chars | Memory value |
| `ttlHours` | integer | No | 1–87,600 | Time-to-live in hours |

### Notifications Domain

#### `ProviderConfigSchema`

Schema describing a notification provider's configuration fields.

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `providerId` | string | No | Provider identifier |
| `displayName` | string | No | Human-readable provider name |
| `description` | string | No | Provider description |
| `fields` | `ConfigField[]` | No | Configuration fields |

#### `ConfigField`

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `key` | string | No | Configuration key |
| `label` | string | No | Display label |
| `type` | string | No | Field type (e.g., "string", "boolean") |
| `required` | boolean | No | Whether the field is required |
| `description` | string | Yes | Help text |
| `placeholder` | string | Yes | Input placeholder |

### Instruction Templates

#### `InstructionTemplateRequest`

Create or update an instruction template.

| Property | Type | Required | Constraints | Description |
|----------|------|----------|-------------|-------------|
| `name` | string | Yes | max 200 chars | Template name |
| `description` | string | No | max 1,000 chars | Template description |
| `content` | string | Yes | 1–100,000 chars | Template content |

#### `InstructionTemplateResponse`

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `id` | string | No | Template ID |
| `name` | string | No | Template name |
| `description` | string | Yes | Template description |
| `content` | string | No | Template content |
| `createdAt` | datetime | No | Creation timestamp |
| `updatedAt` | datetime | No | Last modification timestamp |

### Workspace & GitHub

#### `OnboardResult`

Result of onboarding a project.

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `scan` | `ProjectScanResult` | No | Project scan results |
| `workspace` | `WorkspaceMeta` | No | Workspace metadata |
| `specTaskCreated` | boolean | No | Whether a spec-writing task was created |
| `roomId` | string | Yes | Room ID if a task was created |

#### `ProjectScanResult`

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `path` | string | No | Scanned directory path |
| `projectName` | string | Yes | Detected project name |
| `techStack` | string[] | No | Detected technologies |
| `hasSpecs` | boolean | No | Whether specs directory exists |
| `hasReadme` | boolean | No | Whether README exists |
| `isGitRepo` | boolean | No | Whether directory is a git repo |
| `gitBranch` | string | Yes | Current git branch |
| `detectedFiles` | string[] | No | Notable config/project files found |
| `repositoryUrl` | string | Yes | Remote repository URL |
| `defaultBranch` | string | Yes | Default git branch name |
| `hostProvider` | string | Yes | Git host (e.g., "github") |

#### `GitHubStatusResponse`

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `isConfigured` | boolean | No | Whether GitHub integration is configured |
| `repository` | string | Yes | Repository name |
| `authSource` | string | No | Auth source (default: "none") |

### Settings

#### `SettingResponse`

| Property | Type | Nullable | Description |
|----------|------|----------|-------------|
| `key` | string | No | Setting key |
| `value` | string | No | Setting value |

---

## Endpoint Count by Domain

| Domain | Endpoints |
|--------|-----------|
| System & Health | 9 |
| Authentication | 3 |
| Workspace | 5 |
| Rooms (CRUD, membership, messages, operations, plan, analytics, sessions) | 22 |
| Direct Messages | 5 |
| Agents (state, knowledge, quota, config) | 15 |
| Tasks (CRUD, lifecycle, dependencies, metadata, bulk) | 13 |
| Sprints (CRUD, lifecycle, artifacts, metrics, schedule) | 15 |
| Commands (execution, audit) | 5 |
| Specs | 2 |
| Analytics & Usage | 7 |
| Memory System | 6 |
| Retrospectives & Digests | 6 |
| Sessions | 2 |
| Notifications | 8 |
| Instruction Templates | 5 |
| Data Export | 4 |
| Search | 1 |
| Filesystem | 1 |
| GitHub | 1 |
| Settings | 3 |
| Worktrees | 1 |
| Activity & Real-Time | 2 + SignalR |
| **Total** | **141 REST + 3 SSE + 1 SignalR hub** |

## Known Gaps

1. ~~**Request/response schemas**~~ — **Resolved**: Full property definitions for all 39+ request/response types documented in [Request/Response Schemas](#requestresponse-schemas) section, including enums reference, validation constraints, and nullability. See domain-specific specs for behavioral contracts.
2. ~~**Rate limiting**~~ — **Resolved**: Three independent mechanisms documented — Consultant API HTTP-level limiter (429 + Retry-After), per-agent command rate limiter (denied envelope), and per-agent hourly quotas (agent paused). See [Rate Limiting](#rate-limiting).
3. ~~**Pagination consistency**~~ — **Resolved**: Three pagination styles documented — cursor-based (`after`), limit/offset with total count, and limit-only. Per-endpoint table with defaults and maximums. See [Pagination](#pagination).
4. ~~**Room artifacts**~~ — **Resolved**: `GET /api/rooms/{roomId}/artifacts` returns append-only event log of file operations (Created, Updated, Committed) tracked by `RoomArtifactTracker`. Artifacts recorded from `write_file` SDK tool and `COMMIT_CHANGES` command. Per-file commit attribution via `git diff-tree`. 19 new tests.
5. ~~**Room evaluations**~~ — **Resolved**: `GET /api/rooms/{roomId}/evaluations` now returns real artifact quality evaluations via `ArtifactEvaluatorService`. Evaluates each tracked file for existence (40pts), non-empty content (20pts), syntax validity for JSON/XML (25pts), and completeness markers (15pts). Deduplicates to latest operation per file, excludes deleted files. Path traversal protection included. 21 new tests.

## Revision History

| Date | Change |
|------|--------|
| 2026-04-15 | Add Request/Response Schemas section — full property definitions for 39+ API types — resolves gap 1 |
| 2026-04-14 | Add Rate Limiting section (3 mechanisms) and Pagination section (3 styles) — resolves gaps 2 and 3 |
| 2026-04-14 | Initial catalog — 145 endpoints across 26 controllers |
