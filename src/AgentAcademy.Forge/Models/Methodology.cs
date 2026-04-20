using System.Text.Json.Serialization;

namespace AgentAcademy.Forge.Models;

/// <summary>
/// Methodology definition — loaded from methodology.json.
/// Defines the ordered phases the pipeline executes.
/// </summary>
public sealed record MethodologyDefinition
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("max_attempts_default")]
    public int MaxAttemptsDefault { get; init; } = 3;

    [JsonPropertyName("phases")]
    public required IReadOnlyList<PhaseDefinition> Phases { get; init; }
}

/// <summary>
/// A single phase in the methodology pipeline.
/// </summary>
public sealed record PhaseDefinition
{
    /// <summary>Snake_case phase identifier (e.g. "requirements", "implementation").</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("goal")]
    public required string Goal { get; init; }

    /// <summary>Phase IDs whose accepted artifacts are inputs to this phase.</summary>
    [JsonPropertyName("inputs")]
    public required IReadOnlyList<string> Inputs { get; init; }

    /// <summary>Artifact schema identifier (e.g. "requirements/v1").</summary>
    [JsonPropertyName("output_schema")]
    public required string OutputSchema { get; init; }

    [JsonPropertyName("instructions")]
    public required string Instructions { get; init; }

    /// <summary>Per-phase override; falls back to methodology default.</summary>
    [JsonPropertyName("max_attempts")]
    public int? MaxAttempts { get; init; }

    /// <summary>Extracts artifact type from output_schema (e.g. "requirements/v1" → "requirements").</summary>
    [JsonIgnore]
    public string ArtifactType => OutputSchema.Split('/')[0];

    /// <summary>Extracts schema version from output_schema (e.g. "requirements/v1" → "1").</summary>
    [JsonIgnore]
    public string SchemaVersion => OutputSchema.Split('/')[1].TrimStart('v');
}
