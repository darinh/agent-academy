namespace AgentAcademy.Forge.Models;

/// <summary>
/// Closed drift taxonomy for intent fidelity checking.
/// These are the only valid codes — adding a 6th requires a methodology version bump.
/// </summary>
public enum DriftCode
{
    /// <summary>A constraint from source intent was dropped during pipeline execution.</summary>
    OMITTED_CONSTRAINT,

    /// <summary>A requirement appeared in output that has no basis in source intent.</summary>
    INVENTED_REQUIREMENT,

    /// <summary>Scope was expanded beyond what source intent specified.</summary>
    SCOPE_BROADENED,

    /// <summary>Scope was narrowed, omitting capabilities from source intent.</summary>
    SCOPE_NARROWED,

    /// <summary>An explicit constraint was weakened (e.g., "must" became "should").</summary>
    CONSTRAINT_WEAKENED
}

/// <summary>
/// Drift code severity classification.
/// </summary>
public static class DriftSeverity
{
    /// <summary>Blocking drift codes — these indicate the output may be incorrect.</summary>
    public static readonly IReadOnlySet<DriftCode> Blocking = new HashSet<DriftCode>
    {
        DriftCode.OMITTED_CONSTRAINT,
        DriftCode.CONSTRAINT_WEAKENED
    };

    /// <summary>Advisory drift codes — these indicate the output may differ from intent but isn't necessarily wrong.</summary>
    public static readonly IReadOnlySet<DriftCode> Advisory = new HashSet<DriftCode>
    {
        DriftCode.INVENTED_REQUIREMENT,
        DriftCode.SCOPE_BROADENED,
        DriftCode.SCOPE_NARROWED
    };

    /// <summary>Returns true if the given drift code is blocking.</summary>
    public static bool IsBlocking(DriftCode code) => Blocking.Contains(code);
}
