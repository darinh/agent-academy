using System.Text.Json.Serialization;

namespace AgentAcademy.Forge.Models;

/// <summary>
/// Review summary trace contract (review-summary.json).
/// Comparison metadata only — no artifact payloads.
/// </summary>
public sealed record ReviewSummaryTrace
{
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("taskId")]
    public required string TaskId { get; init; }

    [JsonPropertyName("pipelineOutcome")]
    public required string PipelineOutcome { get; init; }

    [JsonPropertyName("controlOutcome")]
    public string? ControlOutcome { get; init; }

    [JsonPropertyName("costRatio")]
    public double? CostRatio { get; init; }

    [JsonPropertyName("blindReviewInputs")]
    public Dictionary<string, string>? BlindReviewInputs { get; init; }

    [JsonPropertyName("sealedLabelMap")]
    public string? SealedLabelMap { get; init; }
}

/// <summary>
/// Task brief stored as task.json in each run directory.
/// </summary>
public sealed record TaskBrief
{
    [JsonPropertyName("taskId")]
    public required string TaskId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }
}
