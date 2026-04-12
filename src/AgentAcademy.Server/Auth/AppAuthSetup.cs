namespace AgentAcademy.Server.Auth;

/// <summary>
/// Precomputed authentication configuration derived from <see cref="IConfiguration"/>.
/// Created before DI registration so both Program.cs and extension methods
/// can reference the same flags without re-reading config.
/// </summary>
public sealed record AppAuthSetup(
    bool GitHubAuthEnabled,
    bool ConsultantAuthEnabled,
    string GitHubFrontendUrl)
{
    public bool AnyAuthEnabled => GitHubAuthEnabled || ConsultantAuthEnabled;

    public string GitHubClientId { get; init; } = "";
    public string GitHubClientSecret { get; init; } = "";
    public string GitHubCallbackPath { get; init; } = "/api/auth/callback";

    public static AppAuthSetup FromConfiguration(IConfiguration configuration)
    {
        var clientId = configuration["GitHub:ClientId"] ?? "";
        var clientSecret = configuration["GitHub:ClientSecret"] ?? "";
        var gitHubEnabled = !string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret);

        var consultantSecret = configuration["ConsultantApi:SharedSecret"] ?? "";
        var consultantEnabled = !string.IsNullOrEmpty(consultantSecret);

        var frontendUrl = configuration["GitHub:FrontendUrl"] ?? "http://localhost:5173";
        var callbackPath = configuration["GitHub:CallbackPath"] ?? "/api/auth/callback";

        return new AppAuthSetup(gitHubEnabled, consultantEnabled, frontendUrl)
        {
            GitHubClientId = clientId,
            GitHubClientSecret = clientSecret,
            GitHubCallbackPath = callbackPath,
        };
    }
}
