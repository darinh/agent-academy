using System.Text.Json;
using AgentAcademy.Forge.Models;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Forge.Storage;

/// <summary>
/// Disk-backed run store. Uses write-temp-then-rename for atomic writes.
/// Directory layout follows the frozen storage-layout.md contract.
/// </summary>
public sealed class DiskRunStore : IRunStore
{
    private readonly string _rootDir;
    private readonly ILogger<DiskRunStore> _logger;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions NdjsonWriteOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DiskRunStore(string rootDir, ILogger<DiskRunStore> logger)
    {
        _rootDir = rootDir;
        _logger = logger;
    }

    public string GetRunDirectory(string runId) => Path.Combine(_rootDir, "runs", runId);

    public async Task InitializeRunAsync(string runId, RunTrace run, TaskBrief task, MethodologyDefinition methodology, CancellationToken ct = default)
    {
        var runDir = GetRunDirectory(runId);
        Directory.CreateDirectory(runDir);
        Directory.CreateDirectory(Path.Combine(runDir, "phases"));

        await WriteAtomicJsonAsync(Path.Combine(runDir, "run.json"), run, ct);
        await WriteAtomicJsonAsync(Path.Combine(runDir, "task.json"), task, ct);
        await WriteAtomicJsonAsync(Path.Combine(runDir, "methodology.json"), methodology, ct);

        _logger.LogInformation("Initialized run directory {RunId}", runId);
    }

    public async Task WriteRunSnapshotAsync(string runId, RunTrace run, CancellationToken ct = default)
    {
        var path = Path.Combine(GetRunDirectory(runId), "run.json");
        await WriteAtomicJsonAsync(path, run, ct);
    }

    public async Task<RunTrace?> ReadRunAsync(string runId, CancellationToken ct = default)
    {
        var path = Path.Combine(GetRunDirectory(runId), "run.json");
        return await ReadJsonAsync<RunTrace>(path, ct);
    }

    public async Task WritePhaseRunScratchAsync(string runId, int phaseIndex, string phaseId, PhaseRunTrace phaseRun, CancellationToken ct = default)
    {
        var phaseDir = GetPhaseDirectory(runId, phaseIndex, phaseId);
        Directory.CreateDirectory(phaseDir);
        await WriteAtomicJsonAsync(Path.Combine(phaseDir, "phase-run.json"), phaseRun, ct);
    }

    public async Task WritePhaseRunsRollupAsync(string runId, IReadOnlyList<PhaseRunTrace> phaseRuns, CancellationToken ct = default)
    {
        var path = Path.Combine(GetRunDirectory(runId), "phase-runs.json");
        await WriteAtomicJsonAsync(path, phaseRuns, ct);
    }

    public async Task<IReadOnlyList<PhaseRunTrace>?> ReadPhaseRunsRollupAsync(string runId, CancellationToken ct = default)
    {
        var path = Path.Combine(GetRunDirectory(runId), "phase-runs.json");
        return await ReadJsonAsync<List<PhaseRunTrace>>(path, ct);
    }

    public async Task<PhaseRunTrace?> ReadPhaseRunScratchAsync(string runId, int phaseIndex, string phaseId, CancellationToken ct = default)
    {
        var phaseDir = GetPhaseDirectory(runId, phaseIndex, phaseId);
        var path = Path.Combine(phaseDir, "phase-run.json");
        return await ReadJsonAsync<PhaseRunTrace>(path, ct);
    }

    public async Task WriteAttemptFilesAsync(string runId, int phaseIndex, string phaseId, int attemptNumber, AttemptFiles files, CancellationToken ct = default)
    {
        var attemptDir = GetAttemptDirectory(runId, phaseIndex, phaseId, attemptNumber);
        Directory.CreateDirectory(attemptDir);

        if (files.PromptText is not null)
            await WriteAtomicTextAsync(Path.Combine(attemptDir, "prompt.txt"), files.PromptText, ct);

        if (files.ResponseRaw is not null)
            await WriteAtomicTextAsync(Path.Combine(attemptDir, "response.raw.txt"), files.ResponseRaw, ct);

        if (files.ResponseParsedJson is not null)
            await WriteAtomicTextAsync(Path.Combine(attemptDir, "response.parsed.json"), files.ResponseParsedJson, ct);

        if (files.ValidatorReportJson is not null)
            await WriteAtomicTextAsync(Path.Combine(attemptDir, "validator-report.json"), files.ValidatorReportJson, ct);

        if (files.MetaJson is not null)
            await WriteAtomicTextAsync(Path.Combine(attemptDir, "meta.json"), files.MetaJson, ct);
    }

    public async Task WriteReviewSummaryAsync(string runId, ReviewSummaryTrace summary, CancellationToken ct = default)
    {
        var path = Path.Combine(GetRunDirectory(runId), "review-summary.json");
        await WriteAtomicJsonAsync(path, summary, ct);
    }

    public async Task AppendTraceEventAsync(string runId, object traceEvent, CancellationToken ct = default)
    {
        var path = Path.Combine(GetRunDirectory(runId), "trace.log");
        var json = JsonSerializer.Serialize(traceEvent, NdjsonWriteOptions);
        // Append NDJSON — one JSON object per line
        await File.AppendAllTextAsync(path, json + Environment.NewLine, ct);
    }

    public Task<IReadOnlyList<string>> ListRunsAsync(CancellationToken ct = default)
    {
        var runsDir = Path.Combine(_rootDir, "runs");
        if (!Directory.Exists(runsDir))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var runs = Directory.GetDirectories(runsDir)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .OrderBy(name => name, StringComparer.Ordinal) // ULID sort = time sort
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(runs!);
    }

    public Task<bool> RunExistsAsync(string runId, CancellationToken ct = default)
    {
        return Task.FromResult(Directory.Exists(GetRunDirectory(runId)));
    }

    public async Task<TaskBrief?> ReadTaskAsync(string runId, CancellationToken ct = default)
    {
        var path = Path.Combine(GetRunDirectory(runId), "task.json");
        return await ReadJsonAsync<TaskBrief>(path, ct);
    }

    public async Task<MethodologyDefinition?> ReadMethodologyAsync(string runId, CancellationToken ct = default)
    {
        var path = Path.Combine(GetRunDirectory(runId), "methodology.json");
        return await ReadJsonAsync<MethodologyDefinition>(path, ct);
    }

    // --- Directory naming per storage-layout.md ---

    private string GetPhaseDirectory(string runId, int phaseIndex, string phaseId)
    {
        // NN-<phase_id> with underscores converted to hyphens for filesystem
        var dirName = $"{phaseIndex + 1:D2}-{phaseId.Replace('_', '-')}";
        return Path.Combine(GetRunDirectory(runId), "phases", dirName);
    }

    private string GetAttemptDirectory(string runId, int phaseIndex, string phaseId, int attemptNumber)
    {
        return Path.Combine(GetPhaseDirectory(runId, phaseIndex, phaseId), "attempts", $"{attemptNumber:D2}");
    }

    // --- Atomic file operations ---

    private static async Task WriteAtomicJsonAsync<T>(string targetPath, T value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, WriteOptions);
        await WriteAtomicTextAsync(targetPath, json, ct);
    }

    private static async Task WriteAtomicTextAsync(string targetPath, string content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(dir);
        var tmpPath = Path.Combine(dir, $"{Path.GetFileName(targetPath)}.tmp.{Environment.ProcessId}.{Random.Shared.Next()}");

        try
        {
            await using (var stream = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
                await writer.WriteAsync(content.AsMemory(), ct);
                await writer.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tmpPath, targetPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { /* best effort */ }
            throw;
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken ct) where T : class
    {
        if (!File.Exists(path))
            return null;

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<T>(json, ReadOptions);
    }
}
