using System.Diagnostics;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Shared validation for the per-session <c>scopeRoot</c> that
/// <see cref="CodeWriteToolWrapper"/> and <see cref="CodeReadToolWrapper"/>
/// both honour. Encapsulates the §4.6 rules from the P1.9 blocker B design:
/// canonicalize, require existence, resolve top-level symlink, and verify the
/// directory is a registered git worktree (catches an upstream bug threading
/// an arbitrary path).
/// </summary>
internal static class ScopeRootValidator
{
    /// <summary>
    /// Validates and canonicalizes <paramref name="scopeRoot"/>. Returns null
    /// when the caller passed null (main-room behaviour). Throws
    /// <see cref="ArgumentException"/> when the directory does not exist or is
    /// not a git worktree.
    /// </summary>
    /// <remarks>
    /// The stronger "must be a worktree of the develop checkout" check is
    /// enforced in production via the wiring in <c>CopilotExecutor</c> →
    /// <c>AgentToolRegistry</c>, which only ever passes a worktree path that
    /// <c>WorktreeService</c> created from the develop repo. Verifying
    /// "is-a-worktree" here catches the categorically dangerous failure mode
    /// (upstream bug threading an arbitrary filesystem path through and
    /// silently widening the agent's blast radius), without breaking unit
    /// tests that set up isolated temp repos.
    /// </remarks>
    /// <param name="scopeRoot">Caller-supplied scope root (may be null).</param>
    /// <param name="paramName">Parameter name for the thrown exception.</param>
    public static string? ValidateAndCanonicalize(string? scopeRoot, string paramName)
    {
        if (scopeRoot is null) return null;
        if (string.IsNullOrWhiteSpace(scopeRoot))
            throw new ArgumentException($"{paramName} must not be empty when provided.", paramName);

        var canonical = Path.GetFullPath(scopeRoot);
        if (!Directory.Exists(canonical))
            throw new ArgumentException(
                $"{paramName} does not exist: {canonical}. Refusing to silently fall back to the develop checkout.",
                paramName);

        // Resolve any symlink in the scope root itself so security checks
        // compare resolved-target ↔ resolved-root once at construction.
        try
        {
            var info = new DirectoryInfo(canonical);
            var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is not null)
                canonical = Path.GetFullPath(resolved.FullName);
        }
        catch
        {
            // Edge filesystems may throw — canonical without symlink resolution
            // is still a safe upper bound for prefix checks.
        }

        // is-a-git-worktree check.
        try
        {
            var common = RunGitCommonDir(canonical);
            if (common is null)
                throw new ArgumentException(
                    $"{paramName} is not a git worktree: {canonical}.",
                    paramName);
        }
        catch (ArgumentException) { throw; }
        catch
        {
            // git binary missing or unexpected failure — surface canonical path
            // and let runtime checks catch downstream issues.
        }

        return canonical;
    }

    private static string? RunGitCommonDir(string workingDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workingDir,
            };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("--git-common-dir");
            using var p = Process.Start(psi);
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            if (p.ExitCode != 0 || string.IsNullOrEmpty(stdout)) return null;
            var resolved = Path.IsPathRooted(stdout) ? stdout : Path.GetFullPath(Path.Combine(workingDir, stdout));
            return Path.GetFullPath(resolved);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="canonicalScopeRoot"/> is a linked
    /// git worktree (i.e., NOT the main checkout). Used by P1.9 blocker D
    /// enforcement: code-write tools refuse to operate against the main develop
    /// checkout because parallel-task work would contaminate it.
    /// </summary>
    /// <remarks>
    /// Detection compares <c>git rev-parse --git-dir</c> with
    /// <c>git rev-parse --git-common-dir</c>: in the main checkout the two
    /// resolve to the same path; in a linked worktree, <c>--git-dir</c> points
    /// inside <c>--git-common-dir/worktrees/{name}/</c>. This is the
    /// authoritative check git itself uses to distinguish main vs. linked
    /// worktrees, and is robust to repos that happen to live under a directory
    /// containing the substring <c>worktrees</c> (a substring-only check would
    /// misclassify <c>/home/x/worktrees/myrepo/.git</c> as a linked worktree).
    /// When git is unavailable or rev-parse fails, returns <c>null</c> so callers
    /// can choose their fallback (typically: fail-closed under
    /// <c>requireWorktree</c> to avoid silently re-introducing the contamination).
    /// </remarks>
    public static bool? IsLinkedWorktree(string canonicalScopeRoot)
    {
        if (string.IsNullOrWhiteSpace(canonicalScopeRoot)) return null;

        var gitDir = RunGitDir(canonicalScopeRoot);
        if (gitDir is null) return null;

        var commonDir = RunGitCommonDir(canonicalScopeRoot);
        if (commonDir is null) return null;

        // Strip trailing separators so the comparison treats `.../X` and
        // `.../X/` as identical regardless of how the two `git rev-parse`
        // invocations chose to render them.
        var normalizedDir = gitDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCommon = commonDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Main checkout: --git-dir == --git-common-dir (same physical .git).
        // Linked worktree: --git-dir is the per-worktree pointer dir
        // (.git/worktrees/{name}); --git-common-dir is the shared .git itself.
        return !string.Equals(normalizedDir, normalizedCommon, StringComparison.Ordinal);
    }

    private static string? RunGitDir(string workingDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workingDir,
            };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("--git-dir");
            using var p = Process.Start(psi);
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            if (p.ExitCode != 0 || string.IsNullOrEmpty(stdout)) return null;
            var resolved = Path.IsPathRooted(stdout) ? stdout : Path.GetFullPath(Path.Combine(workingDir, stdout));
            return Path.GetFullPath(resolved);
        }
        catch
        {
            return null;
        }
    }
}
