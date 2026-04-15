using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Scans a local project directory to detect its tech stack, git state, and configuration.
/// </summary>
public interface IProjectScanner
{
    /// <summary>
    /// Scans a directory and returns a <see cref="ProjectScanResult"/>.
    /// </summary>
    ProjectScanResult ScanProject(string dirPath);
}
