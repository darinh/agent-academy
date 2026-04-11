using System.Diagnostics;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles RUN_TESTS — runs the project test suite and returns results + exit code.
/// Supports optional scope: all (default), backend, frontend, or file:path.
/// </summary>
public sealed class RunTestsHandler : ICommandHandler
{
    private static readonly SemaphoreSlim TestLock = new(1, 1);

    public string CommandName => "RUN_TESTS";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        await TestLock.WaitAsync();

        try
        {
            var projectRoot = FindProjectRoot();

            // Parse optional scope
            var scope = "all";
            if (command.Args.TryGetValue("scope", out var scopeObj) && scopeObj is string s && !string.IsNullOrWhiteSpace(s))
                scope = s.ToLowerInvariant();

            string fileName;
            string arguments;

            switch (scope)
            {
                case "frontend":
                    fileName = "npm";
                    arguments = "test -- --run";
                    projectRoot = Path.Combine(projectRoot, "src", "agent-academy-client");
                    break;
                case "backend":
                    fileName = "dotnet";
                    arguments = "test --nologo -v q";
                    break;
                case var _ when scope.StartsWith("file:"):
                    var filter = scope[5..];
                    fileName = "dotnet";
                    arguments = $"test --nologo -v q --filter \"{filter}\"";
                    break;
                default: // "all"
                    fileName = "dotnet";
                    arguments = "test --nologo -v q";
                    break;
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = projectRoot
            };

            using var process = Process.Start(psi);
            if (process is null)
                return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Execution, Error = "Failed to start test process." };

            using var cts = new CancellationTokenSource(TestTimeout);
            var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            var output = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
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
                    ["scope"] = scope,
                    ["success"] = process.ExitCode == 0
                },
                Error = process.ExitCode != 0 ? $"Tests failed with exit code {process.ExitCode}" : null
            };
        }
        catch (OperationCanceledException)
        {
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Timeout, Error = $"Tests timed out after {TestTimeout.TotalMinutes} minutes." };
        }
        catch (Exception ex)
        {
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Execution, Error = $"Test run failed: {ex.Message}" };
        }
        finally
        {
            TestLock.Release();
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
