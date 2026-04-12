namespace AgentAcademy.Shared.Models;

/// <summary>
/// Results from a workspace-wide full-text search across messages and tasks.
/// </summary>
public sealed record SearchResults(
    IReadOnlyList<MessageSearchResult> Messages,
    IReadOnlyList<TaskSearchResult> Tasks,
    int TotalCount,
    string Query);

/// <summary>
/// A message that matched the search query.
/// </summary>
public sealed record MessageSearchResult(
    string MessageId,
    string RoomId,
    string RoomName,
    string SenderName,
    string SenderKind,
    string? SenderRole,
    string Snippet,
    DateTime SentAt,
    string? SessionId,
    string Source);

/// <summary>
/// A task that matched the search query.
/// </summary>
public sealed record TaskSearchResult(
    string TaskId,
    string Title,
    string Status,
    string? AssignedAgentName,
    string Snippet,
    DateTime CreatedAt,
    string? RoomId);
