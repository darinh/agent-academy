using System.Text.Json.Serialization;

namespace AgentAcademy.Forge.Models;

/// <summary>
/// On-disk run.json trace contract. This is the consumer-facing run summary.
/// </summary>
public sealed record RunTrace
{
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("taskId")]
    public required string TaskId { get; init; }

    [JsonPropertyName("methodologyVersion")]
    public required string MethodologyVersion { get; init; }

    [JsonPropertyName("startedAt")]
    public required DateTime StartedAt { get; init; }

    [JsonPropertyName("endedAt")]
    public DateTime? EndedAt { get; init; }

    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("controlOutcome")]
    public string? ControlOutcome { get; init; }

    [JsonPropertyName("pipelineTokens")]
    public required TokenCount PipelineTokens { get; init; }

    [JsonPropertyName("controlTokens")]
    public required TokenCount ControlTokens { get; init; }

    [JsonPropertyName("costRatio")]
    public double? CostRatio { get; init; }

    /// <summary>
    /// Map keyed by phaseId. Omit key if phase produced no terminal artifact.
    /// Values are "sha256:..." prefixed hashes.
    /// </summary>
    [JsonPropertyName("finalArtifactHashes")]
    public required Dictionary<string, string> FinalArtifactHashes { get; init; }
}

/// <summary>Token count for input/output.</summary>
public sealed record TokenCount
{
    [JsonPropertyName("in")]
    public int In { get; init; }

    [JsonPropertyName("out")]
    public int Out { get; init; }
}
