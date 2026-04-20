using System.Text;
using System.Text.Json;
using AgentAcademy.Forge.Artifacts;
using AgentAcademy.Forge.Costs;
using AgentAcademy.Forge.Llm;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Prompt;
using AgentAcademy.Forge.Schemas;
using AgentAcademy.Forge.Storage;
using AgentAcademy.Forge.Validation;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Forge.Execution;

/// <summary>
/// Executes the terminal fidelity phase — compares the final pipeline output
/// against the source intent with zero access to intermediate artifacts.
/// Enforces the hard constraint: inputs must be exactly {source_intent, target_output}.
/// Delegates to <see cref="PhaseExecutor"/> for the attempt loop.
/// </summary>
public sealed class FidelityExecutor
{
    private readonly PhaseExecutor _phaseExecutor;
    private readonly IArtifactStore _artifactStore;
    private readonly ILogger<FidelityExecutor> _logger;

    public FidelityExecutor(
        PhaseExecutor phaseExecutor,
        IArtifactStore artifactStore,
        ILogger<FidelityExecutor> logger)
    {
        _phaseExecutor = phaseExecutor;
        _artifactStore = artifactStore;
        _logger = logger;
    }

    /// <summary>
    /// Execute the fidelity phase. Validates inputs, then delegates to PhaseExecutor.
    /// </summary>
    /// <param name="runId">Current run ID.</param>
    /// <param name="phaseIndex">Phase index for storage (should be after all methodology phases).</param>
    /// <param name="sourceIntentEnvelope">The source-intent artifact.</param>
    /// <param name="targetOutputEnvelope">The final output artifact to check against source intent.</param>
    /// <param name="targetPhaseId">Phase ID that produced the target output (e.g. "implementation").</param>
    /// <param name="methodology">Methodology definition.</param>
    /// <param name="task">Task brief.</param>
    /// <param name="budgetRemaining">Remaining budget for this phase, or null if no budget.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Fidelity execution result with PhaseRunTrace.</returns>
    public async Task<FidelityResult> ExecuteAsync(
        string runId,
        int phaseIndex,
        ArtifactEnvelope sourceIntentEnvelope,
        ArtifactEnvelope targetOutputEnvelope,
        string targetPhaseId,
        MethodologyDefinition methodology,
        TaskBrief task,
        decimal? budgetRemaining = null,
        CancellationToken ct = default)
    {
        // Enforce the hard constraint: exactly 2 inputs (source_intent + target output)
        var validationError = ValidateInputs(sourceIntentEnvelope, targetOutputEnvelope);
        if (validationError is not null)
        {
            _logger.LogError("Fidelity phase input violation: {Error}", validationError);
            return FidelityResult.InputViolation(validationError);
        }

        _logger.LogInformation("Fidelity phase starting: source_intent + {TargetPhase} output",
            targetPhaseId);

        // Build a synthetic PhaseDefinition for the fidelity phase
        var fidelityPhase = BuildFidelityPhaseDefinition(targetPhaseId, methodology);

        // Build input artifacts dict — only source_intent and target phase
        var inputArtifacts = new Dictionary<string, ArtifactEnvelope>(StringComparer.Ordinal)
        {
            ["source_intent"] = sourceIntentEnvelope,
            [targetPhaseId] = targetOutputEnvelope
        };

        // Execute via PhaseExecutor
        var result = await _phaseExecutor.ExecuteAsync(
            runId, phaseIndex, fidelityPhase, methodology, task,
            inputArtifacts, budgetRemaining, ct);

        // Extract fidelity-specific results from the accepted artifact
        string? fidelityOutcome = null;
        IReadOnlyList<string>? driftCodes = null;

        if (result.Status == PhaseRunStatus.Succeeded && result.AcceptedArtifactHash is not null)
        {
            var fidelityEnvelope = await _artifactStore.ReadAsync(result.AcceptedArtifactHash, ct);
            if (fidelityEnvelope is not null)
            {
                (fidelityOutcome, driftCodes) = ExtractFidelityResults(fidelityEnvelope);
            }
        }

        _logger.LogInformation("Fidelity phase completed: status={Status}, outcome={Outcome}, driftCodes={DriftCount}",
            result.Status, fidelityOutcome ?? "none", driftCodes?.Count ?? 0);

        return new FidelityResult
        {
            PhaseRunTrace = result.PhaseRunTrace,
            Status = result.Status,
            FidelityOutcome = fidelityOutcome,
            DriftCodes = driftCodes,
            ArtifactHash = result.AcceptedArtifactHash is not null
                ? $"sha256:{result.AcceptedArtifactHash}"
                : null
        };
    }

    /// <summary>
    /// Validate that inputs are exactly {source_intent, target_output}.
    /// Returns an error message if invalid, null if valid.
    /// </summary>
    internal static string? ValidateInputs(
        ArtifactEnvelope sourceIntentEnvelope,
        ArtifactEnvelope targetOutputEnvelope)
    {
        if (sourceIntentEnvelope.ArtifactType != "source_intent")
        {
            return $"First input must be artifactType 'source_intent', got '{sourceIntentEnvelope.ArtifactType}'.";
        }

        if (targetOutputEnvelope.ArtifactType == "source_intent")
        {
            return "Second input cannot be artifactType 'source_intent' — must be the target output artifact.";
        }

        if (targetOutputEnvelope.ArtifactType == "fidelity")
        {
            return "Second input cannot be artifactType 'fidelity' — must be the target output artifact.";
        }

        return null;
    }

    /// <summary>
    /// Build a synthetic PhaseDefinition for the fidelity phase.
    /// </summary>
    internal static PhaseDefinition BuildFidelityPhaseDefinition(
        string targetPhaseId,
        MethodologyDefinition methodology)
    {
        return new PhaseDefinition
        {
            Id = "fidelity",
            Goal = $"Compare the final output from phase '{targetPhaseId}' against the original source intent. " +
                   "Determine whether the output faithfully implements the human's stated requirements, " +
                   "or whether semantic drift occurred during pipeline execution.",
            Inputs = ["source_intent", targetPhaseId],
            OutputSchema = "fidelity/v1",
            Instructions = BuildFidelityInstructions(),
            MaxAttempts = methodology.Fidelity?.MaxAttempts ?? 3,
            Model = methodology.Fidelity?.Model,
            JudgeModel = methodology.Fidelity?.JudgeModel
        };
    }

    private static string BuildFidelityInstructions()
    {
        return """
            You are performing an INTENT FIDELITY CHECK. You have access to exactly two artifacts:
            1. The SOURCE INTENT — the human's original structured request
            2. The FINAL OUTPUT — the artifact produced by the pipeline

            You do NOT have access to any intermediate artifacts (requirements, contract, function_design, etc.).
            This is by design — you are checking end-to-end fidelity, not per-phase consistency.

            Your job:
            1. Read the source_intent's task_brief, acceptance_criteria, and explicit_constraints.
            2. Read the final output artifact.
            3. For each acceptance criterion: determine if the final output satisfies it. Provide evidence.
            4. Check for drift — places where the final output diverges from the source intent.

            DRIFT TAXONOMY (CLOSED — use ONLY these 5 codes):
            - OMITTED_CONSTRAINT: A constraint from source intent was dropped.
            - INVENTED_REQUIREMENT: A requirement appears in the output with no basis in source intent.
            - SCOPE_BROADENED: The output covers more than what was asked for.
            - SCOPE_NARROWED: The output covers less than what was asked for.
            - CONSTRAINT_WEAKENED: An explicit constraint was weakened (e.g., "must" became "should").

            SEVERITY:
            - BLOCKING (indicates possible incorrectness): OMITTED_CONSTRAINT, CONSTRAINT_WEAKENED
            - ADVISORY (indicates divergence): INVENTED_REQUIREMENT, SCOPE_BROADENED, SCOPE_NARROWED

            OVERALL MATCH RULES:
            - PASS: All acceptance criteria satisfied AND zero blocking drift codes.
            - FAIL: Any blocking drift code detected (OMITTED_CONSTRAINT or CONSTRAINT_WEAKENED).
            - PARTIAL: All criteria satisfied but advisory drift codes present.

            Do NOT invent drift where none exists. If the output faithfully implements the source intent,
            report PASS with an empty drift_detected array.
            """;
    }

    /// <summary>
    /// Extract the fidelity outcome and drift codes from a fidelity artifact envelope.
    /// </summary>
    internal static (string? outcome, IReadOnlyList<string>? driftCodes) ExtractFidelityResults(
        ArtifactEnvelope envelope)
    {
        try
        {
            string? outcome = null;
            List<string>? codes = null;

            if (envelope.Payload.TryGetProperty("overall_match", out var matchElement))
            {
                outcome = matchElement.GetString()?.ToLowerInvariant();
            }

            if (envelope.Payload.TryGetProperty("drift_detected", out var driftElement)
                && driftElement.ValueKind == JsonValueKind.Array)
            {
                codes = [];
                foreach (var item in driftElement.EnumerateArray())
                {
                    if (item.TryGetProperty("code", out var codeElement))
                    {
                        var code = codeElement.GetString();
                        if (code is not null)
                            codes.Add(code);
                    }
                }
            }

            return (outcome, codes);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }
}

/// <summary>
/// Result of fidelity phase execution.
/// </summary>
public sealed record FidelityResult
{
    public PhaseRunTrace? PhaseRunTrace { get; init; }

    public PhaseRunStatus Status { get; init; }

    /// <summary>"pass", "fail", or "partial", extracted from the fidelity artifact.</summary>
    public string? FidelityOutcome { get; init; }

    /// <summary>Drift codes detected, or null/empty if none.</summary>
    public IReadOnlyList<string>? DriftCodes { get; init; }

    /// <summary>Hash of the fidelity artifact (sha256:... prefixed), or null if failed.</summary>
    public string? ArtifactHash { get; init; }

    /// <summary>Error message when inputs violate the fidelity constraint.</summary>
    public string? InputViolationError { get; init; }

    /// <summary>Create a result for an input violation (no execution occurred).</summary>
    internal static FidelityResult InputViolation(string error) => new()
    {
        Status = PhaseRunStatus.Failed,
        InputViolationError = error,
        PhaseRunTrace = new PhaseRunTrace
        {
            PhaseId = "fidelity",
            ArtifactType = "fidelity",
            StateTransitions =
            [
                new StateTransition { From = null, To = "pending", At = DateTime.UtcNow },
                new StateTransition { From = "pending", To = "failed", At = DateTime.UtcNow }
            ],
            Attempts = [],
            InputArtifactHashes = [],
            OutputArtifactHashes = []
        }
    };
}
