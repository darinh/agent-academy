using System.Text.Json;
using AgentAcademy.Forge.Artifacts;
using AgentAcademy.Forge.Costs;
using AgentAcademy.Forge.Execution;
using AgentAcademy.Forge.Llm;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Schemas;
using AgentAcademy.Forge.Validation;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Forge.Tests;

public sealed class SourceIntentGeneratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiskArtifactStore _artifactStore;
    private readonly SchemaRegistry _schemas;

    public SourceIntentGeneratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"forge-si-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _artifactStore = new DiskArtifactStore(
            Path.Combine(_tempDir, "artifacts"),
            NullLogger<DiskArtifactStore>.Instance);
        _schemas = new SchemaRegistry();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private SourceIntentGenerator CreateGenerator(StubLlmClient llm)
    {
        var structural = new StructuralValidator(_schemas);
        return new SourceIntentGenerator(
            llm, _schemas, structural, _artifactStore,
            new CostCalculator(), TimeProvider.System,
            NullLogger<SourceIntentGenerator>.Instance);
    }

    private static TaskBrief TestTask => new()
    {
        TaskId = "TEST-SI",
        Title = "Test Task",
        Description = "Build a rate limiter with per-user tracking at 100 requests per hour."
    };

    private static MethodologyDefinition TestMethodology => new()
    {
        Id = "test-v1",
        Phases = [new PhaseDefinition
        {
            Id = "requirements",
            Goal = "Extract requirements",
            Inputs = [],
            OutputSchema = "requirements/v1",
            Instructions = "Produce requirements."
        }],
        Fidelity = new FidelityConfig { TargetPhase = "requirements" }
    };

    private static string ValidSourceIntentResponse(string taskBrief) => $$"""
    {
      "body": {
        "task_brief": "{{taskBrief.Replace("\"", "\\\"")}}",
        "acceptance_criteria": [
          {"id": "AC1", "criterion": "Rate limiter limits to 100 requests per hour", "verifiable": true},
          {"id": "AC2", "criterion": "Rate limiting is per-user", "verifiable": true}
        ],
        "explicit_constraints": [
          {"id": "EC1", "constraint": "100 requests per hour per user"}
        ],
        "examples": [],
        "counter_examples": [],
        "preferred_approach": null
      }
    }
    """;

    [Fact]
    public async Task GenerateAsync_AcceptsValidSourceIntent()
    {
        var task = TestTask;
        var llm = new StubLlmClient((req, _) =>
            Task.FromResult(new LlmResponse
            {
                Content = ValidSourceIntentResponse(task.Description),
                InputTokens = 100, OutputTokens = 200, Model = "test-model", LatencyMs = 50
            }));

        var generator = CreateGenerator(llm);
        var result = await generator.GenerateAsync(task, TestMethodology);

        Assert.Equal("accepted", result.Outcome);
        Assert.NotNull(result.ArtifactHash);
        Assert.StartsWith("sha256:", result.ArtifactHash);
        Assert.NotNull(result.Envelope);
        Assert.Equal("source_intent", result.Envelope.ArtifactType);
        Assert.Equal("1", result.Envelope.SchemaVersion);
        Assert.True(result.Tokens.In > 0);
        Assert.True(result.Cost > 0);
    }

    [Fact]
    public async Task GenerateAsync_FailsGracefullyOnLlmError()
    {
        var llm = StubLlmClient.WithError(LlmErrorKind.Transient, "Rate limited");
        var generator = CreateGenerator(llm);

        var result = await generator.GenerateAsync(TestTask, TestMethodology);

        Assert.Equal("failed", result.Outcome);
        Assert.Null(result.ArtifactHash);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task GenerateAsync_RetriesOnStructuralFailure()
    {
        var task = TestTask;
        var attemptCount = 0;
        var llm = new StubLlmClient((req, _) =>
        {
            attemptCount++;
            // First attempt returns invalid JSON, second returns valid
            var content = attemptCount == 1
                ? """{ "body": { "task_brief": "wrong" } }"""
                : ValidSourceIntentResponse(task.Description);
            return Task.FromResult(new LlmResponse
            {
                Content = content,
                InputTokens = 100, OutputTokens = 200, Model = "test-model", LatencyMs = 50
            });
        });

        var generator = CreateGenerator(llm);
        var result = await generator.GenerateAsync(task, TestMethodology, maxAttempts: 3);

        Assert.Equal("accepted", result.Outcome);
        Assert.Equal(2, attemptCount);
    }

    [Fact]
    public async Task GenerateAsync_FailsAfterMaxAttempts()
    {
        var llm = StubLlmClient.WithFixedResponse("""{ "body": {} }""");
        var generator = CreateGenerator(llm);

        var result = await generator.GenerateAsync(TestTask, TestMethodology, maxAttempts: 2);

        Assert.Equal("failed", result.Outcome);
        Assert.Null(result.ArtifactHash);
    }

    [Fact]
    public void ValidateTaskBriefVerbatim_PassesWhenVerbatim()
    {
        var task = TestTask;
        var envelope = new ArtifactEnvelope
        {
            ArtifactType = "source_intent",
            SchemaVersion = "1",
            ProducedByPhase = "source_intent",
            Payload = JsonDocument.Parse($$"""
            {
                "task_brief": "{{task.Description.Replace("\"", "\\\"")}}"
            }
            """).RootElement
        };

        var result = SourceIntentGenerator.ValidateTaskBriefVerbatim(envelope, task);
        Assert.Null(result); // No error
    }

    [Fact]
    public void ValidateTaskBriefVerbatim_FailsWhenParaphrased()
    {
        var task = TestTask;
        var envelope = new ArtifactEnvelope
        {
            ArtifactType = "source_intent",
            SchemaVersion = "1",
            ProducedByPhase = "source_intent",
            Payload = JsonDocument.Parse("""
            {
                "task_brief": "Create a system that limits API calls."
            }
            """).RootElement
        };

        var result = SourceIntentGenerator.ValidateTaskBriefVerbatim(envelope, task);
        Assert.NotNull(result);
        Assert.Equal("TASK_BRIEF_NOT_VERBATIM", result.Code);
        Assert.True(result.Blocking);
    }

    [Fact]
    public void BuildUserMessage_IncludesTaskTitleAndDescription()
    {
        var task = TestTask;
        var schemaEntry = _schemas.GetSchema("source_intent/v1");

        var message = SourceIntentGenerator.BuildUserMessage(task, schemaEntry);

        Assert.Contains("Title: Test Task", message);
        Assert.Contains(task.Description, message);
        Assert.Contains("source_intent/v1", message);
        Assert.Contains("CRITICAL INSTRUCTION", message);
    }

    [Fact]
    public void BuildUserMessage_IncludesAmendmentNotes_WhenPresent()
    {
        var task = TestTask;
        var schemaEntry = _schemas.GetSchema("source_intent/v1");
        var failures = new List<ValidatorResultTrace>
        {
            new()
            {
                Phase = "structural", Code = "MISSING_FIELD",
                Severity = "error", Blocking = true, AttemptNumber = 1,
                BlockingReason = "task_brief is missing"
            }
        };

        var message = SourceIntentGenerator.BuildUserMessage(task, schemaEntry, failures);

        Assert.Contains("AMENDMENT NOTES", message);
        Assert.Contains("MISSING_FIELD", message);
    }

    [Fact]
    public void ResolveModel_UsesFidelityModel_WhenSet()
    {
        var methodology = TestMethodology with
        {
            Fidelity = new FidelityConfig { TargetPhase = "requirements", Model = "custom-model" }
        };

        Assert.Equal("custom-model", SourceIntentGenerator.ResolveModel(methodology));
    }

    [Fact]
    public void ResolveModel_FallsBackToModelDefaults()
    {
        var methodology = TestMethodology with
        {
            ModelDefaults = new ModelDefaults { Generation = "gpt-4o-mini" },
            Fidelity = new FidelityConfig { TargetPhase = "requirements" }
        };

        Assert.Equal("gpt-4o-mini", SourceIntentGenerator.ResolveModel(methodology));
    }

    [Fact]
    public void ResolveModel_FallsBackToGpt4o()
    {
        Assert.Equal("gpt-4o", SourceIntentGenerator.ResolveModel(TestMethodology));
    }
}
