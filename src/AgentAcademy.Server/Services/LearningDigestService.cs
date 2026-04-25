using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using AgentAcademy.Server.Services.AgentWatchdog;
using AgentAcademy.Server.Services.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Periodically synthesizes retrospective summaries into cross-cutting
/// shared memories. When enough undigested retrospectives accumulate
/// (configurable threshold), the planner agent reviews them and stores
/// the most important cross-cutting learnings as shared memories.
///
/// Singleton with its own DI scopes (same pattern as RetrospectiveService).
/// Thread-safe: uses a semaphore to prevent concurrent digest generation.
/// </summary>
public sealed class LearningDigestService : Contracts.ILearningDigestService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAgentCatalog _catalog;
    private readonly IAgentExecutor _executor;
    private readonly IWatchdogAgentRunner _watchdogRunner;
    private readonly CommandPipeline _commandPipeline;
    private readonly ILogger<LearningDigestService> _logger;
    private readonly SemaphoreSlim _digestLock = new(1, 1);
    private volatile bool _pendingRerun;

    private const string PlannerAgentId = "planner-1";
    private const string DigestRoomPrefix = "digest:";

    public LearningDigestService(
        IServiceScopeFactory scopeFactory,
        IAgentCatalog catalog,
        IAgentExecutor executor,
        IWatchdogAgentRunner watchdogRunner,
        CommandPipeline commandPipeline,
        ILogger<LearningDigestService> logger)
    {
        _scopeFactory = scopeFactory;
        _catalog = catalog;
        _executor = executor;
        _watchdogRunner = watchdogRunner;
        _commandPipeline = commandPipeline;
        _logger = logger;
    }

    /// <summary>
    /// Attempts to generate a learning digest. If <paramref name="force"/> is false,
    /// only generates when undigested retrospectives meet the configured threshold.
    /// Returns the digest ID if one was created, or null if skipped.
    /// </summary>
    public async Task<int?> TryGenerateDigestAsync(bool force = false)
    {
        if (!await _digestLock.WaitAsync(TimeSpan.Zero))
        {
            // Signal the running digest to check again when it finishes
            _pendingRerun = true;
            _logger.LogDebug("Digest generation already in progress, queued rerun");
            return null;
        }

        try
        {
            int? result = null;
            do
            {
                _pendingRerun = false;
                var iterationResult = await GenerateDigestCoreAsync(force);
                result ??= iterationResult;
                // Only honour rerun for non-forced runs (forced is one-shot)
                force = false;
            } while (_pendingRerun);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Learning digest generation failed");
            return null;
        }
        finally
        {
            _digestLock.Release();
        }
    }

    private async Task<int?> GenerateDigestCoreAsync(bool force)
    {
        var planner = _catalog.Agents.FirstOrDefault(a => a.Id == PlannerAgentId);
        if (planner is null)
        {
            _logger.LogWarning("Digest skipped: planner agent {AgentId} not found", PlannerAgentId);
            return null;
        }

        // Gather undigested retrospective comments
        var undigested = await GetUndigestedRetrospectivesAsync();
        if (undigested.Count == 0)
        {
            _logger.LogDebug("No undigested retrospectives found");
            return null;
        }

        if (!force)
        {
            int threshold;
            using (var scope = _scopeFactory.CreateScope())
            {
                var settings = scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();
                threshold = await settings.GetDigestThresholdAsync();
            }

            if (undigested.Count < threshold)
            {
                _logger.LogDebug(
                    "Only {Count} undigested retrospectives (threshold: {Threshold}), skipping",
                    undigested.Count, threshold);
                return null;
            }
        }

        _logger.LogInformation(
            "Generating learning digest from {Count} retrospectives", undigested.Count);

        // Claim the retrospectives transactionally by creating the digest + sources (status: Pending)
        int digestId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

            var digest = new LearningDigestEntity
            {
                CreatedAt = DateTime.UtcNow,
                Summary = string.Empty,
                MemoriesCreated = 0,
                RetrospectivesProcessed = undigested.Count,
                Status = "Pending",
                Sources = undigested.Select(r => new LearningDigestSourceEntity
                {
                    RetrospectiveCommentId = r.CommentId
                }).ToList()
            };

            db.LearningDigests.Add(digest);

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                _logger.LogWarning("Digest source conflict — another digest claimed these retrospectives");
                return null;
            }

            digestId = digest.Id;
        }

        // Build the prompt
        var prompt = PromptBuilder.BuildDigestPrompt(planner, undigested);

        // Create a restricted planner clone: REMEMBER only, no tools
        var digestAgent = planner with
        {
            Permissions = new CommandPermissionSet(
                Allowed: new List<string> { "REMEMBER" },
                Denied: new List<string>()),
            EnabledTools = new List<string>()
        };

        var digestRoomId = $"{DigestRoomPrefix}{digestId}";
        bool sessionStarted = false;

        try
        {
            sessionStarted = true;
            var response = await _watchdogRunner.RunAsync(digestAgent, prompt, digestRoomId);

            if (string.IsNullOrWhiteSpace(response))
            {
                _logger.LogWarning("Digest {DigestId}: planner returned empty response", digestId);
                await UpdateDigestStatusAsync(digestId, "Completed", string.Empty, 0);
                return digestId;
            }

            // Process REMEMBER commands from the response
            int memoriesCreated = 0;
            string remainingText;
            using (var scope = _scopeFactory.CreateScope())
            {
                var pipelineResult = await _commandPipeline.ProcessResponseAsync(
                    planner.Id, response, digestRoomId, digestAgent, scope.ServiceProvider);
                remainingText = pipelineResult.RemainingText;

                // Count successful REMEMBER commands
                memoriesCreated = pipelineResult.Results
                    .Count(r => r.Command == "REMEMBER" && r.Status == CommandStatus.Success);

                // Enforce shared category: update any non-shared memories just created
                if (memoriesCreated > 0)
                {
                    await EnforceSharedCategoryAsync(scope, planner.Id, pipelineResult);
                }
            }

            // Update the digest with results (mark Completed)
            var summary = remainingText.Trim();
            await UpdateDigestStatusAsync(digestId, "Completed", summary, memoriesCreated);

            // Publish activity event
            using (var scope = _scopeFactory.CreateScope())
            {
                var activity = scope.ServiceProvider.GetRequiredService<IActivityPublisher>();
                activity.Publish(
                    ActivityEventType.LearningDigestCompleted,
                    null,
                    planner.Id,
                    null,
                    $"Learning digest #{digestId} completed: synthesized {undigested.Count} retrospectives into {memoriesCreated} shared memories");

                var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
                await db.SaveChangesAsync();
            }

            _logger.LogInformation(
                "Learning digest #{DigestId} completed: {RetroCount} retrospectives → {MemoryCount} shared memories",
                digestId, undigested.Count, memoriesCreated);

            return digestId;
        }
        catch (Exception)
        {
            // Release the claimed retrospectives so they can be retried
            await ReleaseFailedDigestAsync(digestId);
            throw;
        }
        finally
        {
            if (sessionStarted)
            {
                try { await _executor.InvalidateSessionAsync(planner.Id, digestRoomId); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to invalidate digest session"); }
            }
        }
    }

    private async Task UpdateDigestStatusAsync(int digestId, string status, string summary, int memoriesCreated)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var entity = await db.LearningDigests.FindAsync(digestId);
        if (entity is not null)
        {
            entity.Status = status;
            entity.Summary = summary;
            entity.MemoriesCreated = memoriesCreated;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Marks a failed digest and deletes its source claims so the retrospectives
    /// become available for a future digest.
    /// </summary>
    private async Task ReleaseFailedDigestAsync(int digestId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

            var sources = await db.LearningDigestSources
                .Where(s => s.DigestId == digestId)
                .ToListAsync();
            db.LearningDigestSources.RemoveRange(sources);

            var entity = await db.LearningDigests.FindAsync(digestId);
            if (entity is not null)
            {
                entity.Status = "Failed";
            }

            await db.SaveChangesAsync();
            _logger.LogInformation("Released {Count} retrospectives from failed digest #{DigestId}", sources.Count, digestId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release digest #{DigestId} sources", digestId);
        }
    }

    /// <summary>
    /// Returns retrospective comments not yet claimed by any digest.
    /// </summary>
    internal async Task<List<DigestRetrospective>> GetUndigestedRetrospectivesAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        // Only exclude retrospectives claimed by Completed digests.
        // Failed digests release their claims; Pending digests are in-flight.
        var undigested = await db.TaskComments
            .Where(c => c.CommentType == nameof(TaskCommentType.Retrospective))
            .Where(c => !db.LearningDigestSources
                .Any(s => s.RetrospectiveCommentId == c.Id
                    && s.Digest!.Status == "Completed"))
            .OrderBy(c => c.CreatedAt)
            .Select(c => new DigestRetrospective(
                c.Id,
                c.TaskId,
                c.AgentName,
                c.Content,
                c.CreatedAt,
                db.Tasks.Where(t => t.Id == c.TaskId).Select(t => t.Title).FirstOrDefault() ?? c.TaskId
            ))
            .ToListAsync();

        return undigested;
    }

    /// <summary>
    /// Ensures all memories created by the planner during digest processing
    /// use the 'shared' category. Defense-in-depth against prompt non-compliance.
    /// </summary>
    private static async Task EnforceSharedCategoryAsync(
        IServiceScope scope, string plannerId, CommandPipelineResult pipelineResult)
    {
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var rememberedKeys = pipelineResult.Results
            .Where(r => r.Command == "REMEMBER" && r.Status == CommandStatus.Success)
            .Select(r => r.Args.TryGetValue("key", out var k) && k is string ks ? ks : null)
            .Where(k => k is not null)
            .ToList();

        if (rememberedKeys.Count == 0) return;

        var nonShared = await db.AgentMemories
            .Where(m => m.AgentId == plannerId && rememberedKeys.Contains(m.Key) && m.Category != "shared")
            .ToListAsync();

        foreach (var memory in nonShared)
        {
            memory.Category = "shared";
        }

        if (nonShared.Count > 0)
        {
            await db.SaveChangesAsync();
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        return ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true;
    }
}

/// <summary>
/// A retrospective summary ready for digest processing.
/// </summary>
public record DigestRetrospective(
    string CommentId,
    string TaskId,
    string AgentName,
    string Content,
    DateTime CreatedAt,
    string TaskTitle
);
