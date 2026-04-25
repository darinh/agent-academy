namespace AgentAcademy.Server.Services;

/// <summary>
/// Configuration for sprint stage advancement gates.
/// Bound to the <c>Sprint:Stage</c> section in <c>appsettings.json</c>.
/// </summary>
/// <remarks>
/// Defaults bias the system toward <b>full autonomy</b>. Operators opt
/// into manual gates per-stage by populating <see cref="SignOffRequiredStages"/>.
/// This preserves the original Intake/Planning sign-off pattern as a
/// configuration choice, not a hardcoded behaviour.
/// </remarks>
public sealed class SprintStageOptions
{
    public const string SectionName = "Sprint:Stage";

    /// <summary>
    /// Stages that require a human sign-off before advancing. When an agent
    /// triggers <c>ADVANCE_STAGE</c> from a stage in this set, the sprint
    /// enters <c>AwaitingSignOff</c> until <c>POST /api/sprints/{id}/approve-advance</c>
    /// or <c>/reject-advance</c> is called (or the sign-off timeout fires).
    /// <para>
    /// <b>Default: empty</b> — every stage advances automatically once its
    /// artifact and prerequisite gates are satisfied. Add stage names
    /// (e.g. <c>["Intake", "Planning"]</c>) in <c>appsettings.json</c> to
    /// re-introduce manual approval points.
    /// </para>
    /// </summary>
    public string[] SignOffRequiredStages { get; set; } = [];
}
