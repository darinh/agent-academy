using System.Collections.Concurrent;
using System.Text;
using AgentAcademy.Shared.Models;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Configuration;
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
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(2);

    private readonly ILogger<CopilotExecutor> _logger;
    private readonly ILogger<StubExecutor> _stubLogger;
    private readonly CopilotTokenProvider _tokenProvider;
    private readonly string? _configToken;
    private readonly string? _cliPath;
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private CopilotClient? _client;
    private string? _activeToken;
    private bool _clientFailed;
    private StubExecutor? _fallback;
    private Timer? _cleanupTimer;
    private bool _disposed;

    public CopilotExecutor(
        ILogger<CopilotExecutor> logger,
        ILogger<StubExecutor> stubLogger,
        IConfiguration configuration,
        CopilotTokenProvider tokenProvider)
    {
        _logger = logger;
        _stubLogger = stubLogger;
        _configToken = configuration["Copilot:GitHubToken"];
        _cliPath = configuration["Copilot:CliPath"];
        _tokenProvider = tokenProvider;
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

    public async Task<string> RunAsync(
        AgentDefinition agent,
        string prompt,
        string? roomId,
        CancellationToken ct = default)
    {
        var client = await EnsureClientAsync(ct);
        if (client is null)
        {
            _logger.LogDebug("Copilot client unavailable — delegating to StubExecutor");
            return await GetFallback().RunAsync(agent, prompt, roomId, ct);
        }

        var sessionKey = BuildKey(agent.Id, roomId);

        try
        {
            var entry = await GetOrCreateSessionEntryAsync(client, agent, sessionKey, ct);

            // Serialize sends through the same session to prevent
            // concurrent responses from interleaving.
            await entry.SendLock.WaitAsync(ct);
            try
            {
                var response = await SendAndCollectAsync(entry.Session, agent, prompt, ct);
                entry.Touch();
                return response;
            }
            finally
            {
                entry.SendLock.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Copilot call failed for agent {AgentId} in room {RoomId} — falling back to stub",
                agent.Id, roomId);

            // Invalidate the broken session so the next attempt gets a fresh one.
            await InvalidateSessionAsync(agent.Id, roomId);
            return await GetFallback().RunAsync(agent, prompt, roomId, ct);
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
            }

            // If we already failed with this exact token, don't retry.
            if (_clientFailed && _activeToken == token) return null;

            // Reset failure state for new token attempts.
            _clientFailed = false;
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
    }

    /// <summary>
    /// Returns an existing valid session or creates a new one, guarded
    /// by a per-key lock to prevent concurrent creation and session leaks.
    /// </summary>
    private async Task<SessionEntry> GetOrCreateSessionEntryAsync(
        CopilotClient client,
        AgentDefinition agent,
        string sessionKey,
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

            var config = new SessionConfig
            {
                Model = agent.Model ?? "gpt-5",
                Streaming = true,
                // Safe: no SDK tools are registered in the session config, so
                // no permission requests fire. Revisit with a restrictive handler
                // when tool calling is wired up (see spec Known Gaps).
                OnPermissionRequest = PermissionHandler.ApproveAll,
            };

            var session = await client.CreateSessionAsync(config);

            // Prime the session with the agent's startup prompt if provided.
            if (!string.IsNullOrWhiteSpace(agent.StartupPrompt))
            {
                var primeResponse = await CollectResponse(session, agent.StartupPrompt, ct);
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
        CancellationToken ct)
    {
        _logger.LogDebug(
            "Sending prompt to {AgentId}: {PromptPreview}...",
            agent.Id,
            prompt.Length > 80 ? prompt[..80] : prompt);

        var response = await CollectResponse(session, prompt, ct);

        _logger.LogDebug(
            "Received response from {AgentId}: {Length} chars",
            agent.Id, response.Length);

        return response;
    }

    /// <summary>
    /// Sends a prompt and collects the complete streamed response.
    /// </summary>
    private async Task<string> CollectResponse(
        CopilotSession session,
        string prompt,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        var done = new TaskCompletionSource();

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
                case SessionIdleEvent:
                    done.TrySetResult();
                    break;
                case SessionErrorEvent err:
                    done.TrySetException(
                        new InvalidOperationException($"Copilot session error: {err.Data.Message}"));
                    break;
            }
        });

        try
        {
            await session.SendAsync(new MessageOptions { Prompt = prompt });

            // Use a separate CTS for the timeout so cancellation of the caller's
            // token doesn't produce a misleading TimeoutException.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(RequestTimeout);
            var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);

            var completedTask = await Task.WhenAny(done.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                // Distinguish caller cancellation from actual timeout.
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException(
                    $"Copilot response timed out after {RequestTimeout.TotalSeconds}s");
            }

            await done.Task; // Re-await to propagate exceptions
            return sb.ToString();
        }
        finally
        {
            unsubscribe.Dispose();
        }
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
