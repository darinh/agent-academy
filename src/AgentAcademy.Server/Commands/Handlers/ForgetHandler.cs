using AgentAcademy.Server.Data;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles FORGET — deletes a memory entry for the executing agent.
/// </summary>
public sealed class ForgetHandler : ICommandHandler
{
    public string CommandName => "FORGET";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("key", out var keyObj) || keyObj is not string key || string.IsNullOrWhiteSpace(key))
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Validation, Error = "Missing required argument: key" };

        var db = context.Services.GetRequiredService<AgentAcademyDbContext>();
        var entity = await db.AgentMemories.FindAsync(context.AgentId, key);

        if (entity == null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"No memory found with key '{key}'."
            };
        }

        db.AgentMemories.Remove(entity);
        await db.SaveChangesAsync();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["key"] = key,
                ["action"] = "deleted"
            }
        };
    }
}
