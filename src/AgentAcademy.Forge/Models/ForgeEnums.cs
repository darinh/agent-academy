namespace AgentAcademy.Forge.Models;

/// <summary>Run lifecycle status.</summary>
public enum RunStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Aborted
}

/// <summary>PhaseRun lifecycle status.</summary>
public enum PhaseRunStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped
}

/// <summary>Attempt lifecycle status.</summary>
public enum AttemptStatus
{
    Pending,
    Prompting,
    Generating,
    Validating,
    Accepted,
    Rejected,
    Errored
}

/// <summary>Validator pipeline phase.</summary>
public enum ValidatorPhase
{
    Structural,
    Semantic,
    CrossArtifact
}

/// <summary>Validator finding severity.</summary>
public enum ValidatorSeverity
{
    Error,
    Warning,
    Info
}

/// <summary>
/// Schema lifecycle status. Controls whether new pipeline runs may
/// produce artifacts with this schema version.
/// </summary>
public enum SchemaStatus
{
    /// <summary>Can be referenced by new methodologies; new artifacts produced.</summary>
    Active,

    /// <summary>Existing artifacts remain valid. New methodologies should migrate away.
    /// Pipeline logs a warning at run start.</summary>
    Deprecated,

    /// <summary>Still in registry for historical artifact validation.
    /// New runs refuse to produce artifacts with this schema; resume of
    /// historical runs is unaffected.</summary>
    Retired
}
