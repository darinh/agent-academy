using AgentAcademy.Forge.Models;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Abstraction over <see cref="ForgeRunService"/> for command handlers and testing.
/// Exposes only the job-management surface the command system needs.
/// </summary>
public interface IForgeJobService
{
    /// <summary>Enqueue a new forge run. Returns immediately with the job.</summary>
    Task<ForgeJob> StartRunAsync(TaskBrief task, MethodologyDefinition methodology);

    /// <summary>Get a job by ID, or null if not found.</summary>
    ForgeJob? GetJob(string jobId);

    /// <summary>List all jobs, most recent first.</summary>
    IReadOnlyList<ForgeJob> ListJobs();
}
