using System.Diagnostics;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles FIND_REFERENCES — searches for symbol usages in source code using
/// fixed-string matching (not regex). Always scoped to src/ directory.
/// Uses -F (fixed string) and optionally -w (whole word) for precise symbol lookup.
/// </summary>
public sealed class FindReferencesHandler : ICommandHandler
{
    public string CommandName => "FIND_REFERENCES";
    public bool IsRetrySafe => true;

    private const int MaxResults = 50;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("symbol", out var symbolObj) || symbolObj is not string symbol || string.IsNullOrWhiteSpace(symbol))
        {
            if (!command.Args.TryGetValue("value", out symbolObj) || symbolObj is not string val || string.IsNullOrWhiteSpace(val))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Validation,
                    Error = "Missing required argument: symbol (e.g. \"CommandParser\", \"ISpecManager\", \"ExecuteAsync\")"
                };
            }
            symbol = val;
        }

        symbol = symbol.Trim();
        var projectRoot = context.WorkingDirectory ?? FindProjectRoot();

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = projectRoot
        };
        psi.ArgumentList.Add("grep");
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("-F"); // Fixed string — not regex
        psi.ArgumentList.Add("--max-count");
        psi.ArgumentList.Add(MaxResults.ToString());

        if (command.Args.TryGetValue("ignoreCase", out var icObj) &&
            (icObj is true || (icObj is string icStr && icStr.Equals("true", StringComparison.OrdinalIgnoreCase))))
        {
            psi.ArgumentList.Add("-i");
        }

        // Whole-word matching unless explicitly disabled
        var wholeWord = true;
        if (command.Args.TryGetValue("wholeWord", out var wwObj))
        {
            if (wwObj is false ||
                (wwObj is string wwStr && wwStr.Equals("false", StringComparison.OrdinalIgnoreCase)))
            {
                wholeWord = false;
            }
        }

        if (wholeWord)
        {
            psi.ArgumentList.Add("-w");
        }

        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(symbol);

        // Scope to src/ by default, or to a specific subdirectory
        var searchPath = "src/";
        if (command.Args.TryGetValue("path", out var pathObj) && pathObj is string subPath && !string.IsNullOrWhiteSpace(subPath))
        {
            var fullSubPath = Path.GetFullPath(Path.Combine(projectRoot, subPath));
            var projectRootWithSep = projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullSubPath.StartsWith(projectRootWithSep, StringComparison.Ordinal))
            {
                return command with
                {
                    Status = CommandStatus.Denied,
                    ErrorCode = CommandErrorCode.Permission,
                    Error = "Path traversal denied: search path must be within the project directory."
                };
            }

            if (!Directory.Exists(fullSubPath) && !File.Exists(fullSubPath))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.NotFound,
                    Error = $"Path not found: {subPath}"
                };
            }
            searchPath = subPath;
        }

        psi.ArgumentList.Add(searchPath);

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        // git grep exits 1 when no matches found — not an error
        if (process.ExitCode > 1)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = $"Search failed: {stderr.Trim()}"
            };
        }

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
        var isTruncated = parsed.Count > MaxResults;

        // Group results by file for easier consumption
        var fileGroups = matches
            .GroupBy(m => m["file"]?.ToString() ?? "")
            .Select(g => new Dictionary<string, object?>
            {
                ["file"] = g.Key,
                ["count"] = g.Count(),
                ["lines"] = g.Select(m => new Dictionary<string, object?>
                {
                    ["line"] = m["line"],
                    ["text"] = m["text"]
                }).ToList()
            })
            .ToList();

        var result = new Dictionary<string, object?>
        {
            ["symbol"] = symbol,
            ["totalMatches"] = matches.Count,
            ["fileCount"] = fileGroups.Count,
            ["files"] = fileGroups
        };

        if (isTruncated)
        {
            result["truncated"] = true;
            result["hint"] = $"Results capped at {MaxResults}. Add path= to narrow scope.";
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
