using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Services;

internal sealed class CopilotAuthMonitorService : BackgroundService
{
    internal static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(5);

    private readonly ICopilotAuthProbe _probe;
    private readonly IAgentExecutor _executor;
    private readonly ICopilotTokenProvider _tokenProvider;
    private readonly ILogger<CopilotAuthMonitorService> _logger;
    private readonly SemaphoreSlim _probeTrigger = new(0, 1);

    public CopilotAuthMonitorService(
        ICopilotAuthProbe probe,
        IAgentExecutor executor,
        ICopilotTokenProvider tokenProvider,
        ILogger<CopilotAuthMonitorService> logger)
    {
        _probe = probe;
        _executor = executor;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _tokenProvider.TokenChanged += OnTokenChanged;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProbeOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Swallow unexpected exceptions so the monitor keeps running.
                    // Without this, any unhandled error (e.g., ObjectDisposedException,
                    // JsonException) propagates to the host and — with the default
                    // StopHost behavior — kills the entire server.
                    _logger.LogWarning(ex, "Unexpected error during auth probe — will retry on next cycle");
                }

                // Wait for either the interval or an early trigger from TokenChanged
                try
                {
                    await _probeTrigger.WaitAsync(ProbeInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _tokenProvider.TokenChanged -= OnTokenChanged;
        }
    }

    private void OnTokenChanged()
    {
        _logger.LogInformation("Token changed — triggering immediate auth probe");
        // Release the semaphore to wake the loop; TryRelease avoids overflow
        try { _probeTrigger.Release(); }
        catch (SemaphoreFullException) { /* already triggered */ }
    }

    internal async Task ProbeOnceAsync(CancellationToken ct = default)
    {
        // Proactively refresh before the token expires
        if (_tokenProvider.IsTokenExpiringSoon && _tokenProvider.CanRefresh)
        {
            _logger.LogInformation("Access token is expiring soon — attempting proactive refresh");
            if (await TryRefreshTokenAsync(ct))
                return; // Refresh succeeded; token is fresh, no need to probe
        }

        var result = await _probe.ProbeAsync(ct);
        switch (result)
        {
            case CopilotAuthProbeResult.Healthy:
                await _executor.MarkAuthOperationalAsync(ct);
                break;
            case CopilotAuthProbeResult.AuthFailed:
                // Try to refresh before degrading
                if (_tokenProvider.CanRefresh)
                {
                    _logger.LogInformation("Auth probe failed — attempting token refresh before degrading");
                    if (await TryRefreshTokenAsync(ct))
                        return; // Refresh succeeded; re-probe will happen on next cycle via TokenChanged
                }
                await _executor.MarkAuthDegradedAsync(ct);
                break;
            case CopilotAuthProbeResult.TransientFailure:
                _logger.LogDebug("Ignoring transient Copilot auth probe failure");
                break;
            case CopilotAuthProbeResult.Skipped:
                _logger.LogDebug("Skipping Copilot auth probe transition because no token is available");
                break;
        }
    }

    /// <summary>
    /// Attempts to refresh the access token using the stored refresh token.
    /// On success, updates the token provider (which triggers TokenChanged → re-probe).
    /// Returns true if the refresh succeeded.
    /// </summary>
    internal async Task<bool> TryRefreshTokenAsync(CancellationToken ct = default)
    {
        var refreshToken = _tokenProvider.RefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
            return false;

        var result = await _probe.RefreshTokenAsync(refreshToken, ct);
        if (result is null)
        {
            _logger.LogWarning("Token refresh failed — refresh token may be expired or revoked");
            return false;
        }

        // Update the token provider with the new tokens.
        // Note: GitHub rotates refresh tokens on each use, so we always store the new one.
        _tokenProvider.SetTokens(
            result.AccessToken,
            result.RefreshToken,
            result.ExpiresIn,
            result.RefreshTokenExpiresIn);
        _tokenProvider.MarkCookieUpdatePending();

        _logger.LogInformation("Token refresh succeeded — access token renewed");
        await _executor.MarkAuthOperationalAsync(ct);
        return true;
    }
}
