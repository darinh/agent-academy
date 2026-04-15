namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Handles workspace–room orchestration: ensuring default workspace rooms exist
/// and resolving the startup main-room identifier.
/// </summary>
public interface IWorkspaceRoomService
{
    /// <summary>
    /// Ensures a default room exists for the given workspace, moving agents into it
    /// as needed. Returns the default room ID.
    /// </summary>
    Task<string> EnsureDefaultRoomForWorkspaceAsync(string workspacePath);

    /// <summary>
    /// Resolves the main room ID to use at startup for the active workspace.
    /// </summary>
    Task<string> ResolveStartupMainRoomIdAsync(string? activeWorkspace);
}
