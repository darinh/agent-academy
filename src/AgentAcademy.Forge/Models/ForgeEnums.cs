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
