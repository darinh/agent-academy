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
    SpecChangeProposal,
    DirectMessage
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
    InReview,
    ChangesRequested,
    Approved,
    Merging,
    Completed,
    Cancelled
}

/// <summary>
/// Categorizes a task by its nature (feature work, bug fix, etc.).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskType
{
    Feature,
    Bug,
    Chore,
    Spike
}

/// <summary>
/// Type of comment attached to a task.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskCommentType
{
    Comment,
    Finding,
    Evidence,
    Blocker
}

/// <summary>
/// Estimated effort size for a task.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskSize
{
    XS,
    S,
    M,
    L,
    XL
}

/// <summary>
/// Status of a pull request in the review pipeline.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PullRequestStatus
{
    Open,
    ReviewRequested,
    ChangesRequested,
    Approved,
    Merged,
    Closed
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
    ToolIntercepted,
    CommandExecuted,
    CommandDenied,
    CommandFailed,
    TaskClaimed,
    TaskReleased,
    TaskApproved,
    TaskRejected,
    TaskChangesRequested,
    TaskStatusUpdated,
    TaskCommentAdded,
    TaskPrStatusChanged,
    AgentRecalled,
    RoomRenamed,
    DirectMessageSent,
    SpecTaskLinked,
    EvidenceRecorded,
    GateChecked,
    SprintStarted,
    SprintStageAdvanced,
    SprintArtifactStored,
    SprintCompleted
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

/// <summary>
/// Outcome status for a command envelope.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CommandStatus
{
    Success,
    Error,
    Denied
}

/// <summary>
/// Describes the relationship between a task and a spec section.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpecLinkType
{
    Implements,
    Modifies,
    Fixes,
    References
}

/// <summary>
/// Phase of a verification check in the evidence ledger.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvidencePhase
{
    Baseline,
    After,
    Review
}
