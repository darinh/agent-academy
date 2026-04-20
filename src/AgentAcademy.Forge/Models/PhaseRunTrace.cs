using System.Text.Json.Serialization;

namespace AgentAcademy.Forge.Models;

/// <summary>
/// On-disk phase-runs.json trace contract. One entry per phase in the methodology.
/// The top-level file is a JSON array of these records.
/// </summary>
public sealed record PhaseRunTrace
{
    [JsonPropertyName("phaseId")]
    public required string PhaseId { get; init; }

    [JsonPropertyName("artifactType")]
    public required string ArtifactType { get; init; }

    [JsonPropertyName("stateTransitions")]
    public required IReadOnlyList<StateTransition> StateTransitions { get; init; }

    [JsonPropertyName("attempts")]
    public required IReadOnlyList<AttemptTrace> Attempts { get; init; }

    /// <summary>Ordered list of input artifact hashes (sha256:... prefixed).</summary>
    [JsonPropertyName("inputArtifactHashes")]
    public required IReadOnlyList<string> InputArtifactHashes { get; init; }

    /// <summary>Ordered list of accepted output artifact hashes (sha256:... prefixed).</summary>
    [JsonPropertyName("outputArtifactHashes")]
    public required IReadOnlyList<string> OutputArtifactHashes { get; init; }
}

/// <summary>Append-only state transition record.</summary>
public sealed record StateTransition
{
    [JsonPropertyName("from")]
    public string? From { get; init; }

    [JsonPropertyName("to")]
    public required string To { get; init; }

    [JsonPropertyName("at")]
    public required DateTime At { get; init; }
}

/// <summary>
/// Per-attempt trace record. artifactHash is explicitly nullable, never omitted.
/// </summary>
public sealed record AttemptTrace
{
    [JsonPropertyName("attemptNumber")]
    public required int AttemptNumber { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("artifactHash")]
    public string? ArtifactHash { get; init; }

    [JsonPropertyName("validatorResults")]
    public required IReadOnlyList<ValidatorResultTrace> ValidatorResults { get; init; }

    [JsonPropertyName("tokens")]
    public required TokenCount Tokens { get; init; }

    [JsonPropertyName("latencyMs")]
    public long LatencyMs { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("startedAt")]
    public required DateTime StartedAt { get; init; }

    [JsonPropertyName("endedAt")]
    public DateTime? EndedAt { get; init; }
}
