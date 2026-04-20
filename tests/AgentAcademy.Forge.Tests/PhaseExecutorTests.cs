using System.Text.Json;
using AgentAcademy.Forge.Artifacts;
using AgentAcademy.Forge.Execution;
using AgentAcademy.Forge.Llm;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Prompt;
using AgentAcademy.Forge.Schemas;
using AgentAcademy.Forge.Storage;
using AgentAcademy.Forge.Validation;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Forge.Tests;

public sealed class PhaseExecutorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiskArtifactStore _artifactStore;
    private readonly DiskRunStore _runStore;
    private readonly SchemaRegistry _schemas;
    private readonly PromptBuilder _promptBuilder;

    public PhaseExecutorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"forge-executor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _artifactStore = new DiskArtifactStore(
            Path.Combine(_tempDir, "artifacts"),
            NullLogger<DiskArtifactStore>.Instance);
        _runStore = new DiskRunStore(
            _tempDir,
            NullLogger<DiskRunStore>.Instance);
        _schemas = new SchemaRegistry();
        _promptBuilder = new PromptBuilder(_schemas);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private PhaseExecutor CreateExecutor(StubLlmClient llm, TimeProvider? timeProvider = null)
    {
        var structural = new StructuralValidator(_schemas);
        var semantic = new SemanticValidator(llm, NullLogger<SemanticValidator>.Instance);
        var crossArtifact = new CrossArtifactValidator();
        var pipeline = new ValidatorPipeline(structural, semantic, crossArtifact, _schemas);

        return new PhaseExecutor(
            llm,
            _promptBuilder,
            pipeline,
            _artifactStore,
            _runStore,
            timeProvider ?? TimeProvider.System,
            NullLogger<PhaseExecutor>.Instance);
    }

    private static MethodologyDefinition TestMethodology => new()
    {
        Id = "test-v1",
        MaxAttemptsDefault = 3,
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

    private static TaskBrief TestTask => new()
    {
        TaskId = "TEST-1",
        Title = "Test Task",
        Description = "A test task for unit testing."
    };

    private async Task InitializeRun(string runId)
    {
        var run = new RunTrace
        {
            RunId = runId,
            TaskId = "TEST-1",
            MethodologyVersion = "1",
            StartedAt = DateTime.UtcNow,
            Outcome = "Running",
            PipelineTokens = new TokenCount(),
            ControlTokens = new TokenCount(),
            FinalArtifactHashes = new Dictionary<string, string>()
        };
        await _runStore.InitializeRunAsync(runId, run, TestTask, TestMethodology);
    }

    private static string ValidRequirementsResponse => """
    {
      "body": {
        "task_summary": "Build a test system",
        "user_outcomes": [{"id": "U1", "outcome": "System works", "priority": "must"}],
        "functional_requirements": [{"id": "FR1", "statement": "System accepts input", "outcome_ids": ["U1"]}],
        "non_functional_requirements": [],
        "out_of_scope": ["Deployment"],
        "open_questions": []
      }
    }
    """;

    [Fact]
    public async Task AcceptedOnFirstAttempt_Succeeds()
    {
        var llm = new StubLlmClient((req, _) =>
        {
            // Return valid requirements for generation, pass findings for semantic
            if (req.SystemMessage.Contains("phase executor"))
                return Task.FromResult(new LlmResponse
                {
                    Content = ValidRequirementsResponse,
                    InputTokens = 100, OutputTokens = 200, Model = "test-model", LatencyMs = 50
                });

            // Semantic validator call
            return Task.FromResult(new LlmResponse
            {
                Content = """{"findings": []}""",
                InputTokens = 50, OutputTokens = 20, Model = "test-model", LatencyMs = 10
            });
        });

        var runId = ForgeId.NewRunId();
        await InitializeRun(runId);

        var executor = CreateExecutor(llm);
        var phase = TestMethodology.Phases[0];

        var result = await executor.ExecuteAsync(
            runId, 0, phase, TestMethodology, TestTask,
            new Dictionary<string, ArtifactEnvelope>());

        Assert.Equal(PhaseRunStatus.Succeeded, result.Status);
        Assert.NotNull(result.AcceptedArtifactHash);
        Assert.Single(result.PhaseRunTrace.Attempts);
        Assert.Equal("accepted", result.PhaseRunTrace.Attempts[0].Status);

        // Verify artifact was stored
        var exists = await _artifactStore.ExistsAsync(result.AcceptedArtifactHash);
        Assert.True(exists);
    }

    [Fact]
    public async Task RejectedThenAccepted_UsesAmendmentNotes()
    {
        var callCount = 0;
        var llm = new StubLlmClient((req, _) =>
        {
            callCount++;

            if (req.SystemMessage.Contains("phase executor"))
            {
                // First generation: invalid (missing fields)
                // Second generation: valid
                var content = callCount <= 1
                    ? """{"body": {"task_summary": "test"}}"""
                    : ValidRequirementsResponse;

                return Task.FromResult(new LlmResponse
                {
                    Content = content,
                    InputTokens = 100, OutputTokens = 200, Model = "test-model", LatencyMs = 50
                });
            }

            // Semantic validator
            return Task.FromResult(new LlmResponse
            {
                Content = """{"findings": []}""",
                InputTokens = 50, OutputTokens = 20, Model = "test-model", LatencyMs = 10
            });
        });

        var runId = ForgeId.NewRunId();
        await InitializeRun(runId);

        var executor = CreateExecutor(llm);
        var phase = TestMethodology.Phases[0];

        var result = await executor.ExecuteAsync(
            runId, 0, phase, TestMethodology, TestTask,
            new Dictionary<string, ArtifactEnvelope>());

        Assert.Equal(PhaseRunStatus.Succeeded, result.Status);
        Assert.Equal(2, result.PhaseRunTrace.Attempts.Count);
        Assert.Equal("rejected", result.PhaseRunTrace.Attempts[0].Status);
        Assert.Equal("accepted", result.PhaseRunTrace.Attempts[1].Status);
    }

    [Fact]
    public async Task AllAttemptsExhausted_Fails()
    {
        // Always return structurally invalid response
        var llm = new StubLlmClient((req, _) =>
        {
            if (req.SystemMessage.Contains("phase executor"))
                return Task.FromResult(new LlmResponse
                {
                    Content = """{"body": {"task_summary": "test"}}""",
                    InputTokens = 100, OutputTokens = 200, Model = "test-model", LatencyMs = 50
                });

            return Task.FromResult(new LlmResponse
            {
                Content = """{"findings": []}""",
                InputTokens = 50, OutputTokens = 20, Model = "test-model", LatencyMs = 10
            });
        });

        var runId = ForgeId.NewRunId();
        await InitializeRun(runId);

        var executor = CreateExecutor(llm);
        var methodology = TestMethodology with { MaxAttemptsDefault = 2 };
        var phase = methodology.Phases[0];

        var result = await executor.ExecuteAsync(
            runId, 0, phase, methodology, TestTask,
            new Dictionary<string, ArtifactEnvelope>());

        Assert.Equal(PhaseRunStatus.Failed, result.Status);
        Assert.Null(result.AcceptedArtifactHash);
        Assert.Equal(2, result.PhaseRunTrace.Attempts.Count);
        Assert.All(result.PhaseRunTrace.Attempts, a =>
            Assert.True(a.Status is "rejected" or "errored"));
    }

    [Fact]
    public async Task LlmException_CountsAsErrored()
    {
        var callCount = 0;
        var llm = new StubLlmClient((req, _) =>
        {
            callCount++;
            if (req.SystemMessage.Contains("phase executor"))
            {
                if (callCount <= 1)
                    throw new LlmClientException(LlmErrorKind.Transient, "Rate limited");

                return Task.FromResult(new LlmResponse
                {
                    Content = ValidRequirementsResponse,
                    InputTokens = 100, OutputTokens = 200, Model = "test-model", LatencyMs = 50
                });
            }

            return Task.FromResult(new LlmResponse
            {
                Content = """{"findings": []}""",
                InputTokens = 50, OutputTokens = 20, Model = "test-model", LatencyMs = 10
            });
        });

        var runId = ForgeId.NewRunId();
        await InitializeRun(runId);

        var executor = CreateExecutor(llm);
        var phase = TestMethodology.Phases[0];

        var result = await executor.ExecuteAsync(
            runId, 0, phase, TestMethodology, TestTask,
            new Dictionary<string, ArtifactEnvelope>());

        Assert.Equal(PhaseRunStatus.Succeeded, result.Status);
        Assert.Equal(2, result.PhaseRunTrace.Attempts.Count);
        Assert.Equal("errored", result.PhaseRunTrace.Attempts[0].Status);
        Assert.Equal("accepted", result.PhaseRunTrace.Attempts[1].Status);
    }

    [Fact]
    public async Task ParseFailure_MarkedAsErrored()
    {
        var callCount = 0;
        var llm = new StubLlmClient((req, _) =>
        {
            callCount++;
            if (req.SystemMessage.Contains("phase executor"))
            {
                if (callCount <= 1)
                    return Task.FromResult(new LlmResponse
                    {
                        Content = "This is not JSON at all",
                        InputTokens = 100, OutputTokens = 50, Model = "test-model", LatencyMs = 50
                    });

                return Task.FromResult(new LlmResponse
                {
                    Content = ValidRequirementsResponse,
                    InputTokens = 100, OutputTokens = 200, Model = "test-model", LatencyMs = 50
                });
            }

            return Task.FromResult(new LlmResponse
            {
                Content = """{"findings": []}""",
                InputTokens = 50, OutputTokens = 20, Model = "test-model", LatencyMs = 10
            });
        });

        var runId = ForgeId.NewRunId();
        await InitializeRun(runId);

        var executor = CreateExecutor(llm);
        var phase = TestMethodology.Phases[0];

        var result = await executor.ExecuteAsync(
            runId, 0, phase, TestMethodology, TestTask,
            new Dictionary<string, ArtifactEnvelope>());

        Assert.Equal(PhaseRunStatus.Succeeded, result.Status);
        Assert.Equal(2, result.PhaseRunTrace.Attempts.Count);
        Assert.Equal("errored", result.PhaseRunTrace.Attempts[0].Status);
        Assert.Null(result.PhaseRunTrace.Attempts[0].ArtifactHash);
    }

    [Fact]
    public async Task StateTransitions_RecordedCorrectly()
    {
        var llm = new StubLlmClient((req, _) =>
        {
            if (req.SystemMessage.Contains("phase executor"))
                return Task.FromResult(new LlmResponse
                {
                    Content = ValidRequirementsResponse,
                    InputTokens = 100, OutputTokens = 200, Model = "test-model", LatencyMs = 50
                });

            return Task.FromResult(new LlmResponse
            {
                Content = """{"findings": []}""",
                InputTokens = 50, OutputTokens = 20, Model = "test-model", LatencyMs = 10
            });
        });

        var runId = ForgeId.NewRunId();
        await InitializeRun(runId);

        var executor = CreateExecutor(llm);
        var phase = TestMethodology.Phases[0];

        var result = await executor.ExecuteAsync(
            runId, 0, phase, TestMethodology, TestTask,
            new Dictionary<string, ArtifactEnvelope>());

        var transitions = result.PhaseRunTrace.StateTransitions;
        Assert.Equal(3, transitions.Count);
        Assert.Null(transitions[0].From);
        Assert.Equal("pending", transitions[0].To);
        Assert.Equal("pending", transitions[1].From);
        Assert.Equal("running", transitions[1].To);
        Assert.Equal("running", transitions[2].From);
        Assert.Equal("succeeded", transitions[2].To);
    }

    [Fact]
    public async Task RejectedAttempt_StillPersistsArtifactHash()
    {
        // Return body that parses but fails structural validation
        var llm = new StubLlmClient((req, _) =>
        {
            if (req.SystemMessage.Contains("phase executor"))
                return Task.FromResult(new LlmResponse
                {
                    Content = """{"body": {"task_summary": "test"}}""",
                    InputTokens = 100, OutputTokens = 50, Model = "test-model", LatencyMs = 50
                });

            return Task.FromResult(new LlmResponse
            {
                Content = """{"findings": []}""",
                InputTokens = 50, OutputTokens = 20, Model = "test-model", LatencyMs = 10
            });
        });

        var runId = ForgeId.NewRunId();
        await InitializeRun(runId);

        var executor = CreateExecutor(llm);
        var methodology = TestMethodology with { MaxAttemptsDefault = 1 };
        var phase = methodology.Phases[0];

        var result = await executor.ExecuteAsync(
            runId, 0, phase, methodology, TestTask,
            new Dictionary<string, ArtifactEnvelope>());

        Assert.Equal(PhaseRunStatus.Failed, result.Status);
        // Even rejected attempts should have artifact hash if parsing succeeded
        var firstAttempt = result.PhaseRunTrace.Attempts[0];
        Assert.NotNull(firstAttempt.ArtifactHash);
    }

    [Fact]
    public async Task PerPhaseMaxAttempts_OverridesDefault()
    {
        var llm = new StubLlmClient((req, _) =>
        {
            if (req.SystemMessage.Contains("phase executor"))
                return Task.FromResult(new LlmResponse
                {
                    Content = """{"body": {"task_summary": "test"}}""",
                    InputTokens = 100, OutputTokens = 50, Model = "test-model", LatencyMs = 50
                });

            return Task.FromResult(new LlmResponse
            {
                Content = """{"findings": []}""",
                InputTokens = 50, OutputTokens = 20, Model = "test-model", LatencyMs = 10
            });
        });

        var runId = ForgeId.NewRunId();
        await InitializeRun(runId);

        var executor = CreateExecutor(llm);
        var phase = new PhaseDefinition
        {
            Id = "requirements",
            Goal = "Extract requirements",
            Inputs = [],
            OutputSchema = "requirements/v1",
            Instructions = "Produce requirements.",
            MaxAttempts = 1 // Override: only 1 attempt
        };

        var result = await executor.ExecuteAsync(
            runId, 0, phase, TestMethodology, TestTask,
            new Dictionary<string, ArtifactEnvelope>());

        Assert.Equal(PhaseRunStatus.Failed, result.Status);
        Assert.Single(result.PhaseRunTrace.Attempts);
    }

    [Fact]
    public async Task PhaseRunScratch_PersistedToDisk()
    {
        var llm = new StubLlmClient((req, _) =>
        {
            if (req.SystemMessage.Contains("phase executor"))
                return Task.FromResult(new LlmResponse
                {
                    Content = ValidRequirementsResponse,
                    InputTokens = 100, OutputTokens = 200, Model = "test-model", LatencyMs = 50
                });

            return Task.FromResult(new LlmResponse
            {
                Content = """{"findings": []}""",
                InputTokens = 50, OutputTokens = 20, Model = "test-model", LatencyMs = 10
            });
        });

        var runId = ForgeId.NewRunId();
        await InitializeRun(runId);

        var executor = CreateExecutor(llm);
        var phase = TestMethodology.Phases[0];

        await executor.ExecuteAsync(
            runId, 0, phase, TestMethodology, TestTask,
            new Dictionary<string, ArtifactEnvelope>());

        // Verify scratch file was written
        var scratch = await _runStore.ReadPhaseRunScratchAsync(runId, 0, "requirements");
        Assert.NotNull(scratch);
        Assert.Equal("requirements", scratch.PhaseId);
        Assert.NotEmpty(scratch.Attempts);
    }

    [Fact]
    public async Task TokenCounts_CapturedInAttemptTrace()
    {
        var llm = new StubLlmClient((req, _) =>
        {
            if (req.SystemMessage.Contains("phase executor"))
                return Task.FromResult(new LlmResponse
                {
                    Content = ValidRequirementsResponse,
                    InputTokens = 500, OutputTokens = 1200, Model = "gpt-4o", LatencyMs = 2500
                });

            return Task.FromResult(new LlmResponse
            {
                Content = """{"findings": []}""",
                InputTokens = 50, OutputTokens = 20, Model = "test-model", LatencyMs = 10
            });
        });

        var runId = ForgeId.NewRunId();
        await InitializeRun(runId);

        var executor = CreateExecutor(llm);
        var phase = TestMethodology.Phases[0];

        var result = await executor.ExecuteAsync(
            runId, 0, phase, TestMethodology, TestTask,
            new Dictionary<string, ArtifactEnvelope>());

        var attempt = result.PhaseRunTrace.Attempts[0];
        Assert.Equal(500, attempt.Tokens.In);
        Assert.Equal(1200, attempt.Tokens.Out);
        Assert.Equal(2500, attempt.LatencyMs);
        Assert.Equal("gpt-4o", attempt.Model);
    }

    [Fact]
    public async Task ZeroMaxAttempts_ThrowsArgumentException()
    {
        var llm = new StubLlmClient();
        var runId = ForgeId.NewRunId();
        await InitializeRun(runId);

        var executor = CreateExecutor(llm);
        var methodology = new MethodologyDefinition
        {
            Id = "test-v1",
            MaxAttemptsDefault = 0,
            Phases = TestMethodology.Phases
        };
        var phase = methodology.Phases[0];

        await Assert.ThrowsAsync<ArgumentException>(
            () => executor.ExecuteAsync(
                runId, 0, phase, methodology, TestTask,
                new Dictionary<string, ArtifactEnvelope>()));
    }

    [Theory]
    [InlineData("o3", "gpt-4o", "gpt-4o", "o3")]               // Phase override wins
    [InlineData(null, "o3", "gpt-4o", "o3")]                    // Methodology default used
    [InlineData(null, null, "gpt-4o", "gpt-4o")]                // Hardcoded fallback
    [InlineData("", "o3", "gpt-4o", "o3")]                      // Empty string treated as unset
    [InlineData("  ", "o3", "gpt-4o", "o3")]                    // Whitespace treated as unset
    [InlineData(null, "", "gpt-4o", "gpt-4o")]                  // Empty methodology default → fallback
    [InlineData(null, "  ", "gpt-4o", "gpt-4o")]                // Whitespace methodology default → fallback
    public void ResolveModel_PrecedenceIsCorrect(
        string? phaseOverride, string? methodologyDefault, string fallback, string expected)
    {
        var result = PhaseExecutor.ResolveModel(phaseOverride, methodologyDefault, fallback);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ExecuteAsync_UsesPhaseModelOverride_ForGeneration()
    {
        var llm = new StubLlmClient((req, _) =>
        {
            if (req.SystemMessage.Contains("phase executor"))
                return Task.FromResult(new LlmResponse
                {
                    Content = ValidRequirementsResponse,
                    InputTokens = 100, OutputTokens = 200, Model = req.Model, LatencyMs = 50
                });

            return Task.FromResult(new LlmResponse
            {
                Content = """{"findings": []}""",
                InputTokens = 50, OutputTokens = 20, Model = req.Model, LatencyMs = 10
            });
        });

        var runId = ForgeId.NewRunId();
        await InitializeRun(runId);

        var executor = CreateExecutor(llm);
        var methodology = new MethodologyDefinition
        {
            Id = "test-v1",
            MaxAttemptsDefault = 3,
            ModelDefaults = new ModelDefaults { Generation = "default-gen", Judge = "default-judge" },
            Phases =
            [
                new PhaseDefinition
                {
                    Id = "requirements",
                    Goal = "Extract requirements",
                    Inputs = [],
                    OutputSchema = "requirements/v1",
                    Instructions = "Produce requirements.",
                    Model = "phase-gen-override"
                }
            ]
        };
        var phase = methodology.Phases[0];

        await executor.ExecuteAsync(
            runId, 0, phase, methodology, TestTask,
            new Dictionary<string, ArtifactEnvelope>());

        // First request is generation; verify it used the phase override model
        var generationRequest = llm.ReceivedRequests.First(r => r.SystemMessage.Contains("phase executor"));
        Assert.Equal("phase-gen-override", generationRequest.Model);
    }

    [Fact]
    public async Task ExecuteAsync_UsesMethodologyDefaults_WhenPhaseHasNoOverride()
    {
        var llm = new StubLlmClient((req, _) =>
        {
            if (req.SystemMessage.Contains("phase executor"))
                return Task.FromResult(new LlmResponse
                {
                    Content = ValidRequirementsResponse,
                    InputTokens = 100, OutputTokens = 200, Model = req.Model, LatencyMs = 50
                });

            return Task.FromResult(new LlmResponse
            {
                Content = """{"findings": []}""",
                InputTokens = 50, OutputTokens = 20, Model = req.Model, LatencyMs = 10
            });
        });

        var runId = ForgeId.NewRunId();
        await InitializeRun(runId);

        var executor = CreateExecutor(llm);
        var methodology = new MethodologyDefinition
        {
            Id = "test-v1",
            MaxAttemptsDefault = 3,
            ModelDefaults = new ModelDefaults { Generation = "methodology-gen", Judge = "methodology-judge" },
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
        var phase = methodology.Phases[0];

        await executor.ExecuteAsync(
            runId, 0, phase, methodology, TestTask,
            new Dictionary<string, ArtifactEnvelope>());

        var generationRequest = llm.ReceivedRequests.First(r => r.SystemMessage.Contains("phase executor"));
        Assert.Equal("methodology-gen", generationRequest.Model);

        // Semantic validator should use the methodology judge model
        var judgeRequest = llm.ReceivedRequests.FirstOrDefault(r => r.SystemMessage.Contains("semantic validator"));
        if (judgeRequest is not null)
            Assert.Equal("methodology-judge", judgeRequest.Model);
    }

    [Fact]
    public async Task ExecuteAsync_FallsBackToHardcoded_WhenNoModelConfig()
    {
        var llm = new StubLlmClient((req, _) =>
        {
            if (req.SystemMessage.Contains("phase executor"))
                return Task.FromResult(new LlmResponse
                {
                    Content = ValidRequirementsResponse,
                    InputTokens = 100, OutputTokens = 200, Model = req.Model, LatencyMs = 50
                });

            return Task.FromResult(new LlmResponse
            {
                Content = """{"findings": []}""",
                InputTokens = 50, OutputTokens = 20, Model = req.Model, LatencyMs = 10
            });
        });

        var runId = ForgeId.NewRunId();
        await InitializeRun(runId);

        var executor = CreateExecutor(llm);
        // No ModelDefaults, no phase overrides
        var phase = TestMethodology.Phases[0];

        await executor.ExecuteAsync(
            runId, 0, phase, TestMethodology, TestTask,
            new Dictionary<string, ArtifactEnvelope>());

        var generationRequest = llm.ReceivedRequests.First(r => r.SystemMessage.Contains("phase executor"));
        Assert.Equal("gpt-4o", generationRequest.Model);
    }
}
