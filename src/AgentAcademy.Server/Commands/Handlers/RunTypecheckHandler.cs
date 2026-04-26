using System.Diagnostics;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles RUN_TYPECHECK — runs <c>npx tsc --noEmit</c> in the client directory.
/// Shares the frontend lock with <see cref="RunFrontendBuildHandler"/> to prevent
/// concurrent TypeScript operations that may contend on build-info files.
/// </summary>
public sealed class RunTypecheckHandler : ICommandHandler
{
    public string CommandName => "RUN_TYPECHECK";

    private static readonly TimeSpan TypecheckTimeout = TimeSpan.FromMinutes(5);

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        await RunFrontendBuildHandler.FrontendLock.WaitAsync();

        try
        {
            var clientDir = RunFrontendBuildHandler.FindClientDirInRoot(context.WorkingDirectory);

            var psi = new ProcessStartInfo
            {
                FileName = "npx",
                Arguments = "tsc --noEmit",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = clientDir
            };

            using var process = Process.Start(psi);
            if (process is null)
                return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Execution, Error = "Failed to start tsc process." };

            using var cts = new CancellationTokenSource(TypecheckTimeout);
            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
                await Task.WhenAll(stdoutTask, stderrTask);
                await process.WaitForExitAsync(cts.Token);

                var output = string.IsNullOrWhiteSpace(stderrTask.Result)
                    ? stdoutTask.Result
                    : $"{stdoutTask.Result}\n{stderrTask.Result}";
                if (output.Length > 4000)
                    output = output[..2000] + "\n... (truncated) ...\n" + output[^2000..];

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
                    Error = process.ExitCode != 0 ? $"Type check failed with exit code {process.ExitCode}" : null
                };
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Timeout, Error = $"Type check timed out after {TypecheckTimeout.TotalMinutes} minutes." };
            }
        }
        catch (Exception ex)
        {
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Execution, Error = $"Type check failed: {ex.Message}" };
        }
        finally
        {
            RunFrontendBuildHandler.FrontendLock.Release();
        }
    }
}
