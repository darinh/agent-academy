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
    public bool IsRetrySafe => true;

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

        // Build argument list directly (not an Arguments string) to avoid
        // shell-quoting issues with UseShellExecute = false
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = projectRoot
        };
        psi.ArgumentList.Add("--no-pager");
        psi.ArgumentList.Add("log");
        psi.ArgumentList.Add("--oneline");
        psi.ArgumentList.Add("--no-decorate");
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(count.ToString());

        if (command.Args.TryGetValue("since", out var sinceObj) && sinceObj is string since &&
            !string.IsNullOrWhiteSpace(since))
        {
            psi.ArgumentList.Add($"--since={since}");
        }

        if (command.Args.TryGetValue("file", out var fileObj) && fileObj is string file &&
            !string.IsNullOrWhiteSpace(file))
        {
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(file);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Execution, Error = "Failed to start git process." };

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
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Execution, Error = $"Git log failed: {ex.Message}" };
        }
    }

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
