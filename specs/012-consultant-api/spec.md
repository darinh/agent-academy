# 012 — Consultant API

## Purpose

Enable an external CLI agent (e.g., Copilot CLI / Anvil) to communicate with Agent Academy agents via the server API. The consultant acts as a senior engineer who can advise, diagnose issues, and make code changes on behalf of agents.

## Vision

The goal of Agent Academy is to build the best possible multi-agent collaboration environment — one where a team of agents working together becomes more efficient than any single agent operating alone. The consultant's role is to accelerate this by:

- **Unblocking agents** when they're stuck on code issues, broken infrastructure, or unclear requirements
- **Improving the system** by adding commands, fixing bugs (e.g., broken breakout rooms), and tuning agent definitions based on direct observation of how agents struggle
- **Closing the feedback loop** — agents can articulate what they need, and the consultant can deliver it without a human middleman

## Status: Phase 1 & 2 Complete

## Problem Statement

The human operator currently acts as a middleman between the CLI agent (which has deep codebase expertise and can edit code) and the Agent Academy agents (which collaborate on tasks but need guidance). The consultant API removes this bottleneck by letting the CLI agent participate directly in main room conversations.

## Design Decisions

### Authentication: Shared Secret Header

- **Mechanism**: `X-Consultant-Key` header containing a pre-shared secret
- **Configuration**: `ConsultantApi:SharedSecret` in `appsettings.json` or `CONSULTANT_API__SHARED_SECRET` environment variable
- **Identity**: Authenticated as a `Consultant` principal with `SenderKind = User`, `SenderId = "consultant"`, `SenderName = "Consultant"`
- **Scope**: Grants access to the same endpoints as an authenticated human user
- **Migration path**: The auth handler is a standard ASP.NET Core authentication scheme. When a real OAuth2 provider (e.g., Entra ID) is needed, add it as another scheme alongside this one. No endpoint changes required.

**Why not OAuth2 now**: YAGNI. The system runs locally. A shared secret is simple, secure enough for localhost, and the authentication scheme abstraction provides the migration path for free.

### Interaction Model: Main Room Group Conversation

The consultant posts messages to the main room and reads responses from all agents, rather than DMing agents individually. This is because:

1. Agents have varying reliability — hearing from multiple agents provides signal averaging
2. The orchestrator already handles turn-taking and multi-agent coordination
3. It mirrors how the human operator interacts with agents

### Message Polling

After sending a message, the consultant polls for new responses. The design accounts for:

- **Multiple responders**: Several agents may respond to a single message at different times
- **Variable latency**: Some agents respond quickly, others take time (especially if running Copilot SDK calls)
- **Round completion**: The orchestrator runs up to `MaxRoundsPerTrigger` conversation rounds per human message trigger

## API Surface

### Authentication

All endpoints below require either:
- Cookie auth (existing GitHub OAuth flow), OR
- `X-Consultant-Key` header matching the configured shared secret

If `ConsultantApi:SharedSecret` is not configured, the consultant auth scheme is not registered.

### Endpoints

#### Send Message to Room

```
POST /api/rooms/{roomId}/human
Content-Type: application/json
X-Consultant-Key: {secret}

{
  "content": "string"
}
```

**Existing endpoint** — no changes needed. Posts a human message and triggers orchestrator rounds.

Returns: `ChatEnvelope` with the posted message.

#### Get Room Messages (NEW)

```
GET /api/rooms/{roomId}/messages?after={messageId}&limit={n}
X-Consultant-Key: {secret}
```

Returns messages in the room, optionally filtered:
- `after` (optional): Only return messages sent after this message ID (cursor-based pagination)
- `limit` (optional, default 50, max 200): Maximum messages to return

Returns:
```json
{
  "messages": [ChatEnvelope],
  "hasMore": false
}
```

Messages are ordered chronologically (oldest first). This endpoint respects session boundaries (only returns messages from the active conversation session).

#### List Rooms

```
GET /api/rooms
X-Consultant-Key: {secret}
```

**Existing endpoint** — no changes needed. Returns all rooms with recent messages. Use to discover the main room ID.

#### Get Room Details

```
GET /api/rooms/{roomId}
X-Consultant-Key: {secret}
```

**Existing endpoint** — no changes needed. Returns room snapshot with participants and recent messages.

### DM Endpoints (Secondary)

These existing endpoints also work with consultant auth, for targeted agent communication:

```
POST /api/dm/threads/{agentId}    # Send DM to specific agent
GET  /api/dm/threads/{agentId}    # Read DM thread
GET  /api/dm/threads              # List DM threads
```

### Command Execution

The consultant can execute commands through the human command API:

```
POST /api/commands/execute
Content-Type: application/json
X-Consultant-Key: {secret}

{
  "command": "COMMAND_NAME",
  "args": { "key": "value" }
}
```

Returns: `ExecuteCommandResponse` with status, result, and correlationId.

**Command catalog**: `GET /api/commands/metadata` returns all available commands with field schemas for dynamic UI rendering.

**Allowlisted commands** (subset of agent commands safe for human/consultant use):

| Category | Commands |
|----------|----------|
| Code | `READ_FILE`, `SEARCH_CODE` |
| Workspace | `LIST_ROOMS`, `LIST_AGENTS`, `LIST_TASKS`, `LIST_COMMANDS`, `SHOW_REVIEW_QUEUE`, `ROOM_HISTORY`, `ROOM_TOPIC`, `CREATE_ROOM`, `REOPEN_ROOM`, `CLOSE_ROOM`, `CLEANUP_ROOMS`, `INVITE_TO_ROOM`, `SHOW_UNLINKED_CHANGES`, `LINK_TASK_TO_SPEC` |
| Task mgmt | `UPDATE_TASK`, `CANCEL_TASK`, `APPROVE_TASK` |
| Git | `SHOW_DIFF`, `GIT_LOG`, `CREATE_PR`, `POST_PR_REVIEW`, `GET_PR_REVIEWS`, `MERGE_PR` |
| Operations | `RUN_BUILD`, `RUN_TESTS` |
| Memory | `EXPORT_MEMORIES` |

Async commands (`RUN_BUILD`, `RUN_TESTS`, `CREATE_PR`, `MERGE_PR`) return `status: "pending"` with a `correlationId`. Poll `GET /api/commands/{correlationId}` for completion.

**Identity**: Commands execute with `agentId = "human"`, `agentRole = "Human"`. Role gates on handlers respect this (e.g., `APPROVE_TASK` accepts Human role).

## Implementation Plan

### Phase 1: Auth Handler

**Files:**
- `src/AgentAcademy.Server/Auth/ConsultantKeyAuthHandler.cs` (NEW)
- `src/AgentAcademy.Server/Program.cs` (MODIFY — register scheme)
- `src/AgentAcademy.Server/appsettings.json` (MODIFY — add config section)

Create a `ConsultantKeyAuthenticationHandler` that:
1. Reads the `X-Consultant-Key` header
2. Compares against the configured secret (constant-time comparison)
3. On match, creates a `ClaimsPrincipal` with claims: `Name = "Consultant"`, `NameIdentifier = "consultant"`, `Role = "Consultant"`
4. Register as authentication scheme `"ConsultantKey"`

Wire into Program.cs:
- Register the scheme alongside existing cookie auth
- Use a `PolicyScheme` or configure the default to try both schemes
- The FallbackPolicy's `RequireAuthenticatedUser()` will accept either

### Phase 2: Messages Endpoint

**Files:**
- `src/AgentAcademy.Server/Controllers/RoomController.cs` (MODIFY — add GET messages action)
- `src/AgentAcademy.Server/Services/WorkspaceRuntime.cs` (MODIFY — add GetRoomMessagesAsync with cursor)

Add `GetRoomMessagesAsync(string roomId, string? afterMessageId, int limit)` to WorkspaceRuntime:
```csharp
public async Task<(List<ChatEnvelope> Messages, bool HasMore)> GetRoomMessagesAsync(
    string roomId, string? afterMessageId = null, int limit = 50)
```

Query logic:
- Filter by room, non-DM (`RecipientId == null`), active session
- If `afterMessageId` provided, find that message's `SentAt` and filter `> SentAt` (with tiebreaker on ID)
- Order by `SentAt` ascending
- Take `limit + 1` to determine `HasMore`

### Phase 3: Tests

**Files:**
- `tests/AgentAcademy.Server.Tests/ConsultantKeyAuthTests.cs` (NEW)
- `tests/AgentAcademy.Server.Tests/RoomMessagesEndpointTests.cs` (NEW)

Test cases:
- Auth: valid key → 200, invalid key → 401, missing key + no cookie → 401, missing config → scheme not registered
- Messages: returns messages after cursor, respects limit, empty room, chronological order

## Polling Strategy (for CLI consumer)

The CLI agent should follow this pattern when consulting:

```
1. GET /api/rooms → find main room ID
2. POST /api/rooms/{roomId}/human → send message, note returned message ID
3. Wait 5 seconds (let orchestrator trigger agents)
4. Loop:
   a. GET /api/rooms/{roomId}/messages?after={lastSeenId}
   b. If new messages: update lastSeenId, reset idle timer
   c. If no new messages and idle > 30 seconds: round is likely complete
   d. If idle > 60 seconds: definitely done, exit loop
5. Process all collected responses
```

This handles:
- Fast agents (responses within seconds)
- Slow agents (responses after 10-20 seconds)
- Multi-round conversations (orchestrator chains rounds)
- Agent silence (no response = pass, handled by timeout)

## Risk Assessment

| Component | Risk | Rationale |
|-----------|------|-----------|
| Auth handler | 🔴 | New authentication mechanism |
| Messages endpoint | 🟡 | New read-only query on existing data |
| Program.cs auth config | 🔴 | Modifying auth pipeline |
| appsettings.json | 🟢 | Additive config |
| Tests | 🟢 | New test files |

## Future Considerations (Not in scope)

- **OAuth2 provider**: Replace shared secret with Entra ID or similar. The auth scheme abstraction makes this a swap.
- **WebSocket/SSE streaming**: Replace polling with server-sent events for real-time responses. Would require a new hub method or SSE endpoint.
- **Consultant identity in UI**: Show consultant messages differently from human messages in the frontend.
- **Rate limiting**: Prevent the consultant from overwhelming agents with rapid-fire messages.
