using System.Text.Json;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles UPDATE_GOAL_CARD_STATUS — transitions a goal card through its lifecycle.
/// Validates legal transitions (e.g., Active → Completed, Active → Challenged).
/// </summary>
public sealed class UpdateGoalCardStatusHandler : ICommandHandler
{
    public string CommandName => "UPDATE_GOAL_CARD_STATUS";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var goalCards = context.Services.GetRequiredService<IGoalCardService>();

        string? goalCardId = GetStringArg(command.Args, "goal_card_id");
        string? statusStr = GetStringArg(command.Args, "status");

        if (string.IsNullOrWhiteSpace(goalCardId) || string.IsNullOrWhiteSpace(statusStr))
        {
            return command with
            {
                Status = CommandStatus.Error,
                Result = new Dictionary<string, object?>
                {
                    ["error"] = "Missing required fields: goal_card_id, status",
                    ["valid_statuses"] = new[] { "Active", "Completed", "Challenged", "Abandoned" }
                }
            };
        }

        if (!Enum.TryParse<GoalCardStatus>(statusStr, ignoreCase: true, out var newStatus))
        {
            return command with
            {
                Status = CommandStatus.Error,
                Result = new Dictionary<string, object?>
                {
                    ["error"] = $"Invalid status '{statusStr}'. Must be one of: Active, Completed, Challenged, Abandoned"
                }
            };
        }

        try
        {
            var card = await goalCards.UpdateStatusAsync(goalCardId, newStatus);
            if (card is null)
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    Result = new Dictionary<string, object?> { ["error"] = $"Goal card '{goalCardId}' not found" }
                };
            }

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["goalCardId"] = card.Id,
                    ["status"] = card.Status.ToString(),
                    ["message"] = $"Goal card {card.Id} status updated to {card.Status}"
                }
            };
        }
        catch (InvalidOperationException ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                Result = new Dictionary<string, object?> { ["error"] = ex.Message }
            };
        }
    }

    private static string? GetStringArg(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value)) return null;
        if (value is string s) return s;
        if (value is JsonElement je && je.ValueKind == JsonValueKind.String) return je.GetString();
        return value?.ToString();
    }
}
