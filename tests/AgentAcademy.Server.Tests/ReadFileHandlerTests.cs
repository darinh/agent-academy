using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for the ReadFileHandler including truncation, line ranges,
/// directory listing, and path traversal protection.
/// </summary>
[Collection("CwdMutating")]
public sealed class ReadFileHandlerTests : IDisposable
{
    private readonly ReadFileHandler _handler = new();
    private readonly string _tempDir;

    public ReadFileHandlerTests()
    {
        // Create a temp directory with AgentAcademy.sln so FindProjectRoot works
        _tempDir = Path.Combine(Path.GetTempPath(), $"readfile-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "AgentAcademy.sln"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private CommandContext MakeContext() => new(
        AgentId: "test-agent",
        AgentName: "Tester",
        AgentRole: "SoftwareEngineer",
        RoomId: "main",
        BreakoutRoomId: null,
        Services: null!
    );

    private CommandEnvelope MakeCommand(Dictionary<string, object?> args) => new(
        Command: "READ_FILE",
        Args: args,
        Status: CommandStatus.Success,
        Result: null,
        Error: null,
        CorrelationId: Guid.NewGuid().ToString(),
        Timestamp: DateTime.UtcNow,
        ExecutedBy: "test-agent"
    );

    [Fact]
    public async Task ReadFile_ReturnsContent()
    {
        var filePath = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(filePath, "line1\nline2\nline3");

        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await _handler.ExecuteAsync(
                MakeCommand(new() { ["path"] = "test.txt" }), MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Equal(3, dict["totalLines"]);
            Assert.Equal("line1\nline2\nline3", dict["content"]);
        }
        finally
        {
            Directory.SetCurrentDirectory(oldDir);
        }
    }

    [Fact]
    public async Task ReadFile_LineRange_ReturnsSubset()
    {
        var filePath = Path.Combine(_tempDir, "range.txt");
        File.WriteAllLines(filePath, ["A", "B", "C", "D", "E"]);

        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await _handler.ExecuteAsync(
                MakeCommand(new() { ["path"] = "range.txt", ["startLine"] = "2", ["endLine"] = "4" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Equal(2, dict["startLine"]);
            Assert.Equal(4, dict["endLine"]);
            Assert.Equal("B\nC\nD", dict["content"]);
        }
        finally
        {
            Directory.SetCurrentDirectory(oldDir);
        }
    }

    [Fact]
    public async Task ReadFile_LargeFile_Truncates()
    {
        var filePath = Path.Combine(_tempDir, "large.txt");
        // Create a file with enough content to exceed 12,000 chars
        var lines = Enumerable.Range(1, 500)
            .Select(i => $"Line {i}: {new string('x', 50)}")
            .ToArray();
        File.WriteAllLines(filePath, lines);

        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await _handler.ExecuteAsync(
                MakeCommand(new() { ["path"] = "large.txt" }), MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Equal(true, dict["truncated"]);
            Assert.True(dict.ContainsKey("hint"));
            var hint = (string)dict["hint"]!;
            Assert.Contains("startLine", hint);

            // Content should be less than 12,000 chars
            var content = (string)dict["content"]!;
            Assert.True(content.Length <= 12_000);
        }
        finally
        {
            Directory.SetCurrentDirectory(oldDir);
        }
    }

    [Fact]
    public async Task ReadFile_SmallFile_NotTruncated()
    {
        var filePath = Path.Combine(_tempDir, "small.txt");
        File.WriteAllText(filePath, "hello world");

        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await _handler.ExecuteAsync(
                MakeCommand(new() { ["path"] = "small.txt" }), MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.False(dict.ContainsKey("truncated"));
        }
        finally
        {
            Directory.SetCurrentDirectory(oldDir);
        }
    }

    [Fact]
    public async Task ReadFile_Directory_ListsEntries()
    {
        var subDir = Path.Combine(_tempDir, "mydir");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "a.txt"), "");
        File.WriteAllText(Path.Combine(subDir, "b.txt"), "");

        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await _handler.ExecuteAsync(
                MakeCommand(new() { ["path"] = "mydir" }), MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Equal("directory", dict["type"]);
            Assert.Equal(2, dict["count"]);
        }
        finally
        {
            Directory.SetCurrentDirectory(oldDir);
        }
    }

    [Fact]
    public async Task ReadFile_PathTraversal_Denied()
    {
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await _handler.ExecuteAsync(
                MakeCommand(new() { ["path"] = "../../etc/passwd" }), MakeContext());

            Assert.Equal(CommandStatus.Denied, result.Status);
            Assert.Contains("traversal", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.SetCurrentDirectory(oldDir);
        }
    }

    [Fact]
    public async Task ReadFile_MissingPath_ReturnsError()
    {
        var result = await _handler.ExecuteAsync(
            MakeCommand(new()), MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("path", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadFile_NotFound_ReturnsError()
    {
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await _handler.ExecuteAsync(
                MakeCommand(new() { ["path"] = "nonexistent.txt" }), MakeContext());

            Assert.Equal(CommandStatus.Error, result.Status);
            Assert.Contains("not found", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.SetCurrentDirectory(oldDir);
        }
    }

    [Fact]
    public async Task ReadFile_HonoursContextWorkingDirectory_NotCwd()
    {
        // P1.9 blocker B regression: when the breakout passes a worktree path
        // through CommandContext.WorkingDirectory, the handler must read from
        // *that* directory, not from FindProjectRoot() walking up from cwd.
        var altRoot = Path.Combine(Path.GetTempPath(), $"alt-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(altRoot);
        try
        {
            File.WriteAllText(Path.Combine(altRoot, "scoped.txt"), "from alt root");
            // Also drop a file with the same name in the cwd so we can detect
            // a regression that reads from the wrong tree.
            File.WriteAllText(Path.Combine(_tempDir, "scoped.txt"), "from cwd");

            var oldDir = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(_tempDir);
            try
            {
                var ctx = new CommandContext(
                    AgentId: "test-agent",
                    AgentName: "Tester",
                    AgentRole: "SoftwareEngineer",
                    RoomId: "main",
                    BreakoutRoomId: null,
                    Services: null!,
                    WorkingDirectory: altRoot);

                var result = await _handler.ExecuteAsync(
                    MakeCommand(new() { ["path"] = "scoped.txt" }), ctx);

                Assert.Equal(CommandStatus.Success, result.Status);
                var content = (string)((Dictionary<string, object?>)result.Result!)["content"]!;
                Assert.Equal("from alt root", content);
            }
            finally { Directory.SetCurrentDirectory(oldDir); }
        }
        finally
        {
            try { Directory.Delete(altRoot, true); } catch { }
        }
    }
}
