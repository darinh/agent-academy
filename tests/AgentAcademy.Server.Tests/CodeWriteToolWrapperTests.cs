using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for CodeWriteToolWrapper input validation and security restrictions.
/// File write tests that touch the file system are scoped to temp directories.
/// Serialized with WorkspaceRuntime to avoid git state interference.
/// </summary>
[Collection("WorkspaceRuntime")]
public sealed class CodeWriteToolWrapperTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly CodeWriteToolWrapper _wrapper;

    public CodeWriteToolWrapperTests()
    {
        var services = new ServiceCollection();
        // CodeWriteToolWrapper needs IServiceScopeFactory for CommitChangesAsync
        // but validation tests return before it's used.
        _sp = services.BuildServiceProvider();
        _wrapper = new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "test-agent", "TestAgent",
            new AgentGitIdentity("Test Agent", "test@agent.local"));
    }

    public void Dispose() => _sp.Dispose();

    // ── WriteFileAsync: Input validation ────────────────────────

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
        var result = await _wrapper.WriteFileAsync("src/test.cs", null!);
        Assert.StartsWith("Error: content is required", result);
    }

    [Fact]
    public async Task WriteFileAsync_ContentTooLarge_ReturnsError()
    {
        var bigContent = new string('x', 100_001);
        var result = await _wrapper.WriteFileAsync("src/test.cs", bigContent);
        Assert.Contains("Content too large", result);
        Assert.Contains("100,001", result);
    }

    [Fact]
    public async Task WriteFileAsync_ExactlyAtLimit_DoesNotReturnSizeError()
    {
        // 100,000 chars is at the limit — should NOT be rejected for size.
        // Uses a non-src path to trigger path restriction error instead of writing to disk.
        var content = new string('x', 100_000);
        var result = await _wrapper.WriteFileAsync("docs/test.cs", content);
        Assert.DoesNotContain("Content too large", result);
        // It should fail for being outside src/, not for content size
        Assert.Contains("Writes are restricted to the src/ directory", result);
    }

    [Fact]
    public async Task WriteFileAsync_BinaryContent_ReturnsError()
    {
        var result = await _wrapper.WriteFileAsync("src/test.cs", "hello\0world");
        Assert.Contains("Binary content detected", result);
    }

    // ── WriteFileAsync: Path security ───────────────────────────

    [Fact]
    public async Task WriteFileAsync_PathTraversal_ReturnsError()
    {
        var result = await _wrapper.WriteFileAsync("../../etc/passwd", "content");
        Assert.Contains("Path traversal denied", result);
    }

    [Fact]
    public async Task WriteFileAsync_AbsolutePathOutsideProject_ReturnsError()
    {
        var result = await _wrapper.WriteFileAsync("/tmp/evil.cs", "content");
        // Depending on resolution, this should be blocked (traversal or not in src/)
        Assert.Contains("Error:", result);
    }

    [Fact]
    public async Task WriteFileAsync_NotInSrcDirectory_ReturnsError()
    {
        var result = await _wrapper.WriteFileAsync("tests/test.cs", "content");
        Assert.Contains("Writes are restricted to the src/ directory", result);
    }

    [Fact]
    public async Task WriteFileAsync_RootLevelFile_ReturnsError()
    {
        var result = await _wrapper.WriteFileAsync("README.md", "content");
        Assert.Contains("Writes are restricted to the src/ directory", result);
    }

    [Theory]
    [InlineData("src/AgentAcademy.Server/Services/AgentOrchestrator.cs")]
    [InlineData("src/AgentAcademy.Server/Services/CopilotExecutor.cs")]
    [InlineData("src/AgentAcademy.Server/Services/GitService.cs")]
    [InlineData("src/AgentAcademy.Server/Program.cs")]
    [InlineData("src/AgentAcademy.Server/Services/AgentToolFunctions.cs")]
    [InlineData("src/AgentAcademy.Server/Services/AgentToolRegistry.cs")]
    [InlineData("src/AgentAcademy.Server/Services/IAgentToolRegistry.cs")]
    public async Task WriteFileAsync_ProtectedFile_ReturnsError(string path)
    {
        var result = await _wrapper.WriteFileAsync(path, "content");
        Assert.Contains("protected infrastructure file", result);
    }

    [Fact]
    public async Task WriteFileAsync_NonProtectedSrcFile_DoesNotReturnProtectedError()
    {
        // Verify that a non-protected path in src/ does NOT trigger the protected file error.
        // The file may actually be written (since it passes all validation), so we clean up.
        var uniqueName = $"_anvil_test_{Guid.NewGuid():N}.cs";
        var testPath = $"src/AgentAcademy.Server/{uniqueName}";
        try
        {
            var result = await _wrapper.WriteFileAsync(testPath, "// test content");
            Assert.DoesNotContain("protected infrastructure file", result);
        }
        finally
        {
            // Clean up any file that was actually created
            var projectRoot = AgentToolFunctions.FindProjectRoot();
            var fullPath = Path.Combine(projectRoot, testPath);
            if (File.Exists(fullPath))
                File.Delete(fullPath);

            // Unstage it
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

    // ── CommitChangesAsync: Input validation ────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CommitChangesAsync_EmptyMessage_ReturnsError(string? message)
    {
        var result = await _wrapper.CommitChangesAsync(message!);
        Assert.StartsWith("Error: message is required", result);
    }

    [Fact]
    public async Task CommitChangesAsync_MessageTooLong_ReturnsError()
    {
        var longMessage = new string('x', 5001);
        var result = await _wrapper.CommitChangesAsync(longMessage);
        Assert.Contains("exceeds 5000 characters", result);
    }

    [Fact]
    public async Task CommitChangesAsync_ExactlyAtLimit_DoesNotReturnLengthError()
    {
        // 5000 chars is at the limit — should not be rejected for length.
        // Will fail because there's no GitService registered, but not for length.
        var message = new string('x', 5000);
        var result = await _wrapper.CommitChangesAsync(message);
        Assert.DoesNotContain("exceeds 5000 characters", result);
        // Verify it failed for a service/git reason, not for validation
        Assert.Contains("Error:", result);
    }
}
