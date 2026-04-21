using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Workspace persistence — CRUD and activation.
/// Orchestration of side effects (session archival, executor invalidation)
/// remains in the controller.
/// </summary>
public sealed class WorkspaceService : IWorkspaceService
{
    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<WorkspaceService> _logger;

    public WorkspaceService(AgentAcademyDbContext db, ILogger<WorkspaceService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Gets the currently active workspace, or null if none.
    /// </summary>
    public async Task<WorkspaceMeta?> GetActiveWorkspaceAsync()
    {
        var entity = await _db.Workspaces.FirstOrDefaultAsync(w => w.IsActive);
        return entity is null ? null : ToMeta(entity);
    }

    /// <summary>
    /// Lists all known workspaces ordered by most-recently-accessed first.
    /// </summary>
    public async Task<List<WorkspaceMeta>> ListWorkspacesAsync()
    {
        var entities = await _db.Workspaces
            .OrderByDescending(w => w.LastAccessedAt)
            .ToListAsync();
        return entities.Select(ToMeta).ToList();
    }

    /// <summary>
    /// Activates a workspace: deactivates all others, upserts the target,
    /// marks it active, and trims history to 20 workspaces.
    /// </summary>
    public async Task<WorkspaceMeta> ActivateWorkspaceAsync(ProjectScanResult scan)
    {
        var now = DateTime.UtcNow;

        await using var transaction = await _db.Database.BeginTransactionAsync();

        await _db.Workspaces
            .Where(w => w.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.IsActive, false));

        var entity = await _db.Workspaces.FindAsync(scan.Path);
        if (entity is null)
        {
            entity = new WorkspaceEntity
            {
                Path = scan.Path,
                ProjectName = scan.ProjectName,
                IsActive = true,
                LastAccessedAt = now,
                CreatedAt = now,
                RepositoryUrl = scan.RepositoryUrl,
                DefaultBranch = scan.DefaultBranch,
                HostProvider = scan.HostProvider
            };
            _db.Workspaces.Add(entity);
        }
        else
        {
            entity.ProjectName = scan.ProjectName;
            entity.IsActive = true;
            entity.LastAccessedAt = now;
            if (scan.RepositoryUrl is not null) entity.RepositoryUrl = scan.RepositoryUrl;
            if (scan.DefaultBranch is not null) entity.DefaultBranch = scan.DefaultBranch;
            if (scan.HostProvider is not null) entity.HostProvider = scan.HostProvider;
        }

        await _db.SaveChangesAsync();

        var count = await _db.Workspaces.CountAsync();
        if (count > 20)
        {
            var stale = await _db.Workspaces
                .Where(w => !w.IsActive)
                .OrderBy(w => w.LastAccessedAt)
                .Take(count - 20)
                .ToListAsync();
            _db.Workspaces.RemoveRange(stale);
            await _db.SaveChangesAsync();
        }

        await transaction.CommitAsync();
        return ToMeta(entity);
    }

    private static WorkspaceMeta ToMeta(WorkspaceEntity entity) =>
        new(Path: entity.Path,
            ProjectName: entity.ProjectName,
            LastAccessedAt: entity.LastAccessedAt,
            RepositoryUrl: entity.RepositoryUrl,
            DefaultBranch: entity.DefaultBranch,
            HostProvider: entity.HostProvider);
}
