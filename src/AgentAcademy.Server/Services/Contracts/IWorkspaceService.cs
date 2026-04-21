using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Abstraction for workspace management: listing, activation, and metadata.
/// </summary>
public interface IWorkspaceService
{
    Task<WorkspaceMeta?> GetActiveWorkspaceAsync();
    Task<List<WorkspaceMeta>> ListWorkspacesAsync();
    Task<WorkspaceMeta> ActivateWorkspaceAsync(ProjectScanResult scan);
}
