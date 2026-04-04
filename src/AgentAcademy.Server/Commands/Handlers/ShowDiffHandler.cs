using System.Diagnostics;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles SHOW_DIFF — shows git diff output for the current workspace.
/// Supports optional taskId, branch, or agentId filters.
/// </summary>
public sealed class ShowDiffHandler : ICommandHandler
{
    public string CommandName => "SHOW_DIFF";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var projectRoot = FindProjectRoot();

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
        psi.ArgumentList.Add("diff");

        if (command.Args.TryGetValue("branch", out var branchObj) && branchObj is string branch &&
            !string.IsNullOrWhiteSpace(branch) && !branch.StartsWith('-'))
        {
            psi.ArgumentList.Add(branch);
        }

        psi.ArgumentList.Add("--stat");
        psi.ArgumentList.Add("-p");

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return command with { Status = CommandStatus.Error, Error = "Failed to start git process." };

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (output.Length > 5000)
                output = output[..2500] + "\n... (truncated) ...\n" + output[^2500..];

            if (string.IsNullOrWhiteSpace(output))
                output = "(no changes)";

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["diff"] = output.Trim(),
                    ["exitCode"] = process.ExitCode
                }
            };
        }
        catch (Exception ex)
        {
            return command with { Status = CommandStatus.Error, Error = $"Diff failed: {ex.Message}" };
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
