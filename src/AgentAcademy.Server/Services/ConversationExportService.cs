using System.Text;
using System.Text.Json;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Exports room and DM conversation history as JSON or Markdown.
/// </summary>
public sealed class ConversationExportService : IConversationExportService
{
    private const int MaxExportMessages = 10_000;

    private static readonly string[] HumanSideSenderIds = ["human", "consultant"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly AgentAcademyDbContext _db;

    public ConversationExportService(AgentAcademyDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Fetches all messages in a room (non-DM) up to <see cref="MaxExportMessages"/>.
    /// Returns null if the room doesn't exist.
    /// </summary>
    public async Task<(RoomEntity Room, List<MessageEntity> Messages, bool Truncated)?> GetRoomMessagesForExportAsync(
        string roomId, CancellationToken ct = default)
    {
        var room = await _db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roomId, ct);
        if (room is null) return null;

        var messages = await _db.Messages
            .AsNoTracking()
            .Where(m => m.RoomId == roomId && m.RecipientId == null)
            .OrderBy(m => m.SentAt)
            .ThenBy(m => m.Id)
            .Take(MaxExportMessages + 1)
            .ToListAsync(ct);

        var truncated = messages.Count > MaxExportMessages;
        if (truncated)
            messages = messages.Take(MaxExportMessages).ToList();

        return (room, messages, truncated);
    }

    /// <summary>
    /// Fetches all DM messages between the human and a specific agent up to <see cref="MaxExportMessages"/>.
    /// Returns null if no messages exist for the thread.
    /// </summary>
    public async Task<(string AgentId, List<MessageEntity> Messages, bool Truncated)?> GetDmMessagesForExportAsync(
        string agentId, CancellationToken ct = default)
    {
        var messages = await _db.Messages
            .AsNoTracking()
            .Where(m => m.RecipientId != null &&
                        ((HumanSideSenderIds.Contains(m.SenderId) && m.RecipientId == agentId) ||
                         (m.SenderId == agentId && HumanSideSenderIds.Contains(m.RecipientId!))))
            .OrderBy(m => m.SentAt)
            .ThenBy(m => m.Id)
            .Take(MaxExportMessages + 1)
            .ToListAsync(ct);

        if (messages.Count == 0) return null;

        var truncated = messages.Count > MaxExportMessages;
        if (truncated)
            messages = messages.Take(MaxExportMessages).ToList();

        return (agentId, messages, truncated);
    }

    /// <summary>
    /// Formats messages as a JSON array of message objects.
    /// </summary>
    public static string FormatAsJson(List<MessageEntity> messages, string? roomName = null, string? agentId = null)
    {
        var export = new
        {
            exportedAt = DateTime.UtcNow,
            roomName,
            agentId,
            messageCount = messages.Count,
            messages = messages.Select(m => new
            {
                id = m.Id,
                senderId = m.SenderId,
                senderName = m.SenderName,
                senderRole = m.SenderRole,
                senderKind = m.SenderKind,
                kind = m.Kind,
                content = m.Content,
                sentAt = m.SentAt,
                replyToMessageId = m.ReplyToMessageId,
            }),
        };

        return JsonSerializer.Serialize(export, JsonOptions);
    }

    /// <summary>
    /// Formats messages as human-readable Markdown.
    /// </summary>
    public static string FormatAsMarkdown(List<MessageEntity> messages, string? roomName = null, string? agentId = null)
    {
        var sb = new StringBuilder();

        if (roomName is not null)
            sb.AppendLine($"# Room: {roomName}");
        else if (agentId is not null)
            sb.AppendLine($"# DM Thread: {agentId}");
        else
            sb.AppendLine("# Conversation Export");

        sb.AppendLine();
        sb.AppendLine($"Exported at: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Messages: {messages.Count}");

        if (messages.Count > 0)
        {
            sb.AppendLine($"Date range: {messages[0].SentAt:yyyy-MM-dd HH:mm:ss} — {messages[^1].SentAt:yyyy-MM-dd HH:mm:ss} UTC");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var m in messages)
        {
            var role = m.SenderRole is not null ? $" ({m.SenderRole})" : "";
            sb.AppendLine($"**{m.SenderName}**{role} — {m.SentAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine(m.Content);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
