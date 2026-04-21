using System.Text.Json;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles CREATE_GOAL_CARD — agents create a structured intent artifact
/// before starting significant work. The card captures task vs. intent
/// analysis for drift detection.
/// </summary>
public sealed class CreateGoalCardHandler : ICommandHandler
{
    public string CommandName => "CREATE_GOAL_CARD";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var goalCards = context.Services.GetRequiredService<IGoalCardService>();

        if (context.RoomId is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                Result = new Dictionary<string, object?> { ["error"] = "CREATE_GOAL_CARD requires a room context" }
            };
        }

        // Parse the goal card fields from command args
        string? taskDescription = GetStringArg(command.Args, "task_description");
        string? intent = GetStringArg(command.Args, "intent");
        string? divergence = GetStringArg(command.Args, "divergence");
        string? steelman = GetStringArg(command.Args, "steelman");
        string? strawman = GetStringArg(command.Args, "strawman");
        string? verdictStr = GetStringArg(command.Args, "verdict");
        string? freshEyes1 = GetStringArg(command.Args, "fresh_eyes_1");
        string? freshEyes2 = GetStringArg(command.Args, "fresh_eyes_2");
        string? freshEyes3 = GetStringArg(command.Args, "fresh_eyes_3");
        string? taskId = GetStringArg(command.Args, "task_id");

        // Validate required fields
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(taskDescription)) missing.Add("task_description");
        if (string.IsNullOrWhiteSpace(intent)) missing.Add("intent");
        if (string.IsNullOrWhiteSpace(divergence)) missing.Add("divergence");
        if (string.IsNullOrWhiteSpace(steelman)) missing.Add("steelman");
        if (string.IsNullOrWhiteSpace(strawman)) missing.Add("strawman");
        if (string.IsNullOrWhiteSpace(verdictStr)) missing.Add("verdict");
        if (string.IsNullOrWhiteSpace(freshEyes1)) missing.Add("fresh_eyes_1");
        if (string.IsNullOrWhiteSpace(freshEyes2)) missing.Add("fresh_eyes_2");
        if (string.IsNullOrWhiteSpace(freshEyes3)) missing.Add("fresh_eyes_3");

        if (missing.Count > 0)
        {
            return command with
            {
                Status = CommandStatus.Error,
                Result = new Dictionary<string, object?>
                {
                    ["error"] = $"Missing required fields: {string.Join(", ", missing)}",
                    ["required_fields"] = new[] { "task_description", "intent", "divergence", "steelman", "strawman", "verdict", "fresh_eyes_1", "fresh_eyes_2", "fresh_eyes_3" },
                    ["optional_fields"] = new[] { "task_id" }
                }
            };
        }

        if (!Enum.TryParse<GoalCardVerdict>(verdictStr, ignoreCase: true, out var verdict))
        {
            return command with
            {
                Status = CommandStatus.Error,
                Result = new Dictionary<string, object?>
                {
                    ["error"] = $"Invalid verdict '{verdictStr}'. Must be one of: Proceed, ProceedWithCaveat, Challenge"
                }
            };
        }

        var request = new CreateGoalCardRequest(
            TaskDescription: taskDescription!,
            Intent: intent!,
            Divergence: divergence!,
            Steelman: steelman!,
            Strawman: strawman!,
            Verdict: verdict,
            FreshEyes1: freshEyes1!,
            FreshEyes2: freshEyes2!,
            FreshEyes3: freshEyes3!,
            TaskId: taskId
        );

        GoalCard card;
        try
        {
            card = await goalCards.CreateAsync(
                context.AgentId,
                context.AgentName,
                context.RoomId,
                request);
        }
        catch (ArgumentException ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                Result = new Dictionary<string, object?> { ["error"] = ex.Message }
            };
        }

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["goalCardId"] = card.Id,
                ["verdict"] = card.Verdict.ToString(),
                ["status"] = card.Status.ToString(),
                ["message"] = card.Verdict == GoalCardVerdict.Challenge
                    ? $"⚠️ Goal card {card.Id} CHALLENGED — work should stop until the challenge is resolved."
                    : $"✅ Goal card {card.Id} created: {card.Verdict}. Proceeding."
            }
        };
    }

    private static string? GetStringArg(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value)) return null;
        if (value is string s) return s;
        if (value is JsonElement je && je.ValueKind == JsonValueKind.String) return je.GetString();
        return value?.ToString();
    }
}
