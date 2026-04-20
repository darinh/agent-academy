using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Schemas;

namespace AgentAcademy.Forge.Validation;

/// <summary>
/// Orchestrates the three-tier validation cascade:
/// Structural → Semantic → CrossArtifact.
/// Short-circuits at the first tier with blocking failures.
/// </summary>
public sealed class ValidatorPipeline
{
    private readonly StructuralValidator _structural;
    private readonly SemanticValidator _semantic;
    private readonly CrossArtifactValidator _crossArtifact;
    private readonly SchemaRegistry _schemas;

    public ValidatorPipeline(
        StructuralValidator structural,
        SemanticValidator semantic,
        CrossArtifactValidator crossArtifact,
        SchemaRegistry schemas)
    {
        _structural = structural;
        _semantic = semantic;
        _crossArtifact = crossArtifact;
        _schemas = schemas;
    }

    /// <summary>
    /// Run the full validation cascade. Returns all findings up to and including
    /// the first tier that produced blocking failures.
    /// </summary>
    /// <param name="envelope">Artifact to validate.</param>
    /// <param name="inputArtifacts">Upstream artifacts for cross-artifact validation.</param>
    /// <param name="attemptNumber">Current attempt number.</param>
    /// <param name="judgeModel">Model for semantic validation, or null to use the default.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ValidationPipelineResult> ValidateAsync(
        ArtifactEnvelope envelope,
        IReadOnlyDictionary<string, ArtifactEnvelope> inputArtifacts,
        int attemptNumber,
        string? judgeModel = null,
        CancellationToken ct = default)
    {
        var allFindings = new List<ValidatorResultTrace>();
        var schemaEntry = _schemas.GetSchema($"{envelope.ArtifactType}/v{envelope.SchemaVersion}");

        // Tier 1: Structural
        var structuralResults = _structural.Validate(envelope, attemptNumber);
        allFindings.AddRange(structuralResults);

        if (HasBlockingFindings(structuralResults))
        {
            return new ValidationPipelineResult
            {
                Findings = allFindings,
                StoppedAtTier = ValidatorPhase.Structural,
                Passed = false
            };
        }

        // Tier 2: Semantic (LLM judge)
        var semanticResults = await _semantic.ValidateAsync(envelope, schemaEntry, attemptNumber, judgeModel, ct);
        allFindings.AddRange(semanticResults);

        if (HasBlockingFindings(semanticResults))
        {
            return new ValidationPipelineResult
            {
                Findings = allFindings,
                StoppedAtTier = ValidatorPhase.Semantic,
                Passed = false
            };
        }

        // Tier 3: Cross-Artifact
        var crossResults = _crossArtifact.Validate(envelope, inputArtifacts, attemptNumber);
        allFindings.AddRange(crossResults);

        if (HasBlockingFindings(crossResults))
        {
            return new ValidationPipelineResult
            {
                Findings = allFindings,
                StoppedAtTier = ValidatorPhase.CrossArtifact,
                Passed = false
            };
        }

        return new ValidationPipelineResult
        {
            Findings = allFindings,
            StoppedAtTier = null,
            Passed = true
        };
    }

    private static bool HasBlockingFindings(IReadOnlyList<ValidatorResultTrace> findings) =>
        findings.Any(f => f.Blocking);
}

/// <summary>
/// Result of the full validation pipeline run.
/// </summary>
public sealed record ValidationPipelineResult
{
    /// <summary>All findings from all tiers that ran (up to the first blocking tier).</summary>
    public required IReadOnlyList<ValidatorResultTrace> Findings { get; init; }

    /// <summary>Which tier stopped the pipeline, or null if all tiers passed.</summary>
    public ValidatorPhase? StoppedAtTier { get; init; }

    /// <summary>True if all tiers passed with no blocking findings.</summary>
    public required bool Passed { get; init; }
}
