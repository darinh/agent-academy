using AgentAcademy.Forge.Artifacts;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Storage;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Forge.Execution;

/// <summary>
/// Runs seeded-defect cases through the fidelity executor and compares
/// LLM verdicts against ground truth. Computes detection rates and
/// threshold pass/fail for the spike falsifiability statement.
/// </summary>
public sealed class SeededDefectRunner
{
    private readonly FidelityExecutor _fidelityExecutor;
    private readonly IRunStore _runStore;
    private readonly ILogger<SeededDefectRunner> _logger;

    public SeededDefectRunner(
        FidelityExecutor fidelityExecutor,
        IRunStore runStore,
        ILogger<SeededDefectRunner> logger)
    {
        _fidelityExecutor = fidelityExecutor;
        _runStore = runStore;
        _logger = logger;
    }

    /// <summary>
    /// Run all cases from the catalog and produce an aggregated report.
    /// </summary>
    public async Task<SeededDefectReport> RunAsync(
        IReadOnlyList<SeededDefect> defects,
        MethodologyDefinition methodology,
        CancellationToken ct = default)
    {
        var results = new List<SeededDefectResult>();

        foreach (var defect in defects)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("Running seeded defect {Id}: {Description}", defect.Id, defect.Description);

            var result = await RunSingleAsync(defect, methodology, ct);
            results.Add(result);

            _logger.LogInformation("  {Id}: expected={Expected}, actual={Actual}, match={Match}, drift={Drift}",
                defect.Id, defect.ExpectedOverallMatch, result.ActualMatch,
                result.MatchCorrect, result.DriftCodesDetected);
        }

        return BuildReport(defects, results);
    }

    private async Task<SeededDefectResult> RunSingleAsync(
        SeededDefect defect,
        MethodologyDefinition methodology,
        CancellationToken ct)
    {
        var runId = ForgeId.NewRunId();
        var task = new TaskBrief
        {
            TaskId = $"SEEDED-{defect.Id}",
            Title = defect.Description,
            Description = defect.Description
        };

        // Initialize a run for the fidelity executor (it needs a run context for storage)
        var runTrace = new RunTrace
        {
            RunId = runId,
            TaskId = task.TaskId,
            MethodologyVersion = methodology.Id,
            StartedAt = DateTime.UtcNow,
            Outcome = "running",
            PipelineTokens = new TokenCount(),
            ControlTokens = new TokenCount(),
            FinalArtifactHashes = new Dictionary<string, string>()
        };
        await _runStore.InitializeRunAsync(runId, runTrace, task, methodology, ct);

        try
        {
            var fidelityResult = await _fidelityExecutor.ExecuteAsync(
                runId,
                phaseIndex: 0,
                sourceIntentEnvelope: defect.SourceIntent,
                targetOutputEnvelope: defect.DriftedOutput,
                targetPhaseId: methodology.Fidelity?.TargetPhase ?? "implementation",
                methodology: methodology,
                task: task,
                ct: ct);

            if (fidelityResult.Status != PhaseRunStatus.Succeeded || fidelityResult.FidelityOutcome is null)
            {
                return new SeededDefectResult
                {
                    DefectId = defect.Id,
                    ExpectedMatch = defect.ExpectedOverallMatch,
                    ActualMatch = fidelityResult.FidelityOutcome,
                    ExpectedDriftCodes = defect.ExpectedDriftCodes,
                    ActualDriftCodes = fidelityResult.DriftCodes ?? [],
                    MatchCorrect = false,
                    DriftCodesDetected = false,
                    FidelityStatus = fidelityResult.Status,
                    Inconclusive = true
                };
            }

            var actualMatch = fidelityResult.FidelityOutcome.ToUpperInvariant();
            var actualCodes = (fidelityResult.DriftCodes ?? []).ToList();

            var matchCorrect = string.Equals(actualMatch, defect.ExpectedOverallMatch,
                StringComparison.OrdinalIgnoreCase);

            // All expected drift codes must be present (superset is OK)
            var driftCodesDetected = defect.ExpectedDriftCodes.All(expected =>
                actualCodes.Any(actual => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)));

            return new SeededDefectResult
            {
                DefectId = defect.Id,
                ExpectedMatch = defect.ExpectedOverallMatch,
                ActualMatch = actualMatch,
                ExpectedDriftCodes = defect.ExpectedDriftCodes,
                ActualDriftCodes = actualCodes,
                MatchCorrect = matchCorrect,
                DriftCodesDetected = driftCodesDetected,
                FidelityStatus = fidelityResult.Status,
                Inconclusive = false
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Seeded defect {Id} failed with exception", defect.Id);
            return new SeededDefectResult
            {
                DefectId = defect.Id,
                ExpectedMatch = defect.ExpectedOverallMatch,
                ActualMatch = null,
                ExpectedDriftCodes = defect.ExpectedDriftCodes,
                ActualDriftCodes = [],
                MatchCorrect = false,
                DriftCodesDetected = false,
                FidelityStatus = PhaseRunStatus.Failed,
                Inconclusive = true
            };
        }
    }

    internal static SeededDefectReport BuildReport(
        IReadOnlyList<SeededDefect> defects,
        IReadOnlyList<SeededDefectResult> results)
    {
        var defectMap = defects.ToDictionary(d => d.Id);
        var conclusive = results.Where(r => !r.Inconclusive).ToList();

        // Blocking detection rate: fraction of blocking defects correctly detected
        var blockingResults = conclusive
            .Where(r => defectMap[r.DefectId].DriftCategory == "blocking")
            .ToList();
        var blockingDetected = blockingResults.Count(r => r.DriftCodesDetected && r.MatchCorrect);
        var blockingRate = blockingResults.Count > 0
            ? (double)blockingDetected / blockingResults.Count
            : 1.0;

        // Advisory detection rate: fraction of advisory defects correctly detected
        var advisoryResults = conclusive
            .Where(r => defectMap[r.DefectId].DriftCategory == "advisory")
            .ToList();
        var advisoryDetected = advisoryResults.Count(r => r.DriftCodesDetected);
        var advisoryRate = advisoryResults.Count > 0
            ? (double)advisoryDetected / advisoryResults.Count
            : 1.0;

        // Overall match accuracy: all threshold-bearing cases (blocking + advisory + clean)
        var thresholdBearing = conclusive
            .Where(r => defectMap[r.DefectId].DriftCategory is "blocking" or "advisory" or "clean")
            .ToList();
        var matchAccuracy = thresholdBearing.Count > 0
            ? (double)thresholdBearing.Count(r => r.MatchCorrect) / thresholdBearing.Count
            : 1.0;

        // False positive rate: fraction of clean cases where drift was incorrectly reported
        var cleanResults = conclusive
            .Where(r => defectMap[r.DefectId].DriftCategory == "clean")
            .ToList();
        var falsePositives = cleanResults.Count(r => r.ActualDriftCodes.Count > 0);
        var fpRate = cleanResults.Count > 0
            ? (double)falsePositives / cleanResults.Count
            : 0.0;

        // Per-code recall
        var perCodeRecall = ComputePerCodeRecall(defects, results);

        return new SeededDefectReport
        {
            Results = results,
            BlockingDetectionRate = blockingRate,
            AdvisoryDetectionRate = advisoryRate,
            OverallMatchAccuracy = matchAccuracy,
            FalsePositiveRate = fpRate,
            InconclusiveCount = results.Count(r => r.Inconclusive),
            PerCodeRecall = perCodeRecall
        };
    }

    private static IReadOnlyDictionary<string, double> ComputePerCodeRecall(
        IReadOnlyList<SeededDefect> defects,
        IReadOnlyList<SeededDefectResult> results)
    {
        var recall = new Dictionary<string, double>();

        // Group defects by each expected drift code
        var codeToDefectIds = new Dictionary<string, List<string>>();
        foreach (var d in defects)
        {
            foreach (var code in d.ExpectedDriftCodes)
            {
                if (!codeToDefectIds.TryGetValue(code, out var list))
                {
                    list = [];
                    codeToDefectIds[code] = list;
                }
                list.Add(d.Id);
            }
        }

        var resultMap = results
            .Where(r => !r.Inconclusive)
            .ToDictionary(r => r.DefectId);

        foreach (var (code, defectIds) in codeToDefectIds)
        {
            var evaluable = defectIds.Where(id => resultMap.ContainsKey(id)).ToList();
            if (evaluable.Count == 0)
            {
                recall[code] = 0.0;
                continue;
            }

            var detected = evaluable.Count(id =>
                resultMap[id].ActualDriftCodes.Any(actual =>
                    string.Equals(actual, code, StringComparison.OrdinalIgnoreCase)));

            recall[code] = (double)detected / evaluable.Count;
        }

        return recall;
    }
}
