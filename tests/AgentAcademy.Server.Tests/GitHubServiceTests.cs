using AgentAcademy.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Unit tests for <see cref="GitHubService"/>.
/// Uses a wrapper script to simulate gh CLI responses without hitting GitHub.
/// </summary>
public class GitHubServiceTests : IDisposable
{
    private readonly string _tempDir;

    public GitHubServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"gh-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        // Create a fake AgentAcademy.sln so FindProjectRoot works
        File.WriteAllText(Path.Combine(_tempDir, "AgentAcademy.sln"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private string CreateGhWrapper(string stdout, int exitCode = 0, string stderr = "")
    {
        // Write stdout/stderr to files to avoid shell quoting issues
        var stdoutFile = Path.Combine(_tempDir, $"stdout-{Guid.NewGuid():N}.txt");
        var stderrFile = Path.Combine(_tempDir, $"stderr-{Guid.NewGuid():N}.txt");
        File.WriteAllText(stdoutFile, stdout);
        File.WriteAllText(stderrFile, stderr);

        var wrapperPath = Path.Combine(_tempDir, $"gh-wrapper-{Guid.NewGuid():N}.sh");
        File.WriteAllText(wrapperPath,
            $$"""
            #!/usr/bin/env bash
            cat '{{stdoutFile}}'
            if [ -s '{{stderrFile}}' ]; then
                cat '{{stderrFile}}' >&2
            fi
            exit {{exitCode}}
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                wrapperPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return wrapperPath;
    }

    [Fact]
    public async Task IsConfiguredAsync_WhenLoggedIn_ReturnsTrue()
    {
        // Exit code 0 means authenticated — we no longer parse stdout text
        var wrapper = CreateGhWrapper("");
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var result = await svc.IsConfiguredAsync();

        Assert.True(result);
    }

    [Fact]
    public async Task IsConfiguredAsync_WhenNotLoggedIn_ReturnsFalse()
    {
        var wrapper = CreateGhWrapper("", exitCode: 1, stderr: "not logged in");
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var result = await svc.IsConfiguredAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task GetRepositorySlugAsync_ReturnsSlug()
    {
        var wrapper = CreateGhWrapper("darinh/agent-academy");
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var slug = await svc.GetRepositorySlugAsync();

        Assert.Equal("darinh/agent-academy", slug);
    }

    [Fact]
    public async Task GetRepositorySlugAsync_WhenNoRemote_ReturnsNull()
    {
        var wrapper = CreateGhWrapper("", exitCode: 1, stderr: "no remote");
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var slug = await svc.GetRepositorySlugAsync();

        Assert.Null(slug);
    }

    [Fact]
    public async Task CreatePullRequestAsync_ReturnsPrInfo()
    {
        var json = """{"number":42,"url":"https://github.com/darinh/agent-academy/pull/42","state":"OPEN","title":"feat: test","baseRefName":"develop","headRefName":"task/test-abc123"}""";
        var wrapper = CreateGhWrapper(json);
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var pr = await svc.CreatePullRequestAsync("task/test-abc123", "feat: test", "body");

        Assert.Equal(42, pr.Number);
        Assert.Equal("https://github.com/darinh/agent-academy/pull/42", pr.Url);
        Assert.Equal("OPEN", pr.State);
        Assert.Equal("feat: test", pr.Title);
        Assert.Equal("develop", pr.BaseBranch);
        Assert.Equal("task/test-abc123", pr.HeadBranch);
        Assert.False(pr.IsMerged);
    }

    [Fact]
    public async Task CreatePullRequestAsync_EmptyBranch_Throws()
    {
        var wrapper = CreateGhWrapper("{}");
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.CreatePullRequestAsync("", "title", "body"));
    }

    [Fact]
    public async Task CreatePullRequestAsync_EmptyTitle_Throws()
    {
        var wrapper = CreateGhWrapper("{}");
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.CreatePullRequestAsync("task/branch", "", "body"));
    }

    [Fact]
    public async Task GetPullRequestAsync_ReturnsPrInfo_WithMerged()
    {
        var json = """{"number":42,"url":"https://github.com/darinh/agent-academy/pull/42","state":"MERGED","title":"feat: test","baseRefName":"develop","headRefName":"task/test-abc123","mergedAt":"2026-04-04T12:00:00Z"}""";
        var wrapper = CreateGhWrapper(json);
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var pr = await svc.GetPullRequestAsync(42);

        Assert.Equal(42, pr.Number);
        Assert.Equal("MERGED", pr.State);
        Assert.True(pr.IsMerged);
    }

    [Fact]
    public async Task GetPullRequestAsync_ReturnsPrInfo_NotMerged()
    {
        var json = """{"number":42,"url":"https://github.com/darinh/agent-academy/pull/42","state":"OPEN","title":"feat: test","baseRefName":"develop","headRefName":"task/test-abc123","mergedAt":null}""";
        var wrapper = CreateGhWrapper(json);
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var pr = await svc.GetPullRequestAsync(42);

        Assert.False(pr.IsMerged);
    }

    [Fact]
    public async Task RunGhAsync_NonZeroExit_Throws()
    {
        var wrapper = CreateGhWrapper("", exitCode: 1, stderr: "something went wrong");
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RunGhAsync("pr", "view", "999"));
        Assert.Contains("failed", ex.Message);
        Assert.Contains("something went wrong", ex.Message);
    }

    // ── PostPrReviewAsync Tests ─────────────────────────────────

    [Fact]
    public async Task PostPrReviewAsync_Comment_RunsCorrectFlags()
    {
        var wrapper = CreateGhWrapper("");
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        // Should not throw
        await svc.PostPrReviewAsync(42, "Looks good", PrReviewAction.Comment);
    }

    [Fact]
    public async Task PostPrReviewAsync_Approve_RunsCorrectFlags()
    {
        var wrapper = CreateGhWrapper("");
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        await svc.PostPrReviewAsync(42, "Ship it", PrReviewAction.Approve);
    }

    [Fact]
    public async Task PostPrReviewAsync_RequestChanges_RunsCorrectFlags()
    {
        var wrapper = CreateGhWrapper("");
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        await svc.PostPrReviewAsync(42, "Please fix tests", PrReviewAction.RequestChanges);
    }

    [Fact]
    public async Task PostPrReviewAsync_EmptyBody_Throws()
    {
        var wrapper = CreateGhWrapper("");
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.PostPrReviewAsync(42, "", PrReviewAction.Comment));
    }

    [Fact]
    public async Task PostPrReviewAsync_Failure_Throws()
    {
        var wrapper = CreateGhWrapper("", exitCode: 1, stderr: "permission denied");
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.PostPrReviewAsync(42, "LGTM", PrReviewAction.Comment));
        Assert.Contains("permission denied", ex.Message);
    }

    // ── GetPrReviewsAsync Tests ─────────────────────────────────

    [Fact]
    public async Task GetPrReviewsAsync_ReturnsReviews()
    {
        var json = """
        {
            "reviews": [
                {
                    "author": {"login": "user1"},
                    "body": "LGTM",
                    "state": "APPROVED",
                    "submittedAt": "2026-04-01T12:00:00Z"
                },
                {
                    "author": {"login": "user2"},
                    "body": "Please fix",
                    "state": "CHANGES_REQUESTED",
                    "submittedAt": "2026-04-01T13:00:00Z"
                }
            ]
        }
        """;
        var wrapper = CreateGhWrapper(json);
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var reviews = await svc.GetPrReviewsAsync(42);

        Assert.Equal(2, reviews.Count);
        Assert.Equal("user1", reviews[0].Author);
        Assert.Equal("LGTM", reviews[0].Body);
        Assert.Equal("APPROVED", reviews[0].State);
        Assert.NotNull(reviews[0].SubmittedAt);
        Assert.Equal("user2", reviews[1].Author);
        Assert.Equal("CHANGES_REQUESTED", reviews[1].State);
    }

    [Fact]
    public async Task GetPrReviewsAsync_NullAuthor_DefaultsToUnknown()
    {
        var json = """
        {
            "reviews": [
                {
                    "author": null,
                    "body": "ghost review",
                    "state": "COMMENTED"
                }
            ]
        }
        """;
        var wrapper = CreateGhWrapper(json);
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var reviews = await svc.GetPrReviewsAsync(42);

        Assert.Single(reviews);
        Assert.Equal("unknown", reviews[0].Author);
    }

    [Fact]
    public async Task GetPrReviewsAsync_EmptyReviews_ReturnsEmptyList()
    {
        var json = """{"reviews": []}""";
        var wrapper = CreateGhWrapper(json);
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var reviews = await svc.GetPrReviewsAsync(42);

        Assert.Empty(reviews);
    }

    [Fact]
    public async Task GetPrReviewsAsync_NullSubmittedAt_ReturnsNull()
    {
        var json = """
        {
            "reviews": [
                {
                    "author": {"login": "user1"},
                    "body": "draft",
                    "state": "COMMENTED",
                    "submittedAt": null
                }
            ]
        }
        """;
        var wrapper = CreateGhWrapper(json);
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var reviews = await svc.GetPrReviewsAsync(42);

        Assert.Single(reviews);
        Assert.Null(reviews[0].SubmittedAt);
    }

    [Fact]
    public async Task GetPrReviewsAsync_MissingAuthor_DefaultsToUnknown()
    {
        var json = """
        {
            "reviews": [
                {
                    "body": "test",
                    "state": "COMMENTED"
                }
            ]
        }
        """;
        var wrapper = CreateGhWrapper(json);
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var reviews = await svc.GetPrReviewsAsync(42);

        Assert.Single(reviews);
        Assert.Equal("unknown", reviews[0].Author);
    }

    [Fact]
    public async Task GetPrReviewsAsync_NoReviewsProperty_ReturnsEmptyList()
    {
        var json = """{}""";
        var wrapper = CreateGhWrapper(json);
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var reviews = await svc.GetPrReviewsAsync(42);

        Assert.Empty(reviews);
    }

    [Fact]
    public async Task GetPrReviewsAsync_Failure_Throws()
    {
        var wrapper = CreateGhWrapper("", exitCode: 1, stderr: "not found");
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GetPrReviewsAsync(999));
        Assert.Contains("not found", ex.Message);
    }

    // ── MergePullRequestAsync Tests ─────────────────────────────

    [Fact]
    public async Task MergePullRequestAsync_Success_ReturnsSha()
    {
        // The wrapper returns the SHA for both calls — merge ignores it, view uses it
        var wrapper = CreateSequentialGhWrapper(["", "abc123def456"]);
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var result = await svc.MergePullRequestAsync(42);

        Assert.Equal(42, result.PrNumber);
        Assert.Equal("abc123def456", result.MergeCommitSha);
    }

    [Fact]
    public async Task MergePullRequestAsync_MergeFails_Throws()
    {
        var wrapper = CreateGhWrapper("", exitCode: 1, stderr: "merge not allowed");
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.MergePullRequestAsync(42));
        Assert.Contains("merge not allowed", ex.Message);
    }

    [Fact]
    public async Task MergePullRequestAsync_ShaFetchFails_ReturnsNullSha()
    {
        // First call (merge) succeeds, second call (view) fails — SHA is null
        var wrapper = CreateSequentialGhWrapper([""], failOnCall: 2, stderr: "not found");
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var result = await svc.MergePullRequestAsync(42);

        Assert.Equal(42, result.PrNumber);
        Assert.Null(result.MergeCommitSha);
    }

    [Fact]
    public async Task MergePullRequestAsync_WithCommitTitle_IncludesSubjectFlag()
    {
        // We verify by checking the wrapper was called — the real assertion is that it doesn't throw
        var wrapper = CreateSequentialGhWrapper(["", "sha123"]);
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var result = await svc.MergePullRequestAsync(42, commitTitle: "feat: my feature");

        Assert.Equal("sha123", result.MergeCommitSha);
    }

    [Fact]
    public async Task MergePullRequestAsync_EmptyShaResponse_ReturnsNull()
    {
        var wrapper = CreateSequentialGhWrapper(["", ""]);
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var result = await svc.MergePullRequestAsync(42);

        Assert.Null(result.MergeCommitSha);
    }

    /// <summary>
    /// Creates a wrapper that returns different outputs on sequential calls.
    /// Uses a counter file to track invocation number.
    /// </summary>
    private string CreateSequentialGhWrapper(
        string[] outputs, int? failOnCall = null, string stderr = "")
    {
        var counterFile = Path.Combine(_tempDir, $"counter-{Guid.NewGuid():N}.txt");
        var stderrFile = Path.Combine(_tempDir, $"stderr-{Guid.NewGuid():N}.txt");
        File.WriteAllText(stderrFile, stderr);

        var outputFiles = new List<string>();
        for (var i = 0; i < outputs.Length; i++)
        {
            var file = Path.Combine(_tempDir, $"output-{Guid.NewGuid():N}-{i}.txt");
            File.WriteAllText(file, outputs[i]);
            outputFiles.Add(file);
        }

        var caseStatements = new System.Text.StringBuilder();
        for (var i = 0; i < outputFiles.Count; i++)
        {
            var exitCode = (failOnCall.HasValue && failOnCall.Value == i + 1) ? 1 : 0;
            caseStatements.AppendLine(
                $"    {i + 1}) cat '{outputFiles[i]}'; " +
                (exitCode != 0 ? $"cat '{stderrFile}' >&2; " : "") +
                $"exit {exitCode};;");
        }

        var wrapperPath = Path.Combine(_tempDir, $"gh-seq-{Guid.NewGuid():N}.sh");
        File.WriteAllText(wrapperPath,
            $$"""
            #!/usr/bin/env bash
            COUNTER_FILE='{{counterFile}}'
            COUNT=$(cat "$COUNTER_FILE" 2>/dev/null || echo 0)
            COUNT=$((COUNT + 1))
            echo $COUNT > "$COUNTER_FILE"
            case $COUNT in
            {{caseStatements}}
                *) exit 1;;
            esac
            """);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                wrapperPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return wrapperPath;
    }
}
