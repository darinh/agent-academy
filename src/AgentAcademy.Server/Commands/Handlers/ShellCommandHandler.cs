using System.Diagnostics;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles SHELL — an allowlisted command runner for specific operational actions.
/// No arbitrary shell execution is permitted; each operation maps to a fixed command.
/// Restricted to Planner and Reviewer role agents.
/// </summary>
public sealed class ShellCommandHandler : ICommandHandler
{
    private static readonly TimeSpan DotnetTimeout = TimeSpan.FromMinutes(10);

    private readonly GitService _gitService;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ShellCommandHandler> _logger;

    public ShellCommandHandler(
        GitService gitService,
        IHostApplicationLifetime lifetime,
        ILogger<ShellCommandHandler> logger)
    {
        _gitService = gitService;
        _lifetime = lifetime;
        _logger = logger;
    }

    public string CommandName => "SHELL";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(context.AgentRole, "Reviewer", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Agent {AgentId} ({Role}) attempted SHELL — denied (Planner/Reviewer only)",
                context.AgentId, context.AgentRole);

            return command with
            {
                Status = CommandStatus.Denied,
                ErrorCode = CommandErrorCode.Permission,
                Error = "SHELL is restricted to Planner and Reviewer role agents."
            };
        }

        if (!ShellCommand.TryParse(command.Args, out var parsed, out var error))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = error
            };
        }

        return parsed!.Operation switch
        {
            "git-checkout" => await ExecuteGitCheckoutAsync(command, parsed, context),
            "git-commit" => await ExecuteGitCommitAsync(command, parsed, context),
            "git-stash-pop" => await ExecuteGitStashPopAsync(command, parsed),
            "restart-server" => await ExecuteRestartServerAsync(command, context, parsed),
            "dotnet-build" => await ExecuteDotnetAsync(command, parsed.Operation, "dotnet", ["build", "--nologo", "-v", "q"], context),
            "dotnet-test" => await ExecuteDotnetAsync(command, parsed.Operation, "dotnet", ["test", "--nologo", "-v", "q"], context),
            _ => command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = $"Unsupported SHELL operation '{parsed.Operation}'."
            }
        };
    }

    private async Task<CommandEnvelope> ExecuteGitCheckoutAsync(CommandEnvelope command, ShellCommand parsed, CommandContext context)
    {
        if (context.WorkingDirectory is not null)
        {
            // Verify the requested branch matches the worktree's branch
            var currentBranch = await _gitService.GetCurrentBranchInDirAsync(context.WorkingDirectory);
            if (currentBranch is not null && !string.Equals(currentBranch, parsed.Branch, StringComparison.OrdinalIgnoreCase))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Validation,
                    Error = $"Cannot checkout '{parsed.Branch}' — agent is working in a worktree on branch '{currentBranch}'. " +
                            $"Worktrees are locked to their assigned branch."
                };
            }

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["operation"] = parsed.Operation,
                    ["branch"] = parsed.Branch,
                    ["exitCode"] = 0,
                    ["success"] = true,
                    ["message"] = $"Already on branch '{currentBranch ?? parsed.Branch}' in worktree — no checkout needed."
                }
            };
        }

        try
        {
            await _gitService.CheckoutBranchAsync(parsed.Branch!);

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["operation"] = parsed.Operation,
                    ["branch"] = parsed.Branch,
                    ["exitCode"] = 0,
                    ["success"] = true,
                    ["message"] = $"Checked out branch '{parsed.Branch}'."
                }
            };
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "SHELL git-checkout failed for branch {Branch}", parsed.Branch);
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = $"git-checkout failed: {ex.Message}"
            };
        }
    }

    private async Task<CommandEnvelope> ExecuteGitCommitAsync(CommandEnvelope command, ShellCommand parsed, CommandContext context)
    {
        try
        {
            string commitSha;
            if (context.WorkingDirectory is not null)
                commitSha = await _gitService.CommitInDirAsync(context.WorkingDirectory, parsed.Message!, context.GitIdentity);
            else
                commitSha = await _gitService.CommitAsync(parsed.Message!, context.GitIdentity);

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["operation"] = parsed.Operation,
                    ["commitSha"] = commitSha,
                    ["exitCode"] = 0,
                    ["success"] = true,
                    ["message"] = "Commit created successfully."
                }
            };
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "SHELL git-commit failed");
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = $"git-commit failed: {ex.Message}"
            };
        }
    }

    private async Task<CommandEnvelope> ExecuteGitStashPopAsync(CommandEnvelope command, ShellCommand parsed)
    {
        try
        {
            var restored = await _gitService.PopAutoStashAsync(parsed.Branch!);
            if (!restored)
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.NotFound,
                    Error = $"git-stash-pop failed: no auto-stash found for branch '{parsed.Branch}'."
                };
            }

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["operation"] = parsed.Operation,
                    ["branch"] = parsed.Branch,
                    ["exitCode"] = 0,
                    ["success"] = true,
                    ["message"] = $"Restored auto-stash for branch '{parsed.Branch}'."
                }
            };
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "SHELL git-stash-pop failed for branch {Branch}", parsed.Branch);
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = $"git-stash-pop failed: {ex.Message}"
            };
        }
    }

    private async Task<CommandEnvelope> ExecuteRestartServerAsync(
        CommandEnvelope command,
        CommandContext context,
        ShellCommand parsed)
    {
        _logger.LogWarning(
            "Server restart requested via SHELL by {AgentId}: {Reason}",
            context.AgentId, parsed.Reason);

        try
        {
            var catalog = context.Services.GetRequiredService<IAgentCatalog>();
        var messages = context.Services.GetRequiredService<MessageService>();
            await messages.PostSystemStatusAsync(catalog.DefaultRoomId,
                $"🔄 **Server restarting**: {parsed.Reason} (requested by {context.AgentName} via SHELL)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post SHELL restart notification");
        }

        Environment.ExitCode = RestartServerHandler.RestartExitCode;
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            _lifetime.StopApplication();
        });

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["operation"] = parsed.Operation,
                ["reason"] = parsed.Reason,
                ["exitCode"] = RestartServerHandler.RestartExitCode,
                ["success"] = true,
                ["message"] = "Server restart initiated. The wrapper script will restart the process."
            }
        };
    }

    private async Task<CommandEnvelope> ExecuteDotnetAsync(
        CommandEnvelope command,
        string operation,
        string fileName,
        IReadOnlyList<string> arguments,
        CommandContext? context = null)
    {
        try
        {
            var result = await ExecuteProcessAsync(fileName, arguments, context?.WorkingDirectory ?? FindProjectRoot(), DotnetTimeout);

            return command with
            {
                Status = result.ExitCode == 0 ? CommandStatus.Success : CommandStatus.Error,
                ErrorCode = result.ExitCode == 0 ? null : CommandErrorCode.Execution,
                Result = new Dictionary<string, object?>
                {
                    ["operation"] = operation,
                    ["exitCode"] = result.ExitCode,
                    ["output"] = result.Output,
                    ["success"] = result.ExitCode == 0
                },
                Error = result.ExitCode == 0 ? null : $"{operation} failed with exit code {result.ExitCode}."
            };
        }
        catch (OperationCanceledException)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Timeout,
                Error = $"{operation} timed out after {DotnetTimeout.TotalMinutes} minutes."
            };
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "SHELL {Operation} failed to start", operation);
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = $"{operation} failed: {ex.Message}"
            };
        }
    }

    private static async Task<ProcessExecutionResult> ExecuteProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        using var cts = new CancellationTokenSource(timeout);
        var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);

        var output = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
        output = output.Trim();
        if (output.Length > 4000)
            output = output[..2000] + "\n... (truncated) ...\n" + output[^2000..];

        return new ProcessExecutionResult(process.ExitCode, output);
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

    private sealed record ProcessExecutionResult(int ExitCode, string Output);
}
