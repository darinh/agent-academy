using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Handles all message operations: room messages (agent, human, system),
/// direct messages, breakout room messages, and message trimming.
/// </summary>
public interface IMessageService
{
    // ── Room Messages ───────────────────────────────────────────

    /// <summary>
    /// Posts an agent message to a room.
    /// </summary>
    Task<ChatEnvelope> PostMessageAsync(PostMessageRequest request);

    /// <summary>
    /// Posts a human message to a room.
    /// When identity parameters are provided, the message is attributed to that user.
    /// Otherwise falls back to generic "Human" identity.
    /// </summary>
    Task<ChatEnvelope> PostHumanMessageAsync(
        string roomId, string content,
        string? userId = null, string? userName = null,
        string? userRole = null);

    /// <summary>
    /// Posts a system message to a room (e.g. "Agent X joined the room.").
    /// </summary>
    Task PostSystemMessageAsync(string roomId, string content);

    /// <summary>
    /// Posts a system status message to a room (no agent sender required).
    /// </summary>
    Task PostSystemStatusAsync(string roomId, string message);

    // ── Direct Messaging ────────────────────────────────────────

    /// <summary>
    /// Stores a direct message and posts a system notification in the recipient's room.
    /// </summary>
    Task<string> SendDirectMessageAsync(
        string senderId, string senderName, string senderRole,
        string recipientId, string message, string currentRoomId);

    /// <summary>
    /// Returns recent DMs for an agent (both sent and received), ordered chronologically.
    /// When unreadOnly is true (default), only returns DMs where the agent is the
    /// recipient and hasn't acknowledged them yet.
    /// </summary>
    Task<List<MessageEntity>> GetDirectMessagesForAgentAsync(
        string agentId, int limit = 20, bool unreadOnly = true);

    /// <summary>
    /// Marks specific DMs as acknowledged by their IDs.
    /// Call this after building a prompt that includes DMs, passing only the
    /// message IDs that were actually included in the prompt.
    /// </summary>
    Task AcknowledgeDirectMessagesAsync(string agentId, IReadOnlyList<string> messageIds);

    /// <summary>
    /// Returns DM thread summaries for the human user, grouped by agent.
    /// </summary>
    Task<List<DmThreadSummary>> GetDmThreadsForHumanAsync();

    /// <summary>
    /// Returns messages in a DM thread between the human and a specific agent.
    /// </summary>
    Task<List<MessageEntity>> GetDmThreadMessagesAsync(
        string agentId, int limit = 50, string? afterMessageId = null);

    // ── Breakout Room Messages ──────────────────────────────────

    /// <summary>
    /// Adds a message to a breakout room's message log.
    /// </summary>
    Task PostBreakoutMessageAsync(
        string breakoutRoomId, string senderId, string senderName,
        string senderRole, string content);

    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Creates a system message entity (used by PostSystemStatusAsync and
    /// other code that needs to insert system messages directly).
    /// </summary>
    MessageEntity CreateMessageEntity(
        string roomId, MessageKind kind, string content,
        string? correlationId, DateTime sentAt);

    /// <summary>
    /// Trims room messages to the most recent limit.
    /// </summary>
    Task TrimMessagesAsync(string roomId);
}
