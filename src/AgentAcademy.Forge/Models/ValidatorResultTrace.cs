using System.Text.Json.Serialization;

namespace AgentAcademy.Forge.Models;

/// <summary>
/// Validator result trace contract (locked).
/// Authoritative fields: phase, code, severity, blocking.
/// Reason fields are advisory prose — never parsed.
/// </summary>
public sealed record ValidatorResultTrace
{
    /// <summary>structural | semantic | cross-artifact</summary>
    [JsonPropertyName("phase")]
    public required string Phase { get; init; }

    /// <summary>STABLE_SCREAMING_SNAKE code.</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>error | warning | info</summary>
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("blocking")]
    public required bool Blocking { get; init; }

    /// <summary>Optional JSONPath into the artifact payload.</summary>
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    /// <summary>Optional short evidence string.</summary>
    [JsonPropertyName("evidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Evidence { get; init; }

    [JsonPropertyName("attemptNumber")]
    public required int AttemptNumber { get; init; }

    /// <summary>Advisory prose — never parsed.</summary>
    [JsonPropertyName("advisoryReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdvisoryReason { get; init; }

    /// <summary>Advisory prose — never parsed.</summary>
    [JsonPropertyName("blockingReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BlockingReason { get; init; }
}
