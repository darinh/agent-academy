using System.Text.Json;
using AgentAcademy.Forge.Artifacts;
using AgentAcademy.Forge.Costs;
using AgentAcademy.Forge.Execution;
using AgentAcademy.Forge.Llm;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Prompt;
using AgentAcademy.Forge.Schemas;
using AgentAcademy.Forge.Storage;
using AgentAcademy.Forge.Validation;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Forge.Tests;

public sealed class SeededDefectTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiskArtifactStore _artifactStore;
    private readonly DiskRunStore _runStore;
    private readonly SchemaRegistry _schemas;

    public SeededDefectTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"forge-seeded-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _artifactStore = new DiskArtifactStore(
            Path.Combine(_tempDir, "artifacts"),
            NullLogger<DiskArtifactStore>.Instance);
        _runStore = new DiskRunStore(
            _tempDir,
            NullLogger<DiskRunStore>.Instance);
        _schemas = new SchemaRegistry();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static MethodologyDefinition TestMethodology => new()
    {
        Id = "seeded-test-v1",
        Phases =
        [
            new PhaseDefinition
            {
                Id = "implementation",
                Goal = "Implement the solution",
                Inputs = [],
                OutputSchema = "implementation/v1",
                Instructions = "Build it."
            }
        ],
        Fidelity = new FidelityConfig { TargetPhase = "implementation" }
    };

    // ── Catalog Completeness Tests ──

    [Fact]
    public void Catalog_Contains_SevenCases()
    {
        Assert.Equal(7, SeededDefectCatalog.All.Count);
    }

    [Fact]
    public void Catalog_CoversAllFiveDriftCodes()
    {
        var allCodes = SeededDefectCatalog.All
            .SelectMany(d => d.ExpectedDriftCodes)
            .Distinct()
            .OrderBy(c => c)
            .ToList();

        Assert.Contains("OMITTED_CONSTRAINT", allCodes);
        Assert.Contains("INVENTED_REQUIREMENT", allCodes);
        Assert.Contains("SCOPE_BROADENED", allCodes);
        Assert.Contains("SCOPE_NARROWED", allCodes);
        Assert.Contains("CONSTRAINT_WEAKENED", allCodes);
    }

    [Fact]
    public void Catalog_HasBlockingAdvisoryCleanAndDiagnosticCases()
    {
        var categories = SeededDefectCatalog.All.Select(d => d.DriftCategory).Distinct().OrderBy(c => c).ToList();
        Assert.Contains("blocking", categories);
        Assert.Contains("advisory", categories);
        Assert.Contains("clean", categories);
        Assert.Contains("diagnostic", categories);
    }

    [Fact]
    public void Catalog_AllIdsAreUnique()
    {
        var ids = SeededDefectCatalog.All.Select(d => d.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Catalog_AllSourceIntentsAreSourceIntentType()
    {
        foreach (var defect in SeededDefectCatalog.All)
        {
            Assert.Equal("source_intent", defect.SourceIntent.ArtifactType);
            Assert.Equal("1", defect.SourceIntent.SchemaVersion);
        }
    }

    [Fact]
    public void Catalog_AllDriftedOutputsAreImplementationType()
    {
        foreach (var defect in SeededDefectCatalog.All)
        {
            Assert.Equal("implementation", defect.DriftedOutput.ArtifactType);
            Assert.Equal("1", defect.DriftedOutput.SchemaVersion);
        }
    }

    [Fact]
    public void Catalog_SourceIntentsAreStructurallyValid()
    {
        var validator = new StructuralValidator(_schemas);
        foreach (var defect in SeededDefectCatalog.All)
        {
            var findings = validator.Validate(defect.SourceIntent, attemptNumber: 1);
            var blocking = findings.Where(f => f.Blocking).ToList();
            Assert.True(blocking.Count == 0,
                $"Defect {defect.Id} source_intent has structural errors: " +
                string.Join("; ", blocking.Select(f => f.Code)));
        }
    }

    [Fact]
    public void Catalog_DriftedOutputsAreStructurallyValid()
    {
        var validator = new StructuralValidator(_schemas);
        foreach (var defect in SeededDefectCatalog.All)
        {
            var findings = validator.Validate(defect.DriftedOutput, attemptNumber: 1);
            var blocking = findings.Where(f => f.Blocking).ToList();
            Assert.True(blocking.Count == 0,
                $"Defect {defect.Id} drifted output has structural errors: " +
                string.Join("; ", blocking.Select(f => f.Code)));
        }
    }

    [Fact]
    public void Catalog_CleanCase_HasEmptyDriftCodes()
    {
        Assert.Empty(SeededDefectCatalog.CleanPass.ExpectedDriftCodes);
        Assert.Equal("PASS", SeededDefectCatalog.CleanPass.ExpectedOverallMatch);
    }

    [Fact]
    public void Catalog_MultiDrift_IsDiagnosticCategory()
    {
        Assert.Equal("diagnostic", SeededDefectCatalog.MultiDrift.DriftCategory);
        Assert.True(SeededDefectCatalog.MultiDrift.ExpectedDriftCodes.Count >= 2);
    }

    [Theory]
    [InlineData("SD-OMIT", "FAIL")]
    [InlineData("SD-INVENT", "PARTIAL")]
    [InlineData("SD-BROAD", "PARTIAL")]
    [InlineData("SD-NARROW", "PARTIAL")]
    [InlineData("SD-WEAKEN", "FAIL")]
    [InlineData("SD-CLEAN", "PASS")]
    [InlineData("SD-MULTI", "FAIL")]
    public void Catalog_ExpectedMatches_AreConsistentWithDriftRules(string id, string expectedMatch)
    {
        var defect = SeededDefectCatalog.All.Single(d => d.Id == id);
        Assert.Equal(expectedMatch, defect.ExpectedOverallMatch);
    }

    // ── Report Building Tests ──

    [Fact]
    public void BuildReport_PerfectDetection_ReportsFullRates()
    {
        var defects = SeededDefectCatalog.All;
        var results = defects.Select(d => new SeededDefectResult
        {
            DefectId = d.Id,
            ExpectedMatch = d.ExpectedOverallMatch,
            ActualMatch = d.ExpectedOverallMatch,
            ExpectedDriftCodes = d.ExpectedDriftCodes,
            ActualDriftCodes = d.ExpectedDriftCodes.ToList(),
            MatchCorrect = true,
            DriftCodesDetected = true,
            FidelityStatus = PhaseRunStatus.Succeeded,
            Inconclusive = false
        }).ToList();

        var report = SeededDefectRunner.BuildReport(defects, results);

        Assert.Equal(1.0, report.BlockingDetectionRate);
        Assert.Equal(1.0, report.AdvisoryDetectionRate);
        Assert.Equal(1.0, report.OverallMatchAccuracy);
        Assert.Equal(0.0, report.FalsePositiveRate);
        Assert.Equal(0, report.InconclusiveCount);
        Assert.True(report.MeetsBlockingThreshold);
        Assert.True(report.MeetsAdvisoryThreshold);

        // Per-code recall should be 1.0 for all codes
        foreach (var (code, recall) in report.PerCodeRecall)
        {
            Assert.Equal(1.0, recall);
        }
    }

    [Fact]
    public void BuildReport_ZeroDetection_ReportsZeroRates()
    {
        var defects = SeededDefectCatalog.All;
        var results = defects.Select(d => new SeededDefectResult
        {
            DefectId = d.Id,
            ExpectedMatch = d.ExpectedOverallMatch,
            ActualMatch = "PASS", // Everything reported as PASS
            ExpectedDriftCodes = d.ExpectedDriftCodes,
            ActualDriftCodes = [], // No drift detected
            MatchCorrect = d.ExpectedOverallMatch == "PASS",
            DriftCodesDetected = d.ExpectedDriftCodes.Count == 0,
            FidelityStatus = PhaseRunStatus.Succeeded,
            Inconclusive = false
        }).ToList();

        var report = SeededDefectRunner.BuildReport(defects, results);

        Assert.Equal(0.0, report.BlockingDetectionRate);
        Assert.Equal(0.0, report.AdvisoryDetectionRate);
        Assert.False(report.MeetsBlockingThreshold);
        Assert.False(report.MeetsAdvisoryThreshold);
    }

    [Fact]
    public void BuildReport_InconclusiveCases_ExcludedFromRates()
    {
        var defects = SeededDefectCatalog.All;
        var results = defects.Select(d => new SeededDefectResult
        {
            DefectId = d.Id,
            ExpectedMatch = d.ExpectedOverallMatch,
            ActualMatch = null,
            ExpectedDriftCodes = d.ExpectedDriftCodes,
            ActualDriftCodes = [],
            MatchCorrect = false,
            DriftCodesDetected = false,
            FidelityStatus = PhaseRunStatus.Failed,
            Inconclusive = true
        }).ToList();

        var report = SeededDefectRunner.BuildReport(defects, results);

        // All inconclusive → rates default to 1.0 (no evaluable cases = no failures)
        Assert.Equal(1.0, report.BlockingDetectionRate);
        Assert.Equal(1.0, report.AdvisoryDetectionRate);
        Assert.Equal(7, report.InconclusiveCount);
    }

    [Fact]
    public void BuildReport_FalsePositiveOnCleanCase_ReportsNonZeroFPRate()
    {
        var defects = new List<SeededDefect> { SeededDefectCatalog.CleanPass };
        var results = new List<SeededDefectResult>
        {
            new()
            {
                DefectId = "SD-CLEAN",
                ExpectedMatch = "PASS",
                ActualMatch = "PARTIAL",
                ExpectedDriftCodes = [],
                ActualDriftCodes = ["INVENTED_REQUIREMENT"],
                MatchCorrect = false,
                DriftCodesDetected = true, // empty expected = vacuously true
                FidelityStatus = PhaseRunStatus.Succeeded,
                Inconclusive = false
            }
        };

        var report = SeededDefectRunner.BuildReport(defects, results);

        Assert.Equal(1.0, report.FalsePositiveRate);
    }

    [Fact]
    public void BuildReport_DiagnosticCases_ExcludedFromThresholds()
    {
        // Only the diagnostic case (SD-MULTI)
        var defects = new List<SeededDefect> { SeededDefectCatalog.MultiDrift };
        var results = new List<SeededDefectResult>
        {
            new()
            {
                DefectId = "SD-MULTI",
                ExpectedMatch = "FAIL",
                ActualMatch = "PASS", // Wrong — but diagnostic, so shouldn't affect thresholds
                ExpectedDriftCodes = ["OMITTED_CONSTRAINT", "SCOPE_BROADENED"],
                ActualDriftCodes = [],
                MatchCorrect = false,
                DriftCodesDetected = false,
                FidelityStatus = PhaseRunStatus.Succeeded,
                Inconclusive = false
            }
        };

        var report = SeededDefectRunner.BuildReport(defects, results);

        // No blocking/advisory/clean cases → all rates default to 1.0
        Assert.Equal(1.0, report.BlockingDetectionRate);
        Assert.Equal(1.0, report.AdvisoryDetectionRate);
    }

    [Fact]
    public void BuildReport_PerCodeRecall_TracksIndividualCodes()
    {
        var defects = new List<SeededDefect>
        {
            SeededDefectCatalog.OmittedConstraint,
            SeededDefectCatalog.ConstraintWeakened
        };

        var results = new List<SeededDefectResult>
        {
            new() // Detected OMITTED_CONSTRAINT
            {
                DefectId = "SD-OMIT",
                ExpectedMatch = "FAIL",
                ActualMatch = "FAIL",
                ExpectedDriftCodes = ["OMITTED_CONSTRAINT"],
                ActualDriftCodes = ["OMITTED_CONSTRAINT"],
                MatchCorrect = true,
                DriftCodesDetected = true,
                FidelityStatus = PhaseRunStatus.Succeeded,
                Inconclusive = false
            },
            new() // Missed CONSTRAINT_WEAKENED
            {
                DefectId = "SD-WEAKEN",
                ExpectedMatch = "FAIL",
                ActualMatch = "PASS",
                ExpectedDriftCodes = ["CONSTRAINT_WEAKENED"],
                ActualDriftCodes = [],
                MatchCorrect = false,
                DriftCodesDetected = false,
                FidelityStatus = PhaseRunStatus.Succeeded,
                Inconclusive = false
            }
        };

        var report = SeededDefectRunner.BuildReport(defects, results);

        Assert.Equal(1.0, report.PerCodeRecall["OMITTED_CONSTRAINT"]);
        Assert.Equal(0.0, report.PerCodeRecall["CONSTRAINT_WEAKENED"]);
    }

    // ── Additional Coverage: edge cases and mixed scenarios ──

    [Fact]
    public void Catalog_AllDefects_HaveNonEmptyDescriptions()
    {
        foreach (var defect in SeededDefectCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(defect.Description),
                $"Defect {defect.Id} has empty description");
        }
    }

    [Fact]
    public void BuildReport_EmptyInputs_ReturnsDefaults()
    {
        var report = SeededDefectRunner.BuildReport(
            Array.Empty<SeededDefect>(),
            Array.Empty<SeededDefectResult>());

        Assert.Equal(1.0, report.BlockingDetectionRate);
        Assert.Equal(1.0, report.AdvisoryDetectionRate);
        Assert.Equal(1.0, report.OverallMatchAccuracy);
        Assert.Equal(0.0, report.FalsePositiveRate);
        Assert.Equal(0, report.InconclusiveCount);
        Assert.Empty(report.PerCodeRecall);
    }

    [Fact]
    public void BuildReport_MixedDetection_CalculatesPartialRates()
    {
        // SD-OMIT (blocking) detected, SD-WEAKEN (blocking) missed,
        // SD-INVENT (advisory) detected, SD-CLEAN clean correct
        var defects = new List<SeededDefect>
        {
            SeededDefectCatalog.OmittedConstraint,
            SeededDefectCatalog.ConstraintWeakened,
            SeededDefectCatalog.InventedRequirement,
            SeededDefectCatalog.CleanPass
        };

        var results = new List<SeededDefectResult>
        {
            new()
            {
                DefectId = "SD-OMIT",
                ExpectedMatch = "FAIL",
                ActualMatch = "FAIL",
                ExpectedDriftCodes = ["OMITTED_CONSTRAINT"],
                ActualDriftCodes = ["OMITTED_CONSTRAINT"],
                MatchCorrect = true,
                DriftCodesDetected = true,
                FidelityStatus = PhaseRunStatus.Succeeded,
                Inconclusive = false
            },
            new()
            {
                DefectId = "SD-WEAKEN",
                ExpectedMatch = "FAIL",
                ActualMatch = "PASS",
                ExpectedDriftCodes = ["CONSTRAINT_WEAKENED"],
                ActualDriftCodes = [],
                MatchCorrect = false,
                DriftCodesDetected = false,
                FidelityStatus = PhaseRunStatus.Succeeded,
                Inconclusive = false
            },
            new()
            {
                DefectId = "SD-INVENT",
                ExpectedMatch = "PARTIAL",
                ActualMatch = "PARTIAL",
                ExpectedDriftCodes = ["INVENTED_REQUIREMENT"],
                ActualDriftCodes = ["INVENTED_REQUIREMENT"],
                MatchCorrect = true,
                DriftCodesDetected = true,
                FidelityStatus = PhaseRunStatus.Succeeded,
                Inconclusive = false
            },
            new()
            {
                DefectId = "SD-CLEAN",
                ExpectedMatch = "PASS",
                ActualMatch = "PASS",
                ExpectedDriftCodes = [],
                ActualDriftCodes = [],
                MatchCorrect = true,
                DriftCodesDetected = true,
                FidelityStatus = PhaseRunStatus.Succeeded,
                Inconclusive = false
            }
        };

        var report = SeededDefectRunner.BuildReport(defects, results);

        // Blocking: 1 detected / 2 total = 0.5
        Assert.Equal(0.5, report.BlockingDetectionRate);
        // Advisory: 1 detected / 1 total = 1.0
        Assert.Equal(1.0, report.AdvisoryDetectionRate);
        // Overall match accuracy: 3 correct / 4 threshold-bearing = 0.75
        Assert.Equal(0.75, report.OverallMatchAccuracy);
        // Clean case correct → 0 false positives
        Assert.Equal(0.0, report.FalsePositiveRate);
        // Thresholds: blocking needs ≥ 0.80 → fails (0.5), advisory needs ≥ 0.60 → passes (1.0)
        Assert.False(report.MeetsBlockingThreshold);
        Assert.True(report.MeetsAdvisoryThreshold);
    }

    // ── Runner Integration Tests (with stub LLM) ──

    [Fact]
    public async Task Runner_WithGroundTruthStub_AchievesPerfectDetection()
    {
        // Stub that returns the ground-truth fidelity verdict for each defect
        var defectQueue = new Queue<SeededDefect>(SeededDefectCatalog.All);
        var llm = new StubLlmClient((req, _) =>
        {
            if (req.SystemMessage.Contains("semantic validator"))
            {
                return Task.FromResult(new LlmResponse
                {
                    Content = """{"findings": []}""",
                    InputTokens = 50, OutputTokens = 20, Model = req.Model, LatencyMs = 10
                });
            }

            // Generation call — return fidelity verdict matching ground truth
            // The runner processes sequentially, so we peek at the current defect
            var defect = defectQueue.Count > 0 ? defectQueue.Peek() : SeededDefectCatalog.CleanPass;

            var fidelityPayload = BuildGroundTruthFidelityResponse(defect);
            return Task.FromResult(new LlmResponse
            {
                Content = fidelityPayload,
                InputTokens = 100, OutputTokens = 200, Model = req.Model, LatencyMs = 50
            });
        });

        var runner = CreateRunner(llm);

        // Use a wrapper that dequeues after each case
        var report = await RunWithDequeue(runner, defectQueue);

        Assert.Equal(0, report.InconclusiveCount);
        Assert.Equal(1.0, report.BlockingDetectionRate);
        Assert.Equal(1.0, report.AdvisoryDetectionRate);
        Assert.Equal(1.0, report.OverallMatchAccuracy);
        Assert.Equal(0.0, report.FalsePositiveRate);
        Assert.True(report.MeetsBlockingThreshold);
        Assert.True(report.MeetsAdvisoryThreshold);
    }

    [Fact]
    public async Task Runner_WithAllPassStub_FailsBlockingThreshold()
    {
        // Stub that always reports PASS with no drift — should fail blocking detection
        var llm = new StubLlmClient((req, _) =>
        {
            if (req.SystemMessage.Contains("semantic validator"))
            {
                return Task.FromResult(new LlmResponse
                {
                    Content = """{"findings": []}""",
                    InputTokens = 50, OutputTokens = 20, Model = req.Model, LatencyMs = 10
                });
            }

            var passResponse = """
            {
              "body": {
                "overall_match": "PASS",
                "acceptance_criteria_results": [
                  {"criterion_id": "AC1", "satisfied": true, "evidence": "All good"}
                ],
                "drift_detected": []
              }
            }
            """;
            return Task.FromResult(new LlmResponse
            {
                Content = passResponse,
                InputTokens = 100, OutputTokens = 200, Model = req.Model, LatencyMs = 50
            });
        });

        var runner = CreateRunner(llm);
        var report = await runner.RunAsync(SeededDefectCatalog.All, TestMethodology);

        // Blocking defects (SD-OMIT, SD-WEAKEN) will have wrong match → 0% detection
        Assert.Equal(0.0, report.BlockingDetectionRate);
        Assert.False(report.MeetsBlockingThreshold);
    }

    // ── Helpers ──

    private SeededDefectRunner CreateRunner(StubLlmClient llm)
    {
        var structural = new StructuralValidator(_schemas);
        var semantic = new SemanticValidator(llm, NullLogger<SemanticValidator>.Instance);
        var crossArtifact = new CrossArtifactValidator();
        var pipeline = new ValidatorPipeline(structural, semantic, crossArtifact, _schemas);
        var promptBuilder = new PromptBuilder(_schemas);

        var phaseExecutor = new PhaseExecutor(
            llm, promptBuilder, pipeline, _artifactStore, _runStore,
            new CostCalculator(), TimeProvider.System,
            NullLogger<PhaseExecutor>.Instance);

        var fidelityExecutor = new FidelityExecutor(
            phaseExecutor, _artifactStore,
            NullLogger<FidelityExecutor>.Instance);

        return new SeededDefectRunner(
            fidelityExecutor, _runStore,
            NullLogger<SeededDefectRunner>.Instance);
    }

    /// <summary>
    /// Run the catalog cases sequentially, dequeuing after each FidelityExecutor call completes.
    /// This ensures the queue-based stub matches each defect to its ground truth response.
    /// </summary>
    private static async Task<SeededDefectReport> RunWithDequeue(
        SeededDefectRunner runner,
        Queue<SeededDefect> defectQueue)
    {
        var defects = SeededDefectCatalog.All;
        var results = new List<SeededDefectResult>();

        foreach (var defect in defects)
        {
            var singleReport = await runner.RunAsync([defect], TestMethodology);
            results.Add(singleReport.Results[0]);
            if (defectQueue.Count > 0) defectQueue.Dequeue();
        }

        return SeededDefectRunner.BuildReport(defects, results);
    }

    private static string BuildGroundTruthFidelityResponse(SeededDefect defect)
    {
        var criteriaResults = new List<object>();
        // Parse acceptance criteria from source intent
        if (defect.SourceIntent.Payload.TryGetProperty("acceptance_criteria", out var acArray))
        {
            foreach (var ac in acArray.EnumerateArray())
            {
                var id = ac.GetProperty("id").GetString()!;
                criteriaResults.Add(new
                {
                    criterion_id = id,
                    satisfied = true,
                    evidence = "Satisfied per ground truth"
                });
            }
        }

        if (criteriaResults.Count == 0)
        {
            criteriaResults.Add(new { criterion_id = "AC1", satisfied = true, evidence = "Default" });
        }

        var driftItems = defect.ExpectedDriftCodes.Select(code => new
        {
            code,
            source_intent_ref = "/task_brief",
            evidence_locator = "/files/0/content",
            explanation = $"Ground truth: {code} drift injected for testing"
        }).ToArray();

        var payload = new
        {
            body = new
            {
                overall_match = defect.ExpectedOverallMatch,
                acceptance_criteria_results = criteriaResults,
                drift_detected = driftItems,
                summary = $"Ground truth verdict for {defect.Id}"
            }
        };

        return JsonSerializer.Serialize(payload);
    }
}
