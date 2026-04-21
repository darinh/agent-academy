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
    /// Kill switch for Forge execution paths.
    /// When false, execution endpoints return 503 while read-only endpoints remain available.
    /// </summary>
    public bool ExecutionEnabled { get; set; } = true;

    /// <summary>Whether Forge execution paths are available.</summary>
    public bool ExecutionAvailable => ExecutionEnabled;
}
