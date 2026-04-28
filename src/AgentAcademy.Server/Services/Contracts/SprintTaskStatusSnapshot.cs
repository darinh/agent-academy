namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Aggregate count of a sprint's task statuses, computed in a single DB
/// round-trip by <see cref="ITaskQueryService.GetSprintTaskStatusSnapshotAsync"/>.
/// Used by the terminal-stage driver to classify <c>ReadyForSelfEval</c>
/// without enumerating every task. See
/// <c>specs/100-product-vision/sprint-terminal-stage-handler-design.md §6.3</c>.
/// </summary>
/// <param name="TotalCount">Total number of tasks linked to the sprint.</param>
/// <param name="NonCancelledCount">
/// Tasks with status not equal to <c>Cancelled</c>. Zero non-cancelled
/// tasks means the sprint has nothing meaningful to evaluate; the driver
/// treats it as <c>ImplementationInProgress</c> (NoOp) so the operator
/// must explicitly <c>force=true</c> complete.
/// </param>
/// <param name="AllTerminal">
/// True when every task is in <c>Completed</c> or <c>Cancelled</c> status —
/// the set enforced by <c>RoomLifecycleService.TerminalTaskStatuses</c>.
/// Note: <c>Approved</c> and <c>Merging</c> are intentionally non-terminal
/// to align with <c>SprintStageService.CheckImplementationPrerequisitesAsync</c>.
/// </param>
public sealed record SprintTaskStatusSnapshot(
    int TotalCount,
    int NonCancelledCount,
    bool AllTerminal);
