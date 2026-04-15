using System.Security.Cryptography;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using AgentAcademy.Server.Services.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Config;

/// <summary>
/// Watches agents.json for changes and hot-reloads the catalog.
/// Uses FileSystemWatcher for immediate detection plus a periodic
/// hash-based poll as a reliability fallback.
/// </summary>
public sealed class AgentCatalogWatcher : BackgroundService
{
    private readonly AgentCatalog _catalog;
    private readonly AgentCatalogFileInfo _fileInfo;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IActivityBroadcaster _broadcaster;
    private readonly CopilotSessionPool _sessionPool;
    private readonly ILogger<AgentCatalogWatcher> _logger;

    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private readonly TimeSpan _debounceDelay = TimeSpan.FromMilliseconds(500);
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(30);

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private string _lastHash = string.Empty;

    public AgentCatalogWatcher(
        AgentCatalog catalog,
        AgentCatalogFileInfo fileInfo,
        IServiceScopeFactory scopeFactory,
        IActivityBroadcaster broadcaster,
        CopilotSessionPool sessionPool,
        ILogger<AgentCatalogWatcher> logger)
    {
        _catalog = catalog;
        _fileInfo = fileInfo;
        _scopeFactory = scopeFactory;
        _broadcaster = broadcaster;
        _sessionPool = sessionPool;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lastHash = ComputeFileHash();
        StartFileWatcher();

        // Periodic hash poll as a reliability fallback
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
                var currentHash = ComputeFileHash();
                if (currentHash != _lastHash)
                {
                    _logger.LogInformation("Periodic poll detected agents.json change");
                    await ReloadAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during periodic catalog poll");
            }
        }
    }

    /// <summary>
    /// Public trigger for manual reload (e.g., from a REST endpoint).
    /// </summary>
    public async Task<CatalogReloadResult> TriggerReloadAsync(CancellationToken ct = default)
    {
        return await ReloadAsync(ct);
    }

    private void StartFileWatcher()
    {
        var directory = Path.GetDirectoryName(_fileInfo.FilePath);
        var fileName = Path.GetFileName(_fileInfo.FilePath);

        if (directory is null)
        {
            _logger.LogWarning("Cannot determine directory for {FilePath}, file watcher disabled",
                _fileInfo.FilePath);
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                    | NotifyFilters.CreationTime | NotifyFilters.FileName
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Renamed += (_, _) => OnFileChanged(null, null!);
            _watcher.Error += OnWatcherError;
            _watcher.EnableRaisingEvents = true;

            _logger.LogInformation("Watching {FilePath} for changes", _fileInfo.FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start FileSystemWatcher for {FilePath}, relying on polling only",
                _fileInfo.FilePath);
        }
    }

    private void OnFileChanged(object? sender, FileSystemEventArgs e)
    {
        // Debounce: editors often write multiple events for a single save
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounceDelay, token);
                await ReloadAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Debounce cancelled by a newer event — expected
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during debounced catalog reload");
            }
        }, token);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "FileSystemWatcher error, attempting restart");
        try
        {
            _watcher?.Dispose();
            StartFileWatcher();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart FileSystemWatcher, relying on polling only");
        }
    }

    private async Task<CatalogReloadResult> ReloadAsync(CancellationToken ct)
    {
        if (!await _reloadLock.WaitAsync(TimeSpan.FromSeconds(5), ct))
        {
            _logger.LogDebug("Reload already in progress, skipping");
            return CatalogReloadResult.Skipped();
        }

        try
        {
            var currentHash = ComputeFileHash();
            if (currentHash == _lastHash)
                return CatalogReloadResult.Skipped();

            AgentCatalogOptions newOptions;
            try
            {
                newOptions = AgentCatalogLoader.LoadFromPath(_fileInfo.FilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse agents.json — keeping current catalog");
                return CatalogReloadResult.Failed(ex.Message);
            }

            var oldAgents = _catalog.Snapshot.Agents;
            var newAgents = newOptions.Agents;

            var diff = ComputeDiff(oldAgents, newAgents);

            if (!diff.HasChanges)
            {
                _lastHash = currentHash;
                return CatalogReloadResult.NoChanges();
            }

            // Swap the catalog reference (volatile write — all consumers see new data immediately)
            _catalog.Update(newOptions);
            _lastHash = currentHash;

            _logger.LogInformation(
                "Agent catalog reloaded: {Added} added, {Removed} removed, {Modified} modified",
                diff.Added.Count, diff.Removed.Count, diff.Modified.Count);

            // Reconcile DB state for new agents
            await ReconcileAgentLocationsAsync(diff, ct);

            // Invalidate Copilot sessions for modified/removed agents
            await InvalidateSessionsAsync(diff);

            // Broadcast activity event
            BroadcastReloadEvent(diff);

            return CatalogReloadResult.Success(diff);
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private async Task ReconcileAgentLocationsAsync(CatalogDiff diff, CancellationToken ct)
    {
        if (diff.Added.Count == 0 && diff.Removed.Count == 0)
            return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

            // Create locations for added agents
            foreach (var agent in diff.Added)
            {
                var existing = await db.AgentLocations.FindAsync(new object[] { agent.Id }, ct);
                if (existing is null)
                {
                    db.AgentLocations.Add(new AgentLocationEntity
                    {
                        AgentId = agent.Id,
                        RoomId = _catalog.DefaultRoomId,
                        State = nameof(AgentState.Idle),
                        UpdatedAt = DateTime.UtcNow
                    });

                    _logger.LogInformation("Agent added: {AgentName} ({AgentRole})", agent.Name, agent.Role);
                }
            }

            // Mark removed agents as offline (don't delete — preserve history)
            foreach (var agent in diff.Removed)
            {
                var loc = await db.AgentLocations.FindAsync(new object[] { agent.Id }, ct);
                if (loc is not null)
                {
                    loc.State = nameof(AgentState.Offline);
                    loc.UpdatedAt = DateTime.UtcNow;

                    _logger.LogInformation("Agent removed: {AgentName} ({AgentRole})", agent.Name, agent.Role);
                }
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconcile agent locations after catalog reload");
        }
    }

    private async Task InvalidateSessionsAsync(CatalogDiff diff)
    {
        var affectedIds = diff.Modified.Select(a => a.Id)
            .Concat(diff.Removed.Select(a => a.Id))
            .ToHashSet();

        if (affectedIds.Count == 0)
            return;

        try
        {
            // Session keys use formats: "{agentId}:{roomId}" and "wt:{path}:{agentId}:{roomId}"
            // Match exact agent ID segments to avoid substring collisions (e.g., agent-1 vs agent-10)
            await _sessionPool.InvalidateByFilterAsync(key =>
                affectedIds.Any(id =>
                    key.StartsWith($"{id}:", StringComparison.Ordinal) ||
                    key.Contains($":{id}:", StringComparison.Ordinal)));
            _logger.LogInformation("Invalidated Copilot sessions for {Count} agents", affectedIds.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate Copilot sessions after catalog reload");
        }
    }

    private void BroadcastReloadEvent(CatalogDiff diff)
    {
        var details = new List<string>();
        if (diff.Added.Count > 0)
            details.Add($"added: {string.Join(", ", diff.Added.Select(a => a.Name))}");
        if (diff.Removed.Count > 0)
            details.Add($"removed: {string.Join(", ", diff.Removed.Select(a => a.Name))}");
        if (diff.Modified.Count > 0)
            details.Add($"modified: {string.Join(", ", diff.Modified.Select(a => a.Name))}");

        var evt = new ActivityEvent(
            Guid.NewGuid().ToString(),
            ActivityEventType.AgentCatalogReloaded,
            ActivitySeverity.Info,
            _catalog.DefaultRoomId,
            null, null,
            $"Agent catalog reloaded: {string.Join("; ", details)}",
            null,
            DateTime.UtcNow,
            new Dictionary<string, object?>
            {
                ["added"] = diff.Added.Count,
                ["removed"] = diff.Removed.Count,
                ["modified"] = diff.Modified.Count
            });

        _broadcaster.Broadcast(evt);
    }

    private static CatalogDiff ComputeDiff(
        IReadOnlyList<AgentDefinition> oldAgents,
        IReadOnlyList<AgentDefinition> newAgents)
    {
        var oldById = oldAgents.ToDictionary(a => a.Id);
        var newById = newAgents.ToDictionary(a => a.Id);

        var added = newAgents.Where(a => !oldById.ContainsKey(a.Id)).ToList();
        var removed = oldAgents.Where(a => !newById.ContainsKey(a.Id)).ToList();
        var modified = newAgents
            .Where(a => oldById.TryGetValue(a.Id, out var old) && !AgentsEqual(old, a))
            .ToList();

        return new CatalogDiff(added, removed, modified);
    }

    private static bool AgentsEqual(AgentDefinition a, AgentDefinition b)
    {
        return a.Name == b.Name
            && a.Role == b.Role
            && a.Summary == b.Summary
            && a.StartupPrompt == b.StartupPrompt
            && a.Model == b.Model
            && a.AutoJoinDefaultRoom == b.AutoJoinDefaultRoom
            && a.CapabilityTags.SequenceEqual(b.CapabilityTags)
            && a.EnabledTools.SequenceEqual(b.EnabledTools)
            && PermissionsEqual(a.Permissions, b.Permissions)
            && GitIdentityEqual(a.GitIdentity, b.GitIdentity);
    }

    private static bool PermissionsEqual(CommandPermissionSet? a, CommandPermissionSet? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.Allowed.SequenceEqual(b.Allowed)
            && a.Denied.SequenceEqual(b.Denied);
    }

    private static bool GitIdentityEqual(AgentGitIdentity? a, AgentGitIdentity? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.AuthorName == b.AuthorName
            && a.AuthorEmail == b.AuthorEmail;
    }

    private string ComputeFileHash()
    {
        try
        {
            var bytes = File.ReadAllBytes(_fileInfo.FilePath);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to hash {FilePath}", _fileInfo.FilePath);
            return string.Empty;
        }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        _debounceCts?.Dispose();
        _reloadLock.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Result of a catalog reload operation.
/// </summary>
public record CatalogReloadResult(
    bool WasReloaded,
    bool WasSkipped,
    string? Error,
    CatalogDiff? Diff)
{
    public static CatalogReloadResult Success(CatalogDiff diff) => new(true, false, null, diff);
    public static CatalogReloadResult Skipped() => new(false, true, null, null);
    public static CatalogReloadResult NoChanges() => new(false, false, null, null);
    public static CatalogReloadResult Failed(string error) => new(false, false, error, null);
}

/// <summary>
/// Diff between two catalog versions.
/// </summary>
public record CatalogDiff(
    List<AgentDefinition> Added,
    List<AgentDefinition> Removed,
    List<AgentDefinition> Modified)
{
    public bool HasChanges => Added.Count > 0 || Removed.Count > 0 || Modified.Count > 0;
}
