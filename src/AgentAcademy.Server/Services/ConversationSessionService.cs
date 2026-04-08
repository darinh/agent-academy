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

    // ── Sprint-scoped sessions ──────────────────────────────────

    /// <summary>
    /// Creates a new conversation session tagged with a sprint ID and stage.
    /// Archives the current active session for the room (if any) before
    /// creating the new one. Used when the sprint advances to a new stage
    /// so each stage gets a clean session boundary.
    /// </summary>
    public async Task<ConversationSessionEntity> CreateSessionForStageAsync(
        string roomId, string sprintId, string stage, string roomType = "Main")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sprintId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);

        // Archive any existing active session for this room
        var current = await _db.ConversationSessions
            .Where(s => s.RoomId == roomId && s.Status == "Active")
            .FirstOrDefaultAsync();

        if (current is not null)
        {
            if (current.MessageCount > 0)
            {
                // Use the existing session's RoomType for summary generation
                // so messages are read from the correct table
                var summary = await GenerateSummaryAsync(current, current.RoomType);
                current.Summary = summary;
            }
            current.Status = "Archived";
            current.ArchivedAt = DateTime.UtcNow;
        }

        var session = new ConversationSessionEntity
        {
            RoomId = roomId,
            RoomType = roomType,
            SequenceNumber = await GetNextSequenceNumberAsync(roomId),
            Status = "Active",
            MessageCount = 0,
            SprintId = sprintId,
            SprintStage = stage,
            CreatedAt = DateTime.UtcNow,
        };

        _db.ConversationSessions.Add(session);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Created sprint-scoped session {SessionId} (seq #{Seq}) for room {RoomId}, " +
            "sprint {SprintId} stage {Stage}{Archived}",
            session.Id, session.SequenceNumber, roomId, sprintId, stage,
            current is not null ? $" (archived previous session {current.Id})" : "");

        // Invalidate SDK sessions so agents start with clean context for the new stage
        try
        {
            await _executor.InvalidateRoomSessionsAsync(roomId);
            _logger.LogInformation(
                "Invalidated SDK sessions for room {RoomId} after stage transition to {Stage}",
                roomId, stage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to invalidate SDK sessions for room {RoomId} during stage transition",
                roomId);
        }

        return session;
    }

    /// <summary>
    /// Returns the summary from the most recently archived session for a
    /// given sprint and stage. Used to inject previous-stage context into
    /// the next stage's conversation.
    /// </summary>
    public async Task<string?> GetStageContextAsync(string sprintId, string stage)
    {
        return await _db.ConversationSessions
            .Where(s => s.SprintId == sprintId
                && s.SprintStage == stage
                && s.Status == "Archived"
                && s.Summary != null)
            .OrderByDescending(s => s.SequenceNumber)
            .Select(s => s.Summary)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Returns one summary per stage for a sprint, deduplicated to the latest
    /// archived session per stage, ordered by canonical sprint stage sequence.
    /// Used to build a complete sprint context for agents.
    /// </summary>
    public async Task<List<(string Stage, string Summary)>> GetSprintContextAsync(string sprintId)
    {
        var sessions = await _db.ConversationSessions
            .Where(s => s.SprintId == sprintId
                && s.Status == "Archived"
                && s.Summary != null
                && s.SprintStage != null)
            .OrderByDescending(s => s.SequenceNumber)
            .Select(s => new { s.SprintStage, s.Summary })
            .ToListAsync();

        // Deduplicate: keep only the latest (highest sequence) per stage
        var latestPerStage = sessions
            .GroupBy(s => s.SprintStage!)
            .ToDictionary(g => g.Key, g => g.First().Summary!);

        // Order by canonical stage sequence
        return SprintService.Stages
            .Where(stage => latestPerStage.ContainsKey(stage))
            .Select(stage => (Stage: stage, Summary: latestPerStage[stage]))
            .ToList();
    }

    /// <summary>
    /// Lists conversation sessions for a specific room, ordered by sequence number descending.
    /// </summary>
    public async Task<(List<ConversationSessionSnapshot> Sessions, int TotalCount)> GetRoomSessionsAsync(
        string roomId, string? status = null, int limit = 20, int offset = 0)
    {
        var query = _db.ConversationSessions
            .Where(s => s.RoomId == roomId);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(s => s.Status == status);

        var totalCount = await query.CountAsync();
        var safeOffset = Math.Max(offset, 0);

        var sessions = await query
            .OrderByDescending(s => s.SequenceNumber)
            .Skip(safeOffset)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(s => new ConversationSessionSnapshot(
                s.Id, s.RoomId, s.RoomType, s.SequenceNumber,
                s.Status, s.Summary, s.MessageCount, s.CreatedAt, s.ArchivedAt))
            .ToListAsync();

        return (sessions, totalCount);
    }

    /// <summary>
    /// Lists conversation sessions across all rooms, ordered by creation date descending.
    /// </summary>
    public async Task<(List<ConversationSessionSnapshot> Sessions, int TotalCount)> GetAllSessionsAsync(
        string? status = null, int limit = 20, int offset = 0, int? hoursBack = null)
    {
        var query = _db.ConversationSessions.AsQueryable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(s => s.Status == status);

        if (hoursBack.HasValue)
        {
            var since = DateTime.UtcNow.AddHours(-hoursBack.Value);
            query = query.Where(s => s.CreatedAt >= since);
        }

        var totalCount = await query.CountAsync();
        var safeOffset = Math.Max(offset, 0);

        var sessions = await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip(safeOffset)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(s => new ConversationSessionSnapshot(
                s.Id, s.RoomId, s.RoomType, s.SequenceNumber,
                s.Status, s.Summary, s.MessageCount, s.CreatedAt, s.ArchivedAt))
            .ToListAsync();

        return (sessions, totalCount);
    }

    /// <summary>
    /// Returns a summary of session stats: total sessions, active/archived counts,
    /// total messages across all sessions.
    /// </summary>
    public async Task<SessionStats> GetSessionStatsAsync(int? hoursBack = null)
    {
        var query = _db.ConversationSessions.AsQueryable();

        if (hoursBack.HasValue)
        {
            var since = DateTime.UtcNow.AddHours(-hoursBack.Value);
            query = query.Where(s => s.CreatedAt >= since);
        }

        var stats = await query
            .GroupBy(_ => 1)
            .Select(g => new SessionStats(
                g.Count(),
                g.Count(s => s.Status == "Active"),
                g.Count(s => s.Status == "Archived"),
                g.Sum(s => s.MessageCount)))
            .FirstOrDefaultAsync();

        return stats ?? new SessionStats(0, 0, 0, 0);
    }

    /// <summary>
    /// Archives all active conversation sessions with LLM summaries.
    /// Called before workspace switch so agents can resume context when
    /// the user returns to this project. Empty sessions (no messages)
    /// are archived without summaries since there's nothing to preserve.
    /// Does NOT create replacement Active sessions — they'll be created
    /// on demand when the user returns to this workspace.
    /// Each session is saved independently so partial progress is preserved
    /// if an error occurs mid-archival.
    /// </summary>
    public async Task<int> ArchiveAllActiveSessionsAsync()
    {
        var activeSessions = await _db.ConversationSessions
            .Where(s => s.Status == "Active")
            .ToListAsync();

        if (activeSessions.Count == 0) return 0;

        var archived = 0;
        foreach (var session in activeSessions)
        {
            try
            {
                if (session.MessageCount > 0)
                {
                    var summary = await GenerateSummaryAsync(session, session.RoomType);
                    session.Summary = summary;
                    _logger.LogInformation(
                        "Archived session {SessionId} for room {RoomId} with summary ({MsgCount} messages)",
                        session.Id, session.RoomId, session.MessageCount);
                }
                else
                {
                    _logger.LogDebug(
                        "Archived empty session {SessionId} for room {RoomId}",
                        session.Id, session.RoomId);
                }

                session.Status = "Archived";
                session.ArchivedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                archived++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to archive session {SessionId} for room {RoomId} — skipping",
                    session.Id, session.RoomId);
            }
        }

        _logger.LogInformation("Archived {Count}/{Total} active sessions for workspace switch",
            archived, activeSessions.Count);
        return archived;
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
