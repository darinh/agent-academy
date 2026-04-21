using System.Diagnostics;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles SEARCH_SPEC — searches for text patterns across spec files using git grep.
/// Returns line-level results scoped to the specs/ directory.
/// </summary>
public sealed class SearchSpecHandler : ICommandHandler
{
    public string CommandName => "SEARCH_SPEC";
    public bool IsRetrySafe => true;

    private const int MaxResults = 50;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("query", out var queryObj) || queryObj is not string query || string.IsNullOrWhiteSpace(query))
        {
            if (!command.Args.TryGetValue("value", out queryObj) || queryObj is not string val || string.IsNullOrWhiteSpace(val))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Validation,
                    Error = "Missing required argument: query"
                };
            }
            query = val;
        }

        var projectRoot = FindProjectRoot();
        var specsDir = Path.Combine(projectRoot, "specs");

        if (!Directory.Exists(specsDir))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = "No specs/ directory found in the project."
            };
        }

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
        psi.ArgumentList.Add("--max-count");
        psi.ArgumentList.Add(MaxResults.ToString());

        if (command.Args.TryGetValue("ignoreCase", out var icObj) &&
            (icObj is true || (icObj is string icStr && icStr.Equals("true", StringComparison.OrdinalIgnoreCase))))
        {
            psi.ArgumentList.Add("-i");
        }

        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(query);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("specs/");

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

        var result = new Dictionary<string, object?>
        {
            ["matches"] = matches,
            ["count"] = matches.Count,
            ["query"] = query
        };

        if (isTruncated)
        {
            result["truncated"] = true;
            result["hint"] = $"Results capped at {MaxResults}. Narrow your query.";
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
