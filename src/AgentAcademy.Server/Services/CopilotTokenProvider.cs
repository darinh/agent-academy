namespace AgentAcademy.Server.Services;

/// <summary>
/// Singleton store for the OAuth access token of the most recently
/// authenticated user. The CopilotExecutor queries this to get a
/// GitHub token for the SDK without requiring HttpContext (which is
/// unavailable during background orchestration).
/// </summary>
public sealed class CopilotTokenProvider
{
    private volatile string? _token;
    private volatile string? _refreshToken;
    private DateTime? _tokenSetAt;
    private DateTime? _expiresAtUtc;
    private DateTime? _refreshTokenExpiresAtUtc;
    private volatile bool _hasPendingCookieUpdate;

    /// <summary>
    /// Raised when a new token is set. Subscribers (e.g., the auth monitor)
    /// can use this to trigger an immediate probe instead of waiting for
    /// the next scheduled interval.
    /// </summary>
    public event Action? TokenChanged;

    /// <summary>
    /// The most recently captured OAuth access token, or null if no
    /// user has authenticated since server start.
    /// </summary>
    public string? Token => _token;

    /// <summary>
    /// The refresh token for obtaining new access tokens, or null if not available.
    /// GitHub App user-to-server tokens expire after 8 hours; the refresh token
    /// (valid for 6 months) can be exchanged for a new access token.
    /// </summary>
    public string? RefreshToken => _refreshToken;

    /// <summary>
    /// UTC timestamp when the token was last set, or null if never set.
    /// Used for diagnostics and health reporting.
    /// </summary>
    public DateTime? TokenSetAt => _tokenSetAt;

    /// <summary>
    /// UTC timestamp when the current access token expires, or null if unknown.
    /// </summary>
    public DateTime? ExpiresAtUtc => _expiresAtUtc;

    /// <summary>
    /// UTC timestamp when the refresh token expires, or null if unknown.
    /// </summary>
    public DateTime? RefreshTokenExpiresAtUtc => _refreshTokenExpiresAtUtc;

    /// <summary>
    /// True when tokens were refreshed server-side and the auth cookie
    /// needs to be updated on the next HTTP request.
    /// </summary>
    public bool HasPendingCookieUpdate => _hasPendingCookieUpdate;

    /// <summary>
    /// Whether the current access token is approaching expiry (within 30 minutes)
    /// or has already expired. Returns false if no expiry information is available.
    /// </summary>
    public bool IsTokenExpiringSoon
    {
        get
        {
            if (_expiresAtUtc is null) return false;
            return DateTime.UtcNow >= _expiresAtUtc.Value.AddMinutes(-30);
        }
    }

    /// <summary>
    /// Whether we have a refresh token that hasn't expired.
    /// </summary>
    public bool CanRefresh
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_refreshToken)) return false;
            if (_refreshTokenExpiresAtUtc is not null && DateTime.UtcNow >= _refreshTokenExpiresAtUtc.Value)
                return false;
            return true;
        }
    }

    /// <summary>
    /// Called during OAuth login to capture the user's access token.
    /// Backward-compatible overload for code that doesn't have refresh token info.
    /// </summary>
    public void SetToken(string token)
    {
        _token = token;
        _tokenSetAt = DateTime.UtcNow;

        // Fire-and-forget: don't let subscriber exceptions break the login flow
        try { TokenChanged?.Invoke(); }
        catch { /* subscribers handle their own errors */ }
    }

    /// <summary>
    /// Called during OAuth login or token refresh to store all token data.
    /// </summary>
    public void SetTokens(
        string accessToken,
        string? refreshToken = null,
        TimeSpan? expiresIn = null,
        TimeSpan? refreshTokenExpiresIn = null)
    {
        _token = accessToken;
        _refreshToken = refreshToken ?? _refreshToken;
        _tokenSetAt = DateTime.UtcNow;
        _expiresAtUtc = expiresIn.HasValue ? DateTime.UtcNow.Add(expiresIn.Value) : _expiresAtUtc;
        _refreshTokenExpiresAtUtc = refreshTokenExpiresIn.HasValue
            ? DateTime.UtcNow.Add(refreshTokenExpiresIn.Value)
            : _refreshTokenExpiresAtUtc;

        try { TokenChanged?.Invoke(); }
        catch { /* subscribers handle their own errors */ }
    }

    /// <summary>
    /// Marks that a server-side token refresh occurred and the auth cookie
    /// should be updated on the next HTTP request.
    /// </summary>
    public void MarkCookieUpdatePending() => _hasPendingCookieUpdate = true;

    /// <summary>
    /// Clears the pending cookie update flag after the middleware has updated the cookie.
    /// </summary>
    public void ClearCookieUpdatePending() => _hasPendingCookieUpdate = false;

    /// <summary>
    /// Called on logout to clear all stored tokens.
    /// </summary>
    public void ClearToken()
    {
        _token = null;
        _refreshToken = null;
        _tokenSetAt = null;
        _expiresAtUtc = null;
        _refreshTokenExpiresAtUtc = null;
        _hasPendingCookieUpdate = false;
    }
}
