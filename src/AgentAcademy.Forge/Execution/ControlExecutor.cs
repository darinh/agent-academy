using System.Text;
using AgentAcademy.Forge.Artifacts;
using AgentAcademy.Forge.Costs;
using AgentAcademy.Forge.Llm;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Prompt;
using AgentAcademy.Forge.Schemas;
using AgentAcademy.Forge.Storage;
using AgentAcademy.Forge.Validation;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Forge.Execution;

/// <summary>
/// Executes the control arm — a single-shot LLM baseline that produces
/// the target schema artifact directly from the task brief. No retries,
/// no amendment loop, structural validation only. Used for A/B benchmarking
/// against the multi-phase pipeline.
/// </summary>
public sealed class ControlExecutor
{
    private readonly ILlmClient _llm;
    private readonly SchemaRegistry _schemas;
    private readonly StructuralValidator _structural;
    private readonly IArtifactStore _artifactStore;
    private readonly CostCalculator _costCalculator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ControlExecutor> _logger;

    public ControlExecutor(
        ILlmClient llm,
        SchemaRegistry schemas,
        StructuralValidator structural,
        IArtifactStore artifactStore,
        CostCalculator costCalculator,
        TimeProvider timeProvider,
        ILogger<ControlExecutor> logger)
    {
        _llm = llm;
        _schemas = schemas;
        _structural = structural;
        _artifactStore = artifactStore;
        _costCalculator = costCalculator;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Execute the control arm: single-shot LLM call → parse → structural validation.
    /// </summary>
    public async Task<ControlResult> ExecuteAsync(
        TaskBrief task,
        MethodologyDefinition methodology,
        CancellationToken ct = default)
    {
        var control = methodology.Control
            ?? throw new ArgumentException("Methodology has no control configuration.");

        var schemaEntry = _schemas.GetSchema(control.TargetSchema);
        var model = ResolveControlModel(control, methodology);

        _logger.LogInformation("Control arm starting for task {TaskId}, target schema {Schema}, model {Model}",
            task.TaskId, control.TargetSchema, model);

        // Build the control prompt — same system message as pipeline, same schema/semantic rules,
        // but no upstream artifacts and no amendment loop
        var systemMessage = PromptBuilder.SystemMessage;
        var userMessage = BuildControlUserMessage(task, schemaEntry);

        // Single-shot LLM call
        LlmResponse llmResponse;
        try
        {
            llmResponse = await _llm.GenerateAsync(new LlmRequest
            {
                SystemMessage = systemMessage,
                UserMessage = userMessage,
                Model = model,
                Temperature = 0.2,
                MaxTokens = 8192,
                JsonMode = true
            }, ct);
        }
        catch (LlmClientException ex)
        {
            _logger.LogWarning(ex, "Control arm LLM call failed ({ErrorKind})", ex.ErrorKind);
            return new ControlResult
            {
                Outcome = "failed",
                Tokens = new TokenCount(),
                Cost = 0m
            };
        }

        var tokens = new TokenCount { In = llmResponse.InputTokens, Out = llmResponse.OutputTokens };
        var cost = _costCalculator.Calculate(model, tokens);

        // Parse the response
        var parseResult = AttemptResponseParser.Parse(
            llmResponse.Content,
            schemaEntry.ArtifactType,
            schemaEntry.SchemaVersion,
            "control",
            attemptNumber: 1);

        if (!parseResult.Success)
        {
            _logger.LogInformation("Control arm parse failed: {Failures}",
                string.Join("; ", parseResult.Failures.Select(f => f.Code)));
            return new ControlResult
            {
                Outcome = "structurally_invalid",
                Tokens = tokens,
                Cost = cost
            };
        }

        var envelope = parseResult.Envelope!;

        // Structural validation only — no semantic or cross-artifact.
        // This keeps the control's token count clean for cost comparison.
        var structuralFindings = _structural.Validate(envelope, attemptNumber: 1);
        var hasBlockingFailures = structuralFindings.Any(f => f.Blocking);

        if (hasBlockingFailures)
        {
            _logger.LogInformation("Control arm structurally invalid: {Findings}",
                string.Join("; ", structuralFindings.Where(f => f.Blocking).Select(f => f.Code)));
            return new ControlResult
            {
                Outcome = "structurally_invalid",
                Tokens = tokens,
                Cost = cost
            };
        }

        // Store the artifact for later comparison
        var meta = new ArtifactMeta
        {
            DerivedFrom = [],
            InputHashes = [],
            ProducedAt = _timeProvider.GetUtcNow().UtcDateTime,
            AttemptNumber = 1
        };
        var hash = await _artifactStore.WriteAsync(envelope, meta, ct);

        _logger.LogInformation("Control arm succeeded: {TokensIn}+{TokensOut} tokens, ${Cost:F4}, hash {Hash}",
            tokens.In, tokens.Out, cost, hash[..12]);

        return new ControlResult
        {
            Outcome = "structurally_valid",
            Tokens = tokens,
            Cost = cost,
            ArtifactHash = $"sha256:{hash}"
        };
    }

    /// <summary>
    /// Build the user message for the control arm. Uses the same schema body and
    /// semantic rules as the pipeline, but without upstream artifacts or amendment notes.
    /// </summary>
    internal static string BuildControlUserMessage(TaskBrief task, SchemaEntry schemaEntry)
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== TASK ===");
        sb.AppendLine(task.Description);
        sb.AppendLine();

        sb.AppendLine("=== PHASE ===");
        sb.AppendLine("phase_id: control");
        sb.AppendLine($"goal: Produce a {schemaEntry.ArtifactType} artifact directly from the task description.");
        sb.AppendLine();

        sb.AppendLine("=== OUTPUT CONTRACT ===");
        sb.AppendLine($"You must produce a JSON object whose root field `body` matches schema `{schemaEntry.SchemaId}`.");
        sb.AppendLine("The schema body is:");
        sb.AppendLine(schemaEntry.SchemaBodyJson);
        sb.AppendLine();
        sb.AppendLine("The schema's semantic rules (you will be judged on these):");
        sb.AppendLine(schemaEntry.SemanticRules);
        sb.AppendLine();

        sb.AppendLine("=== INPUTS ===");
        sb.AppendLine("(none — produce the artifact directly from the task description above)");
        sb.AppendLine();

        sb.AppendLine("=== INSTRUCTIONS ===");
        sb.AppendLine($"Produce a complete {schemaEntry.ArtifactType} artifact that satisfies the task description and schema. ");
        sb.AppendLine("You have no prior phase outputs. Work from the task description alone.");
        sb.AppendLine();

        sb.AppendLine("=== AMENDMENT NOTES ===");
        sb.AppendLine("(none — this is the first and only attempt)");
        sb.AppendLine();

        sb.AppendLine("=== RESPONSE FORMAT ===");
        sb.Append($"Respond with a single JSON object on one line or pretty-printed, no surrounding text:");
        sb.AppendLine();
        sb.Append($"{{ \"body\": {{ ...matches schema {schemaEntry.SchemaId}... }} }}");

        return sb.ToString();
    }

    internal static string ResolveControlModel(ControlConfig control, MethodologyDefinition methodology)
    {
        if (!string.IsNullOrWhiteSpace(control.Model))
            return control.Model;
        if (!string.IsNullOrWhiteSpace(methodology.ModelDefaults?.Generation))
            return methodology.ModelDefaults.Generation;
        return "gpt-4o";
    }
}

/// <summary>
/// Result of the control arm execution.
/// </summary>
public sealed record ControlResult
{
    /// <summary>
    /// "structurally_valid" or "structurally_invalid" or "failed" (LLM error).
    /// Distinct from pipeline outcomes to reflect that only structural validation was applied.
    /// </summary>
    public required string Outcome { get; init; }

    public required TokenCount Tokens { get; init; }

    public decimal? Cost { get; init; }

    /// <summary>
    /// Content-addressed hash of the control artifact (sha256:... prefixed),
    /// or null if the control failed to produce a valid artifact.
    /// </summary>
    public string? ArtifactHash { get; init; }
}
