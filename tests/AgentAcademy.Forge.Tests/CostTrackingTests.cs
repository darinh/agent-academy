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

public sealed class CostTrackingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiskArtifactStore _artifactStore;
    private readonly DiskRunStore _runStore;
    private readonly SchemaRegistry _schemas;
    private readonly PromptBuilder _promptBuilder;

    public CostTrackingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"forge-cost-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _artifactStore = new DiskArtifactStore(
            Path.Combine(_tempDir, "artifacts"),
            NullLogger<DiskArtifactStore>.Instance);
        _runStore = new DiskRunStore(_tempDir, NullLogger<DiskRunStore>.Instance);
        _schemas = new SchemaRegistry();
        _promptBuilder = new PromptBuilder(_schemas);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // --- CostCalculator unit tests ---

    [Fact]
    public void Calculate_KnownModel_ReturnsCorrectCost()
    {
        var calc = new CostCalculator();
        // gpt-4o: $2.50/M input, $10.00/M output
        var cost = calc.Calculate("gpt-4o", 1_000_000, 1_000_000);
        Assert.Equal(12.50m, cost);
    }

    [Fact]
    public void Calculate_KnownModel_SmallTokenCounts()
    {
        var calc = new CostCalculator();
        // gpt-4o: $2.50/M input, $10.00/M output
        // 100 input tokens = 100 * 2.50 / 1_000_000 = 0.000250
        // 200 output tokens = 200 * 10.00 / 1_000_000 = 0.002000
        var cost = calc.Calculate("gpt-4o", 100, 200);
        Assert.Equal(0.000250m + 0.002000m, cost);
    }

    [Fact]
    public void Calculate_UnknownModel_ReturnsZero()
    {
        var calc = new CostCalculator();
        var cost = calc.Calculate("unknown-model-xyz", 1000, 2000);
        Assert.Equal(0m, cost);
    }

    [Fact]
    public void Calculate_ZeroTokens_ReturnsZero()
    {
        var calc = new CostCalculator();
        var cost = calc.Calculate("gpt-4o", 0, 0);
        Assert.Equal(0m, cost);
    }

    [Fact]
    public void Calculate_CaseInsensitive()
    {
        var calc = new CostCalculator();
        var lower = calc.Calculate("gpt-4o", 1000, 1000);
        var upper = calc.Calculate("GPT-4O", 1000, 1000);
        Assert.Equal(lower, upper);
        Assert.True(lower > 0);
    }

    [Fact]
    public void Calculate_TokenCountOverload()
    {
        var calc = new CostCalculator();
        var tokens = new TokenCount { In = 500, Out = 300 };
        var cost = calc.Calculate("gpt-4o", tokens);
        var expected = calc.Calculate("gpt-4o", 500, 300);
        Assert.Equal(expected, cost);
    }

    [Fact]
    public void CanPrice_KnownModel_ReturnsTrue()
    {
        var calc = new CostCalculator();
        Assert.True(calc.CanPrice("gpt-4o"));
        Assert.True(calc.CanPrice("gpt-4o-mini"));
    }

    [Fact]
    public void CanPrice_UnknownModel_ReturnsFalse()
    {
        var calc = new CostCalculator();
        Assert.False(calc.CanPrice("unknown-model"));
    }

    [Theory]
    [InlineData("claude-opus-4.7", 1_000_000, 1_000_000, 30.00)]
    [InlineData("claude-haiku-4.5", 1_000_000, 1_000_000, 6.00)]
    public void Calculate_ClaudeModels_ReturnsCorrectCost(string model, int input, int output, decimal expected)
    {
        var calc = new CostCalculator();
        Assert.True(calc.CanPrice(model));
        Assert.Equal(expected, calc.Calculate(model, input, output));
    }

    [Fact]
    public void CustomPricing_OverridesDefaults()
    {
        var prices = new Dictionary<string, ModelPriceEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["test-model"] = new(1.00m, 2.00m)
        };
        var calc = new CostCalculator(prices);

        Assert.True(calc.CanPrice("test-model"));
        Assert.False(calc.CanPrice("gpt-4o")); // Not in custom prices
        Assert.Equal(3.00m, calc.Calculate("test-model", 1_000_000, 1_000_000));
    }

    // --- ValidatePricingForBudget tests ---

    [Fact]
    public void ValidatePricingForBudget_NoBudget_DoesNotThrow()
    {
        var calc = new CostCalculator();
        var methodology = new MethodologyDefinition
        {
            Id = "test", Phases = [SinglePhase("unknown-model")]
        };
        // No budget → no validation
        calc.ValidatePricingForBudget(methodology);
    }

    [Fact]
    public void ValidatePricingForBudget_BudgetWithKnownModels_DoesNotThrow()
    {
        var calc = new CostCalculator();
        var methodology = new MethodologyDefinition
        {
            Id = "test", Budget = 1.00m,
            Phases = [SinglePhase(model: "gpt-4o", judgeModel: "gpt-4o-mini")]
        };
        calc.ValidatePricingForBudget(methodology);
    }

    [Fact]
    public void ValidatePricingForBudget_BudgetWithUnknownGenModel_Throws()
    {
        var calc = new CostCalculator();
        var methodology = new MethodologyDefinition
        {
            Id = "test", Budget = 1.00m,
            Phases = [SinglePhase(model: "unknown-gen")]
        };
        var ex = Assert.Throws<InvalidOperationException>(() => calc.ValidatePricingForBudget(methodology));
        Assert.Contains("unknown-gen", ex.Message);
        Assert.Contains("generation", ex.Message);
    }

    [Fact]
    public void ValidatePricingForBudget_BudgetWithUnknownJudgeModel_Throws()
    {
        var calc = new CostCalculator();
        var methodology = new MethodologyDefinition
        {
            Id = "test", Budget = 1.00m,
            Phases = [SinglePhase(model: "gpt-4o", judgeModel: "unknown-judge")]
        };
        var ex = Assert.Throws<InvalidOperationException>(() => calc.ValidatePricingForBudget(methodology));
        Assert.Contains("unknown-judge", ex.Message);
        Assert.Contains("judge", ex.Message);
    }

    // --- Pipeline integration: cost appears on traces ---

    [Fact]
    public async Task HappyPath_AttemptTraces_HaveCost()
    {
        var llm = CreatePassingLlm("gpt-4o", 100, 200);
        var runner = CreateRunner(llm);

        var result = await runner.ExecuteAsync(TestTask, SinglePhaseMethodology());

        Assert.Equal("succeeded", result.Outcome);
        Assert.NotNull(result.PipelineCost);
        Assert.True(result.PipelineCost > 0, "PipelineCost should be positive");

        // Check attempt-level cost
        var phaseRuns = await _runStore.ReadPhaseRunsRollupAsync(result.RunId);
        Assert.NotNull(phaseRuns);
        var attempt = phaseRuns[0].Attempts[0];
        Assert.NotNull(attempt.Cost);
        Assert.True(attempt.Cost > 0, "Attempt cost should be positive");
    }

    [Fact]
    public async Task HappyPath_JudgeTokens_TrackedOnAttempt()
    {
        var llm = CreatePassingLlm("gpt-4o", 100, 200, judgeInputTokens: 50, judgeOutputTokens: 20);
        var runner = CreateRunner(llm);

        var result = await runner.ExecuteAsync(TestTask, SinglePhaseMethodology());

        var phaseRuns = await _runStore.ReadPhaseRunsRollupAsync(result.RunId);
        Assert.NotNull(phaseRuns);
        var attempt = phaseRuns[0].Attempts[0];

        // Generation tokens
        Assert.Equal(100, attempt.Tokens.In);
        Assert.Equal(200, attempt.Tokens.Out);

        // Judge tokens should be captured
        Assert.NotNull(attempt.JudgeTokens);
        Assert.Equal(50, attempt.JudgeTokens.In);
        Assert.Equal(20, attempt.JudgeTokens.Out);

        // Judge model should be recorded
        Assert.NotNull(attempt.JudgeModel);
    }

    [Fact]
    public async Task HappyPath_PipelineCost_IncludesJudgeAndGeneration()
    {
        // Use a custom pricing table with known prices for the test model
        var prices = new Dictionary<string, ModelPriceEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["test-model"] = new(10.00m, 20.00m), // $10/M in, $20/M out
            ["test-judge"] = new(1.00m, 2.00m),    // $1/M in, $2/M out
        };
        var calc = new CostCalculator(prices);

        var llm = CreatePassingLlm("test-model", 1000, 500, judgeInputTokens: 800, judgeOutputTokens: 200);
        var runner = CreateRunner(llm, calc,
            SinglePhaseMethodology(model: "test-model", judgeModel: "test-judge"));

        var result = await runner.ExecuteAsync(TestTask,
            SinglePhaseMethodology(model: "test-model", judgeModel: "test-judge"));

        // Generation: 1000 * 10/1M + 500 * 20/1M = 0.01 + 0.01 = 0.02
        // Judge: 800 * 1/1M + 200 * 2/1M = 0.0008 + 0.0004 = 0.0012
        // Total: 0.0212
        Assert.NotNull(result.PipelineCost);
        Assert.Equal(0.0212m, result.PipelineCost.Value);
    }

    // --- Budget enforcement ---

    [Fact]
    public async Task Budget_UnderBudget_RunSucceeds()
    {
        var llm = CreatePassingLlm("gpt-4o", 100, 200);
        var runner = CreateRunner(llm);

        var methodology = SinglePhaseMethodology();
        methodology = methodology with { Budget = 100.00m }; // Very generous budget

        var result = await runner.ExecuteAsync(TestTask, methodology);

        Assert.Equal("succeeded", result.Outcome);
    }

    [Fact]
    public async Task Budget_Exceeded_RunAbortsWithReason()
    {
        // Use tiny budget that will be exceeded by the first phase
        var llm = CreatePassingLlm("gpt-4o", 1_000_000, 1_000_000); // Huge token counts
        var runner = CreateRunner(llm);

        var methodology = TwoPhaseMethodology();
        methodology = methodology with { Budget = 0.001m }; // Tiny budget

        var result = await runner.ExecuteAsync(TestTask, methodology);

        Assert.Equal("aborted", result.Outcome);
        Assert.Equal("budget_exceeded", result.AbortReason);
        Assert.NotNull(result.PipelineCost);
    }

    [Fact]
    public async Task Budget_StopsRetryingWhenExhausted()
    {
        // LLM that always gets rejected (validation fails) but uses many tokens
        var attemptCount = 0;
        var llm = new StubLlmClient((req, _) =>
        {
            if (req.SystemMessage.Contains("semantic validator"))
                return Task.FromResult(new LlmResponse
                {
                    Content = """{"findings": [{"rule_index": 0, "passed": false, "severity": "error", "reason": "bad"}]}""",
                    InputTokens = 100, OutputTokens = 50, Model = "gpt-4o-mini", LatencyMs = 5
                });

            attemptCount++;
            return Task.FromResult(new LlmResponse
            {
                Content = ValidRequirements,
                InputTokens = 500_000, // 500K tokens per attempt → big cost
                OutputTokens = 500_000,
                Model = "gpt-4o",
                LatencyMs = 10
            });
        });

        var runner = CreateRunner(llm);
        var methodology = new MethodologyDefinition
        {
            Id = "test",
            MaxAttemptsDefault = 5, // 5 attempts allowed
            Budget = 1.00m, // But budget is only $1
            Phases = [SinglePhase()]
        };

        var result = await runner.ExecuteAsync(TestTask, methodology);

        // Should stop before exhausting all 5 attempts due to budget
        Assert.True(attemptCount < 5, $"Expected budget to stop retries early, but ran {attemptCount} attempts");
        Assert.Equal("aborted", result.Outcome);
        Assert.Equal("budget_exceeded", result.AbortReason);
    }

    [Fact]
    public async Task Budget_NullMeansNoEnforcement()
    {
        var llm = CreatePassingLlm("gpt-4o", 1_000_000, 1_000_000);
        var runner = CreateRunner(llm);

        var methodology = SinglePhaseMethodology();
        // No budget set (null)

        var result = await runner.ExecuteAsync(TestTask, methodology);
        Assert.Equal("succeeded", result.Outcome);
    }

    [Fact]
    public async Task Budget_UnknownModelWithBudget_ThrowsOnStart()
    {
        var llm = CreatePassingLlm("gpt-4o", 100, 200);
        var runner = CreateRunner(llm);

        var methodology = new MethodologyDefinition
        {
            Id = "test",
            Budget = 1.00m,
            Phases = [SinglePhase(model: "unpriced-model")]
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.ExecuteAsync(TestTask, methodology));
    }

    // --- Helpers ---

    private static TaskBrief TestTask => new()
    {
        TaskId = "COST-TEST",
        Title = "Cost Test Task",
        Description = "Build a simple test widget."
    };

    private static PhaseDefinition SinglePhase(
        string? model = null, string? judgeModel = null)
    {
        return new PhaseDefinition
        {
            Id = "requirements",
            Goal = "Extract requirements",
            Inputs = [],
            OutputSchema = "requirements/v1",
            Instructions = "Produce requirements.",
            Model = model,
            JudgeModel = judgeModel
        };
    }

    private static MethodologyDefinition SinglePhaseMethodology(
        string? model = null, string? judgeModel = null)
    {
        return new MethodologyDefinition
        {
            Id = "test-single-v1",
            MaxAttemptsDefault = 2,
            Phases = [SinglePhase(model, judgeModel)]
        };
    }

    private static MethodologyDefinition TwoPhaseMethodology()
    {
        return new MethodologyDefinition
        {
            Id = "test-two-v1",
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
                    Goal = "Define contract",
                    Inputs = ["requirements"],
                    OutputSchema = "contract/v1",
                    Instructions = "Produce contract."
                }
            ]
        };
    }

    private static StubLlmClient CreatePassingLlm(
        string model = "gpt-4o",
        int inputTokens = 100,
        int outputTokens = 200,
        int judgeInputTokens = 50,
        int judgeOutputTokens = 20)
    {
        return new StubLlmClient((req, _) =>
        {
            if (req.SystemMessage.Contains("semantic validator"))
            {
                return Task.FromResult(new LlmResponse
                {
                    Content = """{"findings": []}""",
                    InputTokens = judgeInputTokens,
                    OutputTokens = judgeOutputTokens,
                    Model = req.Model,
                    LatencyMs = 5
                });
            }

            string content;
            if (req.UserMessage.Contains("phase_id: requirements"))
                content = ValidRequirements;
            else if (req.UserMessage.Contains("phase_id: contract"))
                content = ValidContract;
            else
                content = ValidRequirements;

            return Task.FromResult(new LlmResponse
            {
                Content = content,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                Model = model,
                LatencyMs = 10
            });
        });
    }

    private PipelineRunner CreateRunner(
        StubLlmClient llm,
        CostCalculator? costCalculator = null,
        MethodologyDefinition? methodology = null)
    {
        var calc = costCalculator ?? new CostCalculator();
        var structural = new StructuralValidator(_schemas);
        var semantic = new SemanticValidator(llm, NullLogger<SemanticValidator>.Instance);
        var crossArtifact = new CrossArtifactValidator();
        var pipeline = new ValidatorPipeline(structural, semantic, crossArtifact, _schemas);

        var executor = new PhaseExecutor(
            llm, _promptBuilder, pipeline, _artifactStore, _runStore,
            calc, TimeProvider.System, NullLogger<PhaseExecutor>.Instance);

        return new PipelineRunner(
            executor, _artifactStore, _runStore, _schemas,
            calc, TimeProvider.System, NullLogger<PipelineRunner>.Instance);
    }

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
}
