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

    /// <summary>Default model IDs for generation and semantic judging. Per-phase overrides take precedence.</summary>
    [JsonPropertyName("model_defaults")]
    public ModelDefaults? ModelDefaults { get; init; }

    /// <summary>Maximum USD cost for the entire pipeline run. Null means no limit.</summary>
    [JsonPropertyName("budget")]
    public decimal? Budget { get; init; }

    [JsonPropertyName("phases")]
    public required IReadOnlyList<PhaseDefinition> Phases { get; init; }
}

/// <summary>
/// Default model configuration for the methodology.
/// </summary>
public sealed record ModelDefaults
{
    /// <summary>Model used for artifact generation (default: gpt-4o).</summary>
    [JsonPropertyName("generation")]
    public string? Generation { get; init; }

    /// <summary>Model used for semantic validation judging (default: gpt-4o-mini).</summary>
    [JsonPropertyName("judge")]
    public string? Judge { get; init; }
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

    /// <summary>Generation model for this phase. Falls back to methodology ModelDefaults.Generation, then "gpt-4o".</summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// <summary>Semantic judge model for this phase. Falls back to methodology ModelDefaults.Judge, then "gpt-4o-mini".</summary>
    [JsonPropertyName("judge_model")]
    public string? JudgeModel { get; init; }

    /// <summary>Extracts artifact type from output_schema (e.g. "requirements/v1" → "requirements").</summary>
    [JsonIgnore]
    public string ArtifactType
    {
        get
        {
            var parts = OutputSchema.Split('/');
            if (parts.Length < 2)
                throw new InvalidOperationException($"Invalid OutputSchema format '{OutputSchema}': expected 'type/vN'.");
            return parts[0];
        }
    }

    /// <summary>Extracts schema version from output_schema (e.g. "requirements/v1" → "1").</summary>
    [JsonIgnore]
    public string SchemaVersion
    {
        get
        {
            var parts = OutputSchema.Split('/');
            if (parts.Length < 2)
                throw new InvalidOperationException($"Invalid OutputSchema format '{OutputSchema}': expected 'type/vN'.");
            return parts[1].TrimStart('v');
        }
    }
}
