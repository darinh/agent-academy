using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for the multi-root variant of <see cref="CodeWriteToolWrapper"/> — specifically
/// the spec-write tool group as wired by <see cref="AgentToolFunctions.CreateSpecWriteTools"/>,
/// which grants Thucydides writes to both <c>specs/</c> AND <c>docs/</c>.
///
/// Why a separate file: <see cref="SpecWriteToolWrapperTests"/> covers the single-root
/// behaviour (specs-only) which remains valid for the one-root constructor overload.
/// These tests assert the broadened scope the factory produces in production.
/// Serialized with WorkspaceRuntime to avoid git state interference.
/// </summary>
[Collection("WorkspaceRuntime")]
public sealed class SpecWriteDocsScopeTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly CodeWriteToolWrapper _wrapper;

    public SpecWriteDocsScopeTests()
    {
        var services = new ServiceCollection();
        _sp = services.BuildServiceProvider();
        _wrapper = new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "tech-writer-1", "Thucydides",
            new AgentGitIdentity("Thucydides (Writer)", "thucydides@agent-academy.local"),
            roomId: null,
            allowedRoots: new[] { "specs", "docs" },
            protectedPaths: CodeWriteToolWrapper.SpecWriteProtectedPaths);
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    public void AllowedRoots_IncludeSpecsAndDocs()
    {
        Assert.Equal(new[] { "specs", "docs" }, _wrapper.AllowedRoots);
    }

    [Fact]
    public void AllowedRoot_ReturnsFirstConfiguredRoot()
    {
        // Back-compat: single-root callers reading AllowedRoot see the primary root.
        Assert.Equal("specs", _wrapper.AllowedRoot);
    }

    // ── Multi-root happy paths ──────────────────────────────────

    [Fact]
    public async Task WriteFileAsync_SpecsFile_IsAccepted()
    {
        var uniqueName = $"_anvil_specs_multi_{Guid.NewGuid():N}.md";
        var testPath = $"specs/{uniqueName}";
        try
        {
            var result = await _wrapper.WriteFileAsync(testPath, "# spec ok\n");
            Assert.DoesNotContain("Writes are restricted", result);
            Assert.DoesNotContain("Path traversal denied", result);
        }
        finally
        {
            CleanupFile(testPath);
        }
    }

    [Fact]
    public async Task WriteFileAsync_DocsFile_IsAccepted()
    {
        var uniqueName = $"_anvil_docs_multi_{Guid.NewGuid():N}.md";
        var testPath = $"docs/{uniqueName}";
        try
        {
            var result = await _wrapper.WriteFileAsync(testPath, "# docs ok\n");
            Assert.DoesNotContain("Writes are restricted", result);
            Assert.DoesNotContain("Path traversal denied", result);
        }
        finally
        {
            CleanupFile(testPath);
        }
    }

    [Fact]
    public async Task WriteFileAsync_NestedDocsPath_IsAccepted()
    {
        var uniqueName = $"_anvil_docs_nested_{Guid.NewGuid():N}.md";
        var testPath = $"docs/architecture/{uniqueName}";
        try
        {
            var result = await _wrapper.WriteFileAsync(testPath, "## nested\n");
            Assert.DoesNotContain("Writes are restricted", result);
        }
        finally
        {
            CleanupFile(testPath);
        }
    }

    // ── Rejection: other roots remain blocked ───────────────────

    [Fact]
    public async Task WriteFileAsync_SrcDirectory_IsRejected()
    {
        var result = await _wrapper.WriteFileAsync("src/AgentAcademy.Server/Program.cs", "// evil");
        // Multi-root scope message — must list both allowed roots and not allow src.
        Assert.Contains("Writes are restricted to", result);
        Assert.Contains("specs/", result);
        Assert.Contains("docs/", result);
    }

    [Fact]
    public async Task WriteFileAsync_TestsDirectory_IsRejected()
    {
        var result = await _wrapper.WriteFileAsync("tests/poisoned.cs", "// evil");
        Assert.Contains("Writes are restricted to", result);
    }

    [Fact]
    public async Task WriteFileAsync_RootLevelFile_IsRejected()
    {
        var result = await _wrapper.WriteFileAsync("README.md", "hijacked");
        Assert.Contains("Writes are restricted to", result);
    }

    [Fact]
    public async Task WriteFileAsync_CaseVariantOfDocsRoot_IsRejected()
    {
        // Linux is case-sensitive — Docs/ is not docs/.
        var result = await _wrapper.WriteFileAsync("Docs/fake.md", "# capital D\n");
        Assert.Contains("Writes are restricted to", result);
    }

    [Fact]
    public async Task WriteFileAsync_SneakyTraversalOutOfScope_IsRejected()
    {
        // docs/../src/Program.cs normalizes to src/Program.cs — must be rejected.
        var result = await _wrapper.WriteFileAsync("docs/../src/AgentAcademy.Server/Program.cs", "// evil");
        Assert.Contains("Writes are restricted to", result);
    }

    // ── Commit scope: both roots allowed, others blocked ───────

    [Fact]
    public async Task CommitChangesAsync_StagedPathOutsideScope_IsRejected()
    {
        var projectRoot = AgentToolFunctions.FindProjectRoot();
        var uniqueName = $"_anvil_scope_multi_{Guid.NewGuid():N}.cs";
        var outOfScopeRel = $"src/AgentAcademy.Server/{uniqueName}";
        var outOfScopeFull = Path.Combine(projectRoot, outOfScopeRel);

        try
        {
            await File.WriteAllTextAsync(outOfScopeFull, "// staged outside scope\n");
            RunGit(projectRoot, "add", "--", outOfScopeRel);

            var result = await _wrapper.CommitChangesAsync("spec: attempt to piggy-back src/ change");

            Assert.Contains("Commit blocked", result);
            // Error message must describe the configured scope — specs/ and docs/.
            Assert.Contains("specs/", result);
            Assert.Contains("docs/", result);
        }
        finally
        {
            RunGit(projectRoot, "reset", "HEAD", "--", outOfScopeRel);
            if (File.Exists(outOfScopeFull)) File.Delete(outOfScopeFull);
        }
    }

    // ── Constructor validation ──────────────────────────────────

    [Fact]
    public void Constructor_RejectsEmptyRootList()
    {
        Assert.Throws<ArgumentException>(() => new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "a", "A", null, null,
            allowedRoots: Array.Empty<string>(),
            protectedPaths: Array.Empty<string>()));
    }

    [Fact]
    public void Constructor_SingleRootOverload_PreservesValidationContract()
    {
        // Back-compat: the string-based overload must still throw ArgumentException
        // with ParamName = "allowedRoot" and the original message, even though storage
        // is delegated to the multi-root overload internally.
        var ex = Assert.Throws<ArgumentException>(() => new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "a", "A", null, null,
            allowedRoot: "   ",
            protectedPaths: Array.Empty<string>()));
        Assert.Equal("allowedRoot", ex.ParamName);
        Assert.Contains("allowedRoot is required", ex.Message);
    }

    [Fact]
    public void Constructor_RejectsWhitespaceEntry()
    {
        Assert.Throws<ArgumentException>(() => new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "a", "A", null, null,
            allowedRoots: new[] { "specs", "   " },
            protectedPaths: Array.Empty<string>()));
    }

    [Fact]
    public void Constructor_DeduplicatesRoots()
    {
        var w = new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "a", "A", null, null,
            allowedRoots: new[] { "specs", "specs", "docs" },
            protectedPaths: Array.Empty<string>());
        Assert.Equal(new[] { "specs", "docs" }, w.AllowedRoots);
    }

    [Fact]
    public void Constructor_NormalizesTrailingSlashesAndBackslashes()
    {
        var w = new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "a", "A", null, null,
            allowedRoots: new[] { "specs/", "docs\\" },
            protectedPaths: Array.Empty<string>());
        Assert.Equal(new[] { "specs", "docs" }, w.AllowedRoots);
    }

    // ── Helpers ────────────────────────────────────────────────

    private static void CleanupFile(string relativePath)
    {
        var projectRoot = AgentToolFunctions.FindProjectRoot();
        var fullPath = Path.Combine(projectRoot, relativePath);
        if (File.Exists(fullPath))
        {
            try { File.Delete(fullPath); } catch { /* best-effort */ }
        }
        // Unstage if write_file staged it.
        try
        {
            RunGit(projectRoot, "reset", "HEAD", "--", relativePath);
        }
        catch { /* best-effort */ }
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = System.Diagnostics.Process.Start(psi);
        p?.WaitForExit(5000);
    }
}
