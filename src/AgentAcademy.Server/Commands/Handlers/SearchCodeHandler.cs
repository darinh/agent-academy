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
        // only searches tracked files
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
        psi.ArgumentList.Add("-I"); // skip binary files

        // Case-insensitive search if requested
        if (command.Args.TryGetValue("ignoreCase", out var icObj) &&
            (icObj is true || (icObj is string icStr && icStr.Equals("true", StringComparison.OrdinalIgnoreCase))))
        {
            psi.ArgumentList.Add("-i");
        }

        psi.ArgumentList.Add("--max-count");
        psi.ArgumentList.Add(MaxResults.ToString());
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(query);

        // Build pathspec for git grep
        string? globFilter = null;
        string? pathScope = null;
        var pathScopeIsFile = false;

        if (command.Args.TryGetValue("glob", out var globObj) && globObj is string glob && !string.IsNullOrWhiteSpace(glob))
            globFilter = glob;

        if (command.Args.TryGetValue("path", out var pathObj) && pathObj is string subPath && !string.IsNullOrWhiteSpace(subPath))
        {
            var full = Path.GetFullPath(Path.Combine(projectRoot, subPath));
            var projectRootWithSep = projectRoot.EndsWith(Path.DirectorySeparatorChar)
                ? projectRoot : projectRoot + Path.DirectorySeparatorChar;
            if (full.StartsWith(projectRootWithSep, StringComparison.Ordinal) ||
                full.Equals(projectRoot, StringComparison.Ordinal))
            {
                if (!Directory.Exists(full) && !File.Exists(full))
                {
                    return command with
                    {
                        Status = CommandStatus.Error,
                        ErrorCode = CommandErrorCode.NotFound,
                        Error = $"Path not found: {subPath}. Use paths relative to the project root (e.g., src/AgentAcademy.Server/Commands)."
                    };
                }
                pathScope = Path.GetRelativePath(projectRoot, full);
                pathScopeIsFile = File.Exists(full);
            }
        }

        // Combine glob and path into a single pathspec when both are provided
        if (globFilter is not null || pathScope is not null)
        {
            psi.ArgumentList.Add("--");
            if (globFilter is not null && pathScope is not null && !pathScopeIsFile)
            {
                // Combine: only match glob within the directory scope
                psi.ArgumentList.Add($":(glob){pathScope}/**/{globFilter}");
            }
            else if (pathScope is not null)
            {
                // File path or directory without glob — use directly
                psi.ArgumentList.Add(pathScope);
            }
            else
            {
                psi.ArgumentList.Add(globFilter!);
            }
        }

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Execution, Error = "Failed to start search process." };
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            // git grep output format: file:line:content
            var allLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var truncated = allLines.Length > MaxResults;
            var matches = allLines
                .Take(MaxResults)
                .Select(line =>
                {
                    var parts = line.Split(':', 3);
                    if (parts.Length >= 3)
                    {
                        return new Dictionary<string, object?>
                        {
                            ["file"] = parts[0],
                            ["line"] = int.TryParse(parts[1], out var ln) ? ln : 0,
                            ["text"] = parts[2].Trim()
                        };
                    }
                    return new Dictionary<string, object?> { ["text"] = line };
                })
                .ToList();

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
        catch (Exception ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = $"Search failed: {ex.Message}"
            };
        }
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
