using System.Text.Json.Serialization;

namespace AgentAcademy.Shared.Models;

/// <summary>
/// Categorizes notification messages for display and routing to providers.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NotificationType
{
    AgentThinking,
    NeedsInput,
    TaskComplete,
    TaskFailed,
    SpecReview,
    Error
}

/// <summary>
/// A notification to be delivered via one or more notification providers.
/// Contains the message content and optional context for routing.
/// </summary>
public record NotificationMessage(
    NotificationType Type,
    string Title,
    string Body,
    string? RoomId = null,
    string? AgentName = null,
    Dictionary<string, string>? Actions = null
);

/// <summary>
/// A request for user input, optionally scoped to a room and agent.
/// Supports both free-form text and selection from predefined choices.
/// </summary>
public record InputRequest(
    string Prompt,
    string? RoomId = null,
    string? AgentName = null,
    List<string>? Choices = null,
    bool AllowFreeform = true
);

/// <summary>
/// The user's response to an <see cref="InputRequest"/>.
/// </summary>
public record UserResponse(
    string Content,
    string? SelectedChoice = null,
    string ProviderId = ""
);

/// <summary>
/// Schema describing the configuration fields required by a notification provider.
/// Used to dynamically render provider setup UI.
/// </summary>
public record ProviderConfigSchema(
    string ProviderId,
    string DisplayName,
    string Description,
    List<ConfigField> Fields
);

/// <summary>
/// A single configuration field within a <see cref="ProviderConfigSchema"/>.
/// </summary>
public record ConfigField(
    string Key,
    string Label,
    string Type,
    bool Required,
    string? Description = null,
    string? Placeholder = null
);
