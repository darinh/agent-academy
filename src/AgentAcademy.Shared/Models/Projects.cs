namespace AgentAcademy.Shared.Models;

/// <summary>
/// Result of scanning a local project directory, capturing tech stack,
/// git state, and detected configuration files.
/// </summary>
public record ProjectScanResult(
    string Path,
    string? ProjectName,
    List<string> TechStack,
    bool HasSpecs,
    bool HasReadme,
    bool IsGitRepo,
    string? GitBranch,
    List<string> DetectedFiles
);

/// <summary>
/// Metadata about a workspace, used for project selection and display.
/// </summary>
public record WorkspaceMeta(
    string Path,
    string? ProjectName,
    DateTime? LastAccessedAt = null
);
