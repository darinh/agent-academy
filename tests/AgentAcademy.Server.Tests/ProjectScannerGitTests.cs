using System.Diagnostics;
using AgentAcademy.Server.Services;

namespace AgentAcademy.Server.Tests;

public class ProjectScannerGitTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { ForceDeleteDirectory(dir); } catch { }
    }

    [Theory]
    [InlineData("https://github.com/org/repo.git", "github")]
    [InlineData("git@github.com:org/repo.git", "github")]
    [InlineData("https://dev.azure.com/org/project/_git/repo", "azure-devops")]
    [InlineData("https://org.visualstudio.com/project/_git/repo", "azure-devops")]
    [InlineData("https://gitlab.com/org/repo.git", "gitlab")]
    [InlineData("git@gitlab.example.com:org/repo.git", "gitlab")]
    [InlineData("https://bitbucket.org/org/repo.git", "bitbucket")]
    [InlineData("https://internal.server/repo", null)]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void ParseHostProvider_DetectsProvider(string? url, string? expected)
    {
        var result = ProjectScanner.ParseHostProvider(url);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ScanProject_DetectsGitRemoteUrl()
    {
        var repoDir = CreateRepoWithRemote("https://github.com/test/repo.git");
        var scanner = new ProjectScanner();
        var result = scanner.ScanProject(repoDir);
        Assert.True(result.IsGitRepo);
        Assert.Equal("https://github.com/test/repo.git", result.RepositoryUrl);
        Assert.Equal("github", result.HostProvider);
    }

    [Fact]
    public void ScanProject_DetectsDefaultBranch_Develop()
    {
        var repoDir = CreateRepoWithBranches("develop", "main");
        var scanner = new ProjectScanner();
        var result = scanner.ScanProject(repoDir);
        Assert.Equal("develop", result.DefaultBranch);
    }

    [Fact]
    public void ScanProject_DetectsDefaultBranch_Main()
    {
        var repoDir = CreateRepoWithBranches("main");
        var scanner = new ProjectScanner();
        var result = scanner.ScanProject(repoDir);
        Assert.Equal("main", result.DefaultBranch);
    }

    [Fact]
    public void ScanProject_NoGitDir_ReturnsNulls()
    {
        var dir = CreateTempDir("no-git");
        var scanner = new ProjectScanner();
        var result = scanner.ScanProject(dir);
        Assert.False(result.IsGitRepo);
        Assert.Null(result.RepositoryUrl);
        Assert.Null(result.DefaultBranch);
        Assert.Null(result.HostProvider);
    }

    [Fact]
    public void ScanProject_NoRemote_ReturnsNullUrl()
    {
        var repoDir = CreateTempDir("no-remote");
        RunGit(repoDir, "init");
        RunGit(repoDir, "config", "user.name", "Test");
        RunGit(repoDir, "config", "user.email", "t@t.local");
        RunGit(repoDir, "checkout", "-b", "main");
        File.WriteAllText(Path.Combine(repoDir, "f.txt"), "x");
        RunGit(repoDir, "add", "f.txt");
        RunGit(repoDir, "commit", "-m", "init");
        var scanner = new ProjectScanner();
        var result = scanner.ScanProject(repoDir);
        Assert.True(result.IsGitRepo);
        Assert.Null(result.RepositoryUrl);
        Assert.Null(result.HostProvider);
        Assert.Equal("main", result.DefaultBranch);
    }

    private string CreateTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private string CreateRepoWithRemote(string remoteUrl)
    {
        var dir = CreateTempDir("scanner-remote");
        RunGit(dir, "init");
        RunGit(dir, "config", "user.name", "Test");
        RunGit(dir, "config", "user.email", "t@t.local");
        RunGit(dir, "checkout", "-b", "main");
        File.WriteAllText(Path.Combine(dir, "README.md"), "init");
        RunGit(dir, "add", "README.md");
        RunGit(dir, "commit", "-m", "init");
        RunGit(dir, "remote", "add", "origin", remoteUrl);
        return dir;
    }

    private string CreateRepoWithBranches(params string[] branchNames)
    {
        var dir = CreateTempDir("scanner-branches");
        RunGit(dir, "init");
        RunGit(dir, "config", "user.name", "Test");
        RunGit(dir, "config", "user.email", "t@t.local");
        RunGit(dir, "checkout", "-b", "temp-init");
        File.WriteAllText(Path.Combine(dir, "README.md"), "init");
        RunGit(dir, "add", "README.md");
        RunGit(dir, "commit", "-m", "init");
        foreach (var branch in branchNames) RunGit(dir, "branch", branch);
        RunGit(dir, "checkout", branchNames[0]);
        return dir;
    }

    private static string RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git", WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {stderr.Trim()}");
        return stdout.Trim();
    }

    private static void ForceDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
