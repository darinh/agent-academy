namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Tracks an active worktree managed by <see cref="WorktreeService"/>.
/// </summary>
public record WorktreeInfo(string Branch, string Path, DateTimeOffset CreatedAt);

/// <summary>
/// Represents a worktree entry as reported by <c>git worktree list --porcelain</c>.
/// </summary>
public record GitWorktreeEntry(string Path, string? Head, string? Branch, bool Bare);

/// <summary>
/// Git-level status for a single worktree: dirty files, diff stats, and last commit.
/// </summary>
public record WorktreeGitStatus(
    bool StatusAvailable,
    string? Error,
    int TotalDirtyFiles,
    List<string> DirtyFilesPreview,
    int FilesChanged,
    int Insertions,
    int Deletions,
    string? LastCommitSha,
    string? LastCommitMessage,
    string? LastCommitAuthor,
    DateTimeOffset? LastCommitDate)
{
    public static WorktreeGitStatus Unavailable(string error) => new(
        StatusAvailable: false,
        Error: error,
        TotalDirtyFiles: 0,
        DirtyFilesPreview: [],
        FilesChanged: 0,
        Insertions: 0,
        Deletions: 0,
        LastCommitSha: null,
        LastCommitMessage: null,
        LastCommitAuthor: null,
        LastCommitDate: null
    );
}
