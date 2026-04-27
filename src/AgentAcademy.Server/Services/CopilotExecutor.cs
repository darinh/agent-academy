using AgentAcademy.Shared.Models;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Notifications.Contracts;
using AgentAcademy.Server.Services.AgentWatchdog;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Runs agent prompts through the GitHub Copilot SDK, coordinating
/// session management (via <see cref="CopilotSessionPool"/>),
/// retry logic (via <see cref="CopilotSdkSender"/>), and client
/// lifecycle (via <see cref="CopilotClientFactory"/>).
///
/// This class owns auth-state transitions, circuit breaker logic,
/// and fallback management. Session caching and send/retry are
/// delegated to their respective focused classes.
///
/// Authentication (IMPORTANT — read before debugging "agent offline" issues):
///
/// The primary auth mechanism is GitHub OAuth. When a user logs in via the
/// browser, the OAuth callback saves the access token into
/// <see cref="CopilotTokenProvider"/> (a singleton). This token is then used
/// by all agent interactions, even in background orchestration where there
/// is no HttpContext. After a server restart, the token is restored from
/// the auth cookie on the first authenticated HTTP request.
///
/// Token resolution chain (handled by <see cref="CopilotClientFactory"/>):
/// 1. User's OAuth token (from <see cref="CopilotTokenProvider"/> — captured at login)
/// 2. Static config token (<c>Copilot:GitHubToken</c> — for non-OAuth deployments only)
/// 3. Environment variables (<c>COPILOT_GITHUB_TOKEN</c>, <c>GH_TOKEN</c>, <c>GITHUB_TOKEN</c>)
/// 4. Copilot CLI login state (SDK default)
/// 5. <see cref="StubExecutor"/> fallback (offline notice, not fake responses)
///
/// Common "offline" scenarios:
/// - Server just restarted and no browser request has restored the token yet
/// - OAuth is not configured (no GitHub:ClientId/ClientSecret)
/// - A stale StubExecutor message from before login is visible in chat history
///   (this is NOT a live connectivity issue — it's a persisted historical message)
/// </summary>
public sealed class CopilotExecutor : IAgentExecutor, IAsyncDisposable
{
    /// <summary>
    /// SDK built-in tools that bypass the structured-command discipline
    /// and request permission Kinds (<c>shell</c>, <c>write</c>) the
    /// <see cref="AgentPermissionHandler"/> denies. Excluded from every
    /// session to avoid the "unexpected user permission response" turn
    /// crash. Side-effecting work (shell, file mutation) flows through
    /// the structured command system (RUN_BUILD, RUN_TESTS, ADD_TASK,
    /// CREATE_PR, etc.); reads flow through the registered
    /// <c>read_file</c> + <c>search_code</c> tools.
    ///
    /// Kept ordinal-case so the SDK's case-sensitive tool registry
    /// matches: SDK builtin names are lowercase, snake_case.
    ///
    /// Discovered via Sprint #9 ToolDiag telemetry (PR #173) on
    /// 2026-04-27. See P1.9 in roadmap.md.
    ///
    /// Sprint #12 (2026-04-27) extended the list after observing
    /// software-engineer-1 thrash for the entire Implementation stage
    /// on `write_bash`/`read_bash`/`list_bash`/`stop_bash` (the SDK's
    /// shell-session control family — same Kind=shell permission as
    /// `bash`), plus `task` (SDK subagent launch — bypasses the entire
    /// orchestration/worktree workflow) and `create` (alternate
    /// file-create — same Kind=write denial as `write_file`/`create_file`).
    /// None of those calls ever reached AgentPermissionHandler success;
    /// the agent burned the whole turn on tool-error retries and
    /// produced zero `claim_task` / `write_file` / `commit_changes`
    /// calls. Excluding them forces the model back onto the structured
    /// command surface.
    /// </summary>
    internal static readonly IReadOnlyList<string> ExcludedSdkBuiltinTools = new[]
    {
        // Shell-permission tools: confirmed Kind=shell denial in Sprint #9.
        "bash",
        "shell",
        "exec",
        // Shell session-control family: observed in Sprint #12 as the
        // dominant failure mode for software-engineer-1 (52 write_bash +
        // 22 read_bash + 16 list_bash calls in a single Implementation
        // stage, all failing). They wrap the same underlying Kind=shell
        // permission as `bash`.
        "write_bash",
        "read_bash",
        "list_bash",
        "stop_bash",
        // Read tool: confirmed Unhandled error in Sprint #9. Custom
        // `read_file` covers the use case via the workspace-aware path.
        "view",
        // Write-permission tools: same Kind=write denial pattern would
        // apply (no registered custom tool implies "write"). Excluded
        // pre-emptively so future agents don't hit the same trap.
        "write_file",
        "edit",
        "create",
        "create_file",
        "delete_file",
        "str_replace_editor",
        "apply_patch",
        // Subagent launch: bypasses the entire room/breakout/worktree
        // orchestration. Agents must coordinate through the structured
        // command system (CLAIM_TASK, ADD_TASK, room messages), not by
        // forking opaque child agents.
        "task",
    };

    /// <summary>
    /// Returns the SDK builtin names to exclude for a session, with any
    /// name that also exists in <paramref name="registeredToolNames"/>
    /// removed. The SDK's <c>SessionConfig.ExcludedTools</c> filters by
    /// tool name regardless of origin, so blindly excluding a name that
    /// shadows a registered custom tool would silently drop the custom
    /// tool too. The current real overlap is <c>write_file</c> (registered
    /// in <see cref="AgentToolFunctions"/>); resolving dynamically also
    /// protects against future name collisions.
    /// </summary>
    internal static IReadOnlyList<string> ResolveExcludedSdkTools(IReadOnlySet<string> registeredToolNames)
    {
        var result = new List<string>(ExcludedSdkBuiltinTools.Count);
        foreach (var name in ExcludedSdkBuiltinTools)
        {
            if (!registeredToolNames.Contains(name))
                result.Add(name);
        }
        return result;
    }

    private readonly ILogger<CopilotExecutor> _logger;
    private readonly ILogger<StubExecutor> _stubLogger;
    private readonly ICopilotClientFactory _clientFactory;
    private readonly ICopilotSessionPool _sessionPool;
    private readonly ICopilotSdkSender _sender;
    private readonly ICopilotAuthStateNotifier _authStateNotifier;
    private readonly IAgentToolRegistry _toolRegistry;
    private readonly IAgentErrorTracker _errorTracker;
    private readonly IAgentQuotaService _quotaService;
    private readonly IAgentCatalog _catalog;
    private readonly IAgentLivenessTracker _livenessTracker;
    private readonly CopilotCircuitBreaker _circuitBreaker;
    private volatile bool _authFailed;
    private StubExecutor? _fallback;
    private readonly SemaphoreSlim _authStateLock = new(1, 1);
    private bool _disposed;

    public CopilotExecutor(
        ILogger<CopilotExecutor> logger,
        ILogger<StubExecutor> stubLogger,
        ICopilotClientFactory clientFactory,
        ICopilotSessionPool sessionPool,
        ICopilotSdkSender sender,
        IServiceScopeFactory scopeFactory,
        INotificationManager notificationManager,
        IAgentToolRegistry toolRegistry,
        IAgentErrorTracker errorTracker,
        IAgentQuotaService quotaService,
        IAgentCatalog catalog,
        IAgentLivenessTracker livenessTracker,
        CopilotCircuitBreaker? circuitBreaker = null,
        StubExecutor? fallback = null)
        : this(
            logger,
            stubLogger,
            clientFactory,
            sessionPool,
            sender,
            new CopilotAuthStateNotifier(scopeFactory, notificationManager),
            toolRegistry,
            errorTracker,
            quotaService,
            catalog,
            livenessTracker,
            circuitBreaker,
            fallback)
    {
    }

    internal CopilotExecutor(
        ILogger<CopilotExecutor> logger,
        ILogger<StubExecutor> stubLogger,
        ICopilotClientFactory clientFactory,
        ICopilotSessionPool sessionPool,
        ICopilotSdkSender sender,
        ICopilotAuthStateNotifier authStateNotifier,
        IAgentToolRegistry toolRegistry,
        IAgentErrorTracker errorTracker,
        IAgentQuotaService quotaService,
        IAgentCatalog catalog,
        IAgentLivenessTracker livenessTracker,
        CopilotCircuitBreaker? circuitBreaker = null,
        StubExecutor? fallback = null)
    {
        _logger = logger;
        _stubLogger = stubLogger;
        _clientFactory = clientFactory;
        _sessionPool = sessionPool;
        _sender = sender;
        _authStateNotifier = authStateNotifier;
        _toolRegistry = toolRegistry;
        _errorTracker = errorTracker;
        _quotaService = quotaService;
        _catalog = catalog;
        _livenessTracker = livenessTracker;
        _circuitBreaker = circuitBreaker ?? new CopilotCircuitBreaker();
        _fallback = fallback;
    }

    /// <summary>
    /// True once the <see cref="CopilotClient"/> has been successfully
    /// started. False if initialization hasn't been attempted, or if
    /// it failed and we fell back to the stub.
    /// </summary>
    public bool IsFullyOperational => _clientFactory.IsDefaultClientOperational;

    /// <summary>
    /// True when the executor has encountered a definitive authentication
    /// failure. Cleared automatically when a new token is provided.
    /// </summary>
    public bool IsAuthFailed => _authFailed;

    public Task MarkAuthDegradedAsync(CancellationToken ct = default)
        => TransitionAuthStateAsync(degraded: true, ct);

    public Task MarkAuthOperationalAsync(CancellationToken ct = default)
        => TransitionAuthStateAsync(degraded: false, ct);

    /// <summary>
    /// Current state of the circuit breaker protecting the Copilot API.
    /// </summary>
    public CircuitState CircuitBreakerState => _circuitBreaker.State;

    public async Task<string> RunAsync(
        AgentDefinition agent,
        string prompt,
        string? roomId,
        string? workspacePath,
        CancellationToken ct = default,
        string? turnId = null)
    {
        // Quota enforcement: early check before spending resources on session setup.
        // Also checked per-attempt in CopilotSdkSender.SendWithRetryAsync.
        await _quotaService.EnforceQuotaAsync(agent.Id);

        // Circuit breaker: if the API has been consistently failing,
        // skip directly to the fallback without burning through retries.
        if (!_circuitBreaker.AllowRequest())
        {
            _logger.LogWarning(
                "Circuit breaker OPEN for agent {AgentId} — skipping Copilot API, using fallback. " +
                "Consecutive failures: {Failures}",
                agent.Id, _circuitBreaker.ConsecutiveFailures);
            await _errorTracker.RecordAsync(
                agent.Id, roomId, "circuit_open",
                $"Circuit breaker open ({_circuitBreaker.ConsecutiveFailures} consecutive failures). " +
                $"Will probe again after cooldown.",
                recoverable: true);
            return await GetFallback().RunAsync(agent, prompt, roomId, workspacePath: null, ct);
        }

        var result = workspacePath is not null
            ? await _clientFactory.GetWorktreeClientAsync(workspacePath, ct)
            : await _clientFactory.GetClientAsync(ct);

        if (result.WasRecreated)
        {
            _circuitBreaker.Reset();

            // Invalidate all sessions — they belong to the old client.
            // NOTE: A narrow race exists where a concurrent request acquires
            // the new client (WasRecreated=false) and reuses a stale cached
            // session before this invalidation runs. The stale session will
            // fail at SendAsync, be caught, invalidated, and retried — so the
            // impact is one extra fallback-to-stub per token rotation, which
            // self-heals. A generation counter would eliminate this but adds
            // complexity disproportionate to the risk.
            await _sessionPool.InvalidateAllAsync();
        }

        if (result.Client is null)
        {
            _logger.LogDebug("Copilot client unavailable — delegating to StubExecutor");
            return await GetFallback().RunAsync(agent, prompt, roomId, workspacePath: null, ct);
        }

        // Only clear auth failure after confirming the new client started
        // successfully — a token change that fails to create a client should
        // NOT mark auth as operational.
        if (result.WasRecreated && _authFailed)
            _ = MarkAuthOperationalAsync();

        var client = result.Client;
        var sessionKey = BuildWorktreeKey(workspacePath, agent.Id, roomId)
            ?? BuildKey(agent.Id, roomId);

        try
        {
            var response = await _sessionPool.UseAsync(
                sessionKey,
                ct => CreatePrimedSessionAsync(client, agent, roomId, workspacePath, ct),
                session => _sender.SendWithRetryAsync(session, agent, prompt, roomId, ct, turnId),
                ct);

            _circuitBreaker.RecordSuccess();
            return response;
        }
        catch (CopilotAuthException ex)
        {
            // Auth failures do NOT trip the circuit — they have their own
            // recovery pathway (token refresh, auth monitor).
            _logger.LogError(ex,
                "Authentication failure for agent {AgentId} — marking auth failed",
                agent.Id);
            await _errorTracker.RecordAsync(agent.Id, roomId, "authentication", ex.Message, recoverable: false);
            await HandleAuthFailureAsync(agent.Id, roomId);
            return await RunFallbackAsync(agent, prompt, roomId, ct);
        }
        catch (CopilotAuthorizationException ex)
        {
            // Authorization failures do NOT trip the circuit — per-token issue.
            _logger.LogError(ex,
                "Authorization failure for agent {AgentId} — token lacks required permissions",
                agent.Id);
            await _errorTracker.RecordAsync(agent.Id, roomId, "authorization", ex.Message, recoverable: false);
            return await InvalidateSessionAndFallbackAsync(agent, prompt, roomId, sessionKey, ct);
        }
        catch (CopilotQuotaException ex)
        {
            _circuitBreaker.RecordFailure();
            _logger.LogError(ex,
                "Quota error for agent {AgentId} in room {RoomId} — exhausted retries, falling back to stub. " +
                "Circuit breaker: {Failures}/{Threshold} consecutive failures",
                agent.Id, roomId,
                _circuitBreaker.ConsecutiveFailures, _circuitBreaker.FailureThreshold);
            // Already recorded per-attempt in CopilotSdkSender; no duplicate here.
            return await InvalidateSessionAndFallbackAsync(agent, prompt, roomId, sessionKey, ct);
        }
        catch (CopilotTransientException ex)
        {
            _circuitBreaker.RecordFailure();
            _logger.LogError(ex,
                "Transient error for agent {AgentId} in room {RoomId} — exhausted retries, falling back to stub. " +
                "Circuit breaker: {Failures}/{Threshold} consecutive failures",
                agent.Id, roomId,
                _circuitBreaker.ConsecutiveFailures, _circuitBreaker.FailureThreshold);
            // Already recorded per-attempt in CopilotSdkSender; no duplicate here.
            return await InvalidateSessionAndFallbackAsync(agent, prompt, roomId, sessionKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _circuitBreaker.RecordFailure();
            _logger.LogError(ex,
                "Copilot call failed for agent {AgentId} in room {RoomId} — falling back to stub. " +
                "Circuit breaker: {Failures}/{Threshold} consecutive failures",
                agent.Id, roomId,
                _circuitBreaker.ConsecutiveFailures, _circuitBreaker.FailureThreshold);
            await _errorTracker.RecordAsync(agent.Id, roomId, "unknown", ex.Message, recoverable: true);

            // Invalidate the broken session so the next attempt gets a fresh one.
            return await InvalidateSessionAndFallbackAsync(agent, prompt, roomId, sessionKey, ct);
        }
    }

    public async Task InvalidateSessionAsync(string agentId, string? roomId)
    {
        var key = BuildKey(agentId, roomId);
        await _sessionPool.InvalidateAsync(key);

        // Also invalidate any worktree-scoped sessions for this agent+room
        var wtSuffix = $":{agentId}:{roomId ?? "default"}";
        await _sessionPool.InvalidateByFilterAsync(
            k => k.StartsWith("wt:", StringComparison.Ordinal)
              && k.EndsWith(wtSuffix, StringComparison.Ordinal));
    }

    public Task InvalidateRoomSessionsAsync(string roomId)
    {
        var roomSuffix = $":{roomId}";
        return _sessionPool.InvalidateByFilterAsync(
            k => k.EndsWith(roomSuffix, StringComparison.Ordinal));
    }

    public Task InvalidateAllSessionsAsync()
        => _sessionPool.InvalidateAllAsync();

    /// <summary>
    /// Domain-level dispose — called by <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// </summary>
    async Task IAgentExecutor.DisposeAsync() => await DisposeAsync();

    /// <summary>
    /// Framework-level dispose — called automatically by the DI container on shutdown.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _sessionPool.DisposeAsync();
        // Client disposal is handled by CopilotClientFactory.DisposeAsync
        // (DI container disposes it separately).
    }

    /// <summary>
    /// Disposes a worktree-scoped client and all sessions that belong to it.
    /// Called when a worktree is removed (task complete/cancelled).
    /// </summary>
    public async Task DisposeWorktreeClientAsync(string workspacePath)
    {
        var prefix = await _clientFactory.DisposeWorktreeClientAsync(workspacePath);
        if (prefix is null)
            return;

        await _sessionPool.InvalidateByFilterAsync(
            k => k.StartsWith(prefix, StringComparison.Ordinal));
    }

    // ── Internals ───────────────────────────────────────────────

    /// <summary>
    /// Creates a fully-configured and primed <see cref="CopilotSession"/>.
    /// Passed as a factory to <see cref="CopilotSessionPool.UseAsync{T}"/>.
    /// </summary>
    private async Task<CopilotSession> CreatePrimedSessionAsync(
        CopilotClient client,
        AgentDefinition agent,
        string? roomId,
        string? workspacePath,
        CancellationToken ct)
    {
        _logger.LogDebug(
            "Creating new CopilotSession for agent {AgentId}, model={Model}, cwd={WorkspacePath}",
            agent.Id, agent.Model ?? "default", workspacePath ?? "(default)");

        var tools = _toolRegistry.GetToolsForAgent(agent.EnabledTools, agent.Id, agent.Name, roomId, workspacePath);
        var toolNames = new HashSet<string>(tools.Select(t => t.Name), StringComparer.Ordinal);

        // Compute the effective SDK-builtin exclusion list for this session:
        // never exclude a name that's also a registered custom tool, because
        // the SDK's `ExcludedTools` filters by tool name (not by origin) and
        // would silently drop our custom tool too. The current overlap is
        // `write_file`, but checking dynamically guards against future
        // custom tools added with builtin-shadowing names.
        var effectiveExcludedTools = ResolveExcludedSdkTools(toolNames);

        var config = new SessionConfig
        {
            Model = agent.Model ?? "claude-opus-4.7",
            Streaming = true,
            Tools = [.. tools],
            // SDK builtin tools that bypass our structured-command discipline
            // are excluded — see <see cref="ExcludedSdkBuiltinTools"/> and
            // <see cref="ResolveExcludedSdkTools"/>.
            ExcludedTools = [.. effectiveExcludedTools],
            OnPermissionRequest = AgentPermissionHandler.Create(toolNames, _logger, _livenessTracker),
        };

        if (tools.Count > 0)
        {
            _logger.LogInformation(
                "Agent {AgentId} session created with {ToolCount} tools: {ToolNames}",
                agent.Id, tools.Count, string.Join(", ", toolNames));
        }

        var session = await client.CreateSessionAsync(config);

        // Prime the session with the agent's startup prompt if provided.
        if (!string.IsNullOrWhiteSpace(agent.StartupPrompt))
        {
            var primeResponse = await _sender.CollectResponseAsync(
                session, agent.StartupPrompt, agent.Id, roomId, ct);
            _logger.LogDebug(
                "Session primed for {AgentId}: {Length} chars",
                agent.Id, primeResponse.Length);
        }

        return session;
    }

    private StubExecutor GetFallback()
    {
        return _fallback ??= new StubExecutor(_stubLogger);
    }

    private Task<string> RunFallbackAsync(
        AgentDefinition agent,
        string prompt,
        string? roomId,
        CancellationToken ct)
        => GetFallback().RunAsync(agent, prompt, roomId, workspacePath: null, ct);

    private async Task<string> InvalidateSessionAndFallbackAsync(
        AgentDefinition agent,
        string prompt,
        string? roomId,
        string sessionKey,
        CancellationToken ct)
    {
        await _sessionPool.InvalidateAsync(sessionKey);
        return await RunFallbackAsync(agent, prompt, roomId, ct);
    }

    private static string BuildKey(string agentId, string? roomId)
        => $"{agentId}:{roomId ?? "default"}";

    private static string? BuildWorktreeKey(string? workspacePath, string agentId, string? roomId)
        => workspacePath is not null
            ? $"wt:{Path.GetFullPath(workspacePath)}:{agentId}:{roomId ?? "default"}"
            : null;

    // ── Auth failure/recovery notifications ─────────────────────

    private async Task HandleAuthFailureAsync(string agentId, string? roomId)
    {
        // Invalidate both the specific failing session AND the default/worktree
        // sessions for this agent+room — mirrors original InvalidateSessionAsync behavior.
        await InvalidateSessionAsync(agentId, roomId);
        await MarkAuthDegradedAsync();
    }

    private async Task TransitionAuthStateAsync(bool degraded, CancellationToken ct)
    {
        await _authStateLock.WaitAsync(ct);
        try
        {
            if (_authFailed == degraded)
                return;

            _authFailed = degraded;

            var roomId = _catalog.DefaultRoomId;
            await _authStateNotifier.NotifyAsync(degraded, roomId, ct);

            if (degraded)
            {
                _logger.LogWarning("Auth state transitioned to degraded");
            }
            else
            {
                _logger.LogInformation("Auth state transitioned to operational");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                degraded
                    ? "Failed to process auth degradation transition"
                    : "Failed to process auth recovery transition");
        }
        finally
        {
            _authStateLock.Release();
        }
    }
}
