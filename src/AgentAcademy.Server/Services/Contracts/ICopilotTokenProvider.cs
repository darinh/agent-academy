namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Singleton store for OAuth access/refresh tokens. Consumers that only need
/// the current token value should depend on this interface rather than the
/// concrete <see cref="CopilotTokenProvider"/>.
/// </summary>
public interface ICopilotTokenProvider
{
    /// <summary>
    /// Raised when a new token is set. Subscribers (e.g., the auth monitor)
    /// can use this to trigger an immediate probe instead of waiting for
    /// the next scheduled interval.
    /// </summary>
    event Action? TokenChanged;

    /// <summary>
    /// The most recently captured OAuth access token, or null if no
    /// user has authenticated since server start.
    /// </summary>
    string? Token { get; }

    /// <summary>
    /// The refresh token for obtaining new access tokens, or null if not available.
    /// </summary>
    string? RefreshToken { get; }

    /// <summary>
    /// UTC timestamp when the token was last set, or null if never set.
    /// </summary>
    DateTime? TokenSetAt { get; }

    /// <summary>
    /// UTC timestamp when the current access token expires, or null if unknown.
    /// </summary>
    DateTime? ExpiresAtUtc { get; }

    /// <summary>
    /// UTC timestamp when the refresh token expires, or null if unknown.
    /// </summary>
    DateTime? RefreshTokenExpiresAtUtc { get; }

    /// <summary>
    /// True when tokens were refreshed server-side and the auth cookie
    /// needs to be updated on the next HTTP request.
    /// </summary>
    bool HasPendingCookieUpdate { get; }

    /// <summary>
    /// Whether the current access token is approaching expiry (within 30 minutes)
    /// or has already expired. Returns false if no expiry information is available.
    /// </summary>
    bool IsTokenExpiringSoon { get; }

    /// <summary>
    /// Whether we have a refresh token that hasn't expired.
    /// </summary>
    bool CanRefresh { get; }

    /// <summary>
    /// Called during OAuth login to capture the user's access token.
    /// Backward-compatible overload for code that doesn't have refresh token info.
    /// </summary>
    void SetToken(string token);

    /// <summary>
    /// Called during OAuth login or token refresh to store all token data.
    /// </summary>
    void SetTokens(
        string accessToken,
        string? refreshToken = null,
        TimeSpan? expiresIn = null,
        TimeSpan? refreshTokenExpiresIn = null);

    /// <summary>
    /// Marks that a server-side token refresh occurred and the auth cookie
    /// should be updated on the next HTTP request.
    /// </summary>
    void MarkCookieUpdatePending();

    /// <summary>
    /// Clears the pending cookie update flag after the middleware has updated the cookie.
    /// </summary>
    void ClearCookieUpdatePending();

    /// <summary>
    /// Called on logout to clear all stored tokens.
    /// </summary>
    void ClearToken();
}
