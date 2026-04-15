namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Synthesizes retrospective summaries into cross-cutting shared memories.
/// </summary>
public interface ILearningDigestService
{
    /// <summary>
    /// Attempts to generate a learning digest. If <paramref name="force"/> is false,
    /// only generates when undigested retrospectives meet the configured threshold.
    /// Returns the digest ID if one was created, or null if skipped.
    /// </summary>
    Task<int?> TryGenerateDigestAsync(bool force = false);
}
