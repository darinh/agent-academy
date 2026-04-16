# 015 — Security Model

## Purpose

Consolidate the security architecture of Agent Academy into a single reference: trust boundaries, authentication, authorization, agent sandboxing, prompt injection defenses, and accepted risks. Other specs reference individual security mechanisms; this spec is the authoritative threat model and permission consolidation.

## Scope

This spec covers the server (`AgentAcademy.Server`) and its interactions with agents, the frontend client, and the Consultant API. It does **not** cover GitHub's own OAuth infrastructure or the Copilot SDK's internal security — only how Agent Academy uses them.

---

## 1. Trust Boundaries

```
┌─────────────────────────────────────────────────────────────────────┐
│  TRUSTED ZONE — Server Process (AgentAcademy.Server)                │
│                                                                     │
│  ┌──────────┐  ┌──────────────┐  ┌─────────────┐  ┌─────────────┐ │
│  │ ASP.NET  │  │  Command     │  │  Copilot    │  │  EF Core +  │ │
│  │ Pipeline │  │  Pipeline    │  │  Executor   │  │  SQLite DB  │ │
│  └────┬─────┘  └──────┬───────┘  └──────┬──────┘  └─────────────┘ │
│       │               │                 │                           │
└───────┼───────────────┼─────────────────┼───────────────────────────┘
        │               │                 │
   ─────┼───────────────┼─────────────────┼───── TRUST BOUNDARY ─────
        │               │                 │
   ┌────▼────┐   ┌──────▼──────┐   ┌──────▼──────┐
   │ Browser │   │   Agent     │   │  GitHub     │
   │ (React) │   │  Processes  │   │  API/OAuth  │
   │         │   │  (Copilot   │   │             │
   │ Consul- │   │   SDK CLI)  │   │  Copilot    │
   │ tant API│   │             │   │  SDK Cloud  │
   └─────────┘   └─────────────┘   └─────────────┘
```

### Trust Zones

| Zone | Trust Level | Description |
|------|-------------|-------------|
| **Server process** | Fully trusted | All business logic, auth enforcement, data access |
| **Browser client** | Authenticated but untrusted input | GitHub OAuth validates identity; all input is validated |
| **Consultant API** | Authenticated but untrusted input | Shared-secret validates identity; rate-limited |
| **Agent processes** | Semi-trusted | Agents run via Copilot SDK; output is parsed and sanitized before use in prompts; commands are authorized per-agent |
| **GitHub APIs** | External trusted | OAuth provider, Copilot SDK backend; TLS-secured |
| **SQLite database** | Trusted storage | Local file, same-user access, encrypted tokens |

### Key Invariant

All security enforcement happens server-side. The browser client and agents are consumers — they cannot bypass authorization by crafting requests, because the server validates every operation.

---

## 2. Authentication

### 2.1 GitHub OAuth (Primary — Human Users)

**Files**: `Auth/AuthenticationExtensions.cs`, `Auth/AppAuthSetup.cs`, `Controllers/AuthController.cs`, `Auth/CopilotTokenRefreshMiddleware.cs`

**Flow**:
1. User visits `/api/auth/login` → server issues OAuth `Challenge("GitHub")`
2. GitHub redirects to `/api/auth/callback` with authorization code
3. Server exchanges code for access token + refresh token
4. Server fetches GitHub user profile (`/user` endpoint)
5. Tokens stored in `CopilotTokenProvider` and encrypted cookie
6. Cookie auth scheme (`aspnetcore.auth`) authenticates subsequent requests

**Token lifecycle**:
- Access tokens are short-lived (GitHub-determined expiry)
- Refresh tokens extend the session up to ~6 months without re-authentication
- `CopilotTokenRefreshMiddleware` restores tokens from cookie on each request
- `CopilotAuthMonitorService` proactively refreshes tokens 30 minutes before expiry
- On definitive auth failure (HTTP 401/403 from GitHub), system degrades gracefully and notifies via the notification system

**OAuth scopes**: `read:user`, `user:email`, `repo`

**Cookie configuration**:
- `HttpOnly = true` — not accessible to JavaScript
- `Secure = true` — HTTPS only (relaxed in development)
- `SameSite = None` — required for cross-origin requests from the Vite dev server
- Sliding expiration with configurable lifetime

### 2.2 Consultant Key (Secondary — CLI Agents)

**Files**: `Auth/ConsultantKeyAuthHandler.cs`, `Auth/ConsultantRateLimitSettings.cs`

**Mechanism**: Pre-shared secret in `X-Consultant-Key` HTTP header.

**Validation**:
- Server SHA-256 hashes both the configured secret and the presented key
- Comparison uses `CryptographicOperations.FixedTimeEquals` (timing-attack resistant)
- On success, creates a `ClaimsPrincipal` with role `Consultant`

**Identity**: `SenderKind = User`, `SenderId = "consultant"`, `SenderName = "Consultant"`. The consultant has the same API access as an authenticated human user.

**Configuration**: `ConsultantApi:SharedSecret` in `appsettings.json`, user-secrets, or `CONSULTANTAPI__SHAREDSECRET` environment variable. If not configured, the consultant auth scheme is not registered — the endpoint simply doesn't exist.

**Accepted limitations**:
- No token rotation, nonce, or replay protection — acceptable for localhost-only deployment
- Single shared secret — no per-consultant identity differentiation
- Migration path: the auth handler is a standard ASP.NET Core authentication scheme; adding OAuth2/Entra ID is additive, not breaking

### 2.3 Multi-Auth Policy Scheme

**File**: `Auth/AuthenticationExtensions.cs:148-159`

When both GitHub OAuth and Consultant Key are enabled, a policy scheme routes authentication:
- If `X-Consultant-Key` header is present → `ConsultantKey` scheme
- Otherwise → Cookie scheme

This allows both auth mechanisms to coexist on all endpoints without explicit per-controller scheme selection.

### 2.4 Auth-Disabled Mode

**File**: `Auth/AppAuthSetup.cs:19-37`

When neither GitHub OAuth credentials nor a consultant shared secret is configured, authentication is **entirely disabled**. All endpoints become publicly accessible.

**Rationale**: Simplifies local development and first-run setup. The system is designed for single-user localhost operation.

**Invariant**: Auth-disabled mode is determined at startup and cannot change at runtime. The presence/absence of `GitHub:ClientId` and `ConsultantApi:SharedSecret` is the gate.

---

## 3. Authorization

### 3.1 Global Fallback Policy

**File**: `Auth/AuthenticationExtensions.cs:62-67`

```csharp
options.FallbackPolicy = new AuthorizationPolicyBuilder()
    .RequireAuthenticatedUser()
    .Build();
```

All endpoints require authentication **by default**. No `[Authorize]` attribute is needed on individual controllers — the fallback policy covers everything.

**Exceptions** (explicit `[AllowAnonymous]`):
- `AuthController` — the login/callback flow itself
- `GET /healthz` — health check endpoint
- `GET /healthz/instance` — instance health endpoint
- Static files served by the SPA middleware

### 3.2 No Role-Based Endpoint Authorization

Human users and the consultant share identical API access. There is no admin/user role distinction for HTTP endpoints. This is acceptable for a single-user product — the authenticated user owns the entire workspace.

### 3.3 Agent Command Authorization

Agent authorization is enforced at the **command pipeline** level, not the HTTP level. Agents don't call HTTP endpoints directly — they issue commands through the Copilot SDK, which the server's command pipeline processes.

See [007 — Agent Command System](../007-agent-commands/spec.md) for full command permission details. Summary below.

#### Authorization Model

**File**: `Commands/CommandAuthorizer.cs`

- **Default-deny**: If a command is not in an agent's allowed list, it is denied
- **Explicit deny overrides allow**: Deny rules always win over allow rules
- **Wildcard patterns**: `*` (all commands) and `PREFIX_*` (prefix match) are supported
- **Role gates**: Certain commands (e.g., `SHELL`) require specific agent roles regardless of permission lists

#### Permission Matrix

| Agent | Role | Key Allowed | Key Denied |
|-------|------|-------------|------------|
| Aristotle | Planner | All task/room management, `SHELL`, `MERGE_TASK`, `CLOSE_ROOM`, `RUN_BUILD`, `RUN_TESTS`, file/code read | — |
| Archimedes | Architect | Read all, spec commands, `ADD_TASK_COMMENT`, `SHELL`, file/code read | `RESTART_SERVER` |
| Hephaestus | Engineer | Build/test/code/file ops, `COMMIT_CHANGES`, `SHELL` | `APPROVE_TASK`, `REQUEST_CHANGES`, `RESTART_SERVER` |
| Athena | Engineer | Build/test/code/file ops, `COMMIT_CHANGES`, `SHELL` | `APPROVE_TASK`, `REQUEST_CHANGES`, `RESTART_SERVER` |
| Socrates | Reviewer | `APPROVE_TASK`, `REQUEST_CHANGES`, `MERGE_TASK`, `SHELL`, file/code read | `RESTART_SERVER` |
| Thucydides | Documenter | Docs/spec tools, file/code read | `APPROVE_TASK`, `REQUEST_CHANGES`, `RESTART_SERVER` |

**Source of truth**: `Config/agents.json` — permissions are configured, not hardcoded.

#### Pipeline Enforcement Order

1. **Parse** — extract command name and arguments from agent output
2. **Authorize** — check agent permissions against command + args; return `denied` if unauthorized
3. **Rate limit** — sliding-window limiter (default 30 commands / 60 seconds per agent)
4. **Execute** — dispatch to handler
5. **Audit** — record execution as `CommandAuditEntity` with full envelope

---

## 4. Agent Security Boundaries

### 4.1 Process Model

Agents are **not** separate OS processes. They run as Copilot SDK sessions within the server process. The SDK communicates with GitHub's Copilot cloud service, which executes the LLM — but agent tool calls (file reads, command execution) happen server-side in the same process.

**Implication**: There is no OS-level isolation between agents. Security is enforced through application-level controls (command authorization, path validation, SDK permission handling).

### 4.2 SDK Permission Handler

**File**: `Services/AgentPermissionHandler.cs`

The Copilot SDK asks the server to approve/deny each tool call the agent attempts. The handler:

- **Approves**: `custom-tool`, `read`, `tool` permission kinds (our registered tools)
- **Denies**: `shell`, `write`, `url`, `mcp` permission kinds — these are SDK built-in capabilities that would bypass our command pipeline
- **Logs**: All approved and denied permission requests for diagnostics
- **Fallback**: When no tools are registered for an agent, all permissions are approved (backward compatibility)

This prevents agents from using the SDK's built-in shell execution or file writing — they must go through our command pipeline where authorization and auditing occur.

### 4.3 Git Worktree Isolation

**File**: `Services/WorktreeService.cs`

Each agent in a breakout room gets a dedicated git worktree:
- Path: `~/projects/<project>-worktrees[-hash]/<agentId>`
- Created via `git worktree add`
- Cleaned up via `git worktree remove --force` + `git worktree prune` on breakout exit
- The Copilot SDK client's `Cwd` is set to the worktree directory

**Isolation properties**:
- Each agent sees its own branch and working directory
- Agents cannot accidentally overwrite each other's work (separate branches)
- No OS-level ACLs — the server user owns all worktrees

### 4.4 File System Access Controls

**Read access** (`ReadFileHandler`, `AgentToolFunctions`):
- Paths resolved via `Path.GetFullPath(Path.Combine(projectRoot, relativePath))`
- Resolved path must start with `projectRoot` (prefix check prevents traversal)
- Content truncated at 12KB to prevent context flooding
- `startLine`/`endLine` parameters for targeted reads

**Write access** (`CodeWriteToolWrapper`):
- Restricted to files under `src/` directory
- Protected file blocklist (e.g., `Program.cs`, config files)
- Same path traversal prevention as reads

**Search access** (`SearchCodeHandler`, `AgentToolFunctions`):
- Uses `git grep -F` (fixed-string, not regex) to prevent ReDoS
- Results capped at 50 matches
- Path must resolve within project root
- 10-second timeout

**Accepted gap**: No symlink resolution check. A symlink inside the project root could point outside it. Mitigated by: agents don't create symlinks (no command for it), and the project directory is under human control.

### 4.5 Shell Command Sandboxing

**File**: `Commands/Handlers/ShellCommandHandler.cs`

Agents cannot run arbitrary shell commands. The `SHELL` command dispatches only to an allowlist of predefined operations:

| Operation | Binary | Arguments | Timeout |
|-----------|--------|-----------|---------|
| `git-checkout` | `git` | `checkout {branch}` | — |
| `git-commit` | `git` | `commit -m {message}` | — |
| `git-stash-pop` | `git` | `stash pop` | — |
| `restart-server` | (internal) | Server restart flow | — |
| `dotnet-build` | `dotnet` | `build` | 10 min |
| `dotnet-test` | `dotnet` | `test` | 10 min |

All operations use `ProcessStartInfo` with `UseShellExecute = false` and fixed argument lists — no string interpolation of user/agent input into command lines.

### 4.6 Agent Identity

**Files**: `Shared/Models/Agents.cs`, `Commands/CommandPipeline.cs`, `Services/MessageService.cs`

- Identity is assigned from the agent catalog (`AgentDefinition.Id`, `Name`, `Role`)
- Command envelopes are stamped with `ExecutedBy = agentId` server-side
- Message `SenderId` is set by the server based on which SDK session produced the response
- Agents cannot choose arbitrary sender IDs — the server assigns identity

**Git identity**: Each agent has a configured `GitIdentity` (name + email) used for commits. This is set by the server when creating the git process, not by the agent.

### 4.7 Agent Memory Isolation

**File**: See [008 — Agent Memory](../008-agent-memory/spec.md)

- Memory operations are per-agent: `RememberHandler` and `RecallHandler` scope by `agentId`
- An agent cannot read another agent's memories through the command system
- Memory is stored in a shared SQLite database but queries are agent-scoped
- FTS5 search is also agent-scoped

---

## 5. Prompt Injection Defenses

**Files**: `Services/PromptSanitizer.cs`, `Services/PromptBuilder.cs`

### Defense Layers

1. **Boundary markers**: All user-supplied content interpolated into LLM prompts is enclosed in `[UNTRUSTED_CONTENT]` / `[/UNTRUSTED_CONTENT]` delimiters
2. **Boundary instruction**: Every prompt includes a security preamble instructing the LLM to treat marked content as conversation context, not system-level instructions
3. **Metadata sanitization**: Sender names, room names, and memory keys have newlines and control characters stripped to prevent prompt-structure injection via metadata fields
4. **Marker escaping**: Marker sequences within content are escaped to prevent marker injection

### Coverage

| Prompt Surface | Sanitized | Method |
|----------------|-----------|--------|
| Room conversation messages | ✅ | `PromptBuilder.BuildConversationPrompt` |
| Breakout room prompts | ✅ | `PromptBuilder.BuildBreakoutPrompt` |
| Review prompts | ✅ | `PromptBuilder.BuildReviewPrompt` |
| Session summaries | ✅ | `ConversationSessionService.GenerateSummaryAsync` |
| Agent startup prompts | N/A | System-authored, not user content |
| Task descriptions | ✅ | Wrapped as untrusted in context blocks |
| DM content | ✅ | Wrapped as untrusted |

### Accepted Limitation

Prompt injection defenses are **advisory** — they instruct the LLM to treat content appropriately, but cannot cryptographically enforce it. LLMs may still follow injected instructions in adversarial scenarios. This is an industry-wide limitation, not specific to Agent Academy.

**Mitigation**: The command authorization system (Section 3.3) limits what agents can do even if prompt-injected — an agent that follows a malicious instruction still cannot execute commands outside its permission set.

---

## 6. Rate Limiting and Quotas

### 6.1 HTTP Rate Limiting (Consultant API)

**Files**: `Auth/ConsultantRateLimitSettings.cs`, `Auth/ConsultantRateLimitExtensions.cs`

- Applied only to requests authenticated via the `Consultant` role
- Partitioned by authentication scheme (consultant vs. cookie)
- Separate read and write rate windows (configurable)
- Implemented via ASP.NET Core's built-in `RateLimiter` middleware

**Note**: Browser/cookie-authenticated requests are **not** rate-limited at the HTTP level. This is acceptable for single-user localhost operation.

### 6.2 Agent Command Rate Limiting

**File**: `Commands/CommandRateLimiter.cs`

- In-memory sliding window per agent
- Default: 30 commands per 60 seconds
- Returns `RATE_LIMIT` error code when exceeded
- Enforced in the command pipeline after authorization, before execution
- `RATE_LIMIT` errors are not retried by the pipeline (policy enforcement, not transient failure)

### 6.3 Agent LLM Quotas

**File**: `Services/AgentQuotaService.cs`

Per-agent resource quotas (configurable in agent configs):
- `MaxRequestsPerHour` — LLM call rate limit via sliding window
- `MaxTokensPerHour` — token consumption limit
- `MaxCostPerHour` — cost-based limit
- Enforced per LLM attempt including retries

### 6.4 Operational Timeouts

| Operation | Timeout | Source |
|-----------|---------|--------|
| Build (`dotnet build`) | 10 minutes | `RunBuildHandler` |
| Tests (`dotnet test`) | 10 minutes | `RunTestsHandler` |
| Code search (`git grep`) | 10 seconds | `AgentToolFunctions` |
| Breakout room rounds | 200 max, 5 idle rounds | `BreakoutLifecycleService` |

---

## 7. Data Protection

### 7.1 Token Encryption

**File**: `Services/TokenPersistenceService.cs`

OAuth tokens persisted to the database are encrypted using ASP.NET Core Data Protection before storage. The Data Protection key ring is file-based (local machine).

### 7.2 No Row-Level Security

The SQLite database has no row-level security, tenant IDs, or per-user data partitioning. All authenticated users see all data in the active workspace.

**Rationale**: Agent Academy is a single-user product. The authenticated user owns the entire workspace. Multi-tenancy is out of scope.

### 7.3 Secret Sources

| Secret | Source | Priority |
|--------|--------|----------|
| GitHub OAuth Client ID/Secret | `GitHub:ClientId`, `GitHub:ClientSecret` in config/user-secrets | App config |
| Consultant shared secret | `ConsultantApi:SharedSecret` or `CONSULTANTAPI__SHAREDSECRET` env var | App config |
| Copilot GitHub token | `CopilotTokenProvider` (OAuth flow), or fallback: `COPILOT_GITHUB_TOKEN`, `GH_TOKEN`, `GITHUB_TOKEN` env vars | Runtime |
| Data Protection keys | File system (`~/.aspnet/DataProtection-Keys`) | Auto-managed |

**Invariant**: No secrets are hardcoded in source code. All secrets come from configuration, user-secrets, or environment variables.

---

## 8. Network Security

### 8.1 CORS

**File**: `Startup/ServiceCollectionStartupExtensions.cs:39-50`

Configurable via `Cors:Origins` (array) in `appsettings.json` or environment variables (`Cors__Origins__0`, `Cors__Origins__1`, etc.). Falls back to `["http://localhost:5173"]` when not set.

Default policy:
- Allowed origins: configured array (default: `http://localhost:5173`)
- Allowed: any header, any method
- Credentials: allowed

**Production**: Set `Cors:Origins` to the public frontend URL(s). Example environment variable: `Cors__Origins__0=https://academy.example.com`.

### 8.2 SignalR

**File**: `Program.cs`, `Hubs/ActivityHub.cs`

- Hub mapped at `/hubs/activity`
- `ActivityHub` has explicit `[Authorize]` metadata (belt-and-suspenders with fallback policy)
- When auth is disabled at startup, the hub endpoint is explicitly mapped with `.AllowAnonymous()` to preserve first-run/public mode behavior

### 8.3 TLS

The server binds to `http://localhost:5066` by default (no TLS). This is acceptable for localhost-only operation. For non-localhost deployment, TLS termination should be handled by a reverse proxy.

---

## 9. Input Validation

### 9.1 Model Validation

All controllers use `[ApiController]` which enables automatic model validation via Data Annotations (`[Required]`, `[StringLength]`, `[Range]`). Invalid requests receive `400 Bad Request` with `ProblemDetails`.

### 9.2 Path Traversal Prevention

Every file operation (read, write, search) follows the same pattern:

```csharp
var resolved = Path.GetFullPath(Path.Combine(projectRoot, userPath));
if (!resolved.StartsWith(projectRoot))
    return Error("Path outside project root");
```

Applied in: `ReadFileHandler`, `SearchCodeHandler`, `CodeWriteToolWrapper`, `AgentToolFunctions`, `FilesystemController`.

**Accepted gap**: Symlink resolution is not explicitly checked. Mitigated by agents lacking the ability to create symlinks.

### 9.3 Error Responses

All error responses use `ProblemDetails` format per RFC 7807. Internal exceptions are caught and returned as `500` with a generic message — stack traces are not leaked to clients in production.

---

## 10. Audit Trail

### 10.1 Command Audit

**File**: `Commands/CommandPipeline.cs`

Every agent command execution is recorded as a `CommandAuditEntity`:
- Command name and arguments
- Executing agent ID
- Timestamp
- Result (success/failure)
- `ErrorCode` (if failed)
- `RetryCount` (if retried)

Only the final attempt is audited (not intermediate retries).

### 10.2 Permission Logging

`AgentPermissionHandler` logs all approved and denied SDK permission requests at `Information` level, providing a diagnostic trail of what agents attempted.

### 10.3 Activity Events

Room activities (messages, joins, leaves, command executions) are recorded as `ActivityEntity` rows and broadcast via SignalR. This provides a chronological audit trail per room.

---

## 11. Threat Model

### 11.1 Threats and Mitigations

| Threat | Likelihood | Impact | Mitigation | Status |
|--------|------------|--------|------------|--------|
| **Stolen OAuth token** | Low | High — full repo access | Encrypted cookie storage, refresh rotation, auth monitoring, token expiry probing | ✅ Mitigated |
| **Consultant key leak** | Low | Medium — full API access | Timing-safe comparison, rate limiting, localhost-only deployment | ✅ Mitigated |
| **Agent prompt injection** | Medium | Medium — agent executes unintended commands | Boundary markers + command authorization limits blast radius | ✅ Defense-in-depth |
| **Agent path traversal** | Low | High — arbitrary file read/write | `Path.GetFullPath` + prefix check on every file operation | ✅ Mitigated |
| **Agent command escalation** | Low | High — unauthorized operations | Default-deny authorization, explicit deny overrides, role gates | ✅ Mitigated |
| **Cross-agent memory access** | Low | Low — information disclosure | Agent-scoped queries in memory handlers | ✅ Mitigated |
| **ReDoS via search** | Low | Medium — server DoS | Fixed-string search (`git grep -F`), 10s timeout, result cap | ✅ Mitigated |
| **Symlink escape** | Very Low | High — arbitrary file access | Agents cannot create symlinks; project dir is human-controlled | ⚠️ Accepted risk |
| **Server process compromise** | Very Low | Critical — full system access | Out of scope — no OS-level sandboxing for single-user localhost | ⚠️ Accepted risk |
| **Cookie theft (XSS)** | Low | High — session hijack | `HttpOnly` cookies, CSP headers, no inline scripts | ✅ Mitigated |

### 11.2 Accepted Risks (Single-User Product)

These risks are explicitly accepted given the product's single-user, localhost deployment model:

1. **No OS-level agent sandboxing**: Agents share the server process. Adding containers/namespaces would be over-engineering for a local dev tool.
2. **No multi-tenant data isolation**: One user, one workspace. Row-level security adds complexity without value.
3. **No TLS on server**: Localhost traffic. TLS termination is a deployment concern.
4. **Auth-disabled mode exists**: First-run convenience. The user opts in to security by configuring credentials.
5. **Single consultant secret**: No per-client differentiation. Acceptable when the only consultant is the local CLI agent.
6. **Advisory prompt injection defense**: Industry limitation. Defense-in-depth via command authorization limits the blast radius.
7. **No global HTTP rate limiting for cookie auth**: Single user — self-DoS is not a threat.

---

## 12. Security Checklist for New Features

When adding new features, verify:

- [ ] New endpoints inherit the global fallback authorization policy (no `[AllowAnonymous]` unless intentional)
- [ ] New agent commands are added to the permission matrix in `agents.json` with appropriate allow/deny
- [ ] New file operations include path traversal prevention (`GetFullPath` + prefix check)
- [ ] New user-supplied content interpolated into prompts uses `PromptSanitizer`
- [ ] New secrets use configuration/environment variables, never hardcoded values
- [ ] New external process execution uses argument lists (`ProcessStartInfo`), not string interpolation
- [ ] New data deletion is soft-delete or has confirmation/undo mechanism
- [ ] Error responses use `ProblemDetails`, not raw exceptions
- [ ] Async methods accept `CancellationToken` where appropriate

---

## Known Gaps

1. **No symlink resolution in path validation** — Mitigated by agents' inability to create symlinks. Would require `Path.GetFullPath` on the resolved symlink target. Low priority.
2. **No OS-level resource limits on agent processes** — CPU/memory limits would require containerization or cgroup integration. Out of scope for v1.

## Revision History

| Date | Change | Task |
|------|--------|------|
| 2026-04-16 | Documented CORS configurability via `Cors:Origins` config; added explicit config to `appsettings.json`; resolved Known Gap #2 (CORS). | 015-security-model |
| 2026-04-15 | Added explicit `[Authorize]` on `ActivityHub` and documented auth-disabled `.AllowAnonymous()` mapping for `/hubs/activity`; resolved Known Gap #2. | 015-security-model |
| 2026-04-14 | Initial spec — consolidated from codebase survey of auth, authorization, agent boundaries, prompt injection, rate limiting, data protection, and input validation. | 015-security-model |
