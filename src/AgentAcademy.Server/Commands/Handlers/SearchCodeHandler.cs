using System.Diagnostics;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles SEARCH_CODE — searches for text patterns in the project codebase using grep.
/// </summary>
public sealed class SearchCodeHandler : ICommandHandler
{
    public string CommandName => "SEARCH_CODE";

    private const int MaxResults = 50;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("query", out var queryObj) || queryObj is not string query || string.IsNullOrWhiteSpace(query))
        {
            return command with
            {
                Status = CommandStatus.Error,
                Error = "Missing required argument: query"
            };
        }

        var projectRoot = FindProjectRoot();

        // Optional path filter
        var searchPath = projectRoot;
        if (command.Args.TryGetValue("path", out var pathObj) && pathObj is string subPath && !string.IsNullOrWhiteSpace(subPath))
        {
            var full = Path.GetFullPath(Path.Combine(projectRoot, subPath));
            var projectRootWithSep = projectRoot.EndsWith(Path.DirectorySeparatorChar)
                ? projectRoot : projectRoot + Path.DirectorySeparatorChar;
            if (full.StartsWith(projectRootWithSep, StringComparison.Ordinal) ||
                full.Equals(projectRoot, StringComparison.Ordinal))
                searchPath = full;
        }

        // Build grep command using ArgumentList (not Arguments string) to avoid
        // shell-quoting issues with UseShellExecute = false
        var psi = new ProcessStartInfo
        {
            FileName = "grep",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = projectRoot
        };
        psi.ArgumentList.Add("-rn");
        psi.ArgumentList.Add("--color=never");
        if (command.Args.TryGetValue("glob", out var globObj) && globObj is string glob && !string.IsNullOrWhiteSpace(glob))
            psi.ArgumentList.Add($"--include={glob}");
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add(MaxResults.ToString());
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(query);
        psi.ArgumentList.Add(searchPath);

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                return command with { Status = CommandStatus.Error, Error = "Failed to start grep process." };
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            // Parse grep output into structured results
            var matches = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Take(MaxResults)
                .Select(line =>
                {
                    var parts = line.Split(':', 3);
                    if (parts.Length >= 3)
                    {
                        var filePath = Path.GetRelativePath(projectRoot, parts[0]);
                        return new Dictionary<string, object?>
                        {
                            ["file"] = filePath,
                            ["line"] = int.TryParse(parts[1], out var ln) ? ln : 0,
                            ["text"] = parts[2].Trim()
                        };
                    }
                    return new Dictionary<string, object?> { ["text"] = line };
                })
                .ToList();

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["matches"] = matches,
                    ["count"] = matches.Count,
                    ["query"] = query
                }
            };
        }
        catch (Exception ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
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
