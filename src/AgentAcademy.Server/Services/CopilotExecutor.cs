using System.Collections.Concurrent;
using System.Text;
using AgentAcademy.Shared.Models;
using AgentAcademy.Server.Notifications;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Runs agent prompts through the GitHub Copilot SDK, managing one
/// <see cref="CopilotSession"/> per agent-per-room combination.
/// Sessions are cached with a 10-minute sliding TTL and disposed on expiry.
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
/// Token resolution chain (checked in order):
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
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(2);

    // Retry parameters for transient errors (network, 5xx)
    private const int TransientMaxRetries = 3;
    private static readonly TimeSpan[] TransientBackoff = [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
    ];

    // Retry parameters for quota/rate-limit errors
    private const int QuotaMaxRetries = 3;
    private static readonly TimeSpan[] QuotaBackoff = [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
    ];

    private readonly ILogger<CopilotExecutor> _logger;
    private readonly ILogger<StubExecutor> _stubLogger;
    private readonly CopilotTokenProvider _tokenProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NotificationManager _notificationManager;
    private readonly IAgentToolRegistry _toolRegistry;
    private readonly LlmUsageTracker _usageTracker;
    private readonly AgentErrorTracker _errorTracker;
    private readonly CopilotCircuitBreaker _circuitBreaker = new();
    private readonly string? _configToken;
    private readonly string? _cliPath;
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private CopilotClient? _client;
    private readonly ConcurrentDictionary<string, CopilotClient> _worktreeClients = new();
    private string? _activeToken;
    private bool _clientFailed;
    private volatile bool _authFailed;
    private StubExecutor? _fallback;
    private Timer? _cleanupTimer;
    private readonly SemaphoreSlim _authStateLock = new(1, 1);
    private bool _disposed;

    public CopilotExecutor(
        ILogger<CopilotExecutor> logger,
        ILogger<StubExecutor> stubLogger,
        IConfiguration configuration,
        CopilotTokenProvider tokenProvider,
        IServiceScopeFactory scopeFactory,
        NotificationManager notificationManager,
        IAgentToolRegistry toolRegistry,
        LlmUsageTracker usageTracker,
        AgentErrorTracker errorTracker)
    {
        _logger = logger;
        _stubLogger = stubLogger;
        _configToken = configuration["Copilot:GitHubToken"];
        _cliPath = configuration["Copilot:CliPath"];
        _tokenProvider = tokenProvider;
        _scopeFactory = scopeFactory;
        _notificationManager = notificationManager;
        _toolRegistry = toolRegistry;
        _usageTracker = usageTracker;
        _errorTracker = errorTracker;
        _cleanupTimer = new Timer(
            _ => _ = CleanupExpiredSessionsAsync(),
            null,
            CleanupInterval,
            CleanupInterval);
    }

    /// <summary>
    /// True once the <see cref="CopilotClient"/> has been successfully
    /// started. False if initialization hasn't been attempted, or if
    /// it failed and we fell back to the stub.
    /// </summary>
    public bool IsFullyOperational => _client is not null && !_clientFailed;

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
        CancellationToken ct = default)
    {
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

        var client = workspacePath is not null
            ? await EnsureWorktreeClientAsync(workspacePath, ct)
            : await EnsureClientAsync(ct);
        if (client is null)
        {
            _logger.LogDebug("Copilot client unavailable — delegating to StubExecutor");
            return await GetFallback().RunAsync(agent, prompt, roomId, workspacePath: null, ct);
        }

        var sessionKey = workspacePath is not null
            ? $"wt:{Path.GetFullPath(workspacePath)}:{agent.Id}:{roomId ?? "default"}"
            : BuildKey(agent.Id, roomId);

        try
        {
            var entry = await GetOrCreateSessionEntryAsync(client, agent, sessionKey, roomId, ct);

            // Serialize sends through the same session to prevent
            // concurrent responses from interleaving.
            await entry.SendLock.WaitAsync(ct);
            try
            {
                var response = await SendAndCollectWithRetryAsync(entry.Session, agent, prompt, roomId, ct);
                entry.Touch();
                _circuitBreaker.RecordSuccess();
                return response;
            }
            finally
            {
                entry.SendLock.Release();
            }
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
            return await GetFallback().RunAsync(agent, prompt, roomId, workspacePath: null, ct);
        }
        catch (CopilotAuthorizationException ex)
        {
            // Authorization failures do NOT trip the circuit — per-token issue.
            _logger.LogError(ex,
                "Authorization failure for agent {AgentId} — token lacks required permissions",
                agent.Id);
            await _errorTracker.RecordAsync(agent.Id, roomId, "authorization", ex.Message, recoverable: false);
            await InvalidateSessionAsync(agent.Id, roomId);
            return await GetFallback().RunAsync(agent, prompt, roomId, workspacePath: null, ct);
        }
        catch (CopilotQuotaException ex)
        {
            _circuitBreaker.RecordFailure();
            _logger.LogError(ex,
                "Quota error for agent {AgentId} in room {RoomId} — exhausted retries, falling back to stub. " +
                "Circuit breaker: {Failures}/{Threshold} consecutive failures",
                agent.Id, roomId,
                _circuitBreaker.ConsecutiveFailures, _circuitBreaker.FailureThreshold);
            // Already recorded per-attempt in SendAndCollectWithRetryAsync; no duplicate here.
            await InvalidateSessionAsync(agent.Id, roomId);
            return await GetFallback().RunAsync(agent, prompt, roomId, workspacePath: null, ct);
        }
        catch (CopilotTransientException ex)
        {
            _circuitBreaker.RecordFailure();
            _logger.LogError(ex,
                "Transient error for agent {AgentId} in room {RoomId} — exhausted retries, falling back to stub. " +
                "Circuit breaker: {Failures}/{Threshold} consecutive failures",
                agent.Id, roomId,
                _circuitBreaker.ConsecutiveFailures, _circuitBreaker.FailureThreshold);
            // Already recorded per-attempt in SendAndCollectWithRetryAsync; no duplicate here.
            await InvalidateSessionAsync(agent.Id, roomId);
            return await GetFallback().RunAsync(agent, prompt, roomId, workspacePath: null, ct);
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
            await InvalidateSessionAsync(agent.Id, roomId);
            return await GetFallback().RunAsync(agent, prompt, roomId, workspacePath: null, ct);
        }
    }

    public async Task InvalidateSessionAsync(string agentId, string? roomId)
    {
        var key = BuildKey(agentId, roomId);
        if (_sessions.TryRemove(key, out var entry))
        {
            _sessionLocks.TryRemove(key, out _);
            _logger.LogDebug("Invalidating session {Key}", key);
            await DisposeSessionSafe(entry);
        }

        // Also invalidate any worktree-scoped sessions for this agent+room
        var wtSuffix = $":{agentId}:{roomId ?? "default"}";
        var wtKeys = _sessions.Keys
            .Where(k => k.StartsWith("wt:", StringComparison.Ordinal)
                     && k.EndsWith(wtSuffix, StringComparison.Ordinal))
            .ToList();
        foreach (var wtKey in wtKeys)
        {
            if (_sessions.TryRemove(wtKey, out var wtEntry))
            {
                _sessionLocks.TryRemove(wtKey, out _);
                _logger.LogDebug("Invalidating worktree session {Key}", wtKey);
                await DisposeSessionSafe(wtEntry);
            }
        }
    }

    public async Task InvalidateRoomSessionsAsync(string roomId)
    {
        var roomSuffix = $":{roomId}";
        var keys = _sessions.Keys
            .Where(k => k.EndsWith(roomSuffix, StringComparison.Ordinal))
            .ToList();

        foreach (var key in keys)
        {
            if (_sessions.TryRemove(key, out var entry))
            {
                _sessionLocks.TryRemove(key, out _);
                _logger.LogDebug("Invalidating session {Key} (room cleanup)", key);
                await DisposeSessionSafe(entry);
            }
        }
    }

    public async Task InvalidateAllSessionsAsync()
    {
        var keys = _sessions.Keys.ToList();
        _logger.LogInformation("Invalidating all {Count} agent sessions (workspace switch)", keys.Count);

        foreach (var key in keys)
        {
            if (_sessions.TryRemove(key, out var entry))
            {
                _sessionLocks.TryRemove(key, out _);
                await DisposeSessionSafe(entry);
            }
        }
    }

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

        _cleanupTimer?.Dispose();
        _cleanupTimer = null;

        foreach (var kvp in _sessions)
        {
            if (_sessions.TryRemove(kvp.Key, out var entry))
                await DisposeSessionSafe(entry);
        }

        if (_client is not null)
        {
            try
            {
                await _client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing CopilotClient");
            }
            _client = null;
        }

        // Dispose all worktree-scoped clients
        foreach (var kvp in _worktreeClients)
        {
            if (_worktreeClients.TryRemove(kvp.Key, out var wtClient))
            {
                try { await wtClient.DisposeAsync(); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing worktree CopilotClient for {Path}", kvp.Key);
                }
            }
        }
    }

    // ── Internals ───────────────────────────────────────────────

    private async Task<CopilotClient?> EnsureClientAsync(CancellationToken ct)
    {
        // All reads/writes to _client, _activeToken, and _clientFailed are
        // serialized through _clientLock to prevent races where a concurrent
        // token change disposes the client while another thread uses it.
        await _clientLock.WaitAsync(ct);
        try
        {
            var token = ResolveToken();

            // Existing client with matching token — reuse.
            if (_client is not null && !_clientFailed && _activeToken == token)
                return _client;

            // Token changed since last client creation — dispose old client
            // and reset failure state so we try the new token.
            if (_client is not null && _activeToken != token)
            {
                _logger.LogInformation(
                    "Token changed — recreating CopilotClient (old source: {Old}, new source: {New})",
                    DescribeTokenSource(_activeToken),
                    DescribeTokenSource(token));

                await DisposeClientSafe();
                _circuitBreaker.Reset();
            }

            // If we already failed with this exact token, don't retry.
            if (_clientFailed && _activeToken == token) return null;

            // Reset failure state for new token attempts.
            _clientFailed = false;
            var wasAuthFailed = _authFailed;
            _activeToken = token;

            var hasToken = !string.IsNullOrWhiteSpace(token);
            var hasCliPath = !string.IsNullOrWhiteSpace(_cliPath);
            _logger.LogInformation(
                "Starting CopilotClient (token source: {Source}, CLI: {Cli})...",
                DescribeTokenSource(token),
                hasCliPath ? _cliPath : "bundled");

            var options = new CopilotClientOptions();
            if (hasToken) options.GitHubToken = token;
            if (hasCliPath) options.CliPath = _cliPath;
            var client = new CopilotClient(options);
            await client.StartAsync();
            _client = client;
            _logger.LogInformation("CopilotClient started successfully");

            // Only clear auth failure and post recovery AFTER successful start.
            if (wasAuthFailed)
                _ = MarkAuthOperationalAsync();

            return _client;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start CopilotClient — falling back to StubExecutor");
            _clientFailed = true;
            return null;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <summary>
    /// Returns a CopilotClient whose CLI process runs in the given
    /// worktree directory. Clients are cached by path and share the
    /// same auth token as the default client.
    /// </summary>
    private async Task<CopilotClient?> EnsureWorktreeClientAsync(string workspacePath, CancellationToken ct)
    {
        var normalizedPath = Path.GetFullPath(workspacePath);

        // Fast path — cached client.
        if (_worktreeClients.TryGetValue(normalizedPath, out var existing))
            return existing;

        await _clientLock.WaitAsync(ct);
        try
        {
            // Re-check after lock
            if (_worktreeClients.TryGetValue(normalizedPath, out existing))
                return existing;

            var token = ResolveToken();
            var hasToken = !string.IsNullOrWhiteSpace(token);
            var hasCliPath = !string.IsNullOrWhiteSpace(_cliPath);

            _logger.LogInformation(
                "Starting worktree CopilotClient for {WorkspacePath} (token source: {Source})",
                normalizedPath, DescribeTokenSource(token));

            var options = new CopilotClientOptions { Cwd = normalizedPath };
            if (hasToken) options.GitHubToken = token;
            if (hasCliPath) options.CliPath = _cliPath;

            var client = new CopilotClient(options);
            await client.StartAsync();
            _worktreeClients[normalizedPath] = client;

            _logger.LogInformation(
                "Worktree CopilotClient started for {WorkspacePath}", normalizedPath);
            return client;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to start worktree CopilotClient for {WorkspacePath} — falling back to default client",
                normalizedPath);
            // Return null — caller (RunAsync) will fall back to stub.
            // We do NOT call EnsureClientAsync here to avoid deadlock
            // (it also acquires _clientLock).
            return null;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <summary>
    /// Disposes a worktree-scoped client and all sessions that belong to it.
    /// Called when a worktree is removed (task complete/cancelled).
    /// </summary>
    public async Task DisposeWorktreeClientAsync(string workspacePath)
    {
        var normalizedPath = Path.GetFullPath(workspacePath);
        if (!_worktreeClients.TryRemove(normalizedPath, out var client))
            return;

        // Remove sessions that were created on this client
        var prefix = $"wt:{normalizedPath}:";
        var keys = _sessions.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var key in keys)
        {
            if (_sessions.TryRemove(key, out var entry))
            {
                _sessionLocks.TryRemove(key, out _);
                await DisposeSessionSafe(entry);
            }
        }

        try { await client.DisposeAsync(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing worktree CopilotClient for {Path}", normalizedPath);
        }

        _logger.LogInformation("Disposed worktree CopilotClient for {WorkspacePath}", normalizedPath);
    }

    /// <summary>
    /// Resolves the best available GitHub token.
    /// Priority: user OAuth token → config token → null (env/CLI fallback).
    /// </summary>
    private string? ResolveToken()
    {
        // 1. User's OAuth token (captured at login, survives background orchestration)
        var userToken = _tokenProvider.Token;
        if (!string.IsNullOrWhiteSpace(userToken))
            return userToken;

        // 2. Static config token (Copilot:GitHubToken in appsettings / user-secrets)
        if (!string.IsNullOrWhiteSpace(_configToken))
            return _configToken;

        // 3. null → SDK falls back to env vars or CLI login
        return null;
    }

    private string DescribeTokenSource(string? token)
    {
        if (token is null) return "env/CLI login";
        if (token == _tokenProvider.Token) return "user OAuth";
        return "config";
    }

    private async Task DisposeClientSafe()
    {
        if (_client is not null)
        {
            var old = _client;
            _client = null;
            try { await old.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing old CopilotClient"); }
        }

        // Clear all sessions — they belong to the old client.
        foreach (var key in _sessions.Keys.ToList())
        {
            if (_sessions.TryRemove(key, out var entry))
            {
                _sessionLocks.TryRemove(key, out _);
                await DisposeSessionSafe(entry);
            }
        }

        // Also dispose all worktree clients — they used the old token.
        foreach (var kvp in _worktreeClients.ToArray())
        {
            if (_worktreeClients.TryRemove(kvp.Key, out var wtClient))
            {
                try { await wtClient.DisposeAsync(); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing worktree CopilotClient for {Path} during token rotation", kvp.Key);
                }
            }
        }
    }

    /// <summary>
    /// Returns an existing valid session or creates a new one, guarded
    /// by a per-key lock to prevent concurrent creation and session leaks.
    /// </summary>
    private async Task<SessionEntry> GetOrCreateSessionEntryAsync(
        CopilotClient client,
        AgentDefinition agent,
        string sessionKey,
        string? roomId,
        CancellationToken ct)
    {
        // Fast path — valid cached session.
        if (_sessions.TryGetValue(sessionKey, out var existing) && !existing.IsExpired)
        {
            existing.Touch();
            return existing;
        }

        // Slow path — per-key lock for creation.
        var keyLock = _sessionLocks.GetOrAdd(sessionKey, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(ct);
        try
        {
            // Re-check after acquiring lock.
            if (_sessions.TryGetValue(sessionKey, out existing) && !existing.IsExpired)
            {
                existing.Touch();
                return existing;
            }

            // Dispose expired entry if present.
            if (existing is not null)
            {
                _sessions.TryRemove(sessionKey, out _);
                await DisposeSessionSafe(existing);
            }

            _logger.LogDebug(
                "Creating new CopilotSession for agent {AgentId}, model={Model}, key={Key}",
                agent.Id, agent.Model ?? "default", sessionKey);

            var tools = _toolRegistry.GetToolsForAgent(agent.EnabledTools, agent.Id, agent.Name);
            var toolNames = new HashSet<string>(tools.Select(t => t.Name), StringComparer.Ordinal);

            var config = new SessionConfig
            {
                Model = agent.Model ?? "claude-opus-4.6",
                Streaming = true,
                Tools = [.. tools],
                OnPermissionRequest = PermissionHandler.ApproveAll,
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
                var primeResponse = await CollectResponse(session, agent.StartupPrompt, agent.Id, roomId, ct);
                _logger.LogDebug(
                    "Session primed for {AgentId}: {Length} chars",
                    agent.Id, primeResponse.Length);
            }

            var entry = new SessionEntry(session);
            _sessions[sessionKey] = entry;
            return entry;
        }
        finally
        {
            keyLock.Release();
        }
    }

    private async Task<string> SendAndCollectAsync(
        CopilotSession session,
        AgentDefinition agent,
        string prompt,
        string? roomId,
        CancellationToken ct)
    {
        _logger.LogDebug(
            "Sending prompt to {AgentId}: {PromptPreview}...",
            agent.Id,
            prompt.Length > 80 ? prompt[..80] : prompt);

        var response = await CollectResponse(session, prompt, agent.Id, roomId, ct);

        _logger.LogDebug(
            "Received response from {AgentId}: {Length} chars",
            agent.Id, response.Length);

        return response;
    }

    /// <summary>
    /// Wraps <see cref="SendAndCollectAsync"/> with retry logic for
    /// transient and quota errors. Auth errors are never retried.
    /// </summary>
    private async Task<string> SendAndCollectWithRetryAsync(
        CopilotSession session,
        AgentDefinition agent,
        string prompt,
        string? roomId,
        CancellationToken ct)
    {
        Exception? lastException = null;

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await SendAndCollectAsync(session, agent, prompt, roomId, ct);
            }
            catch (CopilotAuthException)
            {
                throw; // Never retry auth failures
            }
            catch (CopilotAuthorizationException)
            {
                throw; // Never retry authorization failures
            }
            catch (CopilotQuotaException ex)
            {
                lastException = ex;
                if (attempt >= QuotaMaxRetries)
                {
                    _logger.LogWarning(
                        "Quota/rate-limit error for {AgentId} after {Attempts} retries — giving up",
                        agent.Id, attempt + 1);
                    await _errorTracker.RecordAsync(agent.Id, roomId, "quota",
                        ex.Message, recoverable: false, retried: true, retryAttempt: attempt + 1);
                    throw;
                }

                var delay = QuotaBackoff[Math.Min(attempt, QuotaBackoff.Length - 1)];
                _logger.LogWarning(
                    "Quota/rate-limit error for {AgentId} (attempt {Attempt}/{Max}), retrying in {Delay}s: {Error}",
                    agent.Id, attempt + 1, QuotaMaxRetries, delay.TotalSeconds, ex.Message);
                await _errorTracker.RecordAsync(agent.Id, roomId, "quota",
                    ex.Message, recoverable: true, retried: true, retryAttempt: attempt + 1);
                await Task.Delay(delay, ct);
            }
            catch (CopilotTransientException ex)
            {
                lastException = ex;
                if (attempt >= TransientMaxRetries)
                {
                    _logger.LogWarning(
                        "Transient error for {AgentId} after {Attempts} retries — giving up",
                        agent.Id, attempt + 1);
                    await _errorTracker.RecordAsync(agent.Id, roomId, "transient",
                        ex.Message, recoverable: false, retried: true, retryAttempt: attempt + 1);
                    throw;
                }

                var delay = TransientBackoff[Math.Min(attempt, TransientBackoff.Length - 1)];
                _logger.LogWarning(
                    "Transient error for {AgentId} (attempt {Attempt}/{Max}), retrying in {Delay}s: {Error}",
                    agent.Id, attempt + 1, TransientMaxRetries, delay.TotalSeconds, ex.Message);
                await _errorTracker.RecordAsync(agent.Id, roomId, "transient",
                    ex.Message, recoverable: true, retried: true, retryAttempt: attempt + 1);
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>
    /// Sends a prompt and collects the complete streamed response.
    /// Classifies SDK errors by <c>ErrorType</c> into typed exceptions.
    /// </summary>
    private async Task<string> CollectResponse(
        CopilotSession session,
        string prompt,
        string agentId,
        string? roomId,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        var done = new TaskCompletionSource();
        AssistantUsageEvent? capturedUsage = null;

        using var registration = ct.Register(() => done.TrySetCanceled(ct));

        var unsubscribe = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent delta:
                    sb.Append(delta.Data.DeltaContent);
                    break;
                case AssistantMessageEvent msg:
                    // Final complete message — overwrite streamed content
                    // if we got a final aggregated version.
                    if (!string.IsNullOrEmpty(msg.Data.Content))
                    {
                        sb.Clear();
                        sb.Append(msg.Data.Content);
                    }
                    break;
                case AssistantUsageEvent usage:
                    capturedUsage = usage;
                    break;
                case SessionIdleEvent:
                    done.TrySetResult();
                    break;
                case SessionErrorEvent err:
                    done.TrySetException(ClassifyError(err));
                    break;
            }
        });

        try
        {
            await session.SendAsync(new MessageOptions { Prompt = prompt });

            // Wait for the response — no internal timeout.
            // Cancellation is handled by the registration at line above.
            await done.Task;

            // Persist usage metrics (fire-and-forget style — errors are caught internally)
            if (capturedUsage is not null)
            {
                var data = capturedUsage.Data;
                await _usageTracker.RecordAsync(
                    agentId, roomId,
                    data.Model,
                    data.InputTokens, data.OutputTokens,
                    data.CacheReadTokens, data.CacheWriteTokens,
                    data.Cost, data.Duration,
                    data.ApiCallId, data.Initiator,
                    data.ReasoningEffort);
            }

            return sb.ToString();
        }
        finally
        {
            unsubscribe.Dispose();
        }
    }

    /// <summary>
    /// Classifies a <see cref="SessionErrorEvent"/> into a typed exception
    /// based on the <c>ErrorType</c> field from the Copilot SDK.
    /// </summary>
    internal static Exception ClassifyError(SessionErrorEvent err)
    {
        var errorType = err.Data.ErrorType?.ToLowerInvariant();
        var message = err.Data.Message ?? "Unknown Copilot session error";

        return errorType switch
        {
            "authentication" => new CopilotAuthException(message),
            "authorization" => new CopilotAuthorizationException(message),
            "quota" => new CopilotQuotaException(errorType, message),
            "rate_limit" => new CopilotQuotaException(errorType, message),
            _ => new CopilotTransientException(message),
        };
    }

    private async Task CleanupExpiredSessionsAsync()
    {
        var expired = _sessions
            .Where(kvp => kvp.Value.IsExpired)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expired)
        {
            if (_sessions.TryRemove(key, out var entry))
            {
                _sessionLocks.TryRemove(key, out _);
                _logger.LogDebug("Cleaning up expired session {Key}", key);
                await DisposeSessionSafe(entry);
            }
        }

        if (expired.Count > 0)
            _logger.LogInformation("Cleaned up {Count} expired session(s)", expired.Count);
    }

    private async Task DisposeSessionSafe(SessionEntry entry)
    {
        try
        {
            await entry.Session.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing CopilotSession");
        }
    }

    private StubExecutor GetFallback()
    {
        return _fallback ??= new StubExecutor(_stubLogger);
    }

    private static string BuildKey(string agentId, string? roomId)
        => $"{agentId}:{roomId ?? "default"}";

    // ── Auth failure/recovery notifications ─────────────────────

    private async Task HandleAuthFailureAsync(string agentId, string? roomId)
    {
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

            using var scope = _scopeFactory.CreateScope();
            var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
            var roomId = runtime.DefaultRoomId;
            var roomMessage = degraded
                ? "⚠️ **Copilot SDK authentication failed.** The OAuth token has expired or been revoked. Please re-authenticate at `/api/auth/login` to restore agent functionality."
                : "✅ **Copilot SDK reconnected.** A new token has been provided — agents are coming back online.";
            var notification = degraded
                ? new NotificationMessage(
                    Type: NotificationType.Error,
                    Title: "Copilot SDK authentication degraded",
                    Body: "The GitHub auth probe received 401/403 from `GET /user`. Re-authenticate at `/api/auth/login` to restore agent functionality.",
                    RoomId: roomId)
                : new NotificationMessage(
                    Type: NotificationType.TaskComplete,
                    Title: "Copilot SDK authentication restored",
                    Body: "Copilot access is healthy again. Agents are coming back online.",
                    RoomId: roomId);

            await runtime.PostSystemStatusAsync(roomId, roomMessage);
            await _notificationManager.SendToAllAsync(notification, ct);

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

    // ── Session entry with TTL tracking ─────────────────────────

    private sealed class SessionEntry
    {
        public CopilotSession Session { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        private DateTime _lastUsed;

        public SessionEntry(CopilotSession session)
        {
            Session = session;
            _lastUsed = DateTime.UtcNow;
        }

        public bool IsExpired => DateTime.UtcNow - _lastUsed > SessionTtl;

        public void Touch() => _lastUsed = DateTime.UtcNow;
    }
}
