namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for a sprint — a numbered iteration of the build cycle.
/// Maps to the "sprints" table.
/// </summary>
public class SprintEntity
{
    public string Id { get; set; } = string.Empty;
    public int Number { get; set; }
    public string WorkspacePath { get; set; } = string.Empty;
    public string Status { get; set; } = "Active"; // Active | Completed | Cancelled
    public string CurrentStage { get; set; } = "Intake"; // Intake | Planning | Discussion | Validation | Implementation | FinalSynthesis
    public string? OverflowFromSprintId { get; set; }
    public bool AwaitingSignOff { get; set; }
    public string? PendingStage { get; set; }
    public DateTime? SignOffRequestedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    // Blocked signal (P1.4 narrow scope). When BlockedAt is non-null, the sprint
    // is still Status="Active" — agents/orchestrator are paused waiting on a
    // human or external resolution. Cleared on UnblockSprintAsync.
    public DateTime? BlockedAt { get; set; }
    public string? BlockReason { get; set; }

    // Self-drive accounting (P1.2, p1-2-self-drive-design.md §3.1). All counters
    // increment by ConversationRoundRunner after each trigger run; RoundsThisStage
    // and SelfDriveContinuations reset on stage transition. Defaults are zero/null
    // so existing rows backfill cleanly with no schema migration data step.
    public int RoundsThisSprint { get; set; }
    public int RoundsThisStage { get; set; }
    public int SelfDriveContinuations { get; set; }
    public DateTime? LastRoundCompletedAt { get; set; }

    // Per-sprint override of Orchestrator:SelfDrive:MaxRoundsPerSprint
    // (p1-2-self-drive-design.md §6). Null means use the configured default.
    public int? MaxRoundsOverride { get; set; }

    // Self-evaluation accounting (P1.4 full scope, foundation).
    // See specs/100-product-vision/p1-4-self-evaluation-design.md §3.1.
    // All four are reset on stage transitions OUT of Implementation
    // (set by the verdict path added in the next P1.4 PR; no behavioural
    // wiring lives in this foundation PR).

    /// <summary>
    /// True between RUN_SELF_EVAL and either ADVANCE_STAGE (on AllPass)
    /// or a new attempt opening (on AnyFail/Unverified). When true, the
    /// orchestrator injects the self-evaluation preamble instead of the
    /// normal Implementation preamble.
    /// </summary>
    public bool SelfEvaluationInFlight { get; set; }

    /// <summary>
    /// Number of self-evaluation reports submitted at Implementation for
    /// this sprint. Capped by Orchestrator:SelfEval:MaxSelfEvalAttempts;
    /// hitting the cap auto-blocks the sprint (added in the next P1.4 PR).
    /// </summary>
    public int SelfEvalAttempts { get; set; }

    /// <summary>Wall-clock timestamp of the most recent self-evaluation submission.</summary>
    public DateTime? LastSelfEvalAt { get; set; }

    /// <summary>
    /// Cached <c>OverallVerdict</c> of the most recent submission
    /// (<c>"AllPass"</c> | <c>"AnyFail"</c> | <c>"Unverified"</c>).
    /// Stored as string for DB readability; compared as
    /// <see cref="AgentAcademy.Shared.Models.SelfEvaluationOverallVerdict"/>
    /// at decision-time.
    /// </summary>
    public string? LastSelfEvalVerdict { get; set; }

    // Terminal-stage ceremony tracking
    // (specs/100-product-vision/sprint-terminal-stage-handler-design.md §6.2).
    // Both columns are additive 🟢 — defaults are null; existing rows backfill
    // cleanly with no schema migration data step. The §6.6 SQL backfill in the
    // migration repairs historical Completed sprints that have a non-terminal
    // currentStage (Sprint #11 documented in §1).

    /// <summary>
    /// Wall-clock timestamp of FIRST entry to FinalSynthesis. Set-once,
    /// never cleared (preserved across completion / cancellation as an audit
    /// signal: "how long did sprint X spend in FinalSynthesis"). Set by
    /// <see cref="AgentAcademy.Server.Services.SprintStageService.AdvanceStageAsync"/>
    /// when transitioning into FinalSynthesis, and by
    /// <see cref="AgentAcademy.Server.Services.SprintService.CompleteSprintAsync"/>
    /// when <c>force=true</c> leaps from a non-terminal stage. Used by the
    /// terminal-stage driver's FinalSynthesis stall watchdog.
    /// </summary>
    public DateTime? FinalSynthesisEnteredAt { get; set; }

    /// <summary>
    /// Wall-clock timestamp of the most recent driver-initiated
    /// <c>StartedSelfEval</c> action. Set by the terminal-stage driver when
    /// transitioning <c>ReadyForSelfEval → SelfEvalInFlight</c>; cleared by
    /// the P1.4 verdict path in
    /// <see cref="AgentAcademy.Server.Services.SprintArtifactService"/> ONLY
    /// when <c>OverallVerdict=AllPass</c> (chain has progressed). On
    /// AnyFail/Unverified verdicts the value is left set, then RE-stamped on
    /// the next <c>StartedSelfEval</c> after the team re-attempts (giving
    /// each attempt a fresh stall window). Used by the self-eval stall
    /// watchdog alongside <see cref="LastSelfEvalAt"/>.
    /// </summary>
    public DateTime? SelfEvalStartedAt { get; set; }

    // Navigation
    public SprintEntity? OverflowFromSprint { get; set; }
}
