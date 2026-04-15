using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles LINK_TASK_TO_SPEC — creates a traceability link between a task and a spec section.
/// Any agent can create links (traceability aid, not a security boundary).
/// </summary>
public sealed class LinkTaskToSpecHandler : ICommandHandler
{
    public string CommandName => "LINK_TASK_TO_SPEC";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("taskId", out var taskIdObj) || taskIdObj is not string taskId
            || string.IsNullOrWhiteSpace(taskId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: taskId"
            };
        }

        if (!command.Args.TryGetValue("specSectionId", out var specObj) || specObj is not string specSectionId
            || string.IsNullOrWhiteSpace(specSectionId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: specSectionId (e.g., '003-agent-system')"
            };
        }

        var linkType = "Implements";
        if (command.Args.TryGetValue("linkType", out var linkTypeObj) && linkTypeObj is string lt
            && !string.IsNullOrWhiteSpace(lt))
        {
            linkType = lt;
        }

        string? note = null;
        if (command.Args.TryGetValue("note", out var noteObj) && noteObj is string n
            && !string.IsNullOrWhiteSpace(n))
        {
            note = n;
        }

        var taskLifecycle = context.Services.GetRequiredService<ITaskLifecycleService>();
        var specManager = context.Services.GetRequiredService<SpecManager>();

        // Validate the spec section exists on disk
        var specContent = await specManager.GetSpecContentAsync(specSectionId);
        if (specContent is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Spec section '{specSectionId}' not found in specs/ directory"
            };
        }

        try
        {
            var link = await taskLifecycle.LinkTaskToSpecAsync(
                taskId, specSectionId, context.AgentId, context.AgentName, linkType, note);

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["linkId"] = link.Id,
                    ["taskId"] = link.TaskId,
                    ["specSectionId"] = link.SpecSectionId,
                    ["linkType"] = link.LinkType.ToString(),
                    ["message"] = $"Linked spec '{specSectionId}' to task '{taskId}' as {linkType}"
                }
            };
        }
        catch (ArgumentException ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = ex.Message
            };
        }
        catch (InvalidOperationException ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Infer(ex.Message),
                Error = ex.Message
            };
        }
    }
}
