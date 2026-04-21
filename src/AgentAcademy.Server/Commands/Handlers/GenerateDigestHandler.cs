using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles GENERATE_DIGEST — manually triggers learning digest generation.
/// The digest synthesizes accumulated retrospective summaries into cross-cutting
/// shared memories via the planner agent.
/// </summary>
public sealed class GenerateDigestHandler : ICommandHandler
{
    public string CommandName => "GENERATE_DIGEST";

    public bool IsRetrySafe => false;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var digestService = context.Services.GetRequiredService<ILearningDigestService>();

        var force = false;
        if (command.Args.TryGetValue("force", out var forceObj))
        {
            force = forceObj switch
            {
                bool b => b,
                string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        var digestId = await digestService.TryGenerateDigestAsync(force);

        if (digestId is null)
        {
            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["generated"] = false,
                    ["message"] = force
                        ? "Digest generation was skipped (another digest may be in progress, or no undigested retrospectives exist)."
                        : "Not enough undigested retrospectives to meet the threshold. Use force=true to override."
                }
            };
        }

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["generated"] = true,
                ["digestId"] = digestId.Value,
                ["message"] = $"Learning digest #{digestId} generated successfully."
            }
        };
    }
}
