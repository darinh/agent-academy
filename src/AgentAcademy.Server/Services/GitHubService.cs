using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// GitHub integration via the gh CLI. Follows the same process-shell pattern
/// as <see cref="GitService"/> for consistency and testability.
/// </summary>
public sealed class GitHubService : IGitHubService
{
    private readonly ILogger<GitHubService> _logger;
    private readonly string _repositoryRoot;
    private readonly string _ghExecutable;

    public GitHubService(
        ILogger<GitHubService> logger,
        string? repositoryRoot = null,
        string ghExecutable = "gh")
    {
        _logger = logger;
        _repositoryRoot = repositoryRoot ?? FindProjectRoot();
        _ghExecutable = string.IsNullOrWhiteSpace(ghExecutable) ? "gh" : ghExecutable;
    }

    public async Task<bool> IsConfiguredAsync()
    {
        try
        {
            // Use exit code rather than parsing locale-dependent stdout text
            await RunGhAsync("auth", "status");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> GetRepositorySlugAsync()
    {
        try
        {
            var slug = await RunGhAsync("repo", "view", "--json", "nameWithOwner", "-q", ".nameWithOwner");
            return string.IsNullOrWhiteSpace(slug) ? null : slug;
        }
        catch
        {
            return null;
        }
    }

    public async Task<PullRequestInfo> CreatePullRequestAsync(
        string branch, string title, string body, string baseBranch = "develop")
    {
        if (string.IsNullOrWhiteSpace(branch))
            throw new ArgumentException("Branch name is required.", nameof(branch));
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("PR title is required.", nameof(title));

        var json = await RunGhAsync(
            "pr", "create",
            "--head", branch,
            "--base", baseBranch,
            "--title", title,
            "--body", body ?? string.Empty,
            "--json", "number,url,state,title,baseRefName,headRefName");

        return ParsePrJson(json);
    }

    public async Task<PullRequestInfo> GetPullRequestAsync(int prNumber)
    {
        var json = await RunGhAsync(
            "pr", "view", prNumber.ToString(),
            "--json", "number,url,state,title,baseRefName,headRefName,mergedAt,reviewDecision");

        return ParsePrJson(json);
    }

    private static PullRequestInfo ParsePrJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var number = root.GetProperty("number").GetInt32();
        var url = root.GetProperty("url").GetString() ?? string.Empty;
        var state = root.GetProperty("state").GetString() ?? "UNKNOWN";
        var title = root.GetProperty("title").GetString() ?? string.Empty;
        var baseBranch = root.GetProperty("baseRefName").GetString() ?? string.Empty;
        var headBranch = root.GetProperty("headRefName").GetString() ?? string.Empty;

        // mergedAt is present in pr view but not pr create; treat absence as not merged
        var isMerged = root.TryGetProperty("mergedAt", out var mergedAt)
            && mergedAt.ValueKind != JsonValueKind.Null;

        // reviewDecision is present in pr view but not pr create
        string? reviewDecision = null;
        if (root.TryGetProperty("reviewDecision", out var rd) && rd.ValueKind == JsonValueKind.String)
            reviewDecision = rd.GetString();

        return new PullRequestInfo(number, url, state, title, baseBranch, headBranch, isMerged, reviewDecision);
    }

    /// <summary>
    /// Runs a gh CLI command and returns stdout. Throws on non-zero exit.
    /// Reads stdout/stderr concurrently to avoid deadlock when pipe buffers fill.
    /// </summary>
    internal async Task<string> RunGhAsync(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ghExecutable,
            WorkingDirectory = _repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        _logger.LogDebug("Running: {GhExecutable} {Args}", _ghExecutable, string.Join(" ", args));

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start gh process");

        // Read both streams concurrently to prevent deadlock when pipe buffers fill
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                $"gh {string.Join(" ", args)} timed out after 60s");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var message = $"gh {string.Join(" ", args)} failed (exit {process.ExitCode}): {stderr.Trim()}";
            _logger.LogWarning("{Message}", message);
            throw new InvalidOperationException(message);
        }

        return stdout.Trim();
    }

    private static string FindProjectRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "AgentAcademy.sln")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
