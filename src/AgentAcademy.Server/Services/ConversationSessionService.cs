using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Manages conversation session (epoch) lifecycle within rooms.
/// When a session's message count exceeds a configurable threshold,
/// the conversation is summarized via LLM and a new session begins.
/// SDK sessions are invalidated at rotation boundaries to reset
/// accumulated context.
/// </summary>
public sealed class ConversationSessionService
{
    private readonly AgentAcademyDbContext _db;
    private readonly SystemSettingsService _settings;
    private readonly IAgentExecutor _executor;
    private readonly ILogger<ConversationSessionService> _logger;

    /// <summary>
    /// System agent identity used for summarization calls.
    /// Does not correspond to a real catalog agent — uses roomId=null
    /// to avoid polluting real agent sessions.
    /// </summary>
    private static readonly AgentDefinition SummarizerAgent = new(
        Id: "system-summarizer",
        Name: "Summarizer",
        Role: "System",
        Summary: "Internal agent for conversation summarization",
        StartupPrompt: "You are a concise summarizer. You produce structured summaries of conversations.",
        Model: null,
        CapabilityTags: [],
        EnabledTools: [],
        AutoJoinDefaultRoom: false
    );

    public ConversationSessionService(
        AgentAcademyDbContext db,
        SystemSettingsService settings,
        IAgentExecutor executor,
        ILogger<ConversationSessionService> logger)
    {
        _db = db;
        _settings = settings;
        _executor = executor;
        _logger = logger;
    }

    /// <summary>
    /// Returns the active session for a room, creating one if none exists.
    /// </summary>
    public async Task<ConversationSessionEntity> GetOrCreateActiveSessionAsync(
        string roomId, string roomType = "Main")
    {
        var session = await _db.ConversationSessions
            .Where(s => s.RoomId == roomId && s.Status == "Active")
            .FirstOrDefaultAsync();

        if (session is not null) return session;

        session = new ConversationSessionEntity
        {
            RoomId = roomId,
            RoomType = roomType,
            SequenceNumber = await GetNextSequenceNumberAsync(roomId),
            Status = "Active",
            MessageCount = 0,
            CreatedAt = DateTime.UtcNow,
        };

        _db.ConversationSessions.Add(session);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Created conversation session {SessionId} (seq #{Seq}) for {RoomType} room {RoomId}",
            session.Id, session.SequenceNumber, roomType, roomId);

        return session;
    }

    /// <summary>
    /// Increments the message count for a session.
    /// Called by WorkspaceRuntime when a message is posted.
    /// </summary>
    public async Task IncrementMessageCountAsync(string sessionId)
    {
        var session = await _db.ConversationSessions.FindAsync(sessionId);
        if (session is not null)
        {
            session.MessageCount++;
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Checks if the active session exceeds the configured threshold
    /// and triggers rotation if needed. Returns true if rotation occurred.
    /// </summary>
    public async Task<bool> CheckAndRotateAsync(string roomId, string roomType = "Main")
    {
        var session = await _db.ConversationSessions
            .Where(s => s.RoomId == roomId && s.Status == "Active")
            .FirstOrDefaultAsync();

        if (session is null) return false;

        var threshold = roomType == "Breakout"
            ? await _settings.GetBreakoutEpochSizeAsync()
            : await _settings.GetMainRoomEpochSizeAsync();

        if (session.MessageCount < threshold) return false;

        _logger.LogInformation(
            "Session {SessionId} has {Count}/{Threshold} messages — rotating",
            session.Id, session.MessageCount, threshold);

        await RotateSessionAsync(session, roomId, roomType);
        return true;
    }

    /// <summary>
    /// Returns the summary from the most recently archived session for a room,
    /// or null if no prior session exists.
    /// </summary>
    public async Task<string?> GetSessionContextAsync(string roomId)
    {
        return await _db.ConversationSessions
            .Where(s => s.RoomId == roomId && s.Status == "Archived" && s.Summary != null)
            .OrderByDescending(s => s.SequenceNumber)
            .Select(s => s.Summary)
            .FirstOrDefaultAsync();
    }

    private async Task RotateSessionAsync(
        ConversationSessionEntity currentSession, string roomId, string roomType)
    {
        // Step 1: Generate LLM summary of the current session
        var summary = await GenerateSummaryAsync(currentSession, roomType);

        // Step 2: Archive the current session
        currentSession.Status = "Archived";
        currentSession.Summary = summary;
        currentSession.ArchivedAt = DateTime.UtcNow;

        // Step 3: Create new active session
        var newSession = new ConversationSessionEntity
        {
            RoomId = roomId,
            RoomType = roomType,
            SequenceNumber = currentSession.SequenceNumber + 1,
            Status = "Active",
            MessageCount = 0,
            CreatedAt = DateTime.UtcNow,
        };
        _db.ConversationSessions.Add(newSession);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Rotated session: archived {OldId} (seq #{OldSeq}, {MsgCount} msgs), " +
            "created {NewId} (seq #{NewSeq}) for room {RoomId}",
            currentSession.Id, currentSession.SequenceNumber, currentSession.MessageCount,
            newSession.Id, newSession.SequenceNumber, roomId);

        // Step 4: Invalidate SDK sessions for this room to reset accumulated context
        try
        {
            await _executor.InvalidateRoomSessionsAsync(roomId);
            _logger.LogInformation(
                "Invalidated SDK sessions for room {RoomId} after epoch rotation", roomId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate SDK sessions for room {RoomId}", roomId);
        }
    }

    private async Task<string> GenerateSummaryAsync(
        ConversationSessionEntity session, string roomType)
    {
        try
        {
            // Load messages from this session
            List<string> messageLines;

            if (roomType == "Breakout")
            {
                var messages = await _db.BreakoutMessages
                    .Where(m => m.SessionId == session.Id)
                    .OrderBy(m => m.SentAt)
                    .ToListAsync();

                messageLines = messages
                    .Select(m => $"[{m.SenderName}]: {m.Content}")
                    .ToList();
            }
            else
            {
                var messages = await _db.Messages
                    .Where(m => m.SessionId == session.Id && m.RecipientId == null)
                    .OrderBy(m => m.SentAt)
                    .ToListAsync();

                messageLines = messages
                    .Select(m => $"[{m.SenderName}]: {m.Content}")
                    .ToList();
            }

            if (messageLines.Count == 0)
                return "No messages to summarize.";

            var conversationText = string.Join("\n", messageLines);
            var prompt =
                $"""
                Summarize this team conversation concisely. Capture:
                - Key decisions made
                - Tasks created or assigned (with agent names)
                - Open questions or unresolved issues
                - Important context for continuing the work
                - Current state of any in-progress work

                Keep it under 500 words. Be factual, not narrative. Use bullet points.

                === CONVERSATION ===
                {conversationText}
                """;

            if (!_executor.IsFullyOperational)
            {
                _logger.LogWarning("Executor not operational — using fallback summary");
                return BuildFallbackSummary(messageLines);
            }

            var summary = await _executor.RunAsync(SummarizerAgent, prompt, null);
            _logger.LogDebug("Generated summary for session {SessionId}: {Length} chars",
                session.Id, summary.Length);

            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to generate LLM summary for session {SessionId} — using fallback",
                session.Id);
            return "Previous conversation archived — summary generation failed.";
        }
    }

    private static string BuildFallbackSummary(List<string> messageLines)
    {
        var count = messageLines.Count;
        var senders = messageLines
            .Select(l => l.Split(']')[0].TrimStart('['))
            .Distinct()
            .Take(10);

        return $"Previous conversation archived ({count} messages). " +
               $"Participants: {string.Join(", ", senders)}. " +
               "No LLM summary available — Copilot was offline during rotation.";
    }

    private async Task<int> GetNextSequenceNumberAsync(string roomId)
    {
        var maxSeq = await _db.ConversationSessions
            .Where(s => s.RoomId == roomId)
            .MaxAsync(s => (int?)s.SequenceNumber);
        return (maxSeq ?? 0) + 1;
    }
}
