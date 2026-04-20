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

public sealed class PipelineRunnerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiskArtifactStore _artifactStore;
    private readonly DiskRunStore _runStore;
    private readonly SchemaRegistry _schemas;
    private readonly PromptBuilder _promptBuilder;

    public PipelineRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"forge-pipeline-test-{Guid.NewGuid():N}");
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

    private PipelineRunner CreateRunner(StubLlmClient llm)
    {
        var structural = new StructuralValidator(_schemas);
        var semantic = new SemanticValidator(llm, NullLogger<SemanticValidator>.Instance);
        var crossArtifact = new CrossArtifactValidator();
        var pipeline = new ValidatorPipeline(structural, semantic, crossArtifact, _schemas);

        var executor = new PhaseExecutor(
            llm, _promptBuilder, pipeline, _artifactStore, _runStore,
            TimeProvider.System, NullLogger<PhaseExecutor>.Instance);

        return new PipelineRunner(
            executor, _artifactStore, _runStore,
            TimeProvider.System, NullLogger<PipelineRunner>.Instance);
    }

    private static TaskBrief TestTask => new()
    {
        TaskId = "PIPE-TEST",
        Title = "Pipeline Test Task",
        Description = "Build a simple test widget."
    };

    private static MethodologyDefinition FullMethodology => new()
    {
        Id = "test-full-v1",
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
            },
            new PhaseDefinition
            {
                Id = "contract",
                Goal = "Define external interface",
                Inputs = ["requirements"],
                OutputSchema = "contract/v1",
                Instructions = "Produce contract."
            },
            new PhaseDefinition
            {
                Id = "function_design",
                Goal = "Decompose into components",
                Inputs = ["requirements", "contract"],
                OutputSchema = "function_design/v1",
                Instructions = "Produce function design."
            },
            new PhaseDefinition
            {
                Id = "implementation",
                Goal = "Produce code files",
                Inputs = ["contract", "function_design"],
                OutputSchema = "implementation/v1",
                Instructions = "Produce implementation."
            },
            new PhaseDefinition
            {
                Id = "review",
                Goal = "Adversarial review",
                Inputs = ["requirements", "contract", "function_design", "implementation"],
                OutputSchema = "review/v1",
                Instructions = "Produce review."
            }
        ]
    };

    /// <summary>
    /// Stub LLM that returns valid responses for each phase + semantic validator.
    /// Cross-artifact references are consistent: FR1 → code_search → C1 → CHK1.
    /// </summary>
    private static StubLlmClient CreatePassingLlm()
    {
        return new StubLlmClient((req, _) =>
        {
            string content;

            // Semantic validator calls — always pass
            if (req.SystemMessage.Contains("semantic validator"))
            {
                content = """{"findings": []}""";
            }
            else if (req.UserMessage.Contains("phase_id: requirements"))
            {
                content = ValidRequirements;
            }
            else if (req.UserMessage.Contains("phase_id: contract"))
            {
                content = ValidContract;
            }
            else if (req.UserMessage.Contains("phase_id: function_design"))
            {
                content = ValidFunctionDesign;
            }
            else if (req.UserMessage.Contains("phase_id: implementation"))
            {
                content = ValidImplementation;
            }
            else if (req.UserMessage.Contains("phase_id: review"))
            {
                content = ValidReview;
            }
            else
            {
                content = """{"body": {}}""";
            }

            return Task.FromResult(new LlmResponse
            {
                Content = content,
                InputTokens = 100,
                OutputTokens = 200,
                Model = "test-model",
                LatencyMs = 10
            });
        });
    }

    // --- Valid phase responses (cross-artifact-consistent) ---

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

    private const string ValidContract = """
    {
      "body": {
        "interfaces": [{
          "name": "widget_create",
          "kind": "function",
          "signature": "create(input: string): Widget",
          "description": "Creates a widget",
          "preconditions": ["input is non-empty"],
          "postconditions": ["Returns a Widget"],
          "errors": [{"condition": "empty input", "behavior": "throws InvalidArgumentError"}],
          "satisfies_fr_ids": ["FR1"]
        }],
        "data_shapes": [{"name": "Widget", "fields": [{"name": "id", "type": "string", "required": true}]}],
        "invariants": ["Widget ID is unique"],
        "examples": [{"scenario": "Create basic widget", "input": "test", "output": {"id": "w1"}, "fr_id": "FR1"}]
      }
    }
    """;

    private const string ValidFunctionDesign = """
    {
      "body": {
        "components": [{
          "id": "C1",
          "name": "WidgetFactory",
          "responsibility": "Creates widget instances",
          "depends_on": [],
          "implements": ["widget_create"]
        }],
        "data_flow": [{"from": "C1", "to": "C1", "carries": "Widget", "trigger": "create call"}],
        "error_handling": [{"component_id": "C1", "failure": "empty input", "response": "throw error"}],
        "deferred_decisions": []
      }
    }
    """;

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

    private const string ValidReview = """
    {
      "body": {
        "verdict": "pass",
        "summary": "Implementation satisfies all requirements",
        "checks": [{
          "id": "CHK1",
          "kind": "fr_satisfied",
          "target_id": "FR1",
          "result": "pass",
          "evidence": "src/widget.ts implements create() which accepts input"
        }],
        "defects": [],
        "improvements_for_next_iteration": []
      }
    }
    """;

    // --- Tests ---

    [Fact]
    public async Task HappyPath_AllPhasesSucceed_RunSucceeds()
    {
        var llm = CreatePassingLlm();
        var runner = CreateRunner(llm);

        var result = await runner.ExecuteAsync(TestTask, FullMethodology);

        Assert.Equal("succeeded", result.Outcome);
        Assert.Equal(5, result.FinalArtifactHashes.Count);
        Assert.True(result.FinalArtifactHashes.ContainsKey("requirements"));
        Assert.True(result.FinalArtifactHashes.ContainsKey("contract"));
        Assert.True(result.FinalArtifactHashes.ContainsKey("function_design"));
        Assert.True(result.FinalArtifactHashes.ContainsKey("implementation"));
        Assert.True(result.FinalArtifactHashes.ContainsKey("review"));
        Assert.NotNull(result.EndedAt);
        Assert.True(result.PipelineTokens.In > 0);
        Assert.True(result.PipelineTokens.Out > 0);
    }

    [Fact]
    public async Task PhaseFailure_StopsRun_ReturnsFailed()
    {
        // LLM that fails on the contract phase (returns invalid JSON)
        var callCount = 0;
        var llm = new StubLlmClient((req, _) =>
        {
            if (req.SystemMessage.Contains("semantic validator"))
                return Task.FromResult(new LlmResponse
                {
                    Content = """{"findings": []}""",
                    InputTokens = 50, OutputTokens = 20, Model = "test", LatencyMs = 5
                });

            if (req.UserMessage.Contains("phase_id: requirements"))
                return Task.FromResult(new LlmResponse
                {
                    Content = ValidRequirements,
                    InputTokens = 100, OutputTokens = 200, Model = "test", LatencyMs = 10
                });

            // Contract phase always returns structurally invalid JSON (missing required fields)
            callCount++;
            return Task.FromResult(new LlmResponse
            {
                Content = """{"body": {"interfaces": []}}""",
                InputTokens = 100, OutputTokens = 50, Model = "test", LatencyMs = 10
            });
        });

        var runner = CreateRunner(llm);
        var result = await runner.ExecuteAsync(TestTask, FullMethodology);

        Assert.Equal("failed", result.Outcome);
        // Requirements should have succeeded, contract should have failed
        Assert.Single(result.FinalArtifactHashes); // Only requirements
        Assert.True(result.FinalArtifactHashes.ContainsKey("requirements"));
        Assert.NotNull(result.EndedAt);
    }

    [Fact]
    public async Task Cancellation_ProducesAbortedRun()
    {
        using var cts = new CancellationTokenSource();

        // Cancel after requirements phase succeeds
        var llm = new StubLlmClient((req, ct) =>
        {
            if (req.SystemMessage.Contains("semantic validator"))
                return Task.FromResult(new LlmResponse
                {
                    Content = """{"findings": []}""",
                    InputTokens = 50, OutputTokens = 20, Model = "test", LatencyMs = 5
                });

            if (req.UserMessage.Contains("phase_id: requirements"))
                return Task.FromResult(new LlmResponse
                {
                    Content = ValidRequirements,
                    InputTokens = 100, OutputTokens = 200, Model = "test", LatencyMs = 10
                });

            // Cancel when we reach the contract phase
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Should not reach here");
        });

        var runner = CreateRunner(llm);
        var result = await runner.ExecuteAsync(TestTask, FullMethodology, cts.Token);

        Assert.Equal("aborted", result.Outcome);
        Assert.NotNull(result.EndedAt);
    }

    [Fact]
    public async Task SinglePhaseMethodology_Succeeds()
    {
        var methodology = new MethodologyDefinition
        {
            Id = "single-v1",
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

        var llm = CreatePassingLlm();
        var runner = CreateRunner(llm);

        var result = await runner.ExecuteAsync(TestTask, methodology);

        Assert.Equal("succeeded", result.Outcome);
        Assert.Single(result.FinalArtifactHashes);
        Assert.True(result.FinalArtifactHashes.ContainsKey("requirements"));
    }

    [Fact]
    public async Task RunPersistsStateToRunStore()
    {
        var llm = CreatePassingLlm();
        var runner = CreateRunner(llm);

        var result = await runner.ExecuteAsync(TestTask, FullMethodology);

        // Verify run.json was written
        var storedRun = await _runStore.ReadRunAsync(result.RunId);
        Assert.NotNull(storedRun);
        Assert.Equal("succeeded", storedRun.Outcome);

        // Verify phase-runs.json rollup was written
        var phaseRuns = await _runStore.ReadPhaseRunsRollupAsync(result.RunId);
        Assert.NotNull(phaseRuns);
        Assert.Equal(5, phaseRuns.Count);
    }

    [Fact]
    public async Task TokenTotals_AccumulateAcrossPhases()
    {
        var llm = CreatePassingLlm();
        var runner = CreateRunner(llm);

        var result = await runner.ExecuteAsync(TestTask, FullMethodology);

        // Each phase: 100 in + 200 out for generation, ~50+20 for semantic.
        // 5 phases × (100+50) in = 750 minimum, 5 × (200+20) out = 1100 minimum.
        Assert.True(result.PipelineTokens.In >= 500, $"Expected >=500 in tokens, got {result.PipelineTokens.In}");
        Assert.True(result.PipelineTokens.Out >= 500, $"Expected >=500 out tokens, got {result.PipelineTokens.Out}");
    }

    [Fact]
    public async Task ArtifactHashes_ArePrefixedWithSha256()
    {
        var llm = CreatePassingLlm();
        var runner = CreateRunner(llm);

        var result = await runner.ExecuteAsync(TestTask, FullMethodology);

        foreach (var (_, hash) in result.FinalArtifactHashes)
        {
            Assert.StartsWith("sha256:", hash);
        }
    }
}
