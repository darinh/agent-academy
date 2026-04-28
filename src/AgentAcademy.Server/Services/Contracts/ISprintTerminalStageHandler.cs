namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Action taken by <see cref="ISprintTerminalStageHandler.AdvanceIfReadyAsync"/>
/// for a single per-round invocation. Used by callers (the round runner) to
/// decide whether to skip the subsequent self-drive decision and by tests for
/// behavioural assertions. See
/// <c>specs/100-product-vision/sprint-terminal-stage-handler-design.md §4.1</c>.
/// </summary>
public enum TerminalStageAction
{
    /// <summary>
    /// No action taken — the sprint is not in a state that permits any
    /// terminal-stage transition this round (covers all "wait" paths:
    /// implementation in progress, self-eval in flight without watchdog
    /// trigger, sprint blocked, awaiting sign-off, etc.).
    /// </summary>
    NoOp,

    /// <summary>
    /// Driver opened the self-evaluation window: flipped
    /// <c>SelfEvaluationInFlight=true</c>, stamped <c>SelfEvalStartedAt</c>,
    /// and woke the workspace rooms. Agents will pick up the self-eval
    /// preamble on the next round.
    /// </summary>
    StartedSelfEval,

    /// <summary>
    /// Driver advanced Implementation → FinalSynthesis after a stored
    /// AllPass <c>SelfEvaluationReport</c>. Stage advanced and workspace
    /// rooms woken.
    /// </summary>
    AdvancedToFinalSynthesis,

    /// <summary>
    /// Driver invoked <c>AdvanceStageAsync</c> in a sign-off-configured
    /// environment (operator set <c>SignOffRequiredStages</c> to include
    /// Implementation). The sprint is now <c>AwaitingSignOff</c>; further
    /// driver invocations land in <see cref="NoOp"/> until the human
    /// approves.
    /// </summary>
    RequestedSignOff,

    /// <summary>
    /// Sprint is at FinalSynthesis without a stored <c>SprintReport</c>;
    /// driver re-woke the orchestrator so the FinalSynthesis preamble drives
    /// agents to produce the report. No DB state change.
    /// </summary>
    SteeredToFinalSynthesis,

    /// <summary>
    /// Driver completed the sprint: <c>Status=Completed</c>, the
    /// <c>SprintReport</c> artifact gate passed, the existing
    /// <c>SprintCompleted</c> activity event fired. No wake — sprint is
    /// terminal.
    /// </summary>
    CompletedSprint,

    /// <summary>
    /// A ceremony step failed structurally (non-stale exception) or a stall
    /// watchdog tripped. <c>MarkSprintBlockedAsync</c> was called with a
    /// <c>"Terminal-stage ceremony failed: …"</c> reason; the existing
    /// <c>SprintBlocked → NeedsInput</c> notification fires.
    /// </summary>
    Blocked,
}

/// <summary>
/// After every agent round, classifies the captured sprint's terminal-stage
/// state and fires at most one ceremony-chain transition per invocation.
/// Stateless; idempotent on retry; fails open (logs and returns on any
/// internal error).
///
/// Wired into <see cref="ConversationRoundRunner"/> immediately after
/// <c>IncrementRoundCountersAsync</c> and BEFORE
/// <c>InvokeSelfDriveDecisionAsync</c> — the driver's job is to short-circuit
/// further self-drive work when the team is actually done, so it must run
/// first. When the driver returns any value other than
/// <see cref="TerminalStageAction.NoOp"/>, the caller MUST skip the
/// subsequent self-drive decision (see design §4.4).
///
/// See <c>specs/100-product-vision/sprint-terminal-stage-handler-design.md</c>.
/// </summary>
public interface ISprintTerminalStageHandler
{
    /// <summary>
    /// Returns the action taken (or <see cref="TerminalStageAction.NoOp"/>)
    /// for observability/test assertions. <b>Never throws</b> — internal
    /// exceptions are logged and converted to <see cref="TerminalStageAction.NoOp"/>.
    /// </summary>
    Task<TerminalStageAction> AdvanceIfReadyAsync(string sprintId, CancellationToken ct = default);
}
