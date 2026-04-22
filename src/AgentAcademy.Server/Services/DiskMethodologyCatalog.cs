using System.Text.Json;
using System.Text.RegularExpressions;
using AgentAcademy.Forge.Models;
using AgentAcademy.Server.Services.Contracts;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Disk-backed methodology catalog. Reads/writes JSON files from a configured directory.
/// Filename is derived from the methodology ID (e.g., "spike-default-v1" → "spike-default-v1.json").
/// </summary>
public sealed class DiskMethodologyCatalog : IMethodologyCatalog
{
    private static readonly Regex ValidIdPattern = new(@"^[a-zA-Z0-9][a-zA-Z0-9_-]{0,98}[a-zA-Z0-9]$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private readonly string _directory;
    private readonly string _resolvedDirectory;
    private readonly ILogger<DiskMethodologyCatalog> _logger;

    public DiskMethodologyCatalog(string directory, ILogger<DiskMethodologyCatalog> logger)
    {
        _directory = directory;
        _resolvedDirectory = Path.GetFullPath(directory);
        _logger = logger;
        Directory.CreateDirectory(_resolvedDirectory);
    }

    public async Task<IReadOnlyList<MethodologySummary>> ListAsync(CancellationToken ct = default)
    {
        var results = new List<MethodologySummary>();

        if (!Directory.Exists(_resolvedDirectory))
            return results;

        foreach (var file in Directory.EnumerateFiles(_resolvedDirectory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var methodology = JsonSerializer.Deserialize<MethodologyDefinition>(json, JsonOptions);
                if (methodology is null || string.IsNullOrWhiteSpace(methodology.Id))
                {
                    _logger.LogWarning("Skipping malformed methodology file: {File}", file);
                    continue;
                }

                results.Add(ToSummary(methodology));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping unreadable methodology file: {File}", file);
            }
        }

        return results.OrderBy(m => m.Id).ToList();
    }

    public async Task<MethodologyDefinition?> GetAsync(string methodologyId, CancellationToken ct = default)
    {
        if (!ValidIdPattern.IsMatch(methodologyId))
            return null;

        var filePath = GetFilePath(methodologyId);
        if (!IsPathSafe(filePath) || !File.Exists(filePath))
            return null;

        var json = await File.ReadAllTextAsync(filePath, ct);
        return JsonSerializer.Deserialize<MethodologyDefinition>(json, JsonOptions);
    }

    public async Task<string> SaveAsync(MethodologyDefinition methodology, CancellationToken ct = default)
    {
        var id = methodology.Id;
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Methodology ID is required.");

        if (!ValidIdPattern.IsMatch(id))
            throw new ArgumentException(
                $"Methodology ID '{id}' is invalid. Must be 2-100 alphanumeric characters, hyphens, or underscores.");

        ValidateMethodology(methodology);

        var filePath = GetFilePath(id);
        if (!IsPathSafe(filePath))
            throw new InvalidOperationException("Methodology ID resolves outside the catalog directory.");

        var json = JsonSerializer.Serialize(methodology, JsonOptions);

        // Atomic write: write to unique temp file, then rename
        var tempPath = filePath + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tempPath, json, ct);
        File.Move(tempPath, filePath, overwrite: true);

        _logger.LogInformation("Saved methodology '{Id}' to catalog", id);
        return id;
    }

    /// <summary>Seed a methodology into the catalog only if it doesn't already exist.</summary>
    /// <summary>
    /// Seed a methodology into the catalog if it doesn't exist. Also re-seeds
    /// when the existing on-disk methodology references a known-deprecated
    /// model (e.g. gpt-4o, which is no longer served by Copilot) — this lets
    /// stale catalogs auto-heal without operator intervention.
    /// </summary>
    public async Task SeedAsync(MethodologyDefinition methodology, CancellationToken ct = default)
    {
        var filePath = GetFilePath(methodology.Id);
        if (File.Exists(filePath))
        {
            if (!await ShouldReseedAsync(filePath, ct))
                return;

            _logger.LogWarning(
                "Methodology '{Id}' on disk references deprecated model defaults — re-seeding with current defaults",
                methodology.Id);
        }

        await SaveAsync(methodology, ct);
        _logger.LogInformation("Seeded default methodology '{Id}' into catalog", methodology.Id);
    }

    private static readonly HashSet<string> DeprecatedModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "gpt-4o",
        "gpt-4o-mini",
        "gpt-4o-2024-08-06"
    };

    private async Task<bool> ShouldReseedAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            var existing = JsonSerializer.Deserialize<MethodologyDefinition>(json, JsonOptions);
            if (existing?.ModelDefaults is null) return false;

            return DeprecatedModels.Contains(existing.ModelDefaults.Generation ?? "")
                || DeprecatedModels.Contains(existing.ModelDefaults.Judge ?? "");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read existing methodology at {Path}; leaving in place", filePath);
            return false;
        }
    }

    private string GetFilePath(string methodologyId) =>
        Path.Combine(_resolvedDirectory, methodologyId + ".json");

    private bool IsPathSafe(string filePath)
    {
        var resolved = Path.GetFullPath(filePath);
        return resolved.StartsWith(_resolvedDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || resolved == _resolvedDirectory;
    }

    private static void ValidateMethodology(MethodologyDefinition methodology)
    {
        if (methodology.Phases is null || methodology.Phases.Count == 0)
            throw new ArgumentException("Methodology must define at least one phase.");

        var phaseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var phase in methodology.Phases)
        {
            if (string.IsNullOrWhiteSpace(phase.Id))
                throw new ArgumentException("Every phase must have an 'id'.");

            if (!phaseIds.Add(phase.Id))
                throw new ArgumentException($"Duplicate phase ID: '{phase.Id}'.");

            if (string.IsNullOrWhiteSpace(phase.OutputSchema))
                throw new ArgumentException($"Phase '{phase.Id}' is missing 'output_schema'.");

            if (string.IsNullOrWhiteSpace(phase.Goal))
                throw new ArgumentException($"Phase '{phase.Id}' is missing 'goal'.");

            if (string.IsNullOrWhiteSpace(phase.Instructions))
                throw new ArgumentException($"Phase '{phase.Id}' is missing 'instructions'.");

            if (phase.Inputs is null)
                throw new ArgumentException($"Phase '{phase.Id}' is missing 'inputs' (use empty array for no inputs).");

            foreach (var input in phase.Inputs)
            {
                if (!phaseIds.Contains(input))
                {
                    // Input might reference a later phase — check all phases
                    if (!methodology.Phases.Any(p => p.Id == input))
                        throw new ArgumentException(
                            $"Phase '{phase.Id}' references unknown input '{input}'.");
                }
            }

            if (phase.MaxAttempts is <= 0)
                throw new ArgumentException($"Phase '{phase.Id}' has invalid max_attempts (must be > 0).");
        }

        // Check for dependency cycles
        DetectCycles(methodology.Phases);

        if (methodology.MaxAttemptsDefault <= 0)
            throw new ArgumentException("max_attempts_default must be > 0.");

        if (methodology.Budget is <= 0)
            throw new ArgumentException("budget must be > 0 when specified.");

        if (methodology.Fidelity is not null)
        {
            if (!phaseIds.Contains(methodology.Fidelity.TargetPhase))
                throw new ArgumentException(
                    $"Fidelity target_phase '{methodology.Fidelity.TargetPhase}' does not match any phase ID.");
        }

        if (methodology.Control is not null)
        {
            if (string.IsNullOrWhiteSpace(methodology.Control.TargetSchema))
                throw new ArgumentException("Control target_schema is required.");
        }
    }

    private static void DetectCycles(IReadOnlyList<PhaseDefinition> phases)
    {
        var graph = phases.ToDictionary(p => p.Id, p => p.Inputs);
        var visited = new HashSet<string>();
        var inStack = new HashSet<string>();

        foreach (var phase in phases)
        {
            if (HasCycle(phase.Id, graph, visited, inStack))
                throw new ArgumentException($"Dependency cycle detected involving phase '{phase.Id}'.");
        }
    }

    private static bool HasCycle(
        string node,
        Dictionary<string, IReadOnlyList<string>> graph,
        HashSet<string> visited,
        HashSet<string> inStack)
    {
        if (inStack.Contains(node)) return true;
        if (visited.Contains(node)) return false;

        visited.Add(node);
        inStack.Add(node);

        if (graph.TryGetValue(node, out var deps))
        {
            foreach (var dep in deps)
            {
                if (graph.ContainsKey(dep) && HasCycle(dep, graph, visited, inStack))
                    return true;
            }
        }

        inStack.Remove(node);
        return false;
    }

    private static MethodologySummary ToSummary(MethodologyDefinition m) => new(
        Id: m.Id,
        Description: m.Description,
        PhaseCount: m.Phases.Count,
        GenerationModel: m.ModelDefaults?.Generation,
        JudgeModel: m.ModelDefaults?.Judge,
        HasBudget: m.Budget.HasValue,
        HasFidelity: m.Fidelity is not null,
        HasControl: m.Control is not null);
}
