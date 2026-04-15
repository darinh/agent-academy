using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Handles all message operations: room messages (agent, human, system),
/// direct messages, breakout room messages, and message trimming.
/// </summary>
public sealed class MessageService : IMessageService
{
    private const int MaxRecentMessages = 200;

    /// <summary>
    /// Sender IDs that represent the human side of DM conversations.
    /// Both the direct human user and the consultant API share the same DM inbox.
    /// </summary>
    private static readonly string[] HumanSideSenderIds = ["human", "consultant"];

    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<MessageService> _logger;
    private readonly IAgentCatalog _catalog;
    private readonly IActivityPublisher _activity;
    private readonly ConversationSessionService _sessionService;
    private readonly MessageBroadcaster _messageBroadcaster;

    public MessageService(
        AgentAcademyDbContext db,
        ILogger<MessageService> logger,
        IAgentCatalog catalog,
        IActivityPublisher activity,
        ConversationSessionService sessionService,
        MessageBroadcaster messageBroadcaster)
    {
        _db = db;
        _logger = logger;
        _catalog = catalog;
        _activity = activity;
        _sessionService = sessionService;
        _messageBroadcaster = messageBroadcaster;
    }

    // ── Room Messages ───────────────────────────────────────────

    /// <summary>
    /// Posts an agent message to a room.
    /// </summary>
    public async Task<ChatEnvelope> PostMessageAsync(PostMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RoomId))
            throw new ArgumentException("RoomId is required", nameof(request));
        if (string.IsNullOrWhiteSpace(request.SenderId))
            throw new ArgumentException("SenderId is required", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Content))
            throw new ArgumentException("Content is required", nameof(request));

        var room = await _db.Rooms.FindAsync(request.RoomId)
            ?? throw new InvalidOperationException($"Room '{request.RoomId}' not found");

        var agent = _catalog.Agents.FirstOrDefault(a => a.Id == request.SenderId)
            ?? throw new InvalidOperationException($"Agent '{request.SenderId}' not found in catalog");

        var now = DateTime.UtcNow;
        var envelope = new ChatEnvelope(
            Id: Guid.NewGuid().ToString("N"),
            RoomId: request.RoomId,
            SenderId: agent.Id,
            SenderName: agent.Name,
            SenderRole: agent.Role,
            SenderKind: MessageSenderKind.Agent,
            Kind: request.Kind,
            Content: request.Content,
            SentAt: now,
            CorrelationId: request.CorrelationId,
            Hint: request.Hint
        );

        var msgEntity = new MessageEntity
        {
            Id = envelope.Id,
            RoomId = envelope.RoomId,
            SenderId = envelope.SenderId,
            SenderName = envelope.SenderName,
            SenderRole = envelope.SenderRole,
            SenderKind = envelope.SenderKind.ToString(),
            Kind = envelope.Kind.ToString(),
            Content = envelope.Content,
            SentAt = envelope.SentAt,
            CorrelationId = envelope.CorrelationId
        };

        var session = await _sessionService.GetOrCreateActiveSessionAsync(request.RoomId);
        msgEntity.SessionId = session.Id;

        _db.Messages.Add(msgEntity);
        await _sessionService.IncrementMessageCountAsync(session.Id);

        await TrimMessagesAsync(request.RoomId);

        room.UpdatedAt = now;

        Publish(ActivityEventType.MessagePosted, request.RoomId, agent.Id, null,
            $"{agent.Name}: {request.Content}");

        await _db.SaveChangesAsync();

        _messageBroadcaster.Broadcast(request.RoomId, envelope);

        return envelope;
    }
    /// When identity parameters are provided, the message is attributed to that user.
    /// Otherwise falls back to generic "Human" identity.
    /// </summary>
    public async Task<ChatEnvelope> PostHumanMessageAsync(
        string roomId, string content,
        string? userId = null, string? userName = null,
        string? userRole = null)
    {
        if (string.IsNullOrWhiteSpace(roomId))
            throw new ArgumentException("roomId is required", nameof(roomId));
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("content is required", nameof(content));

        var room = await _db.Rooms.FindAsync(roomId)
            ?? throw new InvalidOperationException($"Room '{roomId}' not found");

        var senderId = userId ?? "human";
        var senderName = userName ?? "Human";
        var senderRole = userRole ?? "Human";

        var now = DateTime.UtcNow;
        var envelope = new ChatEnvelope(
            Id: Guid.NewGuid().ToString("N"),
            RoomId: roomId,
            SenderId: senderId,
            SenderName: senderName,
            SenderRole: senderRole,
            SenderKind: MessageSenderKind.User,
            Kind: MessageKind.Response,
            Content: content,
            SentAt: now
        );

        var msgEntity = new MessageEntity
        {
            Id = envelope.Id,
            RoomId = roomId,
            SenderId = senderId,
            SenderName = senderName,
            SenderRole = senderRole,
            SenderKind = nameof(MessageSenderKind.User),
            Kind = nameof(MessageKind.Response),
            Content = content,
            SentAt = now
        };

        var session = await _sessionService.GetOrCreateActiveSessionAsync(roomId);
        msgEntity.SessionId = session.Id;

        _db.Messages.Add(msgEntity);
        await _sessionService.IncrementMessageCountAsync(session.Id);

        await TrimMessagesAsync(roomId);

        room.UpdatedAt = now;

        Publish(ActivityEventType.MessagePosted, roomId, "human", null,
            $"{senderName}: {content}");

        await _db.SaveChangesAsync();

        _messageBroadcaster.Broadcast(roomId, envelope);

        return envelope;
    }

    /// <summary>
    /// Posts a system message to a room (e.g. "Agent X joined the room.").
    /// </summary>
    public async Task PostSystemMessageAsync(string roomId, string content)
    {
        var room = await _db.Rooms.FindAsync(roomId)
            ?? throw new InvalidOperationException($"Room '{roomId}' not found");

        var now = DateTime.UtcNow;
        var session = await _sessionService.GetOrCreateActiveSessionAsync(roomId);

        var msgEntity = new MessageEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            RoomId = roomId,
            SenderId = "system",
            SenderName = "System",
            SenderKind = nameof(MessageSenderKind.System),
            Kind = nameof(MessageKind.System),
            Content = content,
            SentAt = now,
            SessionId = session.Id
        };
        _db.Messages.Add(msgEntity);
        await _sessionService.IncrementMessageCountAsync(session.Id);

        room.UpdatedAt = now;
        await _db.SaveChangesAsync();

        _messageBroadcaster.Broadcast(roomId, new ChatEnvelope(
            msgEntity.Id, roomId, "system", "System", null,
            MessageSenderKind.System, MessageKind.System, content, now));
    }

    /// <summary>
    /// Posts a system status message to a room (no agent sender required).
    /// </summary>
    public async Task PostSystemStatusAsync(string roomId, string message)
    {
        var room = await _db.Rooms.FindAsync(roomId)
            ?? throw new InvalidOperationException($"Room '{roomId}' not found");

        var now = DateTime.UtcNow;
        var entity = CreateMessageEntity(roomId, MessageKind.System, message, null, now);
        _db.Messages.Add(entity);
        room.UpdatedAt = now;

        Publish(ActivityEventType.MessagePosted, roomId, null, null,
            $"System: {Truncate(message, 100)}");

        await _db.SaveChangesAsync();

        _messageBroadcaster.Broadcast(roomId, new ChatEnvelope(
            entity.Id, roomId, "system", "System", null,
            MessageSenderKind.System, MessageKind.System, message, now));
    }

    // ── Direct Messaging ────────────────────────────────────────

    /// <summary>
    /// Stores a direct message and posts a system notification in the recipient's room.
    /// </summary>
    public async Task<string> SendDirectMessageAsync(
        string senderId, string senderName, string senderRole,
        string recipientId, string message, string currentRoomId)
    {
        var now = DateTime.UtcNow;
        var messageId = Guid.NewGuid().ToString("N");

        var msgEntity = new MessageEntity
        {
            Id = messageId,
            RoomId = currentRoomId,
            SenderId = senderId,
            SenderName = senderName,
            SenderRole = senderRole,
            SenderKind = senderId == "human" ? nameof(MessageSenderKind.User) : nameof(MessageSenderKind.Agent),
            Kind = nameof(MessageKind.DirectMessage),
            Content = message,
            SentAt = now,
            RecipientId = recipientId
        };
        _db.Messages.Add(msgEntity);

        string? dmNotifyRoomId = null;
        string? dmNotifyMsgId = null;
        string? dmNotifyContent = null;

        if (recipientId != "human")
        {
            var recipientLocation = await _db.AgentLocations.FindAsync(recipientId);
            var notifyRoomId = recipientLocation?.RoomId ?? currentRoomId;
            var notifyRoom = await _db.Rooms.FindAsync(notifyRoomId);
            if (notifyRoom is not null)
            {
                var sysMsgContent = $"📩 {senderName} sent a direct message to {recipientId}.";
                var sysMsg = new MessageEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    RoomId = notifyRoomId,
                    SenderId = "system",
                    SenderName = "System",
                    SenderKind = nameof(MessageSenderKind.System),
                    Kind = nameof(MessageKind.System),
                    Content = sysMsgContent,
                    SentAt = now
                };
                _db.Messages.Add(sysMsg);
                notifyRoom.UpdatedAt = now;

                dmNotifyRoomId = notifyRoomId;
                dmNotifyMsgId = sysMsg.Id;
                dmNotifyContent = sysMsgContent;
            }
        }

        Publish(ActivityEventType.DirectMessageSent, currentRoomId, senderId, null,
            $"DM from {senderName} to {recipientId}");

        await _db.SaveChangesAsync();

        // Broadcast room-visible DM notification AFTER commit to avoid ghost messages
        if (dmNotifyRoomId is not null)
        {
            _messageBroadcaster.Broadcast(dmNotifyRoomId, new ChatEnvelope(
                dmNotifyMsgId!, dmNotifyRoomId, "system", "System", null,
                MessageSenderKind.System, MessageKind.System, dmNotifyContent!, now));
        }

        // Broadcast DM to SSE subscribers on the agent's DM thread
        var isHumanSide = HumanSideSenderIds.Contains(senderId);
        var dmAgentId = isHumanSide ? recipientId : senderId;
        _messageBroadcaster.BroadcastDm(dmAgentId, new DmMessage(
            Id: messageId,
            SenderId: senderId,
            SenderName: senderName,
            SenderRole: senderRole,
            Content: message,
            SentAt: now,
            IsFromHuman: isHumanSide
        ));

        return messageId;
    }

    /// <summary>
    /// Returns recent DMs for an agent (both sent and received), ordered chronologically.
    /// When unreadOnly is true (default), only returns DMs where the agent is the
    /// recipient and hasn't acknowledged them yet.
    /// </summary>
    public async Task<List<MessageEntity>> GetDirectMessagesForAgentAsync(
        string agentId, int limit = 20, bool unreadOnly = true)
    {
        IQueryable<MessageEntity> query;

        if (unreadOnly)
        {
            query = _db.Messages
                .Where(m => m.RecipientId == agentId && m.AcknowledgedAt == null);
        }
        else
        {
            query = _db.Messages
                .Where(m => m.RecipientId != null &&
                            (m.RecipientId == agentId || m.SenderId == agentId));
        }

        return await query
            .OrderByDescending(m => m.SentAt)
            .Take(limit)
            .OrderBy(m => m.SentAt)
            .ToListAsync();
    }

    /// <summary>
    /// Marks specific DMs as acknowledged by their IDs.
    /// Call this after building a prompt that includes DMs, passing only the
    /// message IDs that were actually included in the prompt.
    /// </summary>
    public async Task AcknowledgeDirectMessagesAsync(string agentId, IReadOnlyList<string> messageIds)
    {
        if (messageIds.Count == 0) return;

        var now = DateTime.UtcNow;
        var messages = await _db.Messages
            .Where(m => messageIds.Contains(m.Id) &&
                        m.RecipientId == agentId &&
                        m.AcknowledgedAt == null)
            .ToListAsync();

        foreach (var dm in messages)
            dm.AcknowledgedAt = now;

        if (messages.Count > 0)
            await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Returns DM thread summaries for the human user, grouped by agent.
    /// </summary>
    public async Task<List<DmThreadSummary>> GetDmThreadsForHumanAsync()
    {
        var humanDms = await _db.Messages
            .Where(m => m.RecipientId != null &&
                        (HumanSideSenderIds.Contains(m.RecipientId) ||
                         HumanSideSenderIds.Contains(m.SenderId)))
            .OrderByDescending(m => m.SentAt)
            .Take(500)
            .ToListAsync();

        var threads = new Dictionary<string, DmThreadSummary>(StringComparer.OrdinalIgnoreCase);

        foreach (var dm in humanDms)
        {
            var agentId = HumanSideSenderIds.Contains(dm.SenderId) ? dm.RecipientId! : dm.SenderId;

            if (!threads.ContainsKey(agentId))
            {
                var agent = _catalog.Agents.FirstOrDefault(
                    a => string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase));
                var agentName = agent?.Name ?? agentId;
                var agentRole = agent?.Role ?? "Agent";

                threads[agentId] = new DmThreadSummary(
                    AgentId: agentId,
                    AgentName: agentName,
                    AgentRole: agentRole,
                    LastMessage: dm.Content.Length > 100 ? dm.Content[..100] + "…" : dm.Content,
                    LastMessageAt: dm.SentAt,
                    MessageCount: 0
                );
            }

            threads[agentId] = threads[agentId] with
            {
                MessageCount = threads[agentId].MessageCount + 1
            };
        }

        return threads.Values
            .OrderByDescending(t => t.LastMessageAt)
            .ToList();
    }

    /// <summary>
    /// Returns messages in a DM thread between the human and a specific agent.
    /// </summary>
    public async Task<List<MessageEntity>> GetDmThreadMessagesAsync(string agentId, int limit = 50, string? afterMessageId = null)
    {
        limit = Math.Clamp(limit, 1, 200);

        IQueryable<MessageEntity> query = _db.Messages
            .Where(m => m.RecipientId != null &&
                        ((HumanSideSenderIds.Contains(m.SenderId) && m.RecipientId == agentId) ||
                         (m.SenderId == agentId && HumanSideSenderIds.Contains(m.RecipientId!))));

        if (!string.IsNullOrEmpty(afterMessageId))
        {
            var cursor = await _db.Messages
                .Where(m => m.Id == afterMessageId)
                .Select(m => new { m.SentAt, m.Id })
                .FirstOrDefaultAsync();

            if (cursor is not null)
            {
                query = query.Where(m =>
                    m.SentAt > cursor.SentAt ||
                    (m.SentAt == cursor.SentAt && string.Compare(m.Id, cursor.Id) > 0));
            }
        }

        return await query
            .OrderBy(m => m.SentAt)
            .ThenBy(m => m.Id)
            .Take(limit)
            .ToListAsync();
    }

    // ── Breakout Room Messages ──────────────────────────────────

    /// <summary>
    /// Adds a message to a breakout room's message log.
    /// </summary>
    public async Task PostBreakoutMessageAsync(
        string breakoutRoomId, string senderId, string senderName,
        string senderRole, string content)
    {
        var br = await _db.BreakoutRooms.FindAsync(breakoutRoomId)
            ?? throw new InvalidOperationException($"Breakout room '{breakoutRoomId}' not found");

        if (br.Status != nameof(RoomStatus.Active))
            throw new InvalidOperationException($"Breakout room '{breakoutRoomId}' is archived");

        var now = DateTime.UtcNow;
        var entity = new BreakoutMessageEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            BreakoutRoomId = breakoutRoomId,
            SenderId = senderId,
            SenderName = senderName,
            SenderRole = senderRole,
            SenderKind = senderId == "system"
                ? nameof(MessageSenderKind.System)
                : nameof(MessageSenderKind.Agent),
            Kind = senderId == "system"
                ? nameof(MessageKind.System)
                : nameof(MessageKind.Response),
            Content = content,
            SentAt = now
        };

        var session = await _sessionService.GetOrCreateActiveSessionAsync(breakoutRoomId, "Breakout");
        entity.SessionId = session.Id;

        _db.BreakoutMessages.Add(entity);
        await _sessionService.IncrementMessageCountAsync(session.Id);

        br.UpdatedAt = now;
        await _db.SaveChangesAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Creates a system message entity (used by PostSystemStatusAsync and
    /// other code that needs to insert system messages directly).
    /// </summary>
    public MessageEntity CreateMessageEntity(
        string roomId, MessageKind kind, string content,
        string? correlationId, DateTime sentAt)
    {
        return new MessageEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            RoomId = roomId,
            SenderId = "system",
            SenderName = "System",
            SenderKind = nameof(MessageSenderKind.System),
            Kind = kind.ToString(),
            Content = content,
            SentAt = sentAt,
            CorrelationId = correlationId
        };
    }

    /// <summary>
    /// Trims room messages to the most recent <see cref="MaxRecentMessages"/>.
    /// </summary>
    public async Task TrimMessagesAsync(string roomId)
    {
        var messageCount = await _db.Messages.CountAsync(m => m.RoomId == roomId && m.RecipientId == null);
        var totalAfterSave = messageCount + 1;

        if (totalAfterSave <= MaxRecentMessages) return;

        var toRemove = await _db.Messages
            .Where(m => m.RoomId == roomId && m.RecipientId == null)
            .OrderBy(m => m.SentAt)
            .Take(totalAfterSave - MaxRecentMessages)
            .ToListAsync();

        _db.Messages.RemoveRange(toRemove);
    }

    private ActivityEvent Publish(
        ActivityEventType type,
        string? roomId,
        string? actorId,
        string? taskId,
        string message,
        string? correlationId = null,
        ActivitySeverity severity = ActivitySeverity.Info)
        => _activity.Publish(type, roomId, actorId, taskId, message, correlationId, severity);

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
