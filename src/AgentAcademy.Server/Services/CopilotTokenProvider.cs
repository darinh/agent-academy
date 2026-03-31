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
    private DateTime? _tokenSetAt;

    /// <summary>
    /// The most recently captured OAuth access token, or null if no
    /// user has authenticated since server start.
    /// </summary>
    public string? Token => _token;

    /// <summary>
    /// UTC timestamp when the token was last set, or null if never set.
    /// Used for diagnostics and health reporting.
    /// </summary>
    public DateTime? TokenSetAt => _tokenSetAt;

    /// <summary>
    /// Called during OAuth login to capture the user's access token.
    /// </summary>
    public void SetToken(string token)
    {
        _token = token;
        _tokenSetAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Called on logout to clear the stored token.
    /// </summary>
    public void ClearToken()
    {
        _token = null;
        _tokenSetAt = null;
    }
}
