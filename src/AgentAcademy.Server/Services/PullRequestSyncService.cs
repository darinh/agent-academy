using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Background service that periodically polls GitHub for PR status changes
/// and updates task entities accordingly. Uses <see cref="IGitHubService"/>
/// for GitHub API calls and scoped task services for updates.
/// </summary>
internal sealed class PullRequestSyncService : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IGitHubService _github;
    private readonly ILogger<PullRequestSyncService> _logger;

    public PullRequestSyncService(
        IServiceScopeFactory scopeFactory,
        IGitHubService github,
        ILogger<PullRequestSyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _github = github;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Brief startup delay to let the rest of the app initialize
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await PollOnceAsync(stoppingToken);

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task PollOnceAsync(CancellationToken ct = default)
    {
        try
        {
            if (!await _github.IsConfiguredAsync())
            {
                _logger.LogDebug("GitHub CLI not configured — skipping PR sync");
                return;
            }

            List<(string TaskId, int PrNumber)> activePrs;
            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
                activePrs = await taskQueries.GetTasksWithActivePrsAsync();
            }

            if (activePrs.Count == 0)
            {
                _logger.LogDebug("No active PRs to sync");
                return;
            }

            _logger.LogInformation("Syncing {Count} active PR(s)", activePrs.Count);

            foreach (var (taskId, prNumber) in activePrs)
            {
                if (ct.IsCancellationRequested) break;
                await SyncSinglePrAsync(taskId, prNumber, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — don't log
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PR sync poll failed");
        }
    }

    private async Task SyncSinglePrAsync(string taskId, int prNumber, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var prInfo = await _github.GetPullRequestAsync(prNumber);

            ct.ThrowIfCancellationRequested();

            var newStatus = MapToPrStatus(prInfo);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();

            var updated = await taskLifecycle.SyncTaskPrStatusAsync(taskId, newStatus);
            if (updated != null)
            {
                _logger.LogInformation(
                    "Task {TaskId} PR #{PrNumber} status → {Status}",
                    taskId, prNumber, newStatus);
            }
        }
        catch (OperationCanceledException)
        {
            throw; // Let cancellation propagate to the caller
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync PR #{PrNumber} for task {TaskId}", prNumber, taskId);
        }
    }

    internal static PullRequestStatus MapToPrStatus(PullRequestInfo pr)
    {
        if (pr.IsMerged)
            return PullRequestStatus.Merged;

        if (string.Equals(pr.State, "CLOSED", StringComparison.OrdinalIgnoreCase))
            return PullRequestStatus.Closed;

        // OPEN state — check review decision
        return pr.ReviewDecision?.ToUpperInvariant() switch
        {
            "APPROVED" => PullRequestStatus.Approved,
            "CHANGES_REQUESTED" => PullRequestStatus.ChangesRequested,
            "REVIEW_REQUIRED" => PullRequestStatus.ReviewRequested,
            _ => PullRequestStatus.Open
        };
    }
}
