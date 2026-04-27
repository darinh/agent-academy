using System.Diagnostics;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for the per-session <c>scopeRoot</c> wiring on <see cref="CodeWriteToolWrapper"/>
/// and <see cref="CodeReadToolWrapper"/> — the P1.9 blocker B isolation fix.
/// Each test sets up a real git repo with a worktree under a temp directory so the
/// validation, write, and commit paths exercise actual git semantics.
/// </summary>
[Collection("WorkspaceRuntime")]
public sealed class WorktreeIsolationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _repoRoot;
    private readonly string _worktreeA;
    private readonly string _worktreeB;
    private readonly ServiceProvider _sp;

    public WorktreeIsolationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "aa-worktree-iso-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);

        _repoRoot = Path.Combine(_tempRoot, "repo");
        Directory.CreateDirectory(_repoRoot);
        Git(_repoRoot, "init", "-b", "develop");
        Git(_repoRoot, "config", "user.email", "test@example.com");
        Git(_repoRoot, "config", "user.name", "Test");
        Directory.CreateDirectory(Path.Combine(_repoRoot, "src"));
        File.WriteAllText(Path.Combine(_repoRoot, "src/initial.cs"), "// initial\n");
        // Drop the sentinel so AgentToolFunctions.FindProjectRoot would resolve here
        // if it ever climbs into our temp tree (it shouldn't, because we always pass scopeRoot).
        File.WriteAllText(Path.Combine(_repoRoot, "AgentAcademy.sln"), "// fake sentinel\n");
        Git(_repoRoot, "add", "-A");
        Git(_repoRoot, "commit", "-m", "initial");

        _worktreeA = Path.Combine(_tempRoot, "wt-a");
        _worktreeB = Path.Combine(_tempRoot, "wt-b");
        Git(_repoRoot, "worktree", "add", "-b", "branch-a", _worktreeA);
        Git(_repoRoot, "worktree", "add", "-b", "branch-b", _worktreeB);

        var services = new ServiceCollection();
        _sp = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _sp.Dispose();
        TryDelete(_tempRoot);
    }

    // ── Constructor validation (§4.6) ───────────────────────────

    [Fact]
    public void Constructor_NonExistentScopeRoot_Throws()
    {
        var bogus = Path.Combine(_tempRoot, "does-not-exist");
        var ex = Assert.Throws<ArgumentException>(() => new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "test-agent", "TestAgent",
            gitIdentity: null, roomId: null,
            scopeRoot: bogus));
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void Constructor_ScopeRootNotAGitWorktree_Throws()
    {
        var notARepo = Path.Combine(_tempRoot, "not-a-repo");
        Directory.CreateDirectory(notARepo);
        // No `git init` here — and the parent walk eventually hits _tempRoot / cwd, neither a repo.
        var ex = Assert.Throws<ArgumentException>(() => new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "test-agent", "TestAgent",
            gitIdentity: null, roomId: null,
            scopeRoot: notARepo));
        Assert.Contains("not a git worktree", ex.Message);
    }

    [Fact]
    public void Constructor_ValidWorktree_Succeeds()
    {
        var wrapper = new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "test-agent", "TestAgent",
            gitIdentity: null, roomId: null,
            scopeRoot: _worktreeA);
        Assert.NotNull(wrapper);
    }

    [Fact]
    public void CodeReadToolWrapper_NonExistentScopeRoot_Throws()
    {
        var bogus = Path.Combine(_tempRoot, "no-such-dir");
        var ex = Assert.Throws<ArgumentException>(() =>
            new CodeReadToolWrapper(NullLogger.Instance, bogus));
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void CodeReadToolWrapper_NotAGitWorktree_Throws()
    {
        var notARepo = Path.Combine(_tempRoot, "no-repo-here");
        Directory.CreateDirectory(notARepo);
        var ex = Assert.Throws<ArgumentException>(() =>
            new CodeReadToolWrapper(NullLogger.Instance, notARepo));
        Assert.Contains("not a git worktree", ex.Message);
    }

    [Fact]
    public void CodeReadToolWrapper_ValidWorktree_Succeeds()
    {
        var wrapper = new CodeReadToolWrapper(NullLogger.Instance, _worktreeA);
        Assert.NotNull(wrapper);
    }

    // ── WriteFileAsync routes through scopeRoot ─────────────────

    [Fact]
    public async Task WriteFileAsync_WithScopeRoot_WritesInsideWorktree_NotDevelop()
    {
        var wrapper = new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "test-agent", "TestAgent",
            gitIdentity: null, roomId: null,
            scopeRoot: _worktreeA);

        var result = await wrapper.WriteFileAsync("src/from-a.cs", "// content from worktree A\n");

        Assert.StartsWith("Created", result);
        Assert.True(File.Exists(Path.Combine(_worktreeA, "src/from-a.cs")),
            "file should be written to the worktree");
        Assert.False(File.Exists(Path.Combine(_repoRoot, "src/from-a.cs")),
            "file must NOT appear in the develop checkout (this is the P1.9 blocker B regression)");
        Assert.False(File.Exists(Path.Combine(_worktreeB, "src/from-a.cs")),
            "file must NOT appear in the OTHER worktree (cross-breakout contamination check)");
    }

    [Fact]
    public async Task WriteFileAsync_TwoParallelWorktrees_EachStaysIsolated()
    {
        var wrapperA = new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "agent-a", "AgentA",
            gitIdentity: null, roomId: null,
            scopeRoot: _worktreeA);
        var wrapperB = new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "agent-b", "AgentB",
            gitIdentity: null, roomId: null,
            scopeRoot: _worktreeB);

        await wrapperA.WriteFileAsync("src/file-a.cs", "// A\n");
        await wrapperB.WriteFileAsync("src/file-b.cs", "// B\n");

        Assert.True(File.Exists(Path.Combine(_worktreeA, "src/file-a.cs")));
        Assert.False(File.Exists(Path.Combine(_worktreeA, "src/file-b.cs")));
        Assert.True(File.Exists(Path.Combine(_worktreeB, "src/file-b.cs")));
        Assert.False(File.Exists(Path.Combine(_worktreeB, "src/file-a.cs")));
        Assert.False(File.Exists(Path.Combine(_repoRoot, "src/file-a.cs")));
        Assert.False(File.Exists(Path.Combine(_repoRoot, "src/file-b.cs")));
    }

    // ── CodeReadToolWrapper isolation (G2 / acceptance criterion #2) ──

    [Fact]
    public async Task CodeReadToolWrapper_TwoScopeRoots_EachReadsOnlyItsOwnTree()
    {
        File.WriteAllText(Path.Combine(_worktreeA, "src/divergent.cs"), "// content in A only\n");
        File.WriteAllText(Path.Combine(_worktreeB, "src/divergent.cs"), "// content in B only\n");

        var readerA = new CodeReadToolWrapper(NullLogger.Instance, _worktreeA);
        var readerB = new CodeReadToolWrapper(NullLogger.Instance, _worktreeB);

        var resultA = await readerA.ReadFileAsync("src/divergent.cs");
        var resultB = await readerB.ReadFileAsync("src/divergent.cs");

        Assert.Contains("content in A only", resultA);
        Assert.DoesNotContain("content in B only", resultA);
        Assert.Contains("content in B only", resultB);
        Assert.DoesNotContain("content in A only", resultB);
    }

    // ── P1.9 blocker D: write enforcement ──────────────────────

    /// <summary>
    /// Code-write wrapper without a per-task worktree (scopeRoot=null) must
    /// refuse <c>write_file</c> and <c>commit_changes</c> with a message that
    /// directs the agent to CLAIM_TASK first. Closes P1.9 blocker D.
    /// </summary>
    [Fact]
    public async Task WriteFileAsync_NoWorktree_CodeWriteRefuses()
    {
        var wrapper = new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "test-agent", "TestAgent",
            gitIdentity: null, roomId: null,
            scopeRoot: null,
            requireWorktree: true);

        var result = await wrapper.WriteFileAsync("src/should-not-exist.cs", "// blocked\n");

        Assert.Contains("CLAIM_TASK", result);
        Assert.Contains("write_file", result);
        // Sanity check: the file must NOT have been written anywhere under temp.
        Assert.False(File.Exists(Path.Combine(_repoRoot, "src/should-not-exist.cs")));
    }

    [Fact]
    public async Task CommitChangesAsync_NoWorktree_CodeWriteRefuses()
    {
        var wrapper = new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "test-agent", "TestAgent",
            gitIdentity: null, roomId: null,
            scopeRoot: null,
            requireWorktree: true);

        var result = await wrapper.CommitChangesAsync("feat: should be refused");

        Assert.Contains("CLAIM_TASK", result);
        Assert.Contains("commit_changes", result);
    }

    /// <summary>
    /// When <c>requireWorktree</c> is true and scopeRoot is a real linked
    /// worktree, writes proceed normally (regression check that the gate
    /// only fires for the main checkout / null scope).
    /// </summary>
    [Fact]
    public async Task WriteFileAsync_RealWorktree_RequireWorktree_AllowsWrite()
    {
        var wrapper = new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "test-agent", "TestAgent",
            gitIdentity: null, roomId: null,
            scopeRoot: _worktreeA,
            requireWorktree: true);

        var result = await wrapper.WriteFileAsync("src/from-real-worktree.cs", "// allowed\n");

        Assert.StartsWith("Created", result);
        Assert.True(File.Exists(Path.Combine(_worktreeA, "src/from-real-worktree.cs")));
    }

    /// <summary>
    /// Spec-write (multi-root constructor) defaults to <c>requireWorktree=false</c>:
    /// Thucydides legitimately edits the develop checkout because spec authors
    /// do not claim implementation tasks. Regression check that we did not
    /// accidentally apply the code-write enforcement here.
    /// </summary>
    [Fact]
    public async Task WriteFileAsync_SpecWriteWrapper_NoWorktree_AllowsWriteToDevelopCheckout()
    {
        Directory.CreateDirectory(Path.Combine(_repoRoot, "specs"));

        var wrapper = new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "thucydides", "Thucydides",
            gitIdentity: null, roomId: null,
            allowedRoots: new[] { "specs", "docs" },
            protectedPaths: CodeWriteToolWrapper.SpecWriteProtectedPaths,
            scopeRoot: _repoRoot,
            requireWorktree: false);

        var result = await wrapper.WriteFileAsync("specs/README.md", "# spec write allowed in develop\n");

        Assert.StartsWith("Created", result);
        Assert.True(File.Exists(Path.Combine(_repoRoot, "specs/README.md")));
    }

    /// <summary>
    /// Codex review caught a subtlety: an earlier substring-only detection
    /// would misclassify a normal repo whose path happens to contain the
    /// segment <c>worktrees</c> as a linked worktree. Verify the new
    /// <c>--git-dir</c> vs <c>--git-common-dir</c> comparison is robust.
    /// </summary>
    [Fact]
    public async Task WriteFileAsync_RepoUnderDirNamedWorktrees_TreatedAsMainCheckout()
    {
        var trickyRoot = Path.Combine(_tempRoot, "worktrees", "looks-linked-but-isnt");
        Directory.CreateDirectory(trickyRoot);
        Git(trickyRoot, "init", "-b", "develop");
        Git(trickyRoot, "config", "user.email", "test@example.com");
        Git(trickyRoot, "config", "user.name", "Test");
        Directory.CreateDirectory(Path.Combine(trickyRoot, "src"));
        File.WriteAllText(Path.Combine(trickyRoot, "src/initial.cs"), "// initial\n");
        Git(trickyRoot, "add", "-A");
        Git(trickyRoot, "commit", "-m", "initial");

        var wrapper = new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "test-agent", "TestAgent",
            gitIdentity: null, roomId: null,
            scopeRoot: trickyRoot,
            requireWorktree: true);

        var result = await wrapper.WriteFileAsync("src/should-not-write.cs", "// blocked\n");

        Assert.Contains("CLAIM_TASK", result);
        Assert.False(File.Exists(Path.Combine(trickyRoot, "src/should-not-write.cs")));
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} failed in {dir}: {p.StandardError.ReadToEnd()}");
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                // Fix permissions on .git internals before delete
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(dir, recursive: true);
            }
        }
        catch { /* test cleanup best-effort */ }
    }
}
