using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Manages conversation session (epoch) lifecycle within rooms.
/// When a session's message count exceeds a configurable threshold,
/// the conversation is summarized via LLM and a new session begins.
/// </summary>
public interface IConversationSessionService
{
    /// <summary>
    /// Returns the active session for a room, creating one if none exists.
    /// </summary>
    Task<ConversationSessionEntity> GetOrCreateActiveSessionAsync(
        string roomId, string roomType = "Main");

    /// <summary>
    /// Increments the message count for a session.
    /// Called by MessageService when a message is posted.
    /// </summary>
    Task IncrementMessageCountAsync(string sessionId);

    /// <summary>
    /// Checks if the active session exceeds the configured threshold
    /// and triggers rotation if needed. Returns true if rotation occurred.
    /// </summary>
    Task<bool> CheckAndRotateAsync(string roomId, string roomType = "Main");

    /// <summary>
    /// Creates a new conversation session for a room, archiving the current active session.
    /// Returns a snapshot of the newly created session.
    /// </summary>
    Task<ConversationSessionSnapshot> CreateNewSessionAsync(string roomId, string roomType = "Main");

    /// <summary>
    /// Creates a new conversation session tagged with a sprint ID and stage.
    /// Archives the current active session for the room (if any) before
    /// creating the new one.
    /// </summary>
    Task<ConversationSessionEntity> CreateSessionForStageAsync(
        string roomId, string sprintId, string stage, string roomType = "Main");

    /// <summary>
    /// Archives all active conversation sessions with LLM summaries.
    /// Returns the number of sessions archived.
    /// </summary>
    Task<int> ArchiveAllActiveSessionsAsync();
}
