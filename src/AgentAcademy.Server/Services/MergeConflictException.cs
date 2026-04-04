namespace AgentAcademy.Server.Services;

/// <summary>
/// Thrown when a git rebase or merge encounters unresolvable conflicts.
/// Contains the list of conflicting files for actionable error reporting.
/// </summary>
public sealed class MergeConflictException : InvalidOperationException
{
    public string Branch { get; }
    public IReadOnlyList<string> ConflictingFiles { get; }

    public MergeConflictException(string branch, IReadOnlyList<string> conflictingFiles)
        : base(BuildMessage(branch, conflictingFiles))
    {
        Branch = branch;
        ConflictingFiles = conflictingFiles;
    }

    private static string BuildMessage(string branch, IReadOnlyList<string> files)
    {
        var fileList = files.Count > 0
            ? string.Join(", ", files)
            : "(unable to determine conflicting files)";
        return $"Merge conflict on branch '{branch}': {fileList}";
    }
}
