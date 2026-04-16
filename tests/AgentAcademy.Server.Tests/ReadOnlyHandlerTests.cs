using System.Diagnostics;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for read-only / process-based command handlers that were previously untested:
/// GitLogHandler, ShowDiffHandler, SearchCodeHandler, RunBuildHandler, RunTestsHandler.
/// Uses a real temporary git repository for git-based tests.
/// </summary>
[Collection("WorkspaceRuntime")]
public sealed class GitCommandHandlerTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly string _savedDir;

    public GitCommandHandlerTests()
    {
        _savedDir = Directory.GetCurrentDirectory();
        _repoRoot = Path.Combine(Path.GetTempPath(), $"handler-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoRoot);

        // Create a minimal git repo with AgentAcademy.sln (for FindProjectRoot)
        File.WriteAllText(Path.Combine(_repoRoot, "AgentAcademy.sln"), "");
        RunGit("init");
        RunGit("config", "user.name", "Test User");
        RunGit("config", "user.email", "test@test.local");

        // Initial commit with a tracked file
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "# Test Project\n");
        File.WriteAllText(Path.Combine(_repoRoot, "hello.cs"), "Console.WriteLine(\"hello world\");\n");
        RunGit("add", "-A");
        RunGit("commit", "-m", "feat: initial commit");

        // Second commit for log history
        File.WriteAllText(Path.Combine(_repoRoot, "second.txt"), "second file\n");
        RunGit("add", "-A");
        RunGit("commit", "-m", "fix: add second file");

        // Third commit
        File.WriteAllText(Path.Combine(_repoRoot, "third.txt"), "third file\n");
        RunGit("add", "-A");
        RunGit("commit", "-m", "docs: add third file");

        Directory.SetCurrentDirectory(_repoRoot);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_savedDir);
        try { Directory.Delete(_repoRoot, true); } catch { }
    }

    private CommandContext MakeContext() => new(
        AgentId: "test-agent",
        AgentName: "Tester",
        AgentRole: "SoftwareEngineer",
        RoomId: "main",
        BreakoutRoomId: null,
        Services: null!
    );

    private CommandEnvelope MakeCommand(string name, Dictionary<string, object?> args) => new(
        Command: name,
        Args: args,
        Status: CommandStatus.Success,
        Result: null,
        Error: null,
        CorrelationId: Guid.NewGuid().ToString(),
        Timestamp: DateTime.UtcNow,
        ExecutedBy: "test-agent"
    );

    private string RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return stdout.Trim();
    }

    // ── GIT_LOG ──────────────────────────────────────────────

    [Fact]
    public async Task GitLog_ReturnsCommitHistory()
    {
        var handler = new GitLogHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("GIT_LOG", new() { ["count"] = "10" }), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var commits = (List<Dictionary<string, object?>>)dict["commits"]!;
        Assert.Equal(3, (int)dict["count"]!);
        Assert.Equal(3, commits.Count);
        Assert.Contains("docs: add third file", (string)commits[0]["message"]!);
    }

    [Fact]
    public async Task GitLog_RespectsCountArg()
    {
        var handler = new GitLogHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("GIT_LOG", new() { ["count"] = "1" }), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(1, (int)dict["count"]!);
    }

    [Fact]
    public async Task GitLog_CapsCountAtMax()
    {
        var handler = new GitLogHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("GIT_LOG", new() { ["count"] = "999" }), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        // Should not exceed MaxCount (50), but we only have 3 commits
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(3, (int)dict["count"]!);
    }

    [Fact]
    public async Task GitLog_FileFilter()
    {
        var handler = new GitLogHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("GIT_LOG", new() { ["file"] = "README.md" }), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        // Only the initial commit touches README.md
        Assert.Equal(1, (int)dict["count"]!);
    }

    [Fact]
    public async Task GitLog_DefaultCount_Returns20OrLess()
    {
        var handler = new GitLogHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("GIT_LOG", new()), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        // We have 3 commits, default count is 20
        Assert.Equal(3, (int)dict["count"]!);
    }

    [Fact]
    public async Task GitLog_IntCount()
    {
        var handler = new GitLogHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("GIT_LOG", new() { ["count"] = 2 }), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(2, (int)dict["count"]!);
    }

    [Fact]
    public async Task GitLog_CommitsHaveShaAndMessage()
    {
        var handler = new GitLogHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("GIT_LOG", new()), MakeContext());

        var dict = (Dictionary<string, object?>)result.Result!;
        var commits = (List<Dictionary<string, object?>>)dict["commits"]!;
        foreach (var commit in commits)
        {
            Assert.True(commit.ContainsKey("sha"), "Commit should have sha");
            Assert.True(commit.ContainsKey("message"), "Commit should have message");
            var sha = (string)commit["sha"]!;
            Assert.True(sha.Length >= 7, $"SHA should be at least 7 chars, got '{sha}'");
        }
    }

    // ── SHOW_DIFF ────────────────────────────────────────────

    [Fact]
    public async Task ShowDiff_NoChanges_ReturnsNoChanges()
    {
        var handler = new ShowDiffHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SHOW_DIFF", new()), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("(no changes)", (string)dict["diff"]!);
    }

    [Fact]
    public async Task ShowDiff_WithUnstagedChanges_ShowsDiff()
    {
        File.AppendAllText(Path.Combine(_repoRoot, "README.md"), "new line\n");

        var handler = new ShowDiffHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SHOW_DIFF", new()), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var diff = (string)dict["diff"]!;
        Assert.Contains("new line", diff);
        Assert.Contains("README.md", diff);
    }

    [Fact]
    public async Task ShowDiff_WithBranch_ShowsDiffAgainstBranch()
    {
        // Create a branch, make changes
        RunGit("checkout", "-b", "test-branch");
        File.WriteAllText(Path.Combine(_repoRoot, "branch-file.txt"), "on branch\n");
        RunGit("add", "-A");
        RunGit("commit", "-m", "feat: branch commit");

        var handler = new ShowDiffHandler();
        // Compare current branch (test-branch) to HEAD~1
        var result = await handler.ExecuteAsync(
            MakeCommand("SHOW_DIFF", new() { ["branch"] = "HEAD~1" }), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var diff = (string)dict["diff"]!;
        Assert.Contains("branch-file.txt", diff);
    }

    [Fact]
    public async Task ShowDiff_IgnoresDashPrefixedBranch()
    {
        var handler = new ShowDiffHandler();
        // Branch starting with "-" should be ignored (security)
        var result = await handler.ExecuteAsync(
            MakeCommand("SHOW_DIFF", new() { ["branch"] = "--exec=whoami" }), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    // ── SEARCH_CODE ──────────────────────────────────────────

    [Fact]
    public async Task SearchCode_FindsMatch()
    {
        var handler = new SearchCodeHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SEARCH_CODE", new() { ["query"] = "hello world" }), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var matches = (List<Dictionary<string, object?>>)dict["matches"]!;
        Assert.True((int)dict["count"]! > 0);
        Assert.Contains(matches, m => (string)m["file"]! == "hello.cs");
    }

    [Fact]
    public async Task SearchCode_MissingQuery_ReturnsValidationError()
    {
        var handler = new SearchCodeHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SEARCH_CODE", new()), MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("query", result.Error!);
    }

    [Fact]
    public async Task SearchCode_NoMatch_ReturnsEmptyResults()
    {
        var handler = new SearchCodeHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SEARCH_CODE", new() { ["query"] = "xyznonexistent12345" }), MakeContext());

        // git grep exits 1 on no match, handler returns success with empty results
        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(0, (int)dict["count"]!);
    }

    [Fact]
    public async Task SearchCode_CaseInsensitive()
    {
        var handler = new SearchCodeHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SEARCH_CODE", new()
            {
                ["query"] = "HELLO WORLD",
                ["ignoreCase"] = "true"
            }), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.True((int)dict["count"]! > 0, "Case-insensitive search should find 'hello world'");
    }

    [Fact]
    public async Task SearchCode_PathFilter()
    {
        // Create a file in a subdirectory
        Directory.CreateDirectory(Path.Combine(_repoRoot, "subdir"));
        File.WriteAllText(Path.Combine(_repoRoot, "subdir", "nested.cs"), "Console.WriteLine(\"nested hello\");\n");
        RunGit("add", "-A");
        RunGit("commit", "-m", "add nested file");

        var handler = new SearchCodeHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SEARCH_CODE", new()
            {
                ["query"] = "hello",
                ["path"] = "subdir"
            }), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var matches = (List<Dictionary<string, object?>>)dict["matches"]!;
        Assert.All(matches, m => Assert.StartsWith("subdir", (string)m["file"]!));
    }

    [Fact]
    public async Task SearchCode_InvalidPath_ReturnsNotFound()
    {
        var handler = new SearchCodeHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SEARCH_CODE", new()
            {
                ["query"] = "hello",
                ["path"] = "nonexistent-dir"
            }), MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task SearchCode_MatchesContainLineNumbers()
    {
        var handler = new SearchCodeHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SEARCH_CODE", new() { ["query"] = "hello" }), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var matches = (List<Dictionary<string, object?>>)dict["matches"]!;
        Assert.True(matches.Count > 0);
        foreach (var m in matches)
        {
            Assert.True(m.ContainsKey("line"), "Match should have line number");
            Assert.True((int)m["line"]! > 0, "Line number should be positive");
            Assert.True(m.ContainsKey("file"), "Match should have file");
            Assert.True(m.ContainsKey("text"), "Match should have text");
        }
    }

    [Fact]
    public async Task SearchCode_FromSubdirectory_FindsProjectRoot()
    {
        var subDir = Path.Combine(_repoRoot, "deep", "nested");
        Directory.CreateDirectory(subDir);
        Directory.SetCurrentDirectory(subDir);

        var handler = new SearchCodeHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SEARCH_CODE", new() { ["query"] = "hello world" }), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.True((int)dict["count"]! > 0, "Should find matches from subdirectory via FindProjectRoot");
    }

    [Fact]
    public async Task SearchCode_NotTruncated_NoTruncationFields()
    {
        var handler = new SearchCodeHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SEARCH_CODE", new() { ["query"] = "hello world" }), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.True((int)dict["count"]! < 50);
        Assert.False(dict.ContainsKey("truncated"), "Should not have truncated flag when under limit");
        Assert.False(dict.ContainsKey("hint"), "Should not have hint when under limit");
    }

    [Fact]
    public void SearchCode_CommandName_IsSearchCode()
    {
        var handler = new SearchCodeHandler();
        Assert.Equal("SEARCH_CODE", handler.CommandName);
    }

    [Fact]
    public void SearchCode_IsRetrySafe_ReturnsTrue()
    {
        var handler = new SearchCodeHandler();
        Assert.True(handler.IsRetrySafe);
    }

    [Fact]
    public async Task SearchCode_IgnoreCaseWithBoolTrue_FindsMatch()
    {
        var handler = new SearchCodeHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SEARCH_CODE", new()
            {
                ["query"] = "HELLO WORLD",
                ["ignoreCase"] = true // bool, not string
            }), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.True((int)dict["count"]! > 0, "Case-insensitive search with bool flag should find match");
    }

    [Fact]
    public async Task SearchCode_GlobFilter_OnlyMatchesPattern()
    {
        // hello.cs already exists with "hello world", second.txt has "second file"
        var handler = new SearchCodeHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SEARCH_CODE", new()
            {
                ["query"] = "hello",
                ["glob"] = "*.cs"
            }), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var matches = (List<Dictionary<string, object?>>)dict["matches"]!;
        Assert.True(matches.Count > 0, "Should find hello in .cs files");
        Assert.All(matches, m => Assert.EndsWith(".cs", (string)m["file"]!));
    }

    [Fact]
    public async Task SearchCode_GlobAndPath_CombinesFilter()
    {
        Directory.CreateDirectory(Path.Combine(_repoRoot, "sub"));
        File.WriteAllText(Path.Combine(_repoRoot, "sub", "a.cs"), "match_me_here\n");
        File.WriteAllText(Path.Combine(_repoRoot, "sub", "a.txt"), "match_me_here\n");
        File.WriteAllText(Path.Combine(_repoRoot, "b.cs"), "match_me_here\n");
        RunGit("add", "-A");
        RunGit("commit", "-m", "test: glob+path combo");

        var handler = new SearchCodeHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SEARCH_CODE", new()
            {
                ["query"] = "match_me_here",
                ["path"] = "sub",
                ["glob"] = "*.cs"
            }), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var matches = (List<Dictionary<string, object?>>)dict["matches"]!;
        Assert.Single(matches);
        Assert.Equal("sub/a.cs", (string)matches[0]["file"]!);
    }

    [Fact]
    public async Task SearchCode_PathToSpecificFile_SearchesOnlyThatFile()
    {
        var handler = new SearchCodeHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SEARCH_CODE", new()
            {
                ["query"] = "hello",
                ["path"] = "hello.cs"
            }), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var matches = (List<Dictionary<string, object?>>)dict["matches"]!;
        Assert.True(matches.Count > 0);
        Assert.All(matches, m => Assert.Equal("hello.cs", (string)m["file"]!));
    }

    [Fact]
    public async Task SearchCode_Truncation_CapsAtMaxResults()
    {
        // --max-count in git grep is per-file, so spread matches across multiple files
        // to exceed the 50-result cap in total output (10 files × 10 matches = 100 total)
        for (int i = 0; i < 10; i++)
        {
            var lines = string.Join("\n", Enumerable.Range(1, 10).Select(j => $"truncation_marker_{i}_{j}"));
            File.WriteAllText(Path.Combine(_repoRoot, $"bulk-{i}.txt"), lines + "\n");
        }
        RunGit("add", "-A");
        RunGit("commit", "-m", "test: add bulk files");

        var handler = new SearchCodeHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SEARCH_CODE", new() { ["query"] = "truncation_marker_" }), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(50, (int)dict["count"]!);
        Assert.True(dict.ContainsKey("truncated"), "Should indicate truncation");
        Assert.True((bool)dict["truncated"]!, "truncated flag should be true");
        Assert.NotNull(dict["hint"]);
        Assert.Contains("50", (string)dict["hint"]!);
    }

    [Fact]
    public async Task SearchCode_ResultContainsQueryField()
    {
        var handler = new SearchCodeHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SEARCH_CODE", new() { ["query"] = "hello world" }), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("hello world", (string)dict["query"]!);
    }

    [Fact]
    public async Task SearchCode_PathTraversal_ReturnsDenied()
    {
        var handler = new SearchCodeHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SEARCH_CODE", new()
            {
                ["query"] = "test",
                ["path"] = "../../etc"
            }), MakeContext());

        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
        Assert.Contains("Path traversal denied", result.Error!);
    }

    [Fact]
    public async Task SearchCode_ErrorMessage_ContainsMissingArg()
    {
        var handler = new SearchCodeHandler();
        var resultEmpty = await handler.ExecuteAsync(
            MakeCommand("SEARCH_CODE", new() { ["query"] = "  " }), MakeContext());

        Assert.Equal(CommandStatus.Error, resultEmpty.Status);
        Assert.Contains("query", resultEmpty.Error!);
    }

    // ── RUN_BUILD ────────────────────────────────────────────

    [Fact]
    public async Task RunBuild_ReturnsResultWithExitCode()
    {
        // RunBuildHandler runs `dotnet build` — but in our temp dir there's no dotnet project.
        // The handler should still return a structured result (likely an error exit code).
        var handler = new RunBuildHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("RUN_BUILD", new()), MakeContext());

        // We expect it to complete (not throw) and return a result dict
        Assert.NotNull(result.Result);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.True(dict.ContainsKey("exitCode"), "Should have exitCode");
        Assert.True(dict.ContainsKey("output"), "Should have output");
        Assert.True(dict.ContainsKey("success"), "Should have success flag");
    }

    [Fact]
    public async Task RunBuild_FailedBuild_ReturnsErrorStatus()
    {
        // The temp dir has AgentAcademy.sln but no actual projects, so build will fail
        var handler = new RunBuildHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("RUN_BUILD", new()), MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Execution, result.ErrorCode);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.False((bool)dict["success"]!);
    }

    // ── RUN_TESTS ────────────────────────────────────────────

    [Fact]
    public async Task RunTests_ReturnsStructuredResult()
    {
        // Like RunBuild, in temp dir there's no test project — expect a structured error
        var handler = new RunTestsHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("RUN_TESTS", new()), MakeContext());

        Assert.NotNull(result.Result);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.True(dict.ContainsKey("exitCode"));
        Assert.True(dict.ContainsKey("output"));
        Assert.True(dict.ContainsKey("scope"));
        Assert.Equal("all", (string)dict["scope"]!);
    }

    [Fact]
    public async Task RunTests_BackendScope_SetsCorrectScope()
    {
        var handler = new RunTestsHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("RUN_TESTS", new() { ["scope"] = "backend" }), MakeContext());

        Assert.NotNull(result.Result);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("backend", (string)dict["scope"]!);
    }

    [Fact]
    public async Task RunTests_FileFilter_SetsCorrectScope()
    {
        var handler = new RunTestsHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("RUN_TESTS", new() { ["scope"] = "file:SomeTest" }), MakeContext());

        Assert.NotNull(result.Result);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("file:sometest", (string)dict["scope"]!);
    }
}
