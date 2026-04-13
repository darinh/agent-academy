using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Static helper for building Slack Block Kit payloads and formatting text.
/// Extracted from SlackNotificationProvider to keep messaging logic separate from provider plumbing.
/// </summary>
internal static class SlackMessageBuilder
{
    private static readonly Dictionary<string, string> AgentEmoji = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Planner"] = ":crystal_ball:",
        ["Architect"] = ":building_construction:",
        ["SoftwareEngineer"] = ":computer:",
        ["Reviewer"] = ":mag:",
        ["Validator"] = ":white_check_mark:",
        ["TechnicalWriter"] = ":pencil:",
        ["Human"] = ":bust_in_silhouette:"
    };

    /// <summary>
    /// Builds Slack Block Kit blocks for a notification message.
    /// </summary>
    public static object[] BuildMessageBlocks(NotificationMessage message)
    {
        var emoji = GetTypeEmoji(message.Type);

        var blocks = new List<object>
        {
            new
            {
                type = "section",
                text = new { type = "mrkdwn", text = $"{emoji} *{EscapeSlackText(message.Title)}*" }
            }
        };

        if (!string.IsNullOrEmpty(message.Body))
        {
            var body = message.Body;
            if (body.Length > 2900)
                body = body[..2900] + "…";

            blocks.Add(new
            {
                type = "section",
                text = new { type = "mrkdwn", text = EscapeSlackText(body) }
            });
        }

        var contextParts = new List<object>();
        if (!string.IsNullOrEmpty(message.AgentName))
            contextParts.Add(new { type = "mrkdwn", text = $"*Agent:* {EscapeSlackText(message.AgentName)}" });
        if (!string.IsNullOrEmpty(message.RoomId))
            contextParts.Add(new { type = "mrkdwn", text = $"*Room:* {EscapeSlackText(message.RoomId)}" });

        if (contextParts.Count > 0)
        {
            blocks.Add(new { type = "context", elements = contextParts.ToArray() });
        }

        return blocks.ToArray();
    }

    /// <summary>
    /// Builds Block Kit blocks for an agent question (threaded message style).
    /// </summary>
    public static object[] BuildQuestionBlocks(string agentName, string question, string roomName)
    {
        return
        [
            new { type = "header", text = new { type = "plain_text", text = $"❓ {agentName} asks:", emoji = true } },
            new { type = "section", text = new { type = "mrkdwn", text = EscapeSlackText(question) } },
            new { type = "context", elements = new object[]
            {
                new { type = "mrkdwn", text = $"*Room:* {EscapeSlackText(roomName)} · *Agent:* {EscapeSlackText(agentName)}" }
            }},
            new { type = "divider" }
        ];
    }

    public static string GetTypeEmoji(NotificationType type) => type switch
    {
        NotificationType.Error => "🔴",
        NotificationType.TaskFailed => "❌",
        NotificationType.TaskComplete => "✅",
        NotificationType.NeedsInput => "💬",
        NotificationType.SpecReview => "📋",
        NotificationType.AgentThinking => "🤔",
        _ => "ℹ️"
    };

    public static string? GetAgentEmoji(string? agentName)
    {
        if (agentName is null) return null;

        foreach (var (role, emoji) in AgentEmoji)
        {
            if (agentName.Contains(role, StringComparison.OrdinalIgnoreCase))
                return emoji;
        }

        return ":robot_face:";
    }

    /// <summary>
    /// Escapes special Slack mrkdwn characters to prevent unintended formatting.
    /// </summary>
    public static string EscapeSlackText(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}
