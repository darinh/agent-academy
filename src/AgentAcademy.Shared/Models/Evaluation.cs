using System.Text.Json;

namespace AgentAcademy.Shared.Models;

/// <summary>
/// Result of evaluating a single artifact file against quality criteria.
/// </summary>
public record EvaluationResult(
    string FilePath,
    double Score,
    bool Exists,
    bool NonEmpty,
    bool SyntaxValid,
    bool Complete,
    List<string> Issues
);

/// <summary>
/// Record of an artifact produced by an agent during collaboration.
/// </summary>
public record ArtifactRecord(
    string AgentId,
    string RoomId,
    string FilePath,
    string Operation,
    DateTime Timestamp
);

/// <summary>
/// A single timestamped metrics data point captured during collaboration.
/// </summary>
public record MetricsEntry(
    DateTime Timestamp,
    string Type,
    int Round,
    string Phase,
    string Agent,
    Dictionary<string, JsonElement> Data
);

/// <summary>
/// Aggregated metrics summary for a collaboration session.
/// </summary>
public record MetricsSummary(
    int TotalRounds,
    int TotalArtifacts,
    int PhaseTransitions,
    double AverageScore,
    List<MetricsEntry> Entries
);
