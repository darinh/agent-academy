using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for the spec-write variant of <see cref="CodeWriteToolWrapper"/>.
/// Spec-write restricts writes to <c>specs/</c> (instead of <c>src/</c>) and
/// has no protected-file list, allowing the Technical Writer to maintain the
/// entire spec corpus without granting general code-write access.
/// Serialized with WorkspaceRuntime to avoid git state interference.
/// </summary>
[Collection("WorkspaceRuntime")]
public sealed class SpecWriteToolWrapperTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly CodeWriteToolWrapper _wrapper;

    public SpecWriteToolWrapperTests()
    {
        var services = new ServiceCollection();
        _sp = services.BuildServiceProvider();
        _wrapper = new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "tech-writer-1", "Thucydides",
            new AgentGitIdentity("Thucydides (Writer)", "thucydides@agent-academy.local"),
            roomId: null,
            allowedRoot: "specs",
            protectedPaths: CodeWriteToolWrapper.SpecWriteProtectedPaths);
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    public void AllowedRoot_IsSpecs()
    {
        Assert.Equal("specs", _wrapper.AllowedRoot);
    }

    // ── Path scope: src/ is NOT writable via spec-write ─────────

    [Fact]
    public async Task WriteFileAsync_SrcDirectory_IsRejected()
    {
        var result = await _wrapper.WriteFileAsync("src/AgentAcademy.Server/Program.cs", "// evil");
        Assert.Contains("Writes are restricted to the specs/ directory", result);
    }

    [Fact]
    public async Task WriteFileAsync_RootLevelFile_IsRejected()
    {
        var result = await _wrapper.WriteFileAsync("README.md", "hijacked");
        Assert.Contains("Writes are restricted to the specs/ directory", result);
    }

    [Fact]
    public async Task WriteFileAsync_TestsDirectory_IsRejected()
    {
        var result = await _wrapper.WriteFileAsync("tests/poisoned.cs", "// evil");
        Assert.Contains("Writes are restricted to the specs/ directory", result);
    }

    [Fact]
    public async Task WriteFileAsync_PathTraversal_IsRejected()
    {
        var result = await _wrapper.WriteFileAsync("../../etc/passwd", "root::0:0");
        Assert.Contains("Path traversal denied", result);
    }

    [Fact]
    public async Task WriteFileAsync_SneakyTraversalOutOfSpecs_IsRejected()
    {
        // specs/../src/Program.cs should normalize to src/Program.cs and be rejected.
        var result = await _wrapper.WriteFileAsync("specs/../src/AgentAcademy.Server/Program.cs", "// evil");
        Assert.Contains("Writes are restricted to the specs/ directory", result);
    }

    // ── Input validation still applies ──────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WriteFileAsync_EmptyPath_ReturnsError(string? path)
    {
        var result = await _wrapper.WriteFileAsync(path!, "content");
        Assert.StartsWith("Error: path is required", result);
    }

    [Fact]
    public async Task WriteFileAsync_NullContent_ReturnsError()
    {
        var result = await _wrapper.WriteFileAsync("specs/test.md", null!);
        Assert.StartsWith("Error: content is required", result);
    }

    [Fact]
    public async Task WriteFileAsync_ContentTooLarge_ReturnsError()
    {
        var bigContent = new string('x', 100_001);
        var result = await _wrapper.WriteFileAsync("specs/test.md", bigContent);
        Assert.Contains("Content too large", result);
    }

    [Fact]
    public async Task WriteFileAsync_BinaryContent_ReturnsError()
    {
        var result = await _wrapper.WriteFileAsync("specs/test.md", "hello\0world");
        Assert.Contains("Binary content detected", result);
    }

    // ── Happy path: specs/ writes are accepted ──────────────────

    [Fact]
    public async Task WriteFileAsync_SpecsFile_IsAccepted()
    {
        var uniqueName = $"_anvil_spec_test_{Guid.NewGuid():N}.md";
        var testPath = $"specs/{uniqueName}";
        try
        {
            var result = await _wrapper.WriteFileAsync(testPath, "# Test spec\n");
            // The write may succeed (creating + staging) or fail at the git-stage step
            // depending on test environment — the key assertion is that it was NOT
            // rejected for path/scope reasons.
            Assert.DoesNotContain("Writes are restricted", result);
            Assert.DoesNotContain("Path traversal denied", result);
            Assert.DoesNotContain("protected infrastructure file", result);
        }
        finally
        {
            var projectRoot = AgentToolFunctions.FindProjectRoot();
            var fullPath = Path.Combine(projectRoot, testPath);
            if (File.Exists(fullPath))
                File.Delete(fullPath);
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = projectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add("reset");
                psi.ArgumentList.Add("HEAD");
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add(testPath);
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(5000);
            }
            catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task WriteFileAsync_NestedSpecsPath_IsAccepted()
    {
        var uniqueName = $"_anvil_nested_{Guid.NewGuid():N}.md";
        var testPath = $"specs/300-frontend-ui/{uniqueName}";
        try
        {
            var result = await _wrapper.WriteFileAsync(testPath, "## Nested\n");
            Assert.DoesNotContain("Writes are restricted", result);
        }
        finally
        {
            var projectRoot = AgentToolFunctions.FindProjectRoot();
            var fullPath = Path.Combine(projectRoot, testPath);
            if (File.Exists(fullPath))
                File.Delete(fullPath);
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = projectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add("reset");
                psi.ArgumentList.Add("HEAD");
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add(testPath);
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(5000);
            }
            catch { /* best-effort cleanup */ }
        }
    }

    // ── Symlink escape (defence-in-depth) ───────────────────────

    [Fact]
    public async Task WriteFileAsync_ThroughSymlinkedDirectory_IsRejected()
    {
        // Create specs/_anvil_symlink_<guid> as a symlink pointing to src/ inside
        // the project root, then try to write to specs/_anvil_symlink_<guid>/escaped.md.
        // The lexical prefix check would pass; the symlink-escape detector must catch it.
        if (OperatingSystem.IsWindows())
            return; // creating symlinks on Windows requires elevated privileges in CI

        var projectRoot = AgentToolFunctions.FindProjectRoot();
        var linkName = $"_anvil_symlink_{Guid.NewGuid():N}";
        var linkPath = Path.Combine(projectRoot, "specs", linkName);
        var srcDir = Path.Combine(projectRoot, "src");

        try
        {
            Directory.CreateSymbolicLink(linkPath, srcDir);
            var relativeWrite = $"specs/{linkName}/escaped.md";

            var result = await _wrapper.WriteFileAsync(relativeWrite, "# escape attempt\n");

            Assert.Contains("symlink", result, StringComparison.OrdinalIgnoreCase);
            // And — critical — no file was actually written through the link.
            Assert.False(File.Exists(Path.Combine(projectRoot, "src", "escaped.md")));
        }
        finally
        {
            if (Directory.Exists(linkPath) || File.Exists(linkPath))
            {
                try { File.Delete(linkPath); } catch { /* ignore */ }
                try { Directory.Delete(linkPath); } catch { /* ignore */ }
            }
            var escaped = Path.Combine(projectRoot, "src", "escaped.md");
            if (File.Exists(escaped)) File.Delete(escaped);
        }
    }

    [Fact]
    public async Task WriteFileAsync_ThroughDanglingSymlinkedDirectory_IsRejected()
    {
        // Create specs/_anvil_dangling_<guid> as a symlink pointing to a target that
        // does NOT exist. File.Exists / Directory.Exists return false for the link,
        // but the write would still follow the link and create the target. The
        // detector must catch the ReparsePoint attribute regardless of target validity.
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = AgentToolFunctions.FindProjectRoot();
        var linkName = $"_anvil_dangling_{Guid.NewGuid():N}";
        var linkPath = Path.Combine(projectRoot, "specs", linkName);
        var danglingTarget = Path.Combine(projectRoot, $"_nonexistent_{Guid.NewGuid():N}");

        try
        {
            Directory.CreateSymbolicLink(linkPath, danglingTarget);
            var relativeWrite = $"specs/{linkName}/escaped.md";

            var result = await _wrapper.WriteFileAsync(relativeWrite, "# dangling escape\n");

            Assert.Contains("symlink", result, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(danglingTarget));
        }
        finally
        {
            try { File.Delete(linkPath); } catch { /* ignore */ }
            try { Directory.Delete(linkPath); } catch { /* ignore */ }
            if (Directory.Exists(danglingTarget))
            {
                try { Directory.Delete(danglingTarget, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public async Task WriteFileAsync_CaseVariantOfAllowedRoot_IsRejected()
    {
        // On case-sensitive filesystems (Linux CI) "Specs/" is a different directory
        // from "specs/" — accepting case-insensitive prefix matches would allow the
        // agent to create a parallel out-of-scope tree.
        var result = await _wrapper.WriteFileAsync("Specs/fake.md", "# capital S\n");
        Assert.Contains("Writes are restricted to the specs/ directory", result);
    }

    // ── Commit scope: reject staged paths outside specs/ ───────

    [Fact]
    public async Task CommitChangesAsync_StagedPathOutsideSpecs_IsRejected()
    {
        // Stage a file inside src/ and then call CommitChangesAsync on the
        // spec-write wrapper. It must refuse to commit — Thucydides holding
        // spec-write must not be able to commit code by piggy-backing on a
        // previously staged src/ file.
        var projectRoot = AgentToolFunctions.FindProjectRoot();
        var uniqueName = $"_anvil_scope_{Guid.NewGuid():N}.cs";
        var outOfScopeRel = $"src/AgentAcademy.Server/{uniqueName}";
        var outOfScopeFull = Path.Combine(projectRoot, outOfScopeRel);

        try
        {
            await File.WriteAllTextAsync(outOfScopeFull, "// staged outside scope\n");
            RunGit(projectRoot, "add", "--", outOfScopeRel);

            var result = await _wrapper.CommitChangesAsync("spec: attempt to piggy-back a src/ change");

            Assert.Contains("Commit blocked", result);
            Assert.Contains("specs/", result);
        }
        finally
        {
            RunGit(projectRoot, "reset", "HEAD", "--", outOfScopeRel);
            if (File.Exists(outOfScopeFull)) File.Delete(outOfScopeFull);
        }
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
