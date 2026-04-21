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

public sealed class FidelityExecutorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiskArtifactStore _artifactStore;
    private readonly DiskRunStore _runStore;
    private readonly SchemaRegistry _schemas;

    public FidelityExecutorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"forge-fidelity-test-{Guid.NewGuid():N}");
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

    private FidelityExecutor CreateExecutor(StubLlmClient llm)
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

        return new FidelityExecutor(
            phaseExecutor, _artifactStore,
            NullLogger<FidelityExecutor>.Instance);
    }

    private static MethodologyDefinition TestMethodology => new()
    {
        Id = "test-v1",
        Phases = [new PhaseDefinition
        {
            Id = "implementation",
            Goal = "Implement the solution",
            Inputs = [],
            OutputSchema = "implementation/v1",
            Instructions = "Build it."
        }],
        Fidelity = new FidelityConfig { TargetPhase = "implementation" }
    };

    private static TaskBrief TestTask => new()
    {
        TaskId = "TEST-FID",
        Title = "Test Task",
        Description = "Build a rate limiter."
    };

    private static ArtifactEnvelope SourceIntentEnvelope => new()
    {
        ArtifactType = "source_intent",
        SchemaVersion = "1",
        ProducedByPhase = "source_intent",
        Payload = JsonDocument.Parse("""
        {
            "task_brief": "Build a rate limiter.",
            "acceptance_criteria": [
                {"id": "AC1", "criterion": "Rate limiter works", "verifiable": true}
            ],
            "explicit_constraints": [],
            "examples": [],
            "counter_examples": [],
            "preferred_approach": null
        }
        """).RootElement
    };

    private static ArtifactEnvelope ImplementationEnvelope => new()
    {
        ArtifactType = "implementation",
        SchemaVersion = "1",
        ProducedByPhase = "implementation",
        Payload = JsonDocument.Parse("""
        {
            "language": "typescript",
            "components": [
                {
                    "name": "RateLimiter",
                    "file_path": "src/rate-limiter.ts",
                    "code": "class RateLimiter { check() { return true; } }",
                    "dependencies": [],
                    "exports": ["RateLimiter"]
                }
            ],
            "entry_point": "src/rate-limiter.ts",
            "setup_instructions": "npm install"
        }
        """).RootElement
    };

    // ── Input Validation Tests ──

    [Fact]
    public void ValidateInputs_Passes_WhenCorrectTypes()
    {
        var error = FidelityExecutor.ValidateInputs(SourceIntentEnvelope, ImplementationEnvelope);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateInputs_Rejects_WhenFirstInputNotSourceIntent()
    {
        var wrongEnvelope = new ArtifactEnvelope
        {
            ArtifactType = "requirements",
            SchemaVersion = "1",
            ProducedByPhase = "requirements",
            Payload = JsonDocument.Parse("{}").RootElement
        };

        var error = FidelityExecutor.ValidateInputs(wrongEnvelope, ImplementationEnvelope);

        Assert.NotNull(error);
        Assert.Contains("source_intent", error);
    }

    [Fact]
    public void ValidateInputs_Rejects_WhenSecondInputIsSourceIntent()
    {
        var error = FidelityExecutor.ValidateInputs(SourceIntentEnvelope, SourceIntentEnvelope);

        Assert.NotNull(error);
        Assert.Contains("source_intent", error);
    }

    [Fact]
    public void ValidateInputs_Rejects_WhenSecondInputIsFidelity()
    {
        var fidelityEnvelope = new ArtifactEnvelope
        {
            ArtifactType = "fidelity",
            SchemaVersion = "1",
            ProducedByPhase = "fidelity",
            Payload = JsonDocument.Parse("{}").RootElement
        };

        var error = FidelityExecutor.ValidateInputs(SourceIntentEnvelope, fidelityEnvelope);

        Assert.NotNull(error);
        Assert.Contains("fidelity", error);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsInputViolation_WhenInputsInvalid()
    {
        var llm = StubLlmClient.WithFixedResponse("{}");
        var executor = CreateExecutor(llm);

        var wrongEnvelope = new ArtifactEnvelope
        {
            ArtifactType = "requirements",
            SchemaVersion = "1",
            ProducedByPhase = "requirements",
            Payload = JsonDocument.Parse("{}").RootElement
        };

        var result = await executor.ExecuteAsync(
            "run-1", 0, wrongEnvelope, ImplementationEnvelope,
            "implementation", TestMethodology, TestTask);

        Assert.Equal(PhaseRunStatus.Failed, result.Status);
        Assert.NotNull(result.InputViolationError);
        Assert.Contains("source_intent", result.InputViolationError);
        Assert.NotNull(result.PhaseRunTrace);
        Assert.Equal("fidelity", result.PhaseRunTrace.PhaseId);
    }

    // ── PhaseDefinition Tests ──

    [Fact]
    public void BuildFidelityPhaseDefinition_HasCorrectStructure()
    {
        var phase = FidelityExecutor.BuildFidelityPhaseDefinition("implementation", TestMethodology);

        Assert.Equal("fidelity", phase.Id);
        Assert.Equal("fidelity/v1", phase.OutputSchema);
        Assert.Equal(2, phase.Inputs.Count);
        Assert.Contains("source_intent", phase.Inputs);
        Assert.Contains("implementation", phase.Inputs);
        Assert.Contains("implementation", phase.Goal);
        Assert.Equal(3, phase.MaxAttempts);
    }

    [Fact]
    public void BuildFidelityPhaseDefinition_UsesConfiguredMaxAttempts()
    {
        var methodology = TestMethodology with
        {
            Fidelity = new FidelityConfig { TargetPhase = "implementation", MaxAttempts = 5 }
        };

        var phase = FidelityExecutor.BuildFidelityPhaseDefinition("implementation", methodology);

        Assert.Equal(5, phase.MaxAttempts);
    }

    // ── Fidelity Result Extraction Tests ──

    [Fact]
    public void ExtractFidelityResults_Pass()
    {
        var envelope = new ArtifactEnvelope
        {
            ArtifactType = "fidelity",
            SchemaVersion = "1",
            ProducedByPhase = "fidelity",
            Payload = JsonDocument.Parse("""
            {
                "overall_match": "PASS",
                "acceptance_criteria_results": [
                    {"criterion_id": "AC1", "satisfied": true, "evidence": "Works correctly"}
                ],
                "drift_detected": []
            }
            """).RootElement
        };

        var (outcome, driftCodes) = FidelityExecutor.ExtractFidelityResults(envelope);

        Assert.Equal("pass", outcome);
        Assert.NotNull(driftCodes);
        Assert.Empty(driftCodes);
    }

    [Fact]
    public void ExtractFidelityResults_FailWithDrift()
    {
        var envelope = new ArtifactEnvelope
        {
            ArtifactType = "fidelity",
            SchemaVersion = "1",
            ProducedByPhase = "fidelity",
            Payload = JsonDocument.Parse("""
            {
                "overall_match": "FAIL",
                "acceptance_criteria_results": [
                    {"criterion_id": "AC1", "satisfied": false, "evidence": "Missing per-user tracking"}
                ],
                "drift_detected": [
                    {
                        "code": "OMITTED_CONSTRAINT",
                        "source_intent_ref": "/explicit_constraints/0",
                        "evidence_locator": "/components/0/code",
                        "explanation": "Per-user constraint was dropped"
                    }
                ]
            }
            """).RootElement
        };

        var (outcome, driftCodes) = FidelityExecutor.ExtractFidelityResults(envelope);

        Assert.Equal("fail", outcome);
        Assert.NotNull(driftCodes);
        Assert.Single(driftCodes);
        Assert.Equal("OMITTED_CONSTRAINT", driftCodes[0]);
    }

    [Fact]
    public void ExtractFidelityResults_Partial()
    {
        var envelope = new ArtifactEnvelope
        {
            ArtifactType = "fidelity",
            SchemaVersion = "1",
            ProducedByPhase = "fidelity",
            Payload = JsonDocument.Parse("""
            {
                "overall_match": "PARTIAL",
                "acceptance_criteria_results": [
                    {"criterion_id": "AC1", "satisfied": true, "evidence": "Works"}
                ],
                "drift_detected": [
                    {
                        "code": "SCOPE_BROADENED",
                        "source_intent_ref": "/task_brief",
                        "evidence_locator": "/components/1",
                        "explanation": "Extra caching layer not in original intent"
                    }
                ]
            }
            """).RootElement
        };

        var (outcome, driftCodes) = FidelityExecutor.ExtractFidelityResults(envelope);

        Assert.Equal("partial", outcome);
        Assert.NotNull(driftCodes);
        Assert.Single(driftCodes);
        Assert.Equal("SCOPE_BROADENED", driftCodes[0]);
    }

    [Fact]
    public void ExtractFidelityResults_MultipleDriftCodes()
    {
        var envelope = new ArtifactEnvelope
        {
            ArtifactType = "fidelity",
            SchemaVersion = "1",
            ProducedByPhase = "fidelity",
            Payload = JsonDocument.Parse("""
            {
                "overall_match": "FAIL",
                "acceptance_criteria_results": [],
                "drift_detected": [
                    {"code": "OMITTED_CONSTRAINT", "source_intent_ref": "/ec/0", "evidence_locator": "/x", "explanation": "dropped"},
                    {"code": "INVENTED_REQUIREMENT", "source_intent_ref": "/tb", "evidence_locator": "/y", "explanation": "added"},
                    {"code": "CONSTRAINT_WEAKENED", "source_intent_ref": "/ec/1", "evidence_locator": "/z", "explanation": "weakened"}
                ]
            }
            """).RootElement
        };

        var (_, driftCodes) = FidelityExecutor.ExtractFidelityResults(envelope);

        Assert.NotNull(driftCodes);
        Assert.Equal(3, driftCodes.Count);
        Assert.Contains("OMITTED_CONSTRAINT", driftCodes);
        Assert.Contains("INVENTED_REQUIREMENT", driftCodes);
        Assert.Contains("CONSTRAINT_WEAKENED", driftCodes);
    }
}
