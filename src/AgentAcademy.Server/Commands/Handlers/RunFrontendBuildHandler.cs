using System.Diagnostics;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles RUN_FRONTEND_BUILD — runs <c>npm run build</c> in the client directory.
/// Serialized with a shared frontend lock to prevent concurrent npm operations.
/// </summary>
public sealed class RunFrontendBuildHandler : ICommandHandler
{
    internal static readonly SemaphoreSlim FrontendLock = new(1, 1);

    public string CommandName => "RUN_FRONTEND_BUILD";

    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        await FrontendLock.WaitAsync();

        try
        {
            var clientDir = FindClientDirInRoot(context.WorkingDirectory);

            var psi = new ProcessStartInfo
            {
                FileName = "npm",
                Arguments = "run build",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = clientDir
            };

            using var process = Process.Start(psi);
            if (process is null)
                return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Execution, Error = "Failed to start npm build process." };

            using var cts = new CancellationTokenSource(BuildTimeout);
            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
                await Task.WhenAll(stdoutTask, stderrTask);
                await process.WaitForExitAsync(cts.Token);

                var output = CombineOutput(stdoutTask.Result, stderrTask.Result);
                if (output.Length > 3000)
                    output = output[..1500] + "\n... (truncated) ...\n" + output[^1500..];

                return command with
                {
                    Status = process.ExitCode == 0 ? CommandStatus.Success : CommandStatus.Error,
                    ErrorCode = process.ExitCode != 0 ? CommandErrorCode.Execution : null,
                    Result = new Dictionary<string, object?>
                    {
                        ["exitCode"] = process.ExitCode,
                        ["output"] = output.Trim(),
                        ["success"] = process.ExitCode == 0
                    },
                    Error = process.ExitCode != 0 ? $"Frontend build failed with exit code {process.ExitCode}" : null
                };
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Timeout, Error = $"Frontend build timed out after {BuildTimeout.TotalMinutes} minutes." };
            }
        }
        catch (Exception ex)
        {
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Execution, Error = $"Frontend build failed: {ex.Message}" };
        }
        finally
        {
            FrontendLock.Release();
        }
    }

    private static string CombineOutput(string stdout, string stderr)
        => string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";

    private static void TryKillProcess(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { }
    }

    internal static string FindClientDir()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "AgentAcademy.sln")))
                return Path.Combine(dir, "src", "agent-academy-client");
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "src", "agent-academy-client");
    }

    /// <summary>
    /// Returns the client directory rooted at <paramref name="projectRoot"/> when
    /// supplied — used by per-worktree breakouts so the frontend build runs
    /// against the worktree's checkout, not develop. Falls back to the cwd-walking
    /// helper for main-room callers (no scope).
    /// </summary>
    internal static string FindClientDirInRoot(string? projectRoot)
        => projectRoot is null
            ? FindClientDir()
            : Path.Combine(projectRoot, "src", "agent-academy-client");
}
