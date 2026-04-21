namespace AgentAcademy.Server.Config;

/// <summary>
/// Configuration for the Forge Pipeline Engine integration.
/// Bound from the "Forge" section of appsettings.json.
/// </summary>
public sealed class ForgeOptions
{
    public const string SectionName = "Forge";

    /// <summary>Whether the Forge engine is registered at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Root directory for forge runs. Resolved relative to ContentRootPath.
    /// Default: "forge-runs".
    /// </summary>
    public string RunsDirectory { get; set; } = "forge-runs";

    /// <summary>
    /// Root directory for saved methodology templates. Resolved relative to ContentRootPath.
    /// Default: "methodologies".
    /// </summary>
    public string MethodologiesDirectory { get; set; } = "methodologies";

    /// <summary>
    /// OpenAI API key for LLM calls. When empty, execution endpoints return 503
    /// but read-only endpoints (list runs, get artifacts, schemas) remain available.
    /// Can also be set via user-secrets or FORGE__OPENAIAPIKEYENVAR environment variable.
    /// </summary>
    public string? OpenAiApiKey { get; set; }

    /// <summary>
    /// OpenAI API base URL. Defaults to https://api.openai.com/v1.
    /// </summary>
    public string? OpenAiBaseUrl { get; set; }

    /// <summary>Whether LLM execution is available (API key is configured).</summary>
    public bool ExecutionAvailable => !string.IsNullOrWhiteSpace(OpenAiApiKey);
}
