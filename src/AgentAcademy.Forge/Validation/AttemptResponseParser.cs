using System.Text.Json;
using AgentAcademy.Forge.Models;

namespace AgentAcademy.Forge.Validation;

/// <summary>
/// Parses raw LLM response into an ArtifactEnvelope.
/// Handles the body→payload seam: LLM returns { "body": ... },
/// executor wraps into the identity envelope for hashing/storage.
/// </summary>
public static class AttemptResponseParser
{
    /// <summary>
    /// Parse raw LLM JSON response, extract the "body" field, and wrap
    /// it into an ArtifactEnvelope for hashing and storage.
    /// </summary>
    /// <param name="rawResponse">Raw string content from the LLM.</param>
    /// <param name="phase">Phase definition (for artifactType and schemaVersion).</param>
    /// <param name="attemptNumber">Current attempt number (for error results).</param>
    /// <returns>
    /// Success: the parsed ArtifactEnvelope.
    /// Failure: null envelope with a list of structural validator failures.
    /// </returns>
    public static ParseResult Parse(string rawResponse, PhaseDefinition phase, int attemptNumber) =>
        Parse(rawResponse, phase.ArtifactType, phase.SchemaVersion, phase.Id, attemptNumber);

    /// <summary>
    /// Parse raw LLM JSON response with explicit artifact identity fields.
    /// Used by the control executor where no <see cref="PhaseDefinition"/> exists.
    /// </summary>
    public static ParseResult Parse(
        string rawResponse,
        string artifactType,
        string schemaVersion,
        string producedByPhase,
        int attemptNumber)
    {
        // Step 1: Parse as JSON
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawResponse);
        }
        catch (JsonException ex)
        {
            return ParseResult.Fail(new ValidatorResultTrace
            {
                Phase = "structural",
                Code = "JSON_PARSE_FAILED",
                Severity = "error",
                Blocking = true,
                AttemptNumber = attemptNumber,
                Evidence = TruncateForEvidence(ex.Message),
                BlockingReason = $"LLM response is not valid JSON: {ex.Message}"
            });
        }

        using (doc)
        {
            // Step 2: Extract "body" field
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ParseResult.Fail(new ValidatorResultTrace
                {
                    Phase = "structural",
                    Code = "ROOT_NOT_OBJECT",
                    Severity = "error",
                    Blocking = true,
                    AttemptNumber = attemptNumber,
                    Evidence = $"Root is {doc.RootElement.ValueKind}, expected Object",
                    BlockingReason = "LLM response root must be a JSON object."
                });
            }

            if (!doc.RootElement.TryGetProperty("body", out var bodyElement))
            {
                return ParseResult.Fail(new ValidatorResultTrace
                {
                    Phase = "structural",
                    Code = "BODY_FIELD_MISSING",
                    Severity = "error",
                    Blocking = true,
                    AttemptNumber = attemptNumber,
                    Evidence = $"Root keys: [{string.Join(", ", EnumerateKeys(doc.RootElement))}]",
                    BlockingReason = "LLM response must contain a top-level \"body\" field."
                });
            }

            if (bodyElement.ValueKind != JsonValueKind.Object)
            {
                return ParseResult.Fail(new ValidatorResultTrace
                {
                    Phase = "structural",
                    Code = "BODY_NOT_OBJECT",
                    Severity = "error",
                    Blocking = true,
                    AttemptNumber = attemptNumber,
                    Evidence = $"body is {bodyElement.ValueKind}, expected Object",
                    BlockingReason = "The \"body\" field must be a JSON object."
                });
            }

            // Step 3: Wrap into ArtifactEnvelope (Clone before dispose)
            var envelope = new ArtifactEnvelope
            {
                ArtifactType = artifactType,
                SchemaVersion = schemaVersion,
                ProducedByPhase = producedByPhase,
                Payload = bodyElement.Clone()
            };

            return ParseResult.Ok(envelope);
        }
    }

    private static string TruncateForEvidence(string s) =>
        s.Length > 200 ? s[..200] + "..." : s;

    private static IEnumerable<string> EnumerateKeys(JsonElement obj)
    {
        foreach (var prop in obj.EnumerateObject())
            yield return prop.Name;
    }
}

/// <summary>
/// Result of parsing an LLM response.
/// </summary>
public sealed record ParseResult
{
    public ArtifactEnvelope? Envelope { get; init; }
    public IReadOnlyList<ValidatorResultTrace> Failures { get; init; } = [];
    public bool Success => Envelope is not null;

    public static ParseResult Ok(ArtifactEnvelope envelope) =>
        new() { Envelope = envelope };

    public static ParseResult Fail(ValidatorResultTrace failure) =>
        new() { Failures = [failure] };

    public static ParseResult Fail(IReadOnlyList<ValidatorResultTrace> failures) =>
        new() { Failures = failures };
}
