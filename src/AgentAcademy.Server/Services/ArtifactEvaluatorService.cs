using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentAcademy.Server.Data;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Evaluates artifact files produced by agents in a room against quality criteria.
/// Checks file existence, content, syntax validity, and completeness.
/// </summary>
public sealed partial class ArtifactEvaluatorService
{
    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<ArtifactEvaluatorService> _logger;

    // Scoring weights (out of 100)
    private const double ExistsWeight = 40;
    private const double NonEmptyWeight = 20;
    private const double SyntaxWeight = 25;
    private const double CompleteWeight = 15;

    private const int MaxContentScanBytes = 8192;

    private static readonly HashSet<string> JsonExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".jsonc"
    };

    private static readonly HashSet<string> XmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".csproj", ".vbproj", ".fsproj", ".props", ".targets", ".nuspec", ".config", ".xaml"
    };

    [GeneratedRegex(@"\b(TODO|FIXME|HACK|PLACEHOLDER|XXX|NOT\s+IMPLEMENTED)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IncompleteMarkerRegex();

    public ArtifactEvaluatorService(
        AgentAcademyDbContext db,
        ILogger<ArtifactEvaluatorService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Evaluates all artifact files for a room. Returns per-file results and an aggregate score.
    /// </summary>
    public async Task<(List<EvaluationResult> Artifacts, double AggregateScore)> EvaluateRoomArtifactsAsync(
        string roomId, CancellationToken ct = default)
    {
        var room = await _db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roomId, ct);
        if (room is null)
            return ([], 0.0);

        string? workspacePath = null;
        if (room.WorkspacePath is not null)
        {
            var workspace = await _db.Workspaces.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Path == room.WorkspacePath, ct);
            workspacePath = workspace?.Path;
        }

        // Get all artifacts for the room (no cap) and compute latest operation per file
        var allArtifacts = await _db.RoomArtifacts
            .Where(a => a.RoomId == roomId)
            .OrderByDescending(a => a.Timestamp)
            .ThenByDescending(a => a.Id)
            .ToListAsync(ct);

        // Deduplicate: latest operation per file path
        var latestPerFile = allArtifacts
            .GroupBy(a => a.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Where(a => !string.Equals(a.Operation, "Deleted", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (latestPerFile.Count == 0)
            return ([], 0.0);

        var results = new List<EvaluationResult>();
        foreach (var artifact in latestPerFile)
        {
            var result = await EvaluateFileAsync(artifact.FilePath, workspacePath, ct);
            results.Add(result);
        }

        var aggregateScore = results.Count > 0
            ? results.Average(r => r.Score)
            : 0.0;

        return (results, Math.Round(aggregateScore, 2));
    }

    internal async Task<EvaluationResult> EvaluateFileAsync(
        string filePath, string? workspacePath, CancellationToken ct = default)
    {
        var issues = new List<string>();

        // If no workspace path, we can't check files on disk
        if (workspacePath is null)
        {
            issues.Add("No workspace configured — cannot evaluate file on disk");
            return new EvaluationResult(filePath, 0.0, false, false, false, false, issues);
        }

        // Path traversal protection
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(workspacePath, filePath));
            var rootWithSep = workspacePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootWithSep, StringComparison.Ordinal))
            {
                issues.Add("Path traversal denied — file must be within the workspace directory");
                return new EvaluationResult(filePath, 0.0, false, false, false, false, issues);
            }
        }
        catch (Exception ex)
        {
            issues.Add($"Invalid file path: {ex.Message}");
            return new EvaluationResult(filePath, 0.0, false, false, false, false, issues);
        }

        // Check existence
        bool exists = File.Exists(fullPath);
        if (!exists)
        {
            issues.Add("File does not exist");
            return new EvaluationResult(filePath, 0.0, false, false, false, false, issues);
        }

        // Symlink escape protection: resolve the real target and re-check
        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.LinkTarget is not null)
        {
            var realTarget = Path.GetFullPath(fileInfo.LinkTarget, Path.GetDirectoryName(fullPath)!);
            var rootWithSep2 = workspacePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!realTarget.StartsWith(rootWithSep2, StringComparison.Ordinal))
            {
                issues.Add("Symlink target escapes workspace directory");
                return new EvaluationResult(filePath, 0.0, false, false, false, false, issues);
            }
        }

        // Check non-empty
        bool nonEmpty = fileInfo.Length > 0;
        if (!nonEmpty)
        {
            issues.Add("File is empty (0 bytes)");
            return new EvaluationResult(filePath, ExistsWeight, true, false, false, false, issues);
        }

        // Read content for syntax and completeness checks.
        // For syntax: read full file (JSON/XML are typically small).
        // For completeness: scan first MaxContentScanBytes; flag if truncated.
        string fullContent;
        bool truncated;
        try
        {
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            fullContent = await reader.ReadToEndAsync(ct);
            truncated = false;
        }
        catch (Exception ex)
        {
            issues.Add($"Cannot read file: {ex.Message}");
            double partialScore = ExistsWeight + NonEmptyWeight;
            return new EvaluationResult(filePath, partialScore, true, true, false, false, issues);
        }

        // For completeness scanning, use a limited prefix to avoid scanning huge files
        string scanContent;
        if (fullContent.Length > MaxContentScanBytes)
        {
            scanContent = fullContent[..MaxContentScanBytes];
            truncated = true;
        }
        else
        {
            scanContent = fullContent;
        }

        // Syntax validation (extension-based) — uses full content
        bool syntaxValid = ValidateSyntax(fullPath, fullContent, issues);

        // Completeness check (TODO/FIXME markers) — uses scan prefix
        bool complete = CheckCompleteness(scanContent, truncated, issues);

        double score = ExistsWeight + NonEmptyWeight
            + (syntaxValid ? SyntaxWeight : 0)
            + (complete ? CompleteWeight : 0);

        return new EvaluationResult(filePath, score, true, true, syntaxValid, complete, issues);
    }

    private static bool ValidateSyntax(string fullPath, string content, List<string> issues)
    {
        var extension = Path.GetExtension(fullPath);

        if (JsonExtensions.Contains(extension))
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
                return true;
            }
            catch (JsonException ex)
            {
                issues.Add($"Invalid JSON syntax: {ex.Message}");
                return false;
            }
        }

        if (XmlExtensions.Contains(extension))
        {
            try
            {
                XDocument.Parse(content);
                return true;
            }
            catch (System.Xml.XmlException ex)
            {
                issues.Add($"Invalid XML syntax: {ex.Message}");
                return false;
            }
        }

        // For other file types, we can't validate syntax — pass by default
        return true;
    }

    private static bool CheckCompleteness(string content, bool truncated, List<string> issues)
    {
        var match = IncompleteMarkerRegex().Match(content);
        if (match.Success)
        {
            issues.Add($"Contains incomplete marker: {match.Value}");
            return false;
        }
        if (truncated)
        {
            issues.Add("Completeness check limited to first 8KB of file");
        }
        return true;
    }
}
