using AgentAcademy.Server.Data.Entities;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Abstraction for exporting room and DM conversation history.
/// Static formatting methods (FormatAsJson, FormatAsMarkdown) remain on the
/// concrete <see cref="ConversationExportService"/> class.
/// </summary>
public interface IConversationExportService
{
    /// <summary>
    /// Fetches all messages in a room (non-DM) for export.
    /// Returns null if the room doesn't exist.
    /// </summary>
    Task<(RoomEntity Room, List<MessageEntity> Messages, bool Truncated)?> GetRoomMessagesForExportAsync(
        string roomId, CancellationToken ct = default);

    /// <summary>
    /// Fetches all DM messages between the human and a specific agent for export.
    /// Returns null if no messages exist for the thread.
    /// </summary>
    Task<(string AgentId, List<MessageEntity> Messages, bool Truncated)?> GetDmMessagesForExportAsync(
        string agentId, CancellationToken ct = default);
}
