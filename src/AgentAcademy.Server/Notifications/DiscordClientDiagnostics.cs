using Discord;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Resolves user-facing explanations for Discord client disconnect exceptions.
/// Walks the inner exception chain looking for known Discord gateway/auth error
/// signatures (close codes 4014 / 4004, HTTP 401) and returns an actionable
/// remediation string. Returns <see langword="null"/> when no known signature
/// is found.
/// </summary>
internal static class DiscordDisconnectReasonResolver
{
    public static string? Resolve(Exception? ex)
    {
        var current = ex;
        while (current is not null)
        {
            var msg = current.Message;
            if (msg.Contains("4014", StringComparison.Ordinal) || msg.Contains("Disallowed intent", StringComparison.OrdinalIgnoreCase))
            {
                return "Discord rejected the connection: privileged Message Content Intent is not enabled. "
                     + "Go to https://discord.com/developers/applications → your bot → Bot → Privileged Gateway Intents → enable MESSAGE CONTENT INTENT, then reconnect.";
            }
            if (msg.Contains("4004", StringComparison.Ordinal) || msg.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase))
            {
                return "Discord rejected the bot token — it may be invalid or revoked. Regenerate the token in the Discord Developer Portal and reconfigure.";
            }
            if (msg.Contains("401", StringComparison.Ordinal) && msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                return "Discord returned 401 Unauthorized — the bot token is invalid, expired, or was never a bot token. "
                     + "Go to https://discord.com/developers/applications → your bot → Bot → Reset Token, then reconfigure with the new token.";
            }
            current = current.InnerException;
        }
        return null;
    }
}

/// <summary>
/// Maps Discord.NET <see cref="LogSeverity"/> values to ASP.NET Core
/// <see cref="Microsoft.Extensions.Logging.LogLevel"/> equivalents so Discord
/// client log events can be funneled through the standard logger pipeline.
/// </summary>
internal static class DiscordLogSeverityMapper
{
    public static LogLevel ToLogLevel(LogSeverity severity) => severity switch
    {
        LogSeverity.Critical => LogLevel.Critical,
        LogSeverity.Error => LogLevel.Error,
        LogSeverity.Warning => LogLevel.Warning,
        LogSeverity.Info => LogLevel.Information,
        LogSeverity.Verbose => LogLevel.Debug,
        LogSeverity.Debug => LogLevel.Trace,
        _ => LogLevel.Information
    };
}
