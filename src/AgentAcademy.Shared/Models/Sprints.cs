using System.Text.Json.Serialization;

namespace AgentAcademy.Shared.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SprintStage
{
    Intake,
    Planning,
    Discussion,
    Validation,
    Implementation,
    FinalSynthesis
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SprintStatus
{
    Active,
    Completed,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArtifactType
{
    RequirementsDocument,
    SprintPlan,
    ValidationReport,
    SprintReport,
    OverflowRequirements
}

public record SprintSnapshot(
    string Id,
    int Number,
    SprintStatus Status,
    SprintStage CurrentStage,
    string? OverflowFromSprintId,
    bool AwaitingSignOff,
    SprintStage? PendingStage,
    DateTime CreatedAt,
    DateTime? CompletedAt);

public record SprintArtifact(
    int Id,
    string SprintId,
    SprintStage Stage,
    ArtifactType Type,
    string Content,
    string? CreatedByAgentId,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public record RequirementsDocument(
    string Title,
    string Description,
    List<string> InScope,
    List<string> OutOfScope,
    List<string> AcceptanceCriteria);

public record SprintPlanDocument(
    string Summary,
    List<SprintPlanPhase> Phases,
    List<string>? OverflowRequirements);

public record SprintPlanPhase(
    string Name,
    string Description,
    List<string> Deliverables);

public record ValidationReport(
    string Verdict,
    List<string> Findings,
    List<string>? RequiredChanges);

public record SprintReport(
    string Summary,
    List<string> Delivered,
    List<string> Learnings,
    List<string>? OverflowRequirements);
