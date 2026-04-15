using System.Collections.Concurrent;
using AgentAcademy.Server.Services.Contracts;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Result of attempting to acquire a <see cref="CopilotClient"/>.
/// <paramref name="WasRecreated"/> is true when the client was disposed
/// and rebuilt (e.g., because the auth token changed). Callers use this
/// to reset request-level state such as circuit breakers.
/// </summary>
public sealed record ClientAcquisitionResult(CopilotClient? Client, bool WasRecreated);

/// <summary>
/// Manages the lifecycle of <see cref="CopilotClient"/> instances —
/// one default client and zero-or-more worktree-scoped clients.
/// Owns token resolution, client creation/disposal, and token-rotation
/// detection for both client pools.
///
/// Extracted from CopilotExecutor to isolate client-lifecycle concerns
/// from session management, retry logic, and auth-state transitions.
/// </summary>
public sealed class CopilotClientFactory : ICopilotClientFactory
{
    private readonly ILogger<CopilotClientFactory> _logger;
    private readonly ICopilotTokenProvider _tokenProvider;
    private readonly string? _configToken;
    private readonly string? _cliPath;

    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private CopilotClient? _client;
    private readonly ConcurrentDictionary<string, CopilotClient> _worktreeClients = new();
    private string? _activeToken;
    private bool _clientFailed;
    private bool _disposed;

    public CopilotClientFactory(
        ILogger<CopilotClientFactory> logger,
        IConfiguration configuration,
        ICopilotTokenProvider tokenProvider)
    {
        _logger = logger;
        _tokenProvider = tokenProvider;
        _configToken = configuration["Copilot:GitHubToken"];
        _cliPath = configuration["Copilot:CliPath"];
    }

    /// <summary>
    /// True once the default <see cref="CopilotClient"/> has been
    /// successfully started and hasn't failed.
    /// </summary>
    public bool IsDefaultClientOperational => _client is not null && !_clientFailed;

    /// <summary>
    /// Acquires the default (non-worktree) client. Creates it on first
    /// call; recreates if the auth token has changed since last creation.
    /// </summary>
    public async Task<ClientAcquisitionResult> GetClientAsync(CancellationToken ct)
    {
        await _clientLock.WaitAsync(ct);
        try
        {
            var token = ResolveToken();
            var wasRecreated = false;

            // Existing client with matching token — reuse.
            if (_client is not null && !_clientFailed && _activeToken == token)
                return new ClientAcquisitionResult(_client, WasRecreated: false);

            // Token changed since last client creation — dispose old client
            // and reset failure state so we try the new token.
            if (_client is not null && _activeToken != token)
            {
                _logger.LogInformation(
                    "Token changed — recreating CopilotClient (old source: {Old}, new source: {New})",
                    DescribeTokenSource(_activeToken),
                    DescribeTokenSource(token));

                await DisposeAllClientsUnsafe();
                wasRecreated = true;
            }

            // If we already failed with this exact token, don't retry.
            if (_clientFailed && _activeToken == token)
                return new ClientAcquisitionResult(null, WasRecreated: false);

            // Reset failure state for new token attempts.
            _clientFailed = false;
            _activeToken = token;

            var client = await CreateClientAsync(token, cwd: null);
            if (client is null)
                return new ClientAcquisitionResult(null, wasRecreated);

            _client = client;
            return new ClientAcquisitionResult(_client, wasRecreated);
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <summary>
    /// Acquires a client scoped to a git worktree directory.
    /// Detects token rotation and invalidates all worktree clients
    /// (same as the default client) to prevent stale-token usage.
    /// </summary>
    public async Task<ClientAcquisitionResult> GetWorktreeClientAsync(
        string workspacePath, CancellationToken ct)
    {
        var normalizedPath = Path.GetFullPath(workspacePath);

        await _clientLock.WaitAsync(ct);
        try
        {
            var token = ResolveToken();
            var wasRecreated = false;

            // Token rotation: if the token changed, dispose ALL clients
            // (default + worktree) so everything uses the new token.
            if (_activeToken is not null && _activeToken != token)
            {
                _logger.LogInformation(
                    "Token changed — recreating all clients including worktrees (old source: {Old}, new source: {New})",
                    DescribeTokenSource(_activeToken),
                    DescribeTokenSource(token));

                await DisposeAllClientsUnsafe();
                wasRecreated = true;
            }

            // Always track the current token so rotation detection works
            // even when a worktree client is created before the default client.
            _activeToken = token;

            // Fast path — cached client with current token.
            if (_worktreeClients.TryGetValue(normalizedPath, out var existing))
                return new ClientAcquisitionResult(existing, wasRecreated);

            var client = await CreateClientAsync(token, cwd: normalizedPath);
            if (client is null)
                return new ClientAcquisitionResult(null, wasRecreated);

            _worktreeClients[normalizedPath] = client;
            return new ClientAcquisitionResult(client, wasRecreated);
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <summary>
    /// Disposes a worktree-scoped client. Called when a worktree is
    /// removed (task complete/cancelled). Returns the session key
    /// prefix so callers can invalidate matching sessions.
    /// </summary>
    public async Task<string?> DisposeWorktreeClientAsync(string workspacePath)
    {
        var normalizedPath = Path.GetFullPath(workspacePath);
        if (!_worktreeClients.TryRemove(normalizedPath, out var client))
            return null;

        try { await client.DisposeAsync(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing worktree CopilotClient for {Path}", normalizedPath);
        }

        _logger.LogInformation("Disposed worktree CopilotClient for {WorkspacePath}", normalizedPath);
        return $"wt:{normalizedPath}:";
    }

    /// <summary>
    /// Resolves the best available GitHub token.
    /// Priority: user OAuth token → config token → null (env/CLI fallback).
    /// </summary>
    /// <remarks>
    /// Intentionally returns null (not env vars) because the SDK handles
    /// env-var fallback internally. CopilotAuthProbe.ResolveToken checks env
    /// vars explicitly because it bypasses the SDK for raw HTTP probes.
    /// </remarks>
    internal string? ResolveToken()
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

    internal string DescribeTokenSource(string? token)
    {
        if (token is null) return "env/CLI login";
        if (token == _tokenProvider.Token) return "user OAuth";
        return "config";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_client is not null)
        {
            try { await _client.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing CopilotClient"); }
            _client = null;
        }

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

    // ── Private helpers ─────────────────────────────────────────

    private async Task<CopilotClient?> CreateClientAsync(string? token, string? cwd)
    {
        var hasToken = !string.IsNullOrWhiteSpace(token);
        var hasCliPath = !string.IsNullOrWhiteSpace(_cliPath);
        var label = cwd is not null ? $"worktree ({cwd})" : "default";

        _logger.LogInformation(
            "Starting {Label} CopilotClient (token source: {Source}, CLI: {Cli})...",
            label, DescribeTokenSource(token),
            hasCliPath ? _cliPath : "bundled");

        try
        {
            var options = new CopilotClientOptions();
            if (hasToken) options.GitHubToken = token;
            if (hasCliPath) options.CliPath = _cliPath;
            if (cwd is not null) options.Cwd = cwd;

            var client = new CopilotClient(options);
            await client.StartAsync();
            _logger.LogInformation("{Label} CopilotClient started successfully", label);
            return client;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start {Label} CopilotClient", label);
            if (cwd is null) _clientFailed = true;
            return null;
        }
    }

    /// <summary>
    /// Disposes the default client and ALL worktree clients.
    /// Must be called under <see cref="_clientLock"/>.
    /// </summary>
    private async Task DisposeAllClientsUnsafe()
    {
        if (_client is not null)
        {
            var old = _client;
            _client = null;
            try { await old.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing old CopilotClient"); }
        }

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

        _clientFailed = false;
    }
}
