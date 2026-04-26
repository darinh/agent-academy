using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Per-session wrapper for the <c>read_file</c> and <c>search_code</c> SDK tools.
/// When constructed with an explicit <c>scopeRoot</c> (a worktree path) the wrapper
/// resolves all paths and runs <c>git grep</c> inside that worktree, giving each
/// breakout an isolated view of the tree it owns. When <c>scopeRoot</c> is null
/// the wrapper falls back to <see cref="AgentToolFunctions.FindProjectRoot"/>,
/// preserving the original main-room behaviour.
/// </summary>
internal sealed class CodeReadToolWrapper
{
    private readonly ILogger _logger;
    private readonly string? _scopeRoot;

    internal CodeReadToolWrapper(ILogger logger, string? scopeRoot = null)
    {
        _logger = logger;
        _scopeRoot = ScopeRootValidator.ValidateAndCanonicalize(scopeRoot, nameof(scopeRoot));
    }

    private string ResolveScopeRoot() => _scopeRoot ?? AgentToolFunctions.FindProjectRoot();

    [Description("Read a file's contents from the project. Paths are relative to the project root.")]
    internal async Task<string> ReadFileAsync(
        [Description("File path relative to the project root (e.g., src/AgentAcademy.Server/Program.cs)")]
        string path,
        [Description("Start line number (1-based, default 1)")]
        int startLine = 1,
        [Description("End line number (default: end of file)")]
        int? endLine = null)
    {
        var projectRoot = ResolveScopeRoot();
        _logger.LogDebug("Tool call: read_file (cwd={ScopeRoot}, path={Path}, startLine={Start})", projectRoot, path, startLine);

        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, path));

        var rootWithSep = projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSep, StringComparison.Ordinal) &&
            !fullPath.Equals(projectRoot, StringComparison.Ordinal))
        {
            return "Error: Path traversal denied — file must be within the project directory.";
        }

        if (!AgentToolFunctions.IsResolvedPathInsideRoot(fullPath, projectRoot))
        {
            return "Error: Path traversal denied — symlink target is outside the project directory.";
        }

        if (Directory.Exists(fullPath))
        {
            var entries = Directory.GetFileSystemEntries(fullPath)
                .Select(e => Path.GetRelativePath(projectRoot, e))
                .OrderBy(e => e)
                .ToList();
            return $"Directory: {path}\nEntries ({entries.Count}):\n{string.Join('\n', entries)}";
        }

        if (!File.Exists(fullPath))
            return $"Error: File not found: {path}";

        var lines = await File.ReadAllLinesAsync(fullPath);
        var totalLines = lines.Length;

        startLine = Math.Max(1, startLine);
        var end = endLine ?? totalLines;
        end = Math.Min(totalLines, end);

        var selected = lines.Skip(startLine - 1).Take(end - startLine + 1).ToArray();
        var content = string.Join('\n', selected);

        const int maxLen = 12_000;
        var truncated = false;
        if (content.Length > maxLen)
        {
            truncated = true;
            content = content[..maxLen];
            var lastNewline = content.LastIndexOf('\n');
            if (lastNewline > 0)
                content = content[..lastNewline];
        }

        var header = $"File: {path} ({totalLines} lines, showing {startLine}-{end})";
        if (truncated)
            header += " [TRUNCATED — use startLine/endLine to read more]";
        return $"{header}\n\n{content}";
    }

    [Description("Search for text patterns in the project codebase using git grep.")]
    internal async Task<string> SearchCodeAsync(
        [Description("Search query (text pattern to find)")]
        string query,
        [Description("Subdirectory path to restrict search to (e.g., src/AgentAcademy.Server)")]
        string? path = null,
        [Description("Glob pattern to filter files (e.g., *.cs, *.ts)")]
        string? glob = null,
        [Description("Case-insensitive search (default false)")]
        bool ignoreCase = false)
    {
        var projectRoot = ResolveScopeRoot();
        _logger.LogDebug("Tool call: search_code (cwd={ScopeRoot}, query={Query}, path={Path})", projectRoot, query, path);

        if (path is not null)
        {
            var fullSubPath = Path.GetFullPath(Path.Combine(projectRoot, path));
            var rootWithSep = projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullSubPath.StartsWith(rootWithSep, StringComparison.Ordinal) &&
                !fullSubPath.Equals(projectRoot, StringComparison.Ordinal))
            {
                return "Error: Path traversal denied — path must be within the project directory.";
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = projectRoot
        };
        psi.ArgumentList.Add("--no-pager");
        psi.ArgumentList.Add("grep");
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("--color=never");
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add("-F");

        if (ignoreCase)
            psi.ArgumentList.Add("-i");

        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(query);

        if (glob is not null || path is not null)
        {
            psi.ArgumentList.Add("--");
            if (glob is not null && path is not null)
            {
                var relPath = Path.GetRelativePath(projectRoot,
                    Path.GetFullPath(Path.Combine(projectRoot, path)));
                psi.ArgumentList.Add($":(glob){relPath}/**/{glob}");
            }
            else if (path is not null)
            {
                psi.ArgumentList.Add(path);
            }
            else
            {
                psi.ArgumentList.Add(glob!);
            }
        }

        const int maxResults = 50;

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return "Error: Failed to start search process.";

            var stderrTask = process.StandardError.ReadToEndAsync();

            var lines = new List<string>(maxResults);
            while (lines.Count < maxResults)
            {
                var line = await process.StandardOutput.ReadLineAsync();
                if (line is null) break;
                lines.Add(line);
            }

            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            }

            var stderr = await stderrTask;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { /* process already killed above */ }

            if (process.ExitCode > 1 && lines.Count == 0)
            {
                var errMsg = stderr.Length > 200 ? stderr[..200] : stderr;
                return $"Error: Search failed (exit {process.ExitCode}): {errMsg.Trim()}";
            }

            if (lines.Count == 0)
                return $"No results found for: {query}";

            var truncated = lines.Count >= maxResults;
            var header = $"Search results for \"{query}\" ({lines.Count} matches)";
            if (truncated)
                header += $" [capped at {maxResults} — narrow with path/glob]";

            return $"{header}:\n\n{string.Join('\n', lines)}";
        }
        catch (Exception ex)
        {
            return $"Error: Search failed: {ex.Message}";
        }
    }
}
