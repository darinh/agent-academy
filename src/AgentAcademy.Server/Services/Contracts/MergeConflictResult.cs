namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Result of a conflict detection check between branches.
/// </summary>
public record MergeConflictResult(bool HasConflicts, IReadOnlyList<string> ConflictingFiles);
