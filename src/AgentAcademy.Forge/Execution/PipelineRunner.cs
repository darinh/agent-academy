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
/// Optionally runs a control arm for A/B benchmarking after the pipeline completes.
/// </summary>
public sealed class PipelineRunner
{
    private readonly PhaseExecutor _phaseExecutor;
    private readonly ControlExecutor? _controlExecutor;
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
        ILogger<PipelineRunner> logger,
        ControlExecutor? controlExecutor = null)
    {
        _phaseExecutor = phaseExecutor;
        _artifactStore = artifactStore;
        _runStore = runStore;
        _costCalculator = costCalculator;
        _timeProvider = timeProvider;
        _logger = logger;
        _controlExecutor = controlExecutor;
    }

    /// <summary>
    /// Execute a full pipeline run: all phases in methodology order.
    /// When the methodology has a control arm configured and a <see cref="ControlExecutor"/>
    /// is registered, a single-shot baseline runs after the pipeline (except on abort).
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

        // Execute the pipeline phases
        var pipelineResult = await ExecutePipelinePhasesAsync(runId, task, methodology, runTrace, ct);
        runTrace = pipelineResult.RunTrace;
        var phaseRunTraces = pipelineResult.PhaseRunTraces;

        // Optionally run the control arm — but not when aborted (cancellation or budget)
        if (methodology.Control is not null && _controlExecutor is not null && runTrace.Outcome != "aborted")
        {
            runTrace = await RunControlArmAsync(runTrace, task, methodology, ct);
        }

        // Stamp EndedAt after control arm (if any) so timing reflects true completion
        runTrace = runTrace with { EndedAt = _timeProvider.GetUtcNow().UtcDateTime };

        // Single finalize path: persist final state.
        // Use CancellationToken.None for aborted runs — the original token is cancelled
        // but we still need to persist the final state to disk.
        var writeCt = runTrace.Outcome == "aborted" ? CancellationToken.None : ct;
        await WriteRunFinalState(runId, runTrace, phaseRunTraces, writeCt);

        if (runTrace.Outcome == "succeeded")
        {
            _logger.LogInformation("Pipeline run {RunId} completed successfully ({TotalIn}+{TotalOut} tokens, ${Cost:F4})",
                runId, runTrace.PipelineTokens.In, runTrace.PipelineTokens.Out, runTrace.PipelineCost ?? 0);
        }

        return runTrace;
    }

    /// <summary>
    /// Resume a previously started pipeline run that was interrupted (crash, process exit).
    /// Reads persisted snapshots to determine which phases completed, reconstructs accumulated
    /// tokens/cost from all persisted attempts, and continues from the first non-succeeded phase.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If the run is already terminal (succeeded/failed/aborted), returns the existing trace unchanged.
    /// </para>
    /// <para>
    /// A phase in "running" state (crashed mid-execution) is re-executed from attempt 1.
    /// Tokens and cost from its persisted attempts are still counted toward the run total
    /// so that budget enforcement remains accurate.
    /// </para>
    /// <para>
    /// If the pipeline succeeded but the control arm has no outcome in the snapshot,
    /// the control arm is re-executed. This may produce one duplicate LLM call if the
    /// original control arm completed but the snapshot wasn't updated before the crash.
    /// </para>
    /// </remarks>
    /// <param name="runId">Run ID to resume.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Final run trace after resumption.</returns>
    public async Task<RunTrace> ResumeAsync(string runId, CancellationToken ct = default)
    {
        // 1. Read the persisted run snapshot
        var runTrace = await _runStore.ReadRunAsync(runId, ct);
        if (runTrace is null)
            throw new InvalidOperationException($"Run '{runId}' not found in run store.");

        // Already terminal — nothing to resume
        if (runTrace.Outcome is "succeeded" or "failed" or "aborted")
        {
            _logger.LogInformation("Resume {RunId}: already terminal ({Outcome}), returning as-is",
                runId, runTrace.Outcome);
            return runTrace;
        }

        // 2. Read frozen inputs
        var task = await _runStore.ReadTaskAsync(runId, ct)
            ?? throw new InvalidOperationException($"Run '{runId}' has no task.json — store is inconsistent.");
        var methodology = await _runStore.ReadMethodologyAsync(runId, ct)
            ?? throw new InvalidOperationException($"Run '{runId}' has no methodology.json — store is inconsistent.");

        _logger.LogInformation("Resume {RunId}: rebuilding state from phase snapshots", runId);

        // Validate that all models are priced when budget is set (same as fresh run)
        _costCalculator.ValidatePricingForBudget(methodology);

        // 3. Rebuild state from phase scratch files
        var resumeState = await RebuildStateFromSnapshotsAsync(runId, methodology, ct);

        // Log the resume event
        await _runStore.AppendTraceEventAsync(runId, new
        {
            @event = "run_resumed",
            at = _timeProvider.GetUtcNow().UtcDateTime,
            completedPhases = resumeState.CompletedPhaseIds,
            resumeFromIndex = resumeState.ResumeFromPhaseIndex,
            accumulatedCost = resumeState.AccumulatedCost
        }, ct);

        // 4. Check if a completed phase already hit a terminal state
        if (resumeState.TerminalPhaseOutcome is not null)
        {
            // Phase was terminal (failed) — determine run outcome based on budget
            var outcome = resumeState.TerminalPhaseOutcome;
            string? abortReason = null;

            if (methodology.Budget.HasValue && resumeState.AccumulatedCost >= methodology.Budget.Value)
            {
                outcome = "aborted";
                abortReason = "budget_exceeded";
            }

            runTrace = runTrace with
            {
                Outcome = outcome,
                EndedAt = _timeProvider.GetUtcNow().UtcDateTime,
                PipelineTokens = new TokenCount
                {
                    In = resumeState.AccumulatedTokensIn,
                    Out = resumeState.AccumulatedTokensOut
                },
                PipelineCost = resumeState.AccumulatedCost > 0 ? resumeState.AccumulatedCost : null,
                FinalArtifactHashes = resumeState.FinalArtifactHashes,
                AbortReason = abortReason
            };

            await WriteRunFinalState(runId, runTrace, resumeState.PhaseRunTraces, ct);

            _logger.LogInformation("Resume {RunId}: run was already terminal ({Outcome})", runId, outcome);
            return runTrace;
        }

        // 5. Budget guard: if accumulated cost already exceeds budget, abort immediately
        if (methodology.Budget.HasValue && resumeState.AccumulatedCost >= methodology.Budget.Value)
        {
            _logger.LogWarning("Resume {RunId}: accumulated cost ${Cost:F4} already exceeds budget ${Budget:F4}, aborting",
                runId, resumeState.AccumulatedCost, methodology.Budget.Value);

            runTrace = runTrace with
            {
                Outcome = "aborted",
                AbortReason = "budget_exceeded",
                EndedAt = _timeProvider.GetUtcNow().UtcDateTime,
                PipelineTokens = new TokenCount
                {
                    In = resumeState.AccumulatedTokensIn,
                    Out = resumeState.AccumulatedTokensOut
                },
                PipelineCost = resumeState.AccumulatedCost > 0 ? resumeState.AccumulatedCost : null,
                FinalArtifactHashes = resumeState.FinalArtifactHashes
            };

            await WriteRunFinalState(runId, runTrace, resumeState.PhaseRunTraces, ct);
            return runTrace;
        }

        // 6. Continue execution from the first non-succeeded phase
        runTrace = runTrace with { Outcome = "running" };
        await _runStore.WriteRunSnapshotAsync(runId, runTrace, ct);

        var pipelineResult = await ExecutePipelinePhasesAsync(
            runId, task, methodology, runTrace, ct,
            startPhaseIndex: resumeState.ResumeFromPhaseIndex,
            initialAcceptedArtifacts: resumeState.AcceptedArtifacts,
            initialTokensIn: resumeState.AccumulatedTokensIn,
            initialTokensOut: resumeState.AccumulatedTokensOut,
            initialCost: resumeState.AccumulatedCost);

        runTrace = pipelineResult.RunTrace;
        var phaseRunTraces = MergePhaseRunTraces(resumeState.PhaseRunTraces, pipelineResult.PhaseRunTraces);

        // 7. Optionally run the control arm
        if (methodology.Control is not null && _controlExecutor is not null && runTrace.Outcome != "aborted"
            && runTrace.ControlOutcome is null)
        {
            runTrace = await RunControlArmAsync(runTrace, task, methodology, ct);
        }

        // 8. Finalize
        runTrace = runTrace with { EndedAt = _timeProvider.GetUtcNow().UtcDateTime };
        var writeCt = runTrace.Outcome == "aborted" ? CancellationToken.None : ct;
        await WriteRunFinalState(runId, runTrace, phaseRunTraces, writeCt);

        _logger.LogInformation("Resume {RunId}: completed with outcome {Outcome}", runId, runTrace.Outcome);
        return runTrace;
    }

    /// <summary>
    /// Execute methodology phases in order, optionally starting from a given index
    /// with pre-accumulated state (for crash recovery resume).
    /// </summary>
    private async Task<PipelinePhaseResult> ExecutePipelinePhasesAsync(
        string runId,
        TaskBrief task,
        MethodologyDefinition methodology,
        RunTrace runTrace,
        CancellationToken ct,
        int startPhaseIndex = 0,
        Dictionary<string, ArtifactEnvelope>? initialAcceptedArtifacts = null,
        int initialTokensIn = 0,
        int initialTokensOut = 0,
        decimal initialCost = 0m)
    {
        var phaseRunTraces = new List<PhaseRunTrace>();
        var acceptedArtifacts = initialAcceptedArtifacts ?? new Dictionary<string, ArtifactEnvelope>();
        var totalTokensIn = initialTokensIn;
        var totalTokensOut = initialTokensOut;
        var totalCost = initialCost;

        try
        {
            for (var i = startPhaseIndex; i < methodology.Phases.Count; i++)
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

                    return FinalizePhases(runTrace, "failed", phaseRunTraces,
                        acceptedArtifacts, totalTokensIn, totalTokensOut, totalCost);
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

                        var abortResult = FinalizePhases(runTrace, "aborted", phaseRunTraces,
                            acceptedArtifacts, totalTokensIn, totalTokensOut, totalCost);
                        abortResult = abortResult with
                        {
                            RunTrace = abortResult.RunTrace with { AbortReason = "budget_exceeded" }
                        };
                        return abortResult;
                    }

                    _logger.LogWarning("Pipeline run {RunId}: phase {PhaseId} failed after all attempts",
                        runId, phase.Id);

                    return FinalizePhases(runTrace, "failed", phaseRunTraces,
                        acceptedArtifacts, totalTokensIn, totalTokensOut, totalCost);
                }

                // Phase succeeded — read the accepted artifact from the store
                if (result.AcceptedArtifactHash is not null)
                {
                    var envelope = await _artifactStore.ReadAsync(result.AcceptedArtifactHash, ct);
                    if (envelope is null)
                    {
                        _logger.LogError("Pipeline run {RunId}: accepted artifact {Hash} not found in store",
                            runId, result.AcceptedArtifactHash[..12]);

                        return FinalizePhases(runTrace, "failed", phaseRunTraces,
                            acceptedArtifacts, totalTokensIn, totalTokensOut, totalCost);
                    }

                    acceptedArtifacts[phase.Id] = envelope;
                }

                _logger.LogInformation("Pipeline run {RunId}: phase {PhaseId} succeeded", runId, phase.Id);

                // Budget check between phases (even after success)
                if (methodology.Budget.HasValue && totalCost >= methodology.Budget.Value && i < methodology.Phases.Count - 1)
                {
                    _logger.LogWarning("Pipeline run {RunId}: aborted between phases — budget exceeded (${Cost:F4} / ${Budget:F4})",
                        runId, totalCost, methodology.Budget.Value);

                    var abortResult = FinalizePhases(runTrace, "aborted", phaseRunTraces,
                        acceptedArtifacts, totalTokensIn, totalTokensOut, totalCost);
                    abortResult = abortResult with
                    {
                        RunTrace = abortResult.RunTrace with { AbortReason = "budget_exceeded" }
                    };
                    return abortResult;
                }
            }

            // All phases succeeded
            return FinalizePhases(runTrace, "succeeded", phaseRunTraces,
                acceptedArtifacts, totalTokensIn, totalTokensOut, totalCost);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Pipeline run {RunId} aborted via cancellation", runId);

            return FinalizePhases(runTrace, "aborted", phaseRunTraces,
                acceptedArtifacts, totalTokensIn, totalTokensOut, totalCost);
        }
    }

    /// <summary>
    /// Run the control arm and merge results into the run trace.
    /// </summary>
    private async Task<RunTrace> RunControlArmAsync(
        RunTrace runTrace,
        TaskBrief task,
        MethodologyDefinition methodology,
        CancellationToken ct)
    {
        _logger.LogInformation("Pipeline run {RunId}: starting control arm", runTrace.RunId);

        try
        {
            var controlResult = await _controlExecutor!.ExecuteAsync(task, methodology, ct);

            var costRatio = controlResult.Cost is > 0 && runTrace.PipelineCost is > 0
                ? (double)(runTrace.PipelineCost.Value / controlResult.Cost.Value)
                : (double?)null;

            return runTrace with
            {
                ControlOutcome = controlResult.Outcome,
                ControlTokens = controlResult.Tokens,
                ControlCost = controlResult.Cost,
                ControlArtifactHash = controlResult.ArtifactHash,
                CostRatio = costRatio
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Pipeline run {RunId}: control arm failed with exception", runTrace.RunId);
            return runTrace with
            {
                ControlOutcome = "failed",
                ControlTokens = new TokenCount()
            };
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

    private PipelinePhaseResult FinalizePhases(
        RunTrace current,
        string outcome,
        List<PhaseRunTrace> phaseRunTraces,
        Dictionary<string, ArtifactEnvelope> acceptedArtifacts,
        int totalTokensIn,
        int totalTokensOut,
        decimal totalCost)
    {
        var finalHashes = new Dictionary<string, string>();
        foreach (var (phaseId, envelope) in acceptedArtifacts)
        {
            var hash = CanonicalJson.PrefixedHash(envelope);
            finalHashes[phaseId] = hash;
        }

        var runTrace = current with
        {
            Outcome = outcome,
            PipelineTokens = new TokenCount { In = totalTokensIn, Out = totalTokensOut },
            PipelineCost = totalCost > 0 ? totalCost : null,
            FinalArtifactHashes = finalHashes
        };

        return new PipelinePhaseResult { RunTrace = runTrace, PhaseRunTraces = phaseRunTraces };
    }

    /// <summary>
    /// Merge phase run traces from completed (pre-resume) and newly-executed phases.
    /// </summary>
    private static List<PhaseRunTrace> MergePhaseRunTraces(
        List<PhaseRunTrace> completedTraces,
        List<PhaseRunTrace> newTraces)
    {
        var merged = new List<PhaseRunTrace>(completedTraces);
        merged.AddRange(newTraces);
        return merged;
    }

    /// <summary>
    /// Rebuild pipeline state from per-phase scratch files.
    /// Scans all phases in methodology order, accumulates tokens/cost from every
    /// persisted attempt (including running and failed phases), and determines
    /// the resume point.
    /// </summary>
    private async Task<ResumeState> RebuildStateFromSnapshotsAsync(
        string runId,
        MethodologyDefinition methodology,
        CancellationToken ct)
    {
        var acceptedArtifacts = new Dictionary<string, ArtifactEnvelope>();
        var phaseRunTraces = new List<PhaseRunTrace>();
        var completedPhaseIds = new List<string>();
        var finalArtifactHashes = new Dictionary<string, string>();
        var totalTokensIn = 0;
        var totalTokensOut = 0;
        var totalCost = 0m;
        int resumeFromIndex = 0;
        string? terminalPhaseOutcome = null;

        for (var i = 0; i < methodology.Phases.Count; i++)
        {
            var phase = methodology.Phases[i];
            var phaseRun = await _runStore.ReadPhaseRunScratchAsync(runId, i, phase.Id, ct);

            if (phaseRun is null)
            {
                // Phase never started — resume from here
                resumeFromIndex = i;
                break;
            }

            // Accumulate tokens/cost from ALL persisted attempts in this phase
            foreach (var attempt in phaseRun.Attempts)
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

            // Determine phase terminal state from last state transition
            var lastTransition = phaseRun.StateTransitions.LastOrDefault();
            var phaseTerminalState = lastTransition?.To;

            if (phaseTerminalState == "succeeded")
            {
                // Read the accepted artifact from the store
                if (phaseRun.OutputArtifactHashes.Count > 0)
                {
                    var prefixedHash = phaseRun.OutputArtifactHashes[0];
                    // Artifact store uses raw hex hashes; strip the sha256: prefix
                    var rawHash = StripHashPrefix(prefixedHash);
                    var envelope = await _artifactStore.ReadAsync(rawHash, ct);
                    if (envelope is null)
                    {
                        throw new InvalidOperationException(
                            $"Resume {runId}: phase '{phase.Id}' succeeded but accepted artifact " +
                            $"'{prefixedHash[..Math.Min(prefixedHash.Length, 16)]}' not found in artifact store — store is inconsistent.");
                    }

                    acceptedArtifacts[phase.Id] = envelope;
                    finalArtifactHashes[phase.Id] = prefixedHash;
                }

                completedPhaseIds.Add(phase.Id);
                phaseRunTraces.Add(phaseRun);
                resumeFromIndex = i + 1;
            }
            else if (phaseTerminalState == "failed")
            {
                // Phase terminal — pipeline can't continue
                phaseRunTraces.Add(phaseRun);
                terminalPhaseOutcome = "failed";
                resumeFromIndex = i + 1; // Past the failed phase
                break;
            }
            else
            {
                // Phase was "running" or "pending" (crashed mid-execution)
                // Re-execute from this phase. Tokens/cost already accumulated above.
                resumeFromIndex = i;
                break;
            }
        }

        return new ResumeState
        {
            AcceptedArtifacts = acceptedArtifacts,
            PhaseRunTraces = phaseRunTraces,
            CompletedPhaseIds = completedPhaseIds,
            FinalArtifactHashes = finalArtifactHashes,
            AccumulatedTokensIn = totalTokensIn,
            AccumulatedTokensOut = totalTokensOut,
            AccumulatedCost = totalCost,
            ResumeFromPhaseIndex = resumeFromIndex,
            TerminalPhaseOutcome = terminalPhaseOutcome
        };
    }

    private async Task WriteRunFinalState(
        string runId,
        RunTrace runTrace,
        List<PhaseRunTrace> phaseRunTraces,
        CancellationToken ct)
    {
        try
        {
            await _runStore.WriteRunSnapshotAsync(runId, runTrace, ct);
            await _runStore.WritePhaseRunsRollupAsync(runId, phaseRunTraces, ct);
        }
        catch (Exception ex) when (runTrace.Outcome == "aborted")
        {
            _logger.LogError(ex, "Failed to persist aborted run state for {RunId}", runId);
        }
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

    /// <summary>
    /// Strip the "sha256:" prefix from a prefixed hash, returning the raw hex hash.
    /// If no prefix is present, returns the input unchanged.
    /// </summary>
    private static string StripHashPrefix(string prefixedHash)
    {
        const string prefix = "sha256:";
        return prefixedHash.StartsWith(prefix, StringComparison.Ordinal)
            ? prefixedHash[prefix.Length..]
            : prefixedHash;
    }
}

/// <summary>
/// Internal result of pipeline phase execution (before control arm and final persistence).
/// </summary>
internal sealed record PipelinePhaseResult
{
    public required RunTrace RunTrace { get; init; }
    public required List<PhaseRunTrace> PhaseRunTraces { get; init; }
}

/// <summary>
/// Reconstructed state from persisted phase snapshots for crash recovery resume.
/// </summary>
internal sealed record ResumeState
{
    /// <summary>Accepted artifacts from completed phases, keyed by phase ID.</summary>
    public required Dictionary<string, ArtifactEnvelope> AcceptedArtifacts { get; init; }

    /// <summary>Phase run traces from completed/failed phases (not running ones).</summary>
    public required List<PhaseRunTrace> PhaseRunTraces { get; init; }

    /// <summary>Phase IDs that completed successfully.</summary>
    public required List<string> CompletedPhaseIds { get; init; }

    /// <summary>Final artifact hashes from completed phases.</summary>
    public required Dictionary<string, string> FinalArtifactHashes { get; init; }

    public required int AccumulatedTokensIn { get; init; }
    public required int AccumulatedTokensOut { get; init; }
    public required decimal AccumulatedCost { get; init; }

    /// <summary>Index of the first phase to (re-)execute.</summary>
    public required int ResumeFromPhaseIndex { get; init; }

    /// <summary>If a phase reached a terminal failure state, the outcome string ("failed"). Null if no terminal failure.</summary>
    public string? TerminalPhaseOutcome { get; init; }
}
