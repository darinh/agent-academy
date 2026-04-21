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

public sealed class ControlExecutorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiskArtifactStore _artifactStore;
    private readonly SchemaRegistry _schemas;
    private readonly StructuralValidator _structural;

    public ControlExecutorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"forge-control-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _artifactStore = new DiskArtifactStore(
            Path.Combine(_tempDir, "artifacts"),
            NullLogger<DiskArtifactStore>.Instance);
        _schemas = new SchemaRegistry();
        _structural = new StructuralValidator(_schemas);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private ControlExecutor CreateExecutor(StubLlmClient llm) =>
        new(llm, _schemas, _structural, _artifactStore,
            new CostCalculator(), TimeProvider.System,
            NullLogger<ControlExecutor>.Instance);

    private static TaskBrief TestTask => new()
    {
        TaskId = "CTRL-TEST",
        Title = "Control Test Task",
        Description = "Build a simple test widget."
    };

    private static MethodologyDefinition MethodologyWithControl(string targetSchema = "implementation/v1") => new()
    {
        Id = "test-ctrl-v1",
        MaxAttemptsDefault = 2,
        Control = new ControlConfig
        {
            TargetSchema = targetSchema
        },
        Phases =
        [
            new PhaseDefinition
            {
                Id = "requirements",
                Goal = "Extract requirements",
                Inputs = [],
                OutputSchema = "requirements/v1",
                Instructions = "Produce requirements."
            }
        ]
    };

    // --- Valid artifact responses for different schemas ---

    private const string ValidImplementation = """
    {
      "body": {
        "files": [{
          "path": "src/widget.ts",
          "language": "typescript",
          "content": "export function create(input: string) { return { id: input }; }",
          "implements_component_ids": ["C1"]
        }],
        "build_command": "tsc",
        "test_command": null,
        "notes": "Simple implementation"
      }
    }
    """;

    private const string ValidRequirements = """
    {
      "body": {
        "task_summary": "Build a test widget",
        "user_outcomes": [{"id": "U1", "outcome": "Widget works", "priority": "must"}],
        "functional_requirements": [{"id": "FR1", "statement": "Widget accepts input", "outcome_ids": ["U1"]}],
        "non_functional_requirements": [],
        "out_of_scope": ["Deployment"],
        "open_questions": []
      }
    }
    """;

    // --- Tests ---

    [Fact]
    public async Task HappyPath_StructurallyValid_ReturnsSuccess()
    {
        var llm = StubLlmClient.WithFixedResponse(ValidImplementation, inputTokens: 500, outputTokens: 1000);
        var executor = CreateExecutor(llm);

        var result = await executor.ExecuteAsync(TestTask, MethodologyWithControl());

        Assert.Equal("structurally_valid", result.Outcome);
        Assert.Equal(500, result.Tokens.In);
        Assert.Equal(1000, result.Tokens.Out);
        Assert.NotNull(result.ArtifactHash);
        Assert.StartsWith("sha256:", result.ArtifactHash);
    }

    [Fact]
    public async Task StructurallyValid_ArtifactPersistedToStore()
    {
        var llm = StubLlmClient.WithFixedResponse(ValidImplementation);
        var executor = CreateExecutor(llm);

        var result = await executor.ExecuteAsync(TestTask, MethodologyWithControl());

        Assert.NotNull(result.ArtifactHash);
        // Strip prefix for store lookup
        var rawHash = result.ArtifactHash!.Replace("sha256:", "");
        var stored = await _artifactStore.ReadAsync(rawHash);
        Assert.NotNull(stored);
        Assert.Equal("implementation", stored!.ArtifactType);
        Assert.Equal("control", stored.ProducedByPhase);
    }

    [Fact]
    public async Task InvalidJson_ReturnsStructurallyInvalid()
    {
        var llm = StubLlmClient.WithFixedResponse("not json at all");
        var executor = CreateExecutor(llm);

        var result = await executor.ExecuteAsync(TestTask, MethodologyWithControl());

        Assert.Equal("structurally_invalid", result.Outcome);
        Assert.Null(result.ArtifactHash);
        Assert.True(result.Tokens.In > 0 || result.Tokens.Out > 0);
    }

    [Fact]
    public async Task MissingBodyField_ReturnsStructurallyInvalid()
    {
        var llm = StubLlmClient.WithFixedResponse("""{"data": {}}""");
        var executor = CreateExecutor(llm);

        var result = await executor.ExecuteAsync(TestTask, MethodologyWithControl());

        Assert.Equal("structurally_invalid", result.Outcome);
        Assert.Null(result.ArtifactHash);
    }

    [Fact]
    public async Task StructuralValidationFails_ReturnsStructurallyInvalid()
    {
        // Valid JSON with body, but missing required fields for implementation/v1
        var llm = StubLlmClient.WithFixedResponse("""{"body": {"files": []}}""");
        var executor = CreateExecutor(llm);

        var result = await executor.ExecuteAsync(TestTask, MethodologyWithControl());

        Assert.Equal("structurally_invalid", result.Outcome);
        Assert.Null(result.ArtifactHash);
    }

    [Fact]
    public async Task LlmError_ReturnsFailed()
    {
        var llm = StubLlmClient.WithError(LlmErrorKind.Transient);
        var executor = CreateExecutor(llm);

        var result = await executor.ExecuteAsync(TestTask, MethodologyWithControl());

        Assert.Equal("failed", result.Outcome);
        Assert.Equal(0, result.Tokens.In);
        Assert.Equal(0, result.Tokens.Out);
        Assert.Null(result.ArtifactHash);
    }

    [Fact]
    public async Task CostIsCalculated()
    {
        // gpt-4o: $2.50/M input, $10/M output
        var llm = StubLlmClient.WithFixedResponse(ValidImplementation, inputTokens: 1_000_000, outputTokens: 500_000);
        var executor = CreateExecutor(llm);

        var result = await executor.ExecuteAsync(TestTask, MethodologyWithControl());

        Assert.NotNull(result.Cost);
        // Expected: (1M * 2.50 / 1M) + (500K * 10 / 1M) = $2.50 + $5.00 = $7.50
        Assert.Equal(7.50m, result.Cost);
    }

    [Fact]
    public async Task UsesControlModelOverride()
    {
        var methodology = new MethodologyDefinition
        {
            Id = "test-ctrl-v1",
            MaxAttemptsDefault = 2,
            Control = new ControlConfig
            {
                TargetSchema = "implementation/v1",
                Model = "gpt-4.1"
            },
            Phases =
            [
                new PhaseDefinition
                {
                    Id = "requirements",
                    Goal = "Extract requirements",
                    Inputs = [],
                    OutputSchema = "requirements/v1",
                    Instructions = "Produce requirements."
                }
            ]
        };

        var llm = StubLlmClient.WithFixedResponse(ValidImplementation);
        var executor = CreateExecutor(llm);

        await executor.ExecuteAsync(TestTask, methodology);

        Assert.Single(llm.ReceivedRequests);
        Assert.Equal("gpt-4.1", llm.ReceivedRequests[0].Model);
    }

    [Fact]
    public async Task FallsBackToMethodologyDefaultModel()
    {
        var methodology = new MethodologyDefinition
        {
            Id = "test-ctrl-v1",
            MaxAttemptsDefault = 2,
            ModelDefaults = new ModelDefaults { Generation = "o3-mini" },
            Control = new ControlConfig
            {
                TargetSchema = "implementation/v1"
            },
            Phases =
            [
                new PhaseDefinition
                {
                    Id = "requirements",
                    Goal = "Extract requirements",
                    Inputs = [],
                    OutputSchema = "requirements/v1",
                    Instructions = "Produce requirements."
                }
            ]
        };

        var llm = StubLlmClient.WithFixedResponse(ValidImplementation);
        var executor = CreateExecutor(llm);

        await executor.ExecuteAsync(TestTask, methodology);

        Assert.Single(llm.ReceivedRequests);
        Assert.Equal("o3-mini", llm.ReceivedRequests[0].Model);
    }

    [Fact]
    public async Task FallsBackToGpt4oWhenNoModelConfigured()
    {
        var llm = StubLlmClient.WithFixedResponse(ValidImplementation);
        var executor = CreateExecutor(llm);

        await executor.ExecuteAsync(TestTask, MethodologyWithControl());

        Assert.Single(llm.ReceivedRequests);
        Assert.Equal("gpt-4o", llm.ReceivedRequests[0].Model);
    }

    [Fact]
    public async Task PromptIncludesSchemaAndSemanticRules()
    {
        var llm = StubLlmClient.WithFixedResponse(ValidImplementation);
        var executor = CreateExecutor(llm);

        await executor.ExecuteAsync(TestTask, MethodologyWithControl());

        Assert.Single(llm.ReceivedRequests);
        var userMsg = llm.ReceivedRequests[0].UserMessage;

        // Verify key sections are present
        Assert.Contains("=== TASK ===", userMsg);
        Assert.Contains("=== OUTPUT CONTRACT ===", userMsg);
        Assert.Contains("implementation/v1", userMsg);
        Assert.Contains("=== INPUTS ===", userMsg);
        Assert.Contains("(none", userMsg);
        Assert.Contains("=== AMENDMENT NOTES ===", userMsg);
    }

    [Fact]
    public async Task UsesCorrectSystemMessage()
    {
        var llm = StubLlmClient.WithFixedResponse(ValidImplementation);
        var executor = CreateExecutor(llm);

        await executor.ExecuteAsync(TestTask, MethodologyWithControl());

        Assert.Single(llm.ReceivedRequests);
        // Uses the same system message as the pipeline
        Assert.Equal(PromptBuilder.SystemMessage, llm.ReceivedRequests[0].SystemMessage);
    }

    [Fact]
    public async Task DifferentTargetSchemas_Work()
    {
        var llm = StubLlmClient.WithFixedResponse(ValidRequirements);
        var executor = CreateExecutor(llm);

        var result = await executor.ExecuteAsync(TestTask, MethodologyWithControl("requirements/v1"));

        Assert.Equal("structurally_valid", result.Outcome);
        Assert.NotNull(result.ArtifactHash);
    }

    [Fact]
    public async Task NoControlConfig_Throws()
    {
        var methodology = new MethodologyDefinition
        {
            Id = "no-ctrl",
            MaxAttemptsDefault = 2,
            Phases =
            [
                new PhaseDefinition
                {
                    Id = "requirements",
                    Goal = "Extract requirements",
                    Inputs = [],
                    OutputSchema = "requirements/v1",
                    Instructions = "Produce requirements."
                }
            ]
        };

        var llm = StubLlmClient.WithFixedResponse(ValidImplementation);
        var executor = CreateExecutor(llm);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            executor.ExecuteAsync(TestTask, methodology));
    }

    [Fact]
    public void BuildControlUserMessage_IncludesSemanticRules()
    {
        var schemaEntry = _schemas.GetSchema("implementation/v1");
        var msg = ControlExecutor.BuildControlUserMessage(TestTask, schemaEntry);

        Assert.Contains(schemaEntry.SemanticRules, msg);
        Assert.Contains(schemaEntry.SchemaBodyJson, msg);
        Assert.Contains("phase_id: control", msg);
    }

    [Fact]
    public void ResolveControlModel_PrioritizesControlOverride()
    {
        var control = new ControlConfig { TargetSchema = "x/v1", Model = "custom-model" };
        var methodology = new MethodologyDefinition
        {
            Id = "test",
            MaxAttemptsDefault = 1,
            ModelDefaults = new ModelDefaults { Generation = "methodology-model" },
            Control = control,
            Phases = []
        };

        Assert.Equal("custom-model", ControlExecutor.ResolveControlModel(control, methodology));
    }

    [Fact]
    public void ResolveControlModel_FallsBackToMethodology()
    {
        var control = new ControlConfig { TargetSchema = "x/v1" };
        var methodology = new MethodologyDefinition
        {
            Id = "test",
            MaxAttemptsDefault = 1,
            ModelDefaults = new ModelDefaults { Generation = "methodology-model" },
            Control = control,
            Phases = []
        };

        Assert.Equal("methodology-model", ControlExecutor.ResolveControlModel(control, methodology));
    }

    [Fact]
    public void ResolveControlModel_FallsBackToGpt4o()
    {
        var control = new ControlConfig { TargetSchema = "x/v1" };
        var methodology = new MethodologyDefinition
        {
            Id = "test",
            MaxAttemptsDefault = 1,
            Control = control,
            Phases = []
        };

        Assert.Equal("gpt-4o", ControlExecutor.ResolveControlModel(control, methodology));
    }

    [Fact]
    public async Task Cancellation_PropagatedToLlm()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var llm = new StubLlmClient((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Should not reach here");
        });

        var executor = CreateExecutor(llm);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            executor.ExecuteAsync(TestTask, MethodologyWithControl(), cts.Token));
    }
}
