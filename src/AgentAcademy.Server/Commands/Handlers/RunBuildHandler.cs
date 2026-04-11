using System.Diagnostics;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles RUN_BUILD — runs the project build command and returns output + exit code.
/// </summary>
public sealed class RunBuildHandler : ICommandHandler
{
    private static readonly SemaphoreSlim BuildLock = new(1, 1);

    public string CommandName => "RUN_BUILD";

    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        await BuildLock.WaitAsync();

        try
        {
            var projectRoot = FindProjectRoot();

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "build --nologo -v q",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = projectRoot
            };

            using var process = Process.Start(psi);
            if (process is null)
                return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Execution, Error = "Failed to start build process." };

            using var cts = new CancellationTokenSource(BuildTimeout);
            var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            var output = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
            // Truncate to avoid bloating the context
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
                Error = process.ExitCode != 0 ? $"Build failed with exit code {process.ExitCode}" : null
            };
        }
        catch (OperationCanceledException)
        {
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Timeout, Error = $"Build timed out after {BuildTimeout.TotalMinutes} minutes." };
        }
        catch (Exception ex)
        {
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Execution, Error = $"Build failed: {ex.Message}" };
        }
        finally
        {
            BuildLock.Release();
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
