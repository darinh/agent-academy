using System.Text.Json;
using AgentAcademy.Forge.Llm;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Schemas;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Forge.Validation;

/// <summary>
/// LLM-judge semantic validator. Evaluates artifacts against the schema's
/// semantic rules rubric by asking an LLM to grade each rule.
/// </summary>
public sealed class SemanticValidator
{
    private readonly ILlmClient _llm;
    private readonly ILogger<SemanticValidator> _logger;

    public SemanticValidator(ILlmClient llm, ILogger<SemanticValidator> logger)
    {
        _llm = llm;
        _logger = logger;
    }

    /// <summary>
    /// Evaluate an artifact against its schema's semantic rules.
    /// Returns validator results; empty list means all rules passed.
    /// </summary>
    /// <param name="envelope">Artifact to validate.</param>
    /// <param name="schemaEntry">Schema with semantic rules.</param>
    /// <param name="attemptNumber">Current attempt number.</param>
    /// <param name="model">Model to use for judging, or null to use the default (gpt-4o-mini).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<SemanticValidationResult> ValidateAsync(
        ArtifactEnvelope envelope,
        SchemaEntry schemaEntry,
        int attemptNumber,
        string? model = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(schemaEntry.SemanticRules))
            return new SemanticValidationResult { Findings = [] };

        var effectiveModel = string.IsNullOrWhiteSpace(model) ? DefaultJudgeModel : model;
        var prompt = BuildJudgePrompt(envelope, schemaEntry);

        LlmResponse response;
        try
        {
            response = await _llm.GenerateAsync(new LlmRequest
            {
                SystemMessage = SystemPrompt,
                UserMessage = prompt,
                Model = effectiveModel,
                Temperature = 0.0,
                MaxTokens = 4096,
                JsonMode = true
            }, ct);
        }
        catch (LlmClientException ex)
        {
            _logger.LogWarning(ex, "Semantic validator LLM call failed: {ErrorKind}", ex.ErrorKind);
            return new SemanticValidationResult
            {
                Findings =
                [
                    new ValidatorResultTrace
                    {
                        Phase = "semantic",
                        Code = "SEMANTIC_LLM_FAILED",
                        Severity = "error",
                        Blocking = true,
                        AttemptNumber = attemptNumber,
                        Evidence = $"{ex.ErrorKind}: {Truncate(ex.Message, 200)}",
                        BlockingReason = $"Semantic validator LLM call failed: {ex.ErrorKind}"
                    }
                ]
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        return new SemanticValidationResult
        {
            Findings = ParseJudgeResponse(response.Content, attemptNumber),
            JudgeTokens = new TokenCount { In = response.InputTokens, Out = response.OutputTokens }
        };
    }

    internal const string DefaultJudgeModel = "gpt-4o-mini";

    internal const string SystemPrompt =
        """
        You are a semantic validator for software engineering artifacts.
        You evaluate artifacts against a rubric of semantic rules.
        You return a JSON object with a "findings" array.
        Each finding has: "rule_index" (0-based), "passed" (bool), "severity" ("error" or "warning"), "reason" (string).
        Only include findings for rules that FAILED. If all rules pass, return {"findings": []}.
        Be strict: if a rule is ambiguous, err on the side of failing it.
        """;

    private static string BuildJudgePrompt(ArtifactEnvelope envelope, SchemaEntry schemaEntry)
    {
        var payloadJson = envelope.Payload.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : JsonSerializer.Serialize(envelope.Payload, SerializerOptions);

        return $$"""
            === ARTIFACT ===
            Type: {{envelope.ArtifactType}} (schema: {{schemaEntry.SchemaId}})
            Payload:
            {{payloadJson}}

            === SEMANTIC RULES (evaluate each) ===
            {{schemaEntry.SemanticRules}}

            === INSTRUCTIONS ===
            For each rule above (indexed from 0), determine if the artifact satisfies it.
            Return ONLY a JSON object:
            {"findings": [{"rule_index": 0, "passed": false, "severity": "error", "reason": "..."}]}
            Include only FAILED rules. If all pass, return {"findings": []}.
            """;
    }

    private IReadOnlyList<ValidatorResultTrace> ParseJudgeResponse(string content, int attemptNumber)
    {
        var results = new List<ValidatorResultTrace>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(content);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Semantic validator failed to parse LLM judge response: {Error}", ex.Message);
            return
            [
                new ValidatorResultTrace
                {
                    Phase = "semantic",
                    Code = "SEMANTIC_PARSE_FAILED",
                    Severity = "error",
                    Blocking = true,
                    AttemptNumber = attemptNumber,
                    Evidence = Truncate(content, 200),
                    BlockingReason = "Could not parse semantic validator LLM response as JSON."
                }
            ];
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("findings", out var findings) ||
                findings.ValueKind != JsonValueKind.Array)
            {
                return
                [
                    new ValidatorResultTrace
                    {
                        Phase = "semantic",
                        Code = "SEMANTIC_PARSE_FAILED",
                        Severity = "error",
                        Blocking = true,
                        AttemptNumber = attemptNumber,
                        Evidence = "Missing or non-array 'findings' field",
                        BlockingReason = "Semantic validator LLM response missing 'findings' array."
                    }
                ];
            }

            foreach (var finding in findings.EnumerateArray())
            {
                var ruleIndex = finding.TryGetProperty("rule_index", out var ri)
                    && ri.ValueKind == JsonValueKind.Number
                    ? ri.GetInt32() : -1;
                var passed = finding.TryGetProperty("passed", out var p)
                    && p.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && p.GetBoolean();

                if (passed) continue;

                var severity = (finding.TryGetProperty("severity", out var s)
                    ? s.GetString()?.Trim().ToLowerInvariant() ?? "error"
                    : "error");
                var reason = finding.TryGetProperty("reason", out var r)
                    ? r.GetString() ?? "No reason provided"
                    : "No reason provided";

                // Unknown severity values are treated as blocking
                var isBlocking = severity != "warning";
                if (severity is not "error" and not "warning")
                    severity = "error";

                results.Add(new ValidatorResultTrace
                {
                    Phase = "semantic",
                    Code = $"SEMANTIC_RULE_{ruleIndex}",
                    Severity = severity,
                    Blocking = isBlocking,
                    AttemptNumber = attemptNumber,
                    Evidence = Truncate(reason, 300),
                    AdvisoryReason = isBlocking ? null : reason,
                    BlockingReason = isBlocking ? reason : null
                });
            }
        }

        return results;
    }

    private static string Truncate(string s, int maxLength) =>
        s.Length > maxLength ? s[..maxLength] + "..." : s;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };
}

/// <summary>Result of semantic validation, including judge token usage.</summary>
public sealed record SemanticValidationResult
{
    public required IReadOnlyList<ValidatorResultTrace> Findings { get; init; }
    public TokenCount JudgeTokens { get; init; } = new();
}
