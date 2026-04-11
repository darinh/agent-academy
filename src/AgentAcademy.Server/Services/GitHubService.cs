using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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

    public async Task PostPrReviewAsync(int prNumber, string body, PrReviewAction action = PrReviewAction.Comment)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Review body is required.", nameof(body));

        var flag = action switch
        {
            PrReviewAction.Approve => "--approve",
            PrReviewAction.RequestChanges => "--request-changes",
            _ => "--comment"
        };

        await RunGhAsync("pr", "review", prNumber.ToString(), flag, "--body", body);
    }

    public async Task<IReadOnlyList<PullRequestReview>> GetPrReviewsAsync(int prNumber)
    {
        var json = await RunGhAsync(
            "pr", "view", prNumber.ToString(),
            "--json", "reviews");

        return ParseReviewsJson(json);
    }

    public async Task<PrMergeResult> MergePullRequestAsync(int prNumber, string? commitTitle = null, bool deleteBranch = false)
    {
        var args = new List<string> { "pr", "merge", prNumber.ToString(), "--squash" };

        if (!string.IsNullOrWhiteSpace(commitTitle))
        {
            args.Add("--subject");
            args.Add(commitTitle);
        }

        if (deleteBranch)
            args.Add("--delete-branch");

        await RunGhAsync(args.ToArray());

        // Fetch the merge commit SHA from the now-merged PR
        string? mergeCommitSha = null;
        try
        {
            var sha = await RunGhAsync(
                "pr", "view", prNumber.ToString(),
                "--json", "mergeCommit", "-q", ".mergeCommit.oid");
            mergeCommitSha = string.IsNullOrWhiteSpace(sha) ? null : sha;
        }
        catch
        {
            // Best-effort — the PR is already merged, just can't get the SHA
        }

        return new PrMergeResult(prNumber, mergeCommitSha);
    }

    private static IReadOnlyList<PullRequestReview> ParseReviewsJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("reviews", out var reviews) || reviews.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<PullRequestReview>();
        foreach (var review in reviews.EnumerateArray())
        {
            var author = review.TryGetProperty("author", out var authorEl)
                && authorEl.ValueKind == JsonValueKind.Object
                && authorEl.TryGetProperty("login", out var loginEl)
                ? loginEl.GetString() ?? "unknown"
                : "unknown";

            var body = review.TryGetProperty("body", out var bodyEl)
                ? bodyEl.GetString() ?? string.Empty
                : string.Empty;

            var state = review.TryGetProperty("state", out var stateEl)
                ? stateEl.GetString() ?? "COMMENTED"
                : "COMMENTED";

            DateTime? submittedAt = null;
            if (review.TryGetProperty("submittedAt", out var submittedEl)
                && submittedEl.ValueKind == JsonValueKind.String)
            {
                if (DateTimeOffset.TryParse(submittedEl.GetString(),
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                    submittedAt = parsed.UtcDateTime;
            }

            result.Add(new PullRequestReview(author, body, state, submittedAt));
        }

        return result;
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

        // Retry on ETXTBSY (Linux "Text file busy") — a race between concurrent
        // fork() inheriting a write fd on the executable and our execve() call.
        // Can also occur in production if the gh binary is updated while running.
        const int maxStartAttempts = 3;
        Process? process = null;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                process = Process.Start(psi);
                break;
            }
            catch (Win32Exception ex) when (
                attempt < maxStartAttempts && ex.NativeErrorCode == 26 /* ETXTBSY */)
            {
                _logger.LogDebug("ETXTBSY on attempt {Attempt}, retrying in {Delay}ms",
                    attempt, 10 * attempt);
                await Task.Delay(10 * attempt);
            }
        }

        if (process is null)
            throw new InvalidOperationException("Failed to start gh process");

        using (process)
        {
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
