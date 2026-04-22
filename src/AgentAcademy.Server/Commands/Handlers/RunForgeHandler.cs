using System.Text.Json;
using AgentAcademy.Forge.Models;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles RUN_FORGE — starts a new forge pipeline run.
/// Loads a methodology definition from a JSON file, creates a task brief,
/// and enqueues the run via <see cref="IForgeJobService"/>.
/// Returns immediately with a job ID; the pipeline executes in the background.
/// </summary>
public sealed class RunForgeHandler : ICommandHandler
{
    public string CommandName => "RUN_FORGE";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        // Check Forge is enabled at all
        var options = context.Services.GetRequiredService<ForgeOptions>();
        if (!options.Enabled)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = "Forge engine is disabled on this server. Set Forge:Enabled=true in configuration."
            };
        }

        // Check execution is enabled
        if (!options.ExecutionAvailable)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = "Forge execution is disabled on this server. Set Forge:ExecutionEnabled=true in configuration."
            };
        }

        // Validate required args
        if (!command.Args.TryGetValue("title", out var titleObj) || titleObj is not string title
            || string.IsNullOrWhiteSpace(title))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Required argument 'title' is missing or empty."
            };
        }

        if (!command.Args.TryGetValue("description", out var descObj) || descObj is not string description
            || string.IsNullOrWhiteSpace(description))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Required argument 'description' is missing or empty."
            };
        }

        if (!command.Args.TryGetValue("methodologyPath", out var pathObj) || pathObj is not string methodologyPath
            || string.IsNullOrWhiteSpace(methodologyPath))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Required argument 'methodologyPath' is missing. Provide a path to a methodology JSON file."
            };
        }

        // Resolve path relative to agent working directory
        var workDir = context.WorkingDirectory ?? FindProjectRoot();
        var resolvedPath = Path.IsPathRooted(methodologyPath)
            ? methodologyPath
            : Path.GetFullPath(Path.Combine(workDir, methodologyPath));

        // Path traversal protection: must be within the working root.
        // Also reject symlinks whose targets escape the root.
        var normalizedRoot = Path.GetFullPath(workDir) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(resolvedPath);
        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Permission,
                Error = $"Path '{methodologyPath}' resolves outside the project root. Access denied."
            };
        }

        if (!File.Exists(resolvedPath))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Methodology file not found: {methodologyPath}"
            };
        }

        // Reject symlinks whose real target escapes the project root
        var fileInfo = new FileInfo(resolvedPath);
        if (fileInfo.LinkTarget is not null)
        {
            var realTarget = Path.GetFullPath(
                Path.IsPathRooted(fileInfo.LinkTarget)
                    ? fileInfo.LinkTarget
                    : Path.Combine(Path.GetDirectoryName(resolvedPath)!, fileInfo.LinkTarget));
            if (!realTarget.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Permission,
                    Error = $"Path '{methodologyPath}' is a symlink targeting outside the project root. Access denied."
                };
            }
        }

        // Load and validate methodology
        MethodologyDefinition methodology;
        try
        {
            var json = await File.ReadAllTextAsync(resolvedPath);
            var deserialized = JsonSerializer.Deserialize<MethodologyDefinition>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (deserialized is null)
                throw new JsonException("Methodology file contains null or empty JSON.");
            methodology = deserialized;

            if (string.IsNullOrWhiteSpace(methodology.Id))
                throw new JsonException("Methodology 'id' is required.");
            if (methodology.Phases is null || methodology.Phases.Count == 0)
                throw new JsonException("Methodology must define at least one phase.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = $"Invalid methodology file: {ex.Message}"
            };
        }

        // Optional task ID
        var taskId = command.Args.TryGetValue("taskId", out var taskIdObj) && taskIdObj is string tid
            ? tid
            : Guid.NewGuid().ToString("N")[..8];

        var taskBrief = new TaskBrief
        {
            TaskId = taskId,
            Title = title,
            Description = description
        };

        // Enqueue the run
        var jobService = context.Services.GetRequiredService<IForgeJobService>();
        ForgeJob job;
        try
        {
            job = await jobService.StartRunAsync(taskBrief, methodology);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("queue is full", StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = "Forge run queue is full. Wait for active runs to complete before starting new ones."
            };
        }

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["jobId"] = job.JobId,
                ["status"] = job.Status.ToString().ToLowerInvariant(),
                ["createdAt"] = job.CreatedAt.ToString("O"),
                ["taskId"] = taskBrief.TaskId,
                ["message"] = "Forge run queued. Pipeline executes in background. Use FORGE_STATUS to monitor."
            }
        };
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
