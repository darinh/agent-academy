using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests.Security;

/// <summary>
/// Security tests validating path traversal prevention across all file access paths.
/// Covers: ReadFileHandler, CodeWriteToolWrapper, SearchCodeHandler.
/// Uses temp directories with fake AgentAcademy.sln to control FindProjectRoot().
/// </summary>
[Collection("CwdMutating")]
public sealed class PathTraversalSecurityTests : IDisposable
{
    private readonly string _tempDir;

    public PathTraversalSecurityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sec-traversal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempDir, "src", "AgentAcademy.Server"));
        File.WriteAllText(Path.Combine(_tempDir, "AgentAcademy.sln"), "");
        File.WriteAllText(Path.Combine(_tempDir, "src", "AgentAcademy.Server", "safe.cs"), "// safe");

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "secret-traversal-test.txt"), "SECRET");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
        try { File.Delete(Path.Combine(Path.GetTempPath(), "secret-traversal-test.txt")); } catch { }
    }

    /// <summary>
    /// Temporarily sets cwd to the temp dir for tests that exercise FindProjectRoot().
    /// Must be used in a try/finally block to restore the original cwd.
    /// </summary>
    private string SetCwd()
    {
        var old = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        return old;
    }

    private static CommandContext MakeContext() => new(
        AgentId: "test-agent",
        AgentName: "Tester",
        AgentRole: "SoftwareEngineer",
        RoomId: "main",
        BreakoutRoomId: null,
        Services: null!
    );

    private static CommandEnvelope ReadCmd(Dictionary<string, object?> args) => new(
        Command: "READ_FILE",
        Args: args,
        Status: CommandStatus.Success,
        Result: null,
        Error: null,
        CorrelationId: Guid.NewGuid().ToString(),
        Timestamp: DateTime.UtcNow,
        ExecutedBy: "test-agent"
    );

    private static CommandEnvelope SearchCmd(Dictionary<string, object?> args) => new(
        Command: "SEARCH_CODE",
        Args: args,
        Status: CommandStatus.Success,
        Result: null,
        Error: null,
        CorrelationId: Guid.NewGuid().ToString(),
        Timestamp: DateTime.UtcNow,
        ExecutedBy: "test-agent"
    );

    // ── ReadFileHandler traversal attacks ───────────────────────────

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("../../../etc/shadow")]
    [InlineData("src/../../etc/passwd")]
    [InlineData("src/../../../secret")]
    public async Task ReadFile_RelativeTraversal_Denied(string path)
    {
        var oldCwd = SetCwd();
        try
        {
            var handler = new ReadFileHandler();
            var result = await handler.ExecuteAsync(ReadCmd(new() { ["path"] = path }), MakeContext());

            Assert.Equal(CommandStatus.Denied, result.Status);
            Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
        }
        finally { Directory.SetCurrentDirectory(oldCwd); }
    }

    [Fact]
    public async Task ReadFile_AbsolutePath_OutsideRoot_Denied()
    {
        var oldCwd = SetCwd();
        try
        {
            var handler = new ReadFileHandler();
            var result = await handler.ExecuteAsync(
                ReadCmd(new() { ["path"] = "/etc/passwd" }), MakeContext());

            Assert.Equal(CommandStatus.Denied, result.Status);
        }
        finally { Directory.SetCurrentDirectory(oldCwd); }
    }

    [Fact]
    public async Task ReadFile_NullByteInPath_ThrowsOrDenies()
    {
        var oldCwd = SetCwd();
        try
        {
            var handler = new ReadFileHandler();
            // .NET's Path.GetFullPath throws ArgumentException on null bytes,
            // preventing null byte injection at the runtime level.
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                handler.ExecuteAsync(
                    ReadCmd(new() { ["path"] = "src/safe.cs\0../../etc/passwd" }), MakeContext()));

            Assert.Contains("Null", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.SetCurrentDirectory(oldCwd); }
    }

    [Fact]
    public async Task ReadFile_DeepTraversal_Denied()
    {
        var oldCwd = SetCwd();
        try
        {
            var handler = new ReadFileHandler();
            var result = await handler.ExecuteAsync(
                ReadCmd(new() { ["path"] = "src/AgentAcademy.Server/../../../../../../etc/passwd" }), MakeContext());

            Assert.Equal(CommandStatus.Denied, result.Status);
        }
        finally { Directory.SetCurrentDirectory(oldCwd); }
    }

    [Fact]
    public async Task ReadFile_Symlink_OutsideRoot_IsAcceptedRisk()
    {
        // Security spec 015 §9.2 documents symlink traversal as an accepted risk.
        var targetFile = Path.Combine(Path.GetTempPath(), "secret-traversal-test.txt");
        var symlinkPath = Path.Combine(_tempDir, "src", "AgentAcademy.Server", "link.txt");

        try
        {
            File.CreateSymbolicLink(symlinkPath, targetFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // Symlink creation may fail in CI/containers/unprivileged environments
        }

        var oldCwd = SetCwd();
        try
        {
            var handler = new ReadFileHandler();
            var result = await handler.ExecuteAsync(
                ReadCmd(new() { ["path"] = "src/AgentAcademy.Server/link.txt" }), MakeContext());

            // Documents accepted behavior: symlink within project tree can read outside
            Assert.Equal(CommandStatus.Success, result.Status);
        }
        finally
        {
            Directory.SetCurrentDirectory(oldCwd);
            try { File.Delete(symlinkPath); } catch { }
        }
    }

    // ── CodeWriteToolWrapper traversal attacks ──────────────────────

    [Theory]
    [InlineData("../../etc/evil.txt")]
    [InlineData("../secret.txt")]
    [InlineData("src/../../etc/evil.txt")]
    [InlineData("src/../../../tmp/evil")]
    public async Task WriteFile_RelativeTraversal_Denied(string path)
    {
        var oldCwd = SetCwd();
        try
        {
            var wrapper = CreateWriteWrapper();
            var result = await wrapper.WriteFileAsync(path, "malicious content");

            Assert.StartsWith("Error:", result);
            Assert.Contains("denied", result, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.SetCurrentDirectory(oldCwd); }
    }

    [Fact]
    public async Task WriteFile_AbsolutePath_OutsideRoot_Denied()
    {
        var oldCwd = SetCwd();
        try
        {
            var wrapper = CreateWriteWrapper();
            var result = await wrapper.WriteFileAsync("/tmp/evil.txt", "malicious");

            Assert.StartsWith("Error:", result);
        }
        finally { Directory.SetCurrentDirectory(oldCwd); }
    }

    [Fact]
    public async Task WriteFile_ProtectedInfrastructureFiles_AllDenied()
    {
        var oldCwd = SetCwd();
        try
        {
            var wrapper = CreateWriteWrapper();
            var protectedFiles = new[]
            {
                "src/AgentAcademy.Server/Program.cs",
                "src/AgentAcademy.Server/Services/AgentToolFunctions.cs",
                "src/AgentAcademy.Server/Services/AgentToolRegistry.cs",
                "src/AgentAcademy.Server/Services/CopilotExecutor.cs",
                "src/AgentAcademy.Server/Services/AgentOrchestrator.cs",
                "src/AgentAcademy.Server/Services/GitService.cs",
            };

            foreach (var file in protectedFiles)
            {
                var result = await wrapper.WriteFileAsync(file, "hacked");
                Assert.StartsWith("Error:", result);
                Assert.Contains("protected", result, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { Directory.SetCurrentDirectory(oldCwd); }
    }

    [Fact]
    public async Task WriteFile_OutsideSrcDir_Denied()
    {
        var oldCwd = SetCwd();
        try
        {
            var wrapper = CreateWriteWrapper();
            var result = await wrapper.WriteFileAsync("tests/evil.cs", "malicious");

            Assert.StartsWith("Error:", result);
            Assert.Contains("src/", result);
        }
        finally { Directory.SetCurrentDirectory(oldCwd); }
    }

    [Fact]
    public async Task WriteFile_BinaryContent_Denied()
    {
        var oldCwd = SetCwd();
        try
        {
            var wrapper = CreateWriteWrapper();
            var result = await wrapper.WriteFileAsync("src/AgentAcademy.Server/test.cs", "content\0with\0nulls");

            Assert.StartsWith("Error:", result);
            Assert.Contains("Binary", result, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.SetCurrentDirectory(oldCwd); }
    }

    [Fact]
    public async Task WriteFile_OversizedContent_Denied()
    {
        var oldCwd = SetCwd();
        try
        {
            var wrapper = CreateWriteWrapper();
            var result = await wrapper.WriteFileAsync(
                "src/AgentAcademy.Server/big.cs",
                new string('x', 100_001));

            Assert.StartsWith("Error:", result);
            Assert.Contains("large", result, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.SetCurrentDirectory(oldCwd); }
    }

    // ── SearchCodeHandler path scope (bug fix) ─────────────────────

    [Fact]
    public async Task SearchCode_PathOutsideRoot_Denied()
    {
        var oldCwd = SetCwd();
        try
        {
            var handler = new SearchCodeHandler();
            var result = await handler.ExecuteAsync(
                SearchCmd(new()
                {
                    ["query"] = "root",
                    ["path"] = "../../etc"
                }),
                MakeContext());

            Assert.Equal(CommandStatus.Denied, result.Status);
            Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
        }
        finally { Directory.SetCurrentDirectory(oldCwd); }
    }

    [Fact]
    public async Task SearchCode_AbsolutePathOutsideRoot_Denied()
    {
        var oldCwd = SetCwd();
        try
        {
            var handler = new SearchCodeHandler();
            var result = await handler.ExecuteAsync(
                SearchCmd(new()
                {
                    ["query"] = "password",
                    ["path"] = "/etc"
                }),
                MakeContext());

            Assert.Equal(CommandStatus.Denied, result.Status);
            Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
        }
        finally { Directory.SetCurrentDirectory(oldCwd); }
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static CodeWriteToolWrapper CreateWriteWrapper()
    {
        var scopeFactory = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        return new CodeWriteToolWrapper(
            scopeFactory, NullLogger.Instance,
            "test-agent", "Tester");
    }
}
