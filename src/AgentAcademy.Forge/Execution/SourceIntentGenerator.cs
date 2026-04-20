using System.Text;
using System.Text.Json;
using AgentAcademy.Forge.Artifacts;
using AgentAcademy.Forge.Costs;
using AgentAcademy.Forge.Llm;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Schemas;
using AgentAcademy.Forge.Validation;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Forge.Execution;

/// <summary>
/// Generates the source-intent artifact from a TaskBrief via LLM extraction.
/// Single-shot with retry on structural failures. Source-intent is created once
/// at pipeline start (immutable) and used for grounding the requirements phase
/// and validating fidelity at the end.
/// </summary>
public sealed class SourceIntentGenerator
{
    private readonly ILlmClient _llm;
    private readonly SchemaRegistry _schemas;
    private readonly StructuralValidator _structural;
    private readonly IArtifactStore _artifactStore;
    private readonly CostCalculator _costCalculator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SourceIntentGenerator> _logger;

    public SourceIntentGenerator(
        ILlmClient llm,
        SchemaRegistry schemas,
        StructuralValidator structural,
        IArtifactStore artifactStore,
        CostCalculator costCalculator,
        TimeProvider timeProvider,
        ILogger<SourceIntentGenerator> logger)
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
    /// Generate a source-intent artifact from the task brief.
    /// Retries on structural validation failure up to maxAttempts.
    /// </summary>
    public async Task<SourceIntentResult> GenerateAsync(
        TaskBrief task,
        MethodologyDefinition methodology,
        int maxAttempts = 3,
        CancellationToken ct = default)
    {
        var schemaEntry = _schemas.GetSchema("source_intent/v1");
        var model = ResolveModel(methodology);
        var totalTokens = new TokenCount();
        var totalCost = 0m;

        _logger.LogInformation("Source-intent generation starting for task {TaskId}, model {Model}",
            task.TaskId, model);

        IReadOnlyList<ValidatorResultTrace>? lastFailures = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            _logger.LogInformation("Source-intent: attempt {Attempt}/{Max}", attempt, maxAttempts);

            var userMessage = BuildUserMessage(task, schemaEntry, lastFailures);

            LlmResponse llmResponse;
            try
            {
                llmResponse = await _llm.GenerateAsync(new LlmRequest
                {
                    SystemMessage = SystemMessage,
                    UserMessage = userMessage,
                    Model = model,
                    Temperature = 0.1, // Low temperature for faithful extraction
                    MaxTokens = 4096,
                    JsonMode = true
                }, ct);
            }
            catch (LlmClientException ex)
            {
                _logger.LogWarning(ex, "Source-intent LLM call failed ({ErrorKind})", ex.ErrorKind);
                return new SourceIntentResult
                {
                    Outcome = "failed",
                    Tokens = totalTokens,
                    Cost = totalCost
                };
            }

            var attemptTokens = new TokenCount { In = llmResponse.InputTokens, Out = llmResponse.OutputTokens };
            var attemptCost = _costCalculator.Calculate(model, attemptTokens);
            totalTokens = new TokenCount
            {
                In = totalTokens.In + attemptTokens.In,
                Out = totalTokens.Out + attemptTokens.Out
            };
            totalCost += attemptCost;

            // Parse the response
            var parseResult = AttemptResponseParser.Parse(
                llmResponse.Content, "source_intent", "1", "source_intent", attempt);

            if (!parseResult.Success)
            {
                _logger.LogInformation("Source-intent parse failed on attempt {Attempt}: {Failures}",
                    attempt, string.Join("; ", parseResult.Failures.Select(f => f.Code)));
                lastFailures = parseResult.Failures;
                continue;
            }

            var envelope = parseResult.Envelope!;

            // Structural validation
            var structuralFindings = _structural.Validate(envelope, attemptNumber: attempt);
            var hasBlockingFailures = structuralFindings.Any(f => f.Blocking);

            if (hasBlockingFailures)
            {
                _logger.LogInformation("Source-intent structurally invalid on attempt {Attempt}: {Findings}",
                    attempt, string.Join("; ", structuralFindings.Where(f => f.Blocking).Select(f => f.Code)));
                lastFailures = structuralFindings;
                continue;
            }

            // Verify the task_brief field is verbatim (completeness check)
            var verbatimCheck = ValidateTaskBriefVerbatim(envelope, task);
            if (verbatimCheck is not null)
            {
                _logger.LogInformation("Source-intent verbatim check failed on attempt {Attempt}: {Reason}",
                    attempt, verbatimCheck.Code);
                lastFailures = [verbatimCheck];
                continue;
            }

            // Store the artifact
            var meta = new ArtifactMeta
            {
                DerivedFrom = [],
                InputHashes = [],
                ProducedAt = _timeProvider.GetUtcNow().UtcDateTime,
                AttemptNumber = attempt
            };
            var hash = await _artifactStore.WriteAsync(envelope, meta, ct);

            _logger.LogInformation("Source-intent generated: {TokensIn}+{TokensOut} tokens, ${Cost:F4}, hash {Hash}",
                totalTokens.In, totalTokens.Out, totalCost, hash[..12]);

            return new SourceIntentResult
            {
                Outcome = "accepted",
                Tokens = totalTokens,
                Cost = totalCost,
                ArtifactHash = $"sha256:{hash}",
                Envelope = envelope
            };
        }

        _logger.LogWarning("Source-intent generation failed after {Max} attempts", maxAttempts);
        return new SourceIntentResult
        {
            Outcome = "failed",
            Tokens = totalTokens,
            Cost = totalCost
        };
    }

    /// <summary>
    /// Validate that the task_brief field in the source-intent artifact
    /// contains the original task description (not paraphrased).
    /// </summary>
    internal static ValidatorResultTrace? ValidateTaskBriefVerbatim(
        ArtifactEnvelope envelope,
        TaskBrief task)
    {
        try
        {
            if (envelope.Payload.TryGetProperty("task_brief", out var taskBriefElement))
            {
                var extractedBrief = taskBriefElement.GetString();
                if (extractedBrief is not null)
                {
                    // Normalize whitespace for comparison
                    var normalizedOriginal = NormalizeWhitespace(task.Description);
                    var normalizedExtracted = NormalizeWhitespace(extractedBrief);

                    if (!normalizedExtracted.Contains(normalizedOriginal, StringComparison.OrdinalIgnoreCase)
                        && !normalizedOriginal.Contains(normalizedExtracted, StringComparison.OrdinalIgnoreCase))
                    {
                        // Check if at least 80% of the words are present (handles minor reformatting)
                        var originalWords = normalizedOriginal.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var extractedWords = new HashSet<string>(
                            normalizedExtracted.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                            StringComparer.OrdinalIgnoreCase);

                        var matchCount = originalWords.Count(w => extractedWords.Contains(w));
                        var matchRatio = originalWords.Length > 0 ? (double)matchCount / originalWords.Length : 0;

                        if (matchRatio < 0.8)
                        {
                            return new ValidatorResultTrace
                            {
                                Phase = "structural",
                                Code = "TASK_BRIEF_NOT_VERBATIM",
                                Severity = "error",
                                Blocking = true,
                                AttemptNumber = 1,
                                Evidence = $"task_brief field appears paraphrased (word match: {matchRatio:P0}). " +
                                           "It must contain the original task description verbatim.",
                                BlockingReason = "Source-intent task_brief must be verbatim, not paraphrased."
                            };
                        }
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            // JsonElement access error — structural validator will catch this
        }

        return null;
    }

    private static string NormalizeWhitespace(string text) =>
        string.Join(' ', text.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));

    internal static string BuildUserMessage(
        TaskBrief task,
        SchemaEntry schemaEntry,
        IReadOnlyList<ValidatorResultTrace>? previousFailures = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== TASK ===");
        sb.AppendLine($"Title: {task.Title}");
        sb.AppendLine($"Description: {task.Description}");
        sb.AppendLine();

        sb.AppendLine("=== PHASE ===");
        sb.AppendLine("phase_id: source_intent");
        sb.AppendLine("goal: Extract a structured representation of the human's original intent from the task description above.");
        sb.AppendLine();

        sb.AppendLine("=== OUTPUT CONTRACT ===");
        sb.AppendLine($"You must produce a JSON object whose root field `body` matches schema `{schemaEntry.SchemaId}`.");
        sb.AppendLine("The schema body is:");
        sb.AppendLine(schemaEntry.SchemaBodyJson);
        sb.AppendLine();
        sb.AppendLine("The schema's semantic rules (you will be judged on these):");
        sb.AppendLine(schemaEntry.SemanticRules);
        sb.AppendLine();

        sb.AppendLine("=== CRITICAL INSTRUCTION ===");
        sb.AppendLine("The `task_brief` field MUST contain the exact task description verbatim — copy it character-for-character.");
        sb.AppendLine("Do NOT paraphrase, summarize, abbreviate, or reword the task description in any way.");
        sb.AppendLine("Extract acceptance_criteria, explicit_constraints, examples, and counter_examples from the task description.");
        sb.AppendLine("If the task description does not explicitly contain examples or constraints, use empty arrays.");
        sb.AppendLine("Do NOT invent criteria or constraints that are not stated or clearly implied in the task description.");
        sb.AppendLine();

        if (previousFailures is { Count: > 0 })
        {
            sb.AppendLine("=== AMENDMENT NOTES ===");
            sb.AppendLine("Your previous attempt was rejected. Fix the following issues:");
            foreach (var f in previousFailures)
            {
                sb.AppendLine($"- [{f.Code}] {f.BlockingReason ?? f.Evidence ?? f.Code}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("=== RESPONSE FORMAT ===");
        sb.Append($"Respond with a single JSON object on one line or pretty-printed, no surrounding text:");
        sb.AppendLine();
        sb.Append($"{{ \"body\": {{ ...matches schema {schemaEntry.SchemaId}... }} }}");

        return sb.ToString();
    }

    internal static string ResolveModel(MethodologyDefinition methodology)
    {
        if (methodology.Fidelity?.Model is { Length: > 0 } fidelityModel)
            return fidelityModel;
        if (methodology.ModelDefaults?.Generation is { Length: > 0 } genModel)
            return genModel;
        return "gpt-4o";
    }

    private const string SystemMessage =
        """
        You are a requirements analyst. Your job is to faithfully extract structured intent from a human's task description.
        You do NOT interpret, expand, or improve the task — you extract what is stated.
        You produce a single JSON object matching the schema declared in the user message.
        You do not produce prose, markdown, code fences, or commentary.
        The task_brief field must be an exact verbatim copy of the task description.
        """;
}

/// <summary>
/// Result of source-intent generation.
/// </summary>
public sealed record SourceIntentResult
{
    /// <summary>"accepted" if a valid source-intent was produced, "failed" otherwise.</summary>
    public required string Outcome { get; init; }

    public required TokenCount Tokens { get; init; }

    public decimal Cost { get; init; }

    /// <summary>Content-addressed hash (sha256:... prefixed), or null if failed.</summary>
    public string? ArtifactHash { get; init; }

    /// <summary>The stored envelope, or null if failed.</summary>
    public ArtifactEnvelope? Envelope { get; init; }
}
