using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles LIST_SYSTEM_SETTINGS — returns all runtime system settings
/// with their current values and defaults filled in for known keys.
/// </summary>
public sealed class ListSystemSettingsHandler : ICommandHandler
{
    public string CommandName => "LIST_SYSTEM_SETTINGS";
    public bool IsRetrySafe => true;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var settingsService = context.Services.GetService<ISystemSettingsService>();
        if (settingsService is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Internal,
                Error = "System settings service is not available."
            };
        }

        var allSettings = await settingsService.GetAllWithDefaultsAsync();

        var entries = allSettings
            .OrderBy(kv => kv.Key)
            .Select(kv => new Dictionary<string, object?>
            {
                ["key"] = kv.Key,
                ["value"] = kv.Value
            })
            .ToList();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["settings"] = entries,
                ["count"] = entries.Count
            }
        };
    }
}
