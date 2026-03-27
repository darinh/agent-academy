using System.Text.Json.Serialization;

namespace AgentAcademy.Shared.Models;

/// <summary>
/// Phases of a multi-agent collaboration session.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CollaborationPhase
{
    Intake,
    Planning,
    Discussion,
    Validation,
    Implementation,
    FinalSynthesis
}

/// <summary>
/// Indicates an agent's current readiness to accept work.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentAvailability
{
    Ready,
    Preferred,
    Active,
    Busy,
    Offline
}

/// <summary>
/// Priority level for message delivery routing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryPriority
{
    Low,
    Normal,
    High,
    Urgent
}

/// <summary>
/// Semantic type of a chat message, used for filtering and routing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageKind
{
    System,
    TaskAssignment,
    Coordination,
    Plan,
    Status,
    Review,
    Validation,
    Decision,
    Question,
    Response,
    SpecChangeProposal
}

/// <summary>
/// Identifies the origin category of a message sender.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageSenderKind
{
    System,
    Agent,
    User
}

/// <summary>
/// Lifecycle states for a task within a collaboration room.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskStatus
{
    Queued,
    Active,
    Blocked,
    AwaitingValidation,
    Completed,
    Cancelled
}

/// <summary>
/// Progress state for a workstream (validation or implementation).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkstreamStatus
{
    NotStarted,
    Ready,
    InProgress,
    Blocked,
    Completed
}

/// <summary>
/// Lifecycle state of a collaboration room.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RoomStatus
{
    Idle,
    Active,
    AttentionRequired,
    Completed,
    Archived
}

/// <summary>
/// Categorizes activity events emitted during collaboration.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActivityEventType
{
    AgentLoaded,
    AgentThinking,
    AgentFinished,
    RoomCreated,
    RoomClosed,
    TaskCreated,
    PhaseChanged,
    MessagePosted,
    MessageSent,
    PresenceUpdated,
    RoomStatusChanged,
    ArtifactEvaluated,
    QualityGateChecked,
    IterationRetried,
    CheckpointCreated,
    AgentErrorOccurred,
    AgentWarningOccurred,
    SubagentStarted,
    SubagentCompleted,
    SubagentFailed,
    AgentPlanChanged,
    AgentSnapshotRewound,
    ToolIntercepted
}

/// <summary>
/// Severity level for activity events.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActivitySeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// Physical state of an agent within the workspace.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentState
{
    InRoom,
    Working,
    Presenting,
    Idle
}

/// <summary>
/// Status of an individual task item assigned to an agent.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskItemStatus
{
    Pending,
    Active,
    Done,
    Rejected
}
