using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Wrapper that captures agent identity for write-capable tool functions.
/// Enforces path restrictions: files must be within <see cref="AllowedRoot"/>
/// (e.g. <c>src/</c> for code-write, <c>specs/</c> for spec-write) and cannot
/// modify the configured protected infrastructure files.
/// </summary>
internal sealed class CodeWriteToolWrapper
{
    // Files that agents with code-write access must never modify (core infrastructure).
    internal static readonly IReadOnlyList<string> CodeWriteProtectedPaths =
    [
        "Services/AgentToolFunctions.cs",
        "Services/AgentToolRegistry.cs",
        "Services/IAgentToolRegistry.cs",
        "Services/CopilotExecutor.cs",
        "Services/AgentOrchestrator.cs",
        "Services/GitService.cs",
        "Program.cs",
    ];

    // Spec-write has no protected files inside specs/ — Thucydides owns the whole spec corpus.
    internal static readonly IReadOnlyList<string> SpecWriteProtectedPaths = Array.Empty<string>();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly string _agentId;
    private readonly string _agentName;
    private readonly AgentGitIdentity? _gitIdentity;
    private readonly string? _roomId;
    private readonly IReadOnlyList<string> _allowedRoots;
    private readonly IReadOnlyList<string> _protectedPaths;

    /// <summary>
    /// When true, the wrapper refuses <c>write_file</c> and <c>commit_changes</c>
    /// unless <see cref="_scopeRoot"/> resolves to a linked git worktree. This
    /// closes P1.9 blocker D: agents that get this tool group enabled in the
    /// main room must call <c>CLAIM_TASK</c> first to be routed into a per-task
    /// worktree before they can write — preventing parallel-task work from
    /// landing on the develop checkout. Set true by the code-write factory;
    /// false by spec-write (which legitimately edits the develop checkout
    /// because spec authors don't claim tasks).
    /// </summary>
    private readonly bool _requireWorktree;

    /// <summary>
    /// Cached classification of <see cref="_scopeRoot"/>: <c>true</c> if it is
    /// a linked git worktree, <c>false</c> if it is the main checkout (or
    /// <see cref="_scopeRoot"/> is null), <c>null</c> when git was unavailable
    /// to classify it. Computed once at construction.
    /// </summary>
    private readonly bool? _isLinkedWorktree;

    /// <summary>
    /// When set, all path resolution and git operations target this directory
    /// instead of <see cref="AgentToolFunctions.FindProjectRoot"/>. Set per-session
    /// for breakouts that own a worktree; null for main-room agents that operate
    /// against the develop checkout. Stored as a canonical resolved path.
    /// </summary>
    private readonly string? _scopeRoot;

    /// <summary>
    /// The first configured root. Retained for call-sites (and tests) that treat the wrapper
    /// as having a single root. For multi-root configurations prefer <see cref="AllowedRoots"/>.
    /// Always stored without a trailing separator.
    /// </summary>
    internal string AllowedRoot => _allowedRoots[0];

    /// <summary>
    /// All root directories (relative to the project root) that writes are permitted under.
    /// Stored without trailing separators, normalized to forward slashes. Checks accept a
    /// write if the target path lies under any one of these roots.
    /// </summary>
    internal IReadOnlyList<string> AllowedRoots => _allowedRoots;

    private const int MaxContentLength = 100_000; // 100 KB

    internal CodeWriteToolWrapper(
        IServiceScopeFactory scopeFactory, ILogger logger,
        string agentId, string agentName, AgentGitIdentity? gitIdentity = null, string? roomId = null,
        string? scopeRoot = null, bool requireWorktree = true)
        : this(scopeFactory, logger, agentId, agentName, gitIdentity, roomId, new[] { "src" }, CodeWriteProtectedPaths, scopeRoot, requireWorktree)
    {
    }

    internal CodeWriteToolWrapper(
        IServiceScopeFactory scopeFactory, ILogger logger,
        string agentId, string agentName, AgentGitIdentity? gitIdentity, string? roomId,
        string allowedRoot, IReadOnlyList<string> protectedPaths,
        string? scopeRoot = null, bool requireWorktree = false)
        : this(scopeFactory, logger, agentId, agentName, gitIdentity, roomId,
               ValidateSingleRoot(allowedRoot), protectedPaths, scopeRoot, requireWorktree)
    {
    }

    /// <summary>
    /// Preserves the historical validation contract for the single-root constructor —
    /// the caller sees <c>ArgumentException</c> with <c>ParamName = "allowedRoot"</c> and
    /// the original message — while still delegating storage to the multi-root overload.
    /// </summary>
    private static IReadOnlyList<string> ValidateSingleRoot(string allowedRoot)
    {
        if (string.IsNullOrWhiteSpace(allowedRoot))
            throw new ArgumentException("allowedRoot is required.", nameof(allowedRoot));
        return new[] { allowedRoot };
    }

    internal CodeWriteToolWrapper(
        IServiceScopeFactory scopeFactory, ILogger logger,
        string agentId, string agentName, AgentGitIdentity? gitIdentity, string? roomId,
        IReadOnlyList<string> allowedRoots, IReadOnlyList<string> protectedPaths,
        string? scopeRoot = null, bool requireWorktree = false)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _agentId = agentId;
        _agentName = agentName;
        _gitIdentity = gitIdentity;
        _roomId = roomId;

        if (allowedRoots is null || allowedRoots.Count == 0)
            throw new ArgumentException("At least one allowed root is required.", nameof(allowedRoots));

        var normalized = new List<string>(allowedRoots.Count);
        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                throw new ArgumentException("allowed roots must not contain null, empty, or whitespace entries.", nameof(allowedRoots));
            var canonical = root.Trim().Replace('\\', '/').TrimEnd('/');
            if (canonical.Length == 0)
                throw new ArgumentException("allowed roots must not contain entries that normalize to empty.", nameof(allowedRoots));
            if (!normalized.Contains(canonical, StringComparer.Ordinal))
                normalized.Add(canonical);
        }

        _allowedRoots = normalized;
        _protectedPaths = protectedPaths ?? Array.Empty<string>();
        _scopeRoot = ScopeRootValidator.ValidateAndCanonicalize(scopeRoot, nameof(scopeRoot));
        _requireWorktree = requireWorktree;

        // Classify the scope root once at construction. Null when:
        //   - _scopeRoot is null (no per-session worktree, e.g. spec-write
        //     against the develop checkout), OR
        //   - git couldn't run (binary missing, edge environment) — we can't
        //     know, so we DON'T enforce in that case (fail-open to avoid
        //     bricking environments without git, e.g. some test setups).
        _isLinkedWorktree = _scopeRoot is null
            ? false
            : ScopeRootValidator.IsLinkedWorktree(_scopeRoot);
    }

    /// <summary>
    /// Returns the active scope root: the explicit per-session scope when set,
    /// otherwise the develop checkout discovered by <see cref="AgentToolFunctions.FindProjectRoot"/>.
    /// </summary>
    private string ResolveScopeRoot() => _scopeRoot ?? AgentToolFunctions.FindProjectRoot();

    /// <summary>
    /// Human-readable list of the configured roots with trailing slashes
    /// — used in error messages (e.g. <c>"specs/, docs/"</c>).
    /// </summary>
    private string FormatRootsForDisplay() => string.Join(", ", _allowedRoots.Select(r => r + "/"));

    /// <summary>
    /// Error-message fragment describing the configured scope in grammatically-correct English.
    /// Single root: <c>"the specs/ directory"</c>. Multi-root: <c>"any of: specs/, docs/"</c>.
    /// </summary>
    private string DescribeScope() => _allowedRoots.Count == 1
        ? $"the {_allowedRoots[0]}/ directory"
        : $"any of: {FormatRootsForDisplay()}";

    /// <summary>
    /// Commit-scope variant preserving the historical wording <c>"specs/ scope"</c> for the
    /// single-root case. Multi-root configurations use a readable listing.
    /// </summary>
    private string DescribeCommitScope() => _allowedRoots.Count == 1
        ? $"{_allowedRoots[0]}/ scope"
        : $"scope ({FormatRootsForDisplay()})";

    [Description("Write content to a file in the project. Creates the file if it doesn't exist, overwrites if it does. " +
                 "The file is automatically staged for commit. Paths must be within the allowed root directory (see tool description) and relative to the project root.")]
    internal async Task<string> WriteFileAsync(
        [Description("File path relative to the project root (e.g., src/AgentAcademy.Server/Models/MyModel.cs for code-write, or specs/300-frontend-ui/spec.md for spec-write)")]
        string path,
        [Description("The full content to write to the file")]
        string content)
    {
        _logger.LogInformation("Tool call: write_file by {AgentId} (cwd={ScopeRoot}, path={Path}, length={Length}, allowedRoots={Roots})",
            _agentId, ResolveScopeRoot(), path, content?.Length ?? 0, FormatRootsForDisplay());

        if (string.IsNullOrWhiteSpace(path))
            return "Error: path is required.";
        if (content is null)
            return "Error: content is required (use empty string for empty file).";
        if (content.Length > MaxContentLength)
            return $"Error: Content too large ({content.Length:N0} chars). Maximum is {MaxContentLength:N0} chars.";

        // Reject binary content (null bytes)
        if (content.Contains('\0'))
            return "Error: Binary content detected (null bytes). Only text files are supported.";

        var worktreeRefusal = TryRefuseMainCheckoutWrite("write_file");
        if (worktreeRefusal is not null)
            return worktreeRefusal;

        var projectRoot = ResolveScopeRoot();
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, path));

        // Security: path must be within the project directory
        var rootWithSep = projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSep, StringComparison.Ordinal))
            return "Error: Path traversal denied — file must be within the project directory.";

        // Restrict writes to one of the configured allowed root directories.
        // Use case-sensitive comparison so that on case-sensitive filesystems (Linux CI)
        // a path like "Specs/foo" does not pass the "specs/" check.
        var relativePath = Path.GetRelativePath(projectRoot, fullPath);
        if (!IsUnderAllowedRoot(relativePath))
            return $"Error: Writes are restricted to {DescribeScope()}. Cannot write to: " + relativePath;

        // Defence-in-depth: reject any path whose existing directory chain contains a symlink
        // or reparse point. A lexical prefix check is not sufficient because a symlink like
        // `specs/escape -> ../src` would pass the string check but write outside the allowed root.
        var symlinkViolation = DetectSymlinkEscape(projectRoot, fullPath);
        if (symlinkViolation is not null)
        {
            _logger.LogWarning(
                "Agent {AgentId} attempted to write through symlinked path: {Path} (symlink at {SymlinkPath})",
                _agentId, relativePath, symlinkViolation);
            return $"Error: Path contains a symlink ({symlinkViolation}); writes through symlinks are not allowed.";
        }

        // Block protected infrastructure files
        // Normalize separators to forward slashes for cross-platform comparison
        var normalizedRelative = relativePath.Replace('\\', '/');
        foreach (var protectedPath in _protectedPaths)
        {
            if (normalizedRelative.EndsWith(protectedPath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Agent {AgentId} attempted to write protected file: {Path}",
                    _agentId, relativePath);
                return $"Error: {Path.GetFileName(protectedPath)} is a protected infrastructure file and cannot be modified by agents.";
            }
        }

        try
        {
            // Create parent directories if needed
            var directory = Path.GetDirectoryName(fullPath);
            if (directory is not null && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var isNew = !File.Exists(fullPath);
            await File.WriteAllTextAsync(fullPath, content);

            _logger.LogInformation(
                "Agent {AgentId} ({AgentName}) wrote file: {Path} ({Length} chars, new={IsNew})",
                _agentId, _agentName, relativePath, content.Length, isNew);

            // Stage the file for commit
            var staged = await StageFileAsync(projectRoot, relativePath);

            var action = isNew ? "Created" : "Updated";
            var stageStatus = staged ? "staged for commit" : "written but NOT staged (git add failed)";

            await RecordArtifactAsync(relativePath, action);

            return $"{action}: {relativePath} ({content.Length:N0} chars, {stageStatus})";
        }
        catch (UnauthorizedAccessException)
        {
            return $"Error: Permission denied writing to {relativePath}.";
        }
        catch (IOException ex)
        {
            return $"Error writing file: {ex.Message}";
        }
    }

    /// <summary>
    /// Returns a refusal message if this wrapper requires a per-task worktree
    /// but is operating against the develop checkout (or has no scope set);
    /// returns <c>null</c> when the write is permitted to proceed.
    /// </summary>
    /// <remarks>
    /// Closes P1.9 blocker D. The previous behaviour silently fell through to
    /// the develop checkout when an agent in the main room called write_file,
    /// contaminating develop with parallel-task work product. Now agents must
    /// CLAIM_TASK first — that lazily provisions a worktree, the next-turn
    /// workspace resolver routes the agent to it, and writes proceed normally.
    /// Spec-write wrappers do NOT enforce this (Thucydides legitimately edits
    /// the develop checkout because spec authors don't claim implementation
    /// tasks). When git is unavailable to classify the scope (returns null),
    /// fail-open so non-git environments (some test harnesses) don't break.
    /// </remarks>
    private string? TryRefuseMainCheckoutWrite(string operationName)
    {
        if (!_requireWorktree) return null;
        if (_isLinkedWorktree == true) return null;

        // Re-classify lazily when the construction-time check returned null
        // (codex review round 2): a transient git-spawn failure at session
        // creation must not become a persistent write outage that only clears
        // when the agent's Copilot session is invalidated. The recheck is
        // cheap (one git rev-parse) and only runs when the cached value is
        // unknown — once positively classified, _isLinkedWorktree is a final
        // bool and the cheap fast-path above wins on every subsequent call.
        var classification = _isLinkedWorktree;
        if (classification is null && _scopeRoot is not null)
            classification = ScopeRootValidator.IsLinkedWorktree(_scopeRoot);

        if (classification == true) return null;

        // Fail closed when classification is unknown (codex review round 1):
        // if git is unavailable to confirm the scope is a linked worktree, we
        // cannot prove writes won't land on the develop checkout. Surface a
        // clear refusal rather than silently re-introducing the contamination
        // this enforcement exists to prevent. Tests / non-git environments
        // that legitimately need writes-without-classification opt out via
        // requireWorktree=false.
        if (classification is null)
        {
            _logger.LogWarning(
                "{Operation} by {AgentId} REFUSED — could not classify scope as a linked git worktree (git unavailable). " +
                "scopeRoot={ScopeRoot}",
                operationName, _agentId, ResolveScopeRoot());
            return "Error: " + operationName + " could not verify the working directory is a per-task worktree " +
                   "(git classification unavailable). Refusing rather than risking writes to the develop checkout. " +
                   "Call CLAIM_TASK <taskId> first to provision a per-task worktree, then retry.";
        }

        _logger.LogWarning(
            "{Operation} by {AgentId} REFUSED — scope is not a per-task worktree (cwd={ScopeRoot}). " +
            "Agent must CLAIM_TASK before writing.",
            operationName, _agentId, ResolveScopeRoot());

        return "Error: Cannot " + operationName + " from the develop checkout. " +
               "Call CLAIM_TASK <taskId> first to provision a per-task worktree, " +
               "then retry on your next turn. (P1.9 blocker D enforcement: writes from the main " +
               "room are blocked because they would contaminate develop with parallel-task work.)";
    }

    private async Task<bool> StageFileAsync(string projectRoot, string relativePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = projectRoot
            };
            psi.ArgumentList.Add("add");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(relativePath);

            using var process = Process.Start(psi);
            if (process is not null)
            {
                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                {
                    var stderr = await process.StandardError.ReadToEndAsync();
                    _logger.LogWarning("git add failed for {Path}: {Error}", relativePath, stderr);
                    return false;
                }
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stage file {Path} — file was written but not staged", relativePath);
            return false;
        }
    }

    [Description("Commit all staged changes with a conventional commit message. " +
                 "Use after write_file to persist your changes. Returns the commit SHA on success.")]
    internal async Task<string> CommitChangesAsync(
        [Description("Conventional commit message (e.g., 'feat: add user endpoint with validation'). " +
                     "Use prefixes: feat:, fix:, refactor:, test:, docs:")]
        string message)
    {
        _logger.LogInformation("Tool call: commit_changes by {AgentId} (cwd={ScopeRoot}, message={Message})",
            _agentId, ResolveScopeRoot(), message);

        if (string.IsNullOrWhiteSpace(message))
            return "Error: message is required. Provide a conventional commit message (e.g., 'feat: add ITimeProvider abstraction').";

        if (message.Length > 5000)
            return "Error: Commit message exceeds 5000 characters.";

        var worktreeRefusal = TryRefuseMainCheckoutWrite("commit_changes");
        if (worktreeRefusal is not null)
            return worktreeRefusal;

        var projectRoot = ResolveScopeRoot();

        // Scope enforcement: refuse to commit if any staged path is outside _allowedRoot
        // or matches a protected infrastructure file. This prevents an agent holding
        // (e.g.) spec-write from committing src/ files that another flow happened to stage.
        //
        // Known TOCTOU caveat (P1.9 blocker B review, codex finding): validation happens
        // BEFORE the commit, and another caller could in principle stage a path between
        // validation and commit that then gets included in the commit. In practice, each
        // breakout has exactly one agent assigned to one worktree, so two CodeWriteToolWrapper
        // instances do not share a worktree's index. The race is theoretical, not exploitable
        // by the per-breakout design — but a future refactor that introduces shared-worktree
        // multi-agent commits should switch to explicit pathspec commits (`git commit -- path1
        // path2`) to close the window.
        var scopeViolation = await ValidateStagedPathsAsync(projectRoot);
        if (scopeViolation is not null)
        {
            _logger.LogWarning(
                "Agent {AgentId} blocked from committing out-of-scope staged paths: {Violation}",
                _agentId, scopeViolation);
            return $"Error: Commit blocked — staged changes outside {DescribeCommitScope()}: {scopeViolation}. Unstage those paths before committing.";
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var gitService = scope.ServiceProvider.GetRequiredService<IGitService>();

            // When operating inside a per-session worktree, run the commit in that
            // worktree directly via the scoped commit path (no `git add -A`, since
            // the wrapper has already staged its own paths). Main-room agents
            // (no scope root) keep the legacy GitService.CommitAsync path.
            var commitSha = _scopeRoot is not null
                ? await gitService.CommitStagedInDirAsync(_scopeRoot, message, _gitIdentity)
                : await gitService.CommitAsync(message, _gitIdentity);

            _logger.LogInformation(
                "commit_changes by {AgentId} ({AgentName}): {CommitSha} (cwd={ScopeRoot}) — {Message}",
                _agentId, _agentName, commitSha, projectRoot, message);

            await RecordCommitArtifactAsync(scope, commitSha.Trim());

            return $"Committed: {commitSha.Trim()}\nMessage: {message}";
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("no changes", StringComparison.OrdinalIgnoreCase))
        {
            return "Error: Nothing to commit. Stage files first (write_file auto-stages, or use other file operations).";
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "commit_changes failed for {AgentId}", _agentId);
            return $"Error: Commit failed — {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in commit_changes for {AgentId}", _agentId);
            return $"Error: Unexpected failure — {ex.Message}";
        }
    }

    private async Task RecordArtifactAsync(string filePath, string operation)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var tracker = scope.ServiceProvider.GetRequiredService<IRoomArtifactTracker>();
            await tracker.RecordAsync(_roomId, _agentId, filePath, operation);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record artifact {Operation} for {Path}", operation, filePath);
        }
    }

    private async Task RecordCommitArtifactAsync(IServiceScope scope, string commitSha)
    {
        try
        {
            var gitService = scope.ServiceProvider.GetRequiredService<IGitService>();
            var tracker = scope.ServiceProvider.GetRequiredService<IRoomArtifactTracker>();
            var files = await gitService.GetFilesInCommitAsync(commitSha, _scopeRoot);
            await tracker.RecordCommitAsync(_roomId, _agentId, commitSha, files);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record commit artifacts for {Sha}", commitSha);
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="relativePath"/> (relative to the project
    /// root) lies under any one of the configured allowed roots. Uses the platform-native
    /// directory separator because callers pass a path produced by <see cref="Path.GetRelativePath"/>.
    /// </summary>
    private bool IsUnderAllowedRoot(string relativePath)
    {
        foreach (var root in _allowedRoots)
        {
            var allowedPrefix = root + Path.DirectorySeparatorChar;
            if (relativePath.StartsWith(allowedPrefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Walks the directory chain from <paramref name="projectRoot"/> down to
    /// the deepest existing ancestor of <paramref name="fullPath"/> and returns
    /// the first component that is a symbolic link (or reparse point). Returns
    /// <c>null</c> when no symlinks are present in the chain. The file itself
    /// is also checked if it already exists.
    /// </summary>
    private static string? DetectSymlinkEscape(string projectRoot, string fullPath)
    {
        var normalizedRoot = projectRoot.TrimEnd(Path.DirectorySeparatorChar);
        var current = fullPath;

        // Collect all ancestors that still live under the project root, deepest first.
        var components = new List<string>();
        while (!string.IsNullOrEmpty(current)
               && current.Length > normalizedRoot.Length
               && current.StartsWith(normalizedRoot, StringComparison.Ordinal))
        {
            components.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
            current = parent;
        }

        // Check each existing component — symlinks/reparse points are not allowed inside the scope.
        // Use File.GetAttributes (lstat-semantics on .NET — does not follow links) so that
        // dangling symlinks are still detected as ReparsePoint rather than silently skipped.
        foreach (var component in components)
        {
            FileAttributes attrs;
            try
            {
                attrs = File.GetAttributes(component);
            }
            catch (FileNotFoundException)
            {
                continue; // truly non-existent path component — nothing to check
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (IOException)
            {
                // Unreadable / broken — refuse rather than allow.
                return Path.GetRelativePath(projectRoot, component);
            }
            catch (UnauthorizedAccessException)
            {
                return Path.GetRelativePath(projectRoot, component);
            }

            if ((attrs & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                return Path.GetRelativePath(projectRoot, component);
        }

        return null;
    }

    /// <summary>
    /// Returns a human-readable violation string if any currently staged path is
    /// outside <see cref="_allowedRoot"/> or matches a protected-file rule, or
    /// <c>null</c> if all staged paths are in scope.
    /// </summary>
    private async Task<string?> ValidateStagedPathsAsync(string projectRoot)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = projectRoot
            };
            psi.ArgumentList.Add("diff");
            psi.ArgumentList.Add("--cached");
            psi.ArgumentList.Add("--name-only");
            psi.ArgumentList.Add("-z"); // NUL-delimited output — safe against paths containing newlines or shell-special chars

            using var process = Process.Start(psi);
            if (process is null)
                return null; // best-effort — if git can't start, let the commit proceed and surface the error there

            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                return null;

            var allowedPrefixes = _allowedRoots.Select(r => r + "/").ToArray();
            var outOfScope = new List<string>();
            var protectedHits = new List<string>();

            // git diff --cached -z emits NUL-delimited paths with no quoting.
            foreach (var raw in stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var staged = raw.Replace('\\', '/');
                if (string.IsNullOrEmpty(staged)) continue;

                // Case-sensitive comparison — matches Linux filesystem semantics.
                var matched = false;
                foreach (var prefix in allowedPrefixes)
                {
                    if (staged.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        matched = true;
                        break;
                    }
                }
                if (!matched)
                {
                    outOfScope.Add(staged);
                    continue;
                }

                foreach (var protectedPath in _protectedPaths)
                {
                    if (staged.EndsWith(protectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        protectedHits.Add(staged);
                        break;
                    }
                }
            }

            if (outOfScope.Count > 0 || protectedHits.Count > 0)
            {
                var parts = new List<string>();
                if (outOfScope.Count > 0)
                    parts.Add(string.Join(", ", outOfScope.Take(5)) + (outOfScope.Count > 5 ? $" (+{outOfScope.Count - 5} more)" : ""));
                if (protectedHits.Count > 0)
                    parts.Add("protected: " + string.Join(", ", protectedHits));
                return string.Join("; ", parts);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Staged-paths validation failed; allowing commit to proceed and surface git errors directly");
            return null;
        }
    }
}
