namespace AgentAcademy.Forge.Execution;

/// <summary>
/// Progress event emitted by <see cref="PipelineRunner"/> during pipeline execution.
/// Consumers can subscribe via <see cref="IProgress{ForgeProgressEvent}"/> to receive
/// real-time updates without coupling the Forge engine to any transport layer.
/// </summary>
public sealed record ForgeProgressEvent
{
    public required string RunId { get; init; }
    public required ForgeProgressKind Kind { get; init; }
    public string? PhaseId { get; init; }
    public string? Message { get; init; }
    public int? Wave { get; init; }
    public int? Attempt { get; init; }
    public bool? Passed { get; init; }
    public string? Outcome { get; init; }
}

/// <summary>
/// Discriminator for progress events emitted during pipeline execution.
/// </summary>
public enum ForgeProgressKind
{
    RunStarted,
    WaveStarted,
    PhaseStarted,
    PhaseCompleted,
    PhaseFailed,
    FidelityCompleted,
    ControlCompleted,
    RunCompleted,
    RunFailed
}
