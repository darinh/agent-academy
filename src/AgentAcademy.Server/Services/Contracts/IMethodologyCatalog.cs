using AgentAcademy.Forge.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Catalog of saved methodology templates that users can browse and select
/// when starting a forge pipeline run.
/// </summary>
public interface IMethodologyCatalog
{
    /// <summary>List all valid methodologies in the catalog.</summary>
    Task<IReadOnlyList<MethodologySummary>> ListAsync(CancellationToken ct = default);

    /// <summary>Get a full methodology definition by its ID.</summary>
    Task<MethodologyDefinition?> GetAsync(string methodologyId, CancellationToken ct = default);

    /// <summary>
    /// Save a methodology to the catalog. The filename is derived from the methodology's ID.
    /// Returns the methodology ID on success.
    /// </summary>
    Task<string> SaveAsync(MethodologyDefinition methodology, CancellationToken ct = default);
}

/// <summary>Summary of a saved methodology for listing.</summary>
public sealed record MethodologySummary(
    string Id,
    string? Description,
    int PhaseCount,
    string? GenerationModel,
    string? JudgeModel,
    bool HasBudget,
    bool HasFidelity,
    bool HasControl);
