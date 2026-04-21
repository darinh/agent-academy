using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentAcademy.Forge.Models;

/// <summary>
/// Hash-bound artifact envelope (<hash>.json).
/// Hash = sha256(canonical_json(this)).
/// </summary>
public sealed record ArtifactEnvelope
{
    [JsonPropertyName("artifactType")]
    public required string ArtifactType { get; init; }

    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("producedByPhase")]
    public required string ProducedByPhase { get; init; }

    /// <summary>
    /// Schema-specific payload. Stored as JsonElement to preserve
    /// exact structure for deterministic hashing.
    /// </summary>
    [JsonPropertyName("payload")]
    public required JsonElement Payload { get; init; }
}

/// <summary>
/// Advisory metadata (<hash>.meta.json).
/// Not hash-bound. Missing or corrupted meta doesn't affect identity.
/// </summary>
public sealed record ArtifactMeta
{
    [JsonPropertyName("derivedFrom")]
    public required IReadOnlyList<string> DerivedFrom { get; init; }

    [JsonPropertyName("inputHashes")]
    public required IReadOnlyList<string> InputHashes { get; init; }

    [JsonPropertyName("producedAt")]
    public required DateTime ProducedAt { get; init; }

    [JsonPropertyName("attemptNumber")]
    public required int AttemptNumber { get; init; }
}
