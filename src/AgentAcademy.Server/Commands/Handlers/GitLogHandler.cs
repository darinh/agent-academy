using System.Diagnostics;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles GIT_LOG — shows recent commit history.
/// Supports optional file, since, and count filters.
/// </summary>
public sealed class GitLogHandler : ICommandHandler
{
    public string CommandName => "GIT_LOG";

    private const int DefaultCount = 20;
    private const int MaxCount = 50;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var projectRoot = FindProjectRoot();

        var count = DefaultCount;
        if (command.Args.TryGetValue("count", out var countObj))
        {
            if (countObj is string countStr && int.TryParse(countStr, out var parsed))
                count = Math.Min(parsed, MaxCount);
            else if (countObj is int countInt)
                count = Math.Min(countInt, MaxCount);
        }

        var args = $"--no-pager log --oneline --no-decorate -n {count}";

        if (command.Args.TryGetValue("since", out var sinceObj) && sinceObj is string since &&
            !string.IsNullOrWhiteSpace(since))
        {
            args += $" --since={EscapeArg(since)}";
        }

        if (command.Args.TryGetValue("file", out var fileObj) && fileObj is string file &&
            !string.IsNullOrWhiteSpace(file))
        {
            args += $" -- {EscapeArg(file)}";
        }

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = projectRoot
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return command with { Status = CommandStatus.Error, Error = "Failed to start git process." };

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var commits = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line =>
                {
                    var spaceIdx = line.IndexOf(' ');
                    return spaceIdx > 0
                        ? new Dictionary<string, object?> { ["sha"] = line[..spaceIdx], ["message"] = line[(spaceIdx + 1)..] }
                        : new Dictionary<string, object?> { ["message"] = line };
                })
                .ToList();

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["commits"] = commits,
                    ["count"] = commits.Count
                }
            };
        }
        catch (Exception ex)
        {
            return command with { Status = CommandStatus.Error, Error = $"Git log failed: {ex.Message}" };
        }
    }

    private static string EscapeArg(string arg) => $"'{arg.Replace("'", "'\\''")}'";

    private static string FindProjectRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "AgentAcademy.sln")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
