namespace AgentAcademy.Server.Config;

/// <summary>
/// Indicates whether GitHub OAuth is configured and enabled.
/// Registered as a singleton for controllers to check.
/// </summary>
public record GitHubAuthOptions(bool Enabled, string FrontendUrl = "http://localhost:5173");
