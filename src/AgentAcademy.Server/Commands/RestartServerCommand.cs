namespace AgentAcademy.Server.Commands;

/// <summary>
/// Structured payload for the RESTART_SERVER command.
/// </summary>
public sealed record RestartServerCommand(string Reason)
{
    public static bool TryParse(
        IReadOnlyDictionary<string, object?> args,
        out RestartServerCommand? command,
        out string? error)
    {
        if (!args.TryGetValue("reason", out var reasonObj) ||
            reasonObj is not string reason ||
            string.IsNullOrWhiteSpace(reason))
        {
            command = null;
            error = "Missing required arg: reason. Usage: RESTART_SERVER:\n  Reason: <why the restart is needed>";
            return false;
        }

        command = new RestartServerCommand(reason.Trim());
        error = null;
        return true;
    }
}
