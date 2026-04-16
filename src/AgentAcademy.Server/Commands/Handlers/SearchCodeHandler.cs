using System.Diagnostics;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles SEARCH_CODE — searches for text patterns in the project codebase using git grep.
/// Uses git grep instead of plain grep to automatically respect .gitignore
/// (skips node_modules, bin, obj, etc.) and only search tracked files.
/// </summary>
public sealed class SearchCodeHandler : ICommandHandler
{
    public string CommandName => "SEARCH_CODE";
    public bool IsRetrySafe => true;

    private const int MaxResults = 50;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("query", out var queryObj) || queryObj is not string query || string.IsNullOrWhiteSpace(query))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: query"
            };
        }

        var projectRoot = FindProjectRoot();

        // Build git grep command — respects .gitignore, skips binary files,
        // only searches tracked files. Stdout/stderr are piped (git auto-disables
        // pager + color when stdout is not a TTY, so no explicit flags needed).
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = projectRoot
        };
        psi.ArgumentList.Add("grep");
        psi.ArgumentList.Add("-n");
        // Note: we do not pass -I (skip binary files). git grep emits binary
        // matches as a single "Binary file X matches" line with no colons, which
        // our three-column parser already filters out. Omitting -I keeps the
        // mutation surface minimal.

        // Case-insensitive search if requested
        if (command.Args.TryGetValue("ignoreCase", out var icObj) &&
            (icObj is true || (icObj is string icStr && icStr.Equals("true", StringComparison.OrdinalIgnoreCase))))
        {
            psi.ArgumentList.Add("-i");
        }

        psi.ArgumentList.Add("--max-count");
        psi.ArgumentList.Add(MaxResults.ToString());
        // -e is required so patterns beginning with '-' aren't interpreted as flags.
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(query);

        // Resolve optional path and glob filters. Combine both into a single
        // pathspec when the path points to a directory; when it points to a file,
        // the glob is ignored (a single file is already a precise scope).
        string? globFilter = null;
        if (command.Args.TryGetValue("glob", out var globObj) && globObj is string glob && !string.IsNullOrWhiteSpace(glob))
            globFilter = glob;

        (string Relative, bool IsFile)? pathResolved = null;
        if (command.Args.TryGetValue("path", out var pathObj) && pathObj is string subPath && !string.IsNullOrWhiteSpace(subPath))
        {
            var full = Path.GetFullPath(Path.Combine(projectRoot, subPath));
            var projectRootWithSep = projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(projectRootWithSep, StringComparison.Ordinal) &&
                !full.Equals(projectRoot, StringComparison.Ordinal))
            {
                return command with
                {
                    Status = CommandStatus.Denied,
                    ErrorCode = CommandErrorCode.Permission,
                    Error = "Path traversal denied: search path must be within the project directory."
                };
            }

            var isDir = Directory.Exists(full);
            var isFile = File.Exists(full);
            if (!isDir && !isFile)
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.NotFound,
                    Error = $"Path not found: {subPath}. Use paths relative to the project root (e.g., src/AgentAcademy.Server/Commands)."
                };
            }
            pathResolved = (Path.GetRelativePath(projectRoot, full), isFile);
        }

        if (globFilter is not null || pathResolved is not null)
        {
            // `--` forces every subsequent arg to be treated as a pathspec. This
            // is essential because the user-controlled `glob` arg could otherwise
            // be parsed as a git option (e.g. "--help", "-v", "--no-index").
            psi.ArgumentList.Add("--");
            if (globFilter is not null && pathResolved is { IsFile: false } dirScope)
            {
                // Combine: only match glob within the directory scope
                psi.ArgumentList.Add($":(glob){dirScope.Relative}/**/{globFilter}");
            }
            else if (pathResolved is { } fileOrDir)
            {
                // File path or directory without glob — use directly
                psi.ArgumentList.Add(fileOrDir.Relative);
            }
            else
            {
                psi.ArgumentList.Add(globFilter!);
            }
        }

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();

        // git grep output format: file:line:content. Filter out malformed lines
        // (git emits binary-match lines as "Binary file X matches" with no colons)
        // BEFORE capping, so malformed rows can't consume result slots.
        var allLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var parsed = allLines
            .Select(line => line.Split(':', 3))
            .Where(parts => parts.Length >= 3)
            .Select(parts => new Dictionary<string, object?>
            {
                ["file"] = parts[0],
                ["line"] = int.TryParse(parts[1], out var ln) ? ln : 0,
                ["text"] = parts[2].Trim()
            })
            .ToList();

        var matches = parsed.Take(MaxResults).ToList();
        var truncated = parsed.Count > MaxResults;

        var result = new Dictionary<string, object?>
        {
            ["matches"] = matches,
            ["count"] = matches.Count,
            ["query"] = query
        };

        if (truncated)
        {
            result["truncated"] = true;
            result["hint"] = $"Results capped at {MaxResults}. Narrow your query or add path/glob filters.";
        }

        return command with
        {
            Status = CommandStatus.Success,
            Result = result
        };
    }

    private static string FindProjectRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "AgentAcademy.sln")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
