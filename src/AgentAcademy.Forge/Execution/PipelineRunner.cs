using AgentAcademy.Forge.Artifacts;
using AgentAcademy.Forge.Costs;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Storage;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Forge.Execution;

/// <summary>
/// Top-level pipeline orchestrator. Chains methodology phases in sequence,
/// resolves input artifacts between phases, and owns the Run state machine
/// (Pending → Running → Succeeded | Failed | Aborted).
/// </summary>
public sealed class PipelineRunner
{
    private readonly PhaseExecutor _phaseExecutor;
    private readonly IArtifactStore _artifactStore;
    private readonly IRunStore _runStore;
    private readonly CostCalculator _costCalculator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PipelineRunner> _logger;

    public PipelineRunner(
        PhaseExecutor phaseExecutor,
        IArtifactStore artifactStore,
        IRunStore runStore,
        CostCalculator costCalculator,
        TimeProvider timeProvider,
        ILogger<PipelineRunner> logger)
    {
        _phaseExecutor = phaseExecutor;
        _artifactStore = artifactStore;
        _runStore = runStore;
        _costCalculator = costCalculator;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Execute a full pipeline run: all phases in methodology order.
    /// </summary>
    /// <param name="task">Task brief describing what to build.</param>
    /// <param name="methodology">Frozen methodology definition.</param>
    /// <param name="ct">Cancellation token. Cancelling produces Aborted status.</param>
    /// <returns>Final run trace with token totals and artifact hashes.</returns>
    public async Task<RunTrace> ExecuteAsync(
        TaskBrief task,
        MethodologyDefinition methodology,
        CancellationToken ct = default)
    {
        // Validate that all models are priced when budget is set
        _costCalculator.ValidatePricingForBudget(methodology);

        var runId = ForgeId.NewRunId();
        var startedAt = _timeProvider.GetUtcNow().UtcDateTime;

        _logger.LogInformation("Pipeline run {RunId} starting for task {TaskId} with methodology {MethodId}",
            runId, task.TaskId, methodology.Id);

        // Initialize run as Pending
        var runTrace = NewRunTrace(runId, task.TaskId, methodology.Id, startedAt, "pending");
        await _runStore.InitializeRunAsync(runId, runTrace, task, methodology, ct);

        // Transition to Running
        runTrace = runTrace with { Outcome = "running" };
        await _runStore.WriteRunSnapshotAsync(runId, runTrace, ct);

        var phaseRunTraces = new List<PhaseRunTrace>();
        var acceptedArtifacts = new Dictionary<string, ArtifactEnvelope>();
        var totalTokensIn = 0;
        var totalTokensOut = 0;
        var totalCost = 0m;

        try
        {
            for (var i = 0; i < methodology.Phases.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var phase = methodology.Phases[i];
                _logger.LogInformation("Pipeline run {RunId}: starting phase {PhaseIndex}/{Total} ({PhaseId})",
                    runId, i + 1, methodology.Phases.Count, phase.Id);

                // Resolve input artifacts for this phase
                var inputArtifacts = ResolveInputArtifacts(phase, acceptedArtifacts);
                if (inputArtifacts is null)
                {
                    _logger.LogError("Pipeline run {RunId}: phase {PhaseId} has unresolved inputs",
                        runId, phase.Id);

                    var failedPhaseTrace = CreateInputsMissingTrace(phase, _timeProvider);
                    phaseRunTraces.Add(failedPhaseTrace);

                    runTrace = FinalizeRun(runTrace, startedAt, "failed", phaseRunTraces,
                        acceptedArtifacts, totalTokensIn, totalTokensOut, totalCost, _timeProvider);
                    await WriteRunFinalState(runId, runTrace, phaseRunTraces, ct);
                    return runTrace;
                }

                // Compute remaining budget for this phase
                decimal? budgetRemaining = methodology.Budget.HasValue
                    ? methodology.Budget.Value - totalCost
                    : null;

                // Execute the phase
                var result = await _phaseExecutor.ExecuteAsync(
                    runId, i, phase, methodology, task, inputArtifacts, budgetRemaining, ct);

                phaseRunTraces.Add(result.PhaseRunTrace);

                // Accumulate tokens and cost from all attempts
                foreach (var attempt in result.PhaseRunTrace.Attempts)
                {
                    totalTokensIn += attempt.Tokens.In;
                    totalTokensOut += attempt.Tokens.Out;
                    if (attempt.JudgeTokens is not null)
                    {
                        totalTokensIn += attempt.JudgeTokens.In;
                        totalTokensOut += attempt.JudgeTokens.Out;
                    }
                    totalCost += attempt.Cost ?? 0;
                }

                if (result.Status == PhaseRunStatus.Failed)
                {
                    // Check if failure was due to budget exhaustion
                    if (methodology.Budget.HasValue && totalCost >= methodology.Budget.Value)
                    {
                        _logger.LogWarning("Pipeline run {RunId}: aborted — budget exhausted (${Cost:F4} / ${Budget:F4})",
                            runId, totalCost, methodology.Budget.Value);

                        runTrace = FinalizeRun(runTrace, startedAt, "aborted", phaseRunTraces,
                            acceptedArtifacts, totalTokensIn, totalTokensOut, totalCost, _timeProvider);
                        runTrace = runTrace with { AbortReason = "budget_exceeded" };
                        await WriteRunFinalState(runId, runTrace, phaseRunTraces, ct);
                        return runTrace;
                    }

                    _logger.LogWarning("Pipeline run {RunId}: phase {PhaseId} failed after all attempts",
                        runId, phase.Id);

                    runTrace = FinalizeRun(runTrace, startedAt, "failed", phaseRunTraces,
                        acceptedArtifacts, totalTokensIn, totalTokensOut, totalCost, _timeProvider);
                    await WriteRunFinalState(runId, runTrace, phaseRunTraces, ct);
                    return runTrace;
                }

                // Phase succeeded — read the accepted artifact from the store
                if (result.AcceptedArtifactHash is not null)
                {
                    var envelope = await _artifactStore.ReadAsync(result.AcceptedArtifactHash, ct);
                    if (envelope is null)
                    {
                        _logger.LogError("Pipeline run {RunId}: accepted artifact {Hash} not found in store",
                            runId, result.AcceptedArtifactHash[..12]);

                        runTrace = FinalizeRun(runTrace, startedAt, "failed", phaseRunTraces,
                            acceptedArtifacts, totalTokensIn, totalTokensOut, totalCost, _timeProvider);
                        await WriteRunFinalState(runId, runTrace, phaseRunTraces, ct);
                        return runTrace;
                    }

                    acceptedArtifacts[phase.Id] = envelope;
                }

                _logger.LogInformation("Pipeline run {RunId}: phase {PhaseId} succeeded", runId, phase.Id);

                // Budget check between phases (even after success)
                if (methodology.Budget.HasValue && totalCost >= methodology.Budget.Value && i < methodology.Phases.Count - 1)
                {
                    _logger.LogWarning("Pipeline run {RunId}: aborted between phases — budget exceeded (${Cost:F4} / ${Budget:F4})",
                        runId, totalCost, methodology.Budget.Value);

                    runTrace = FinalizeRun(runTrace, startedAt, "aborted", phaseRunTraces,
                        acceptedArtifacts, totalTokensIn, totalTokensOut, totalCost, _timeProvider);
                    runTrace = runTrace with { AbortReason = "budget_exceeded" };
                    await WriteRunFinalState(runId, runTrace, phaseRunTraces, ct);
                    return runTrace;
                }
            }

            // All phases succeeded
            runTrace = FinalizeRun(runTrace, startedAt, "succeeded", phaseRunTraces,
                acceptedArtifacts, totalTokensIn, totalTokensOut, totalCost, _timeProvider);
            await WriteRunFinalState(runId, runTrace, phaseRunTraces, ct);

            _logger.LogInformation("Pipeline run {RunId} completed successfully ({TotalIn}+{TotalOut} tokens, ${Cost:F4})",
                runId, totalTokensIn, totalTokensOut, totalCost);

            return runTrace;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Pipeline run {RunId} aborted via cancellation", runId);

            runTrace = FinalizeRun(runTrace, startedAt, "aborted", phaseRunTraces,
                acceptedArtifacts, totalTokensIn, totalTokensOut, totalCost, _timeProvider);

            try
            {
                await WriteRunFinalState(runId, runTrace, phaseRunTraces, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist aborted run state for {RunId}", runId);
            }

            return runTrace;
        }
    }

    /// <summary>
    /// Resolve input artifacts for a phase from the accepted artifacts dictionary.
    /// Returns null if any required input is missing.
    /// </summary>
    private static Dictionary<string, ArtifactEnvelope>? ResolveInputArtifacts(
        PhaseDefinition phase,
        Dictionary<string, ArtifactEnvelope> acceptedArtifacts)
    {
        var inputs = new Dictionary<string, ArtifactEnvelope>(StringComparer.Ordinal);

        foreach (var inputPhaseId in phase.Inputs)
        {
            if (!acceptedArtifacts.TryGetValue(inputPhaseId, out var artifact))
                return null; // Missing required input

            inputs[inputPhaseId] = artifact;
        }

        return inputs;
    }

    private static PhaseRunTrace CreateInputsMissingTrace(PhaseDefinition phase, TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return new PhaseRunTrace
        {
            PhaseId = phase.Id,
            ArtifactType = phase.ArtifactType,
            StateTransitions =
            [
                new StateTransition { From = null, To = "pending", At = now },
                new StateTransition { From = "pending", To = "failed", At = now }
            ],
            Attempts = [],
            InputArtifactHashes = [],
            OutputArtifactHashes = []
        };
    }

    private static RunTrace FinalizeRun(
        RunTrace current,
        DateTime startedAt,
        string outcome,
        List<PhaseRunTrace> phaseRunTraces,
        Dictionary<string, ArtifactEnvelope> acceptedArtifacts,
        int totalTokensIn,
        int totalTokensOut,
        decimal totalCost,
        TimeProvider timeProvider)
    {
        var finalHashes = new Dictionary<string, string>();
        foreach (var (phaseId, envelope) in acceptedArtifacts)
        {
            var hash = CanonicalJson.PrefixedHash(envelope);
            finalHashes[phaseId] = hash;
        }

        return current with
        {
            Outcome = outcome,
            EndedAt = timeProvider.GetUtcNow().UtcDateTime,
            PipelineTokens = new TokenCount { In = totalTokensIn, Out = totalTokensOut },
            PipelineCost = totalCost > 0 ? totalCost : null,
            FinalArtifactHashes = finalHashes
        };
    }

    private async Task WriteRunFinalState(
        string runId,
        RunTrace runTrace,
        List<PhaseRunTrace> phaseRunTraces,
        CancellationToken ct)
    {
        await _runStore.WriteRunSnapshotAsync(runId, runTrace, ct);
        await _runStore.WritePhaseRunsRollupAsync(runId, phaseRunTraces, ct);
    }

    private static RunTrace NewRunTrace(
        string runId,
        string taskId,
        string methodologyVersion,
        DateTime startedAt,
        string outcome)
    {
        return new RunTrace
        {
            RunId = runId,
            TaskId = taskId,
            MethodologyVersion = methodologyVersion,
            StartedAt = startedAt,
            Outcome = outcome,
            PipelineTokens = new TokenCount(),
            ControlTokens = new TokenCount(),
            FinalArtifactHashes = new Dictionary<string, string>()
        };
    }
}
