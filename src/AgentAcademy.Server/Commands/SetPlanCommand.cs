namespace AgentAcademy.Server.Commands;

/// <summary>
/// Structured payload for the SET_PLAN command.
/// </summary>
public sealed record SetPlanCommand(string Content)
{
    public static bool TryParse(
        IReadOnlyDictionary<string, object?> args,
        out SetPlanCommand? command,
        out string? error)
    {
        if (!args.TryGetValue("content", out var contentObj) ||
            contentObj is not string content ||
            string.IsNullOrWhiteSpace(content))
        {
            command = null;
            error = "Missing required arg: content. Usage: SET_PLAN:\n  Content: <markdown plan>";
            return false;
        }

        command = new SetPlanCommand(content.Trim());
        error = null;
        return true;
    }
}
