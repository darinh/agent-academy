using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Goal card lifecycle management. Content is immutable after creation; only
/// status transitions are permitted, and only via validated state machine.
/// </summary>
public sealed class GoalCardService : IGoalCardService
{
    private readonly AgentAcademyDbContext _db;
    private readonly IActivityPublisher _activity;
    private readonly ILogger<GoalCardService> _logger;

    private static readonly Dictionary<GoalCardStatus, HashSet<GoalCardStatus>> ValidTransitions = new()
    {
        [GoalCardStatus.Active] = [GoalCardStatus.Completed, GoalCardStatus.Challenged, GoalCardStatus.Abandoned],
        [GoalCardStatus.Challenged] = [GoalCardStatus.Active, GoalCardStatus.Abandoned],
        [GoalCardStatus.Completed] = [],
        [GoalCardStatus.Abandoned] = [],
    };

    public GoalCardService(
        AgentAcademyDbContext db,
        IActivityPublisher activity,
        ILogger<GoalCardService> logger)
    {
        _db = db;
        _activity = activity;
        _logger = logger;
    }

    public async Task<GoalCard> CreateAsync(
        string agentId,
        string agentName,
        string roomId,
        CreateGoalCardRequest request,
        CancellationToken ct = default)
    {
        // Validate task exists if provided
        if (request.TaskId is not null)
        {
            var taskExists = await _db.Tasks.AnyAsync(t => t.Id == request.TaskId, ct);
            if (!taskExists)
                throw new ArgumentException($"Task '{request.TaskId}' not found");
        }

        var entity = new GoalCardEntity
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            AgentId = agentId,
            AgentName = agentName,
            RoomId = roomId,
            TaskId = request.TaskId,
            TaskDescription = request.TaskDescription,
            Intent = request.Intent,
            Divergence = request.Divergence,
            Steelman = request.Steelman,
            Strawman = request.Strawman,
            Verdict = request.Verdict.ToString(),
            FreshEyes1 = request.FreshEyes1,
            FreshEyes2 = request.FreshEyes2,
            FreshEyes3 = request.FreshEyes3,
            PromptVersion = 1,
            Status = request.Verdict == GoalCardVerdict.Challenge
                ? GoalCardStatus.Challenged.ToString()
                : GoalCardStatus.Active.ToString(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.GoalCards.Add(entity);

        var eventType = request.Verdict == GoalCardVerdict.Challenge
            ? ActivityEventType.GoalCardChallenged
            : ActivityEventType.GoalCardCreated;

        var verdictLabel = request.Verdict == GoalCardVerdict.ProceedWithCaveat
            ? "PROCEED-WITH-CAVEAT"
            : request.Verdict.ToString().ToUpperInvariant();

        _activity.Publish(
            eventType,
            roomId,
            agentId,
            request.TaskId,
            $"Goal card {entity.Id} created by {agentName}: {verdictLabel}");

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Goal card {GoalCardId} created by {AgentId} in room {RoomId}: verdict={Verdict}",
            entity.Id, agentId, roomId, entity.Verdict);

        return ToSnapshot(entity);
    }

    public async Task<GoalCard?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var entity = await _db.GoalCards
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id, ct);

        return entity is null ? null : ToSnapshot(entity);
    }

    public async Task<List<GoalCard>> GetActiveAsync(string? roomId = null, CancellationToken ct = default)
    {
        return await QueryAsync(roomId: roomId, status: null, verdict: null, ct: ct)
            .ContinueWith(t => t.Result.Where(g =>
                g.Status == GoalCardStatus.Active || g.Status == GoalCardStatus.Challenged).ToList(), ct);
    }

    public async Task<List<GoalCard>> QueryAsync(
        string? roomId = null,
        GoalCardStatus? status = null,
        GoalCardVerdict? verdict = null,
        CancellationToken ct = default)
    {
        var query = _db.GoalCards.AsNoTracking().AsQueryable();

        if (roomId is not null)
            query = query.Where(g => g.RoomId == roomId);
        if (status is not null)
            query = query.Where(g => g.Status == status.Value.ToString());
        if (verdict is not null)
            query = query.Where(g => g.Verdict == verdict.Value.ToString());

        var entities = await query
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(ToSnapshot).ToList();
    }

    public async Task<List<GoalCard>> GetByAgentAsync(string agentId, CancellationToken ct = default)
    {
        var entities = await _db.GoalCards
            .AsNoTracking()
            .Where(g => g.AgentId == agentId)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(ToSnapshot).ToList();
    }

    public async Task<List<GoalCard>> GetByTaskAsync(string taskId, CancellationToken ct = default)
    {
        var entities = await _db.GoalCards
            .AsNoTracking()
            .Where(g => g.TaskId == taskId)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(ToSnapshot).ToList();
    }

    public async Task<GoalCard?> AttachToTaskAsync(string goalCardId, string taskId, CancellationToken ct = default)
    {
        var entity = await _db.GoalCards.FirstOrDefaultAsync(g => g.Id == goalCardId, ct);
        if (entity is null) return null;

        if (entity.TaskId is not null)
            throw new InvalidOperationException($"Goal card {goalCardId} is already linked to task {entity.TaskId}");

        var taskExists = await _db.Tasks.AnyAsync(t => t.Id == taskId, ct);
        if (!taskExists)
            throw new ArgumentException($"Task '{taskId}' not found");

        entity.TaskId = taskId;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Goal card {GoalCardId} linked to task {TaskId}", goalCardId, taskId);
        return ToSnapshot(entity);
    }

    public async Task<GoalCard?> UpdateStatusAsync(string id, GoalCardStatus newStatus, CancellationToken ct = default)
    {
        var entity = await _db.GoalCards.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (entity is null) return null;

        var currentStatus = Enum.Parse<GoalCardStatus>(entity.Status);

        if (!ValidTransitions.TryGetValue(currentStatus, out var allowed) || !allowed.Contains(newStatus))
            throw new InvalidOperationException(
                $"Cannot transition goal card from {currentStatus} to {newStatus}");

        entity.Status = newStatus.ToString();
        entity.UpdatedAt = DateTime.UtcNow;

        if (newStatus == GoalCardStatus.Challenged)
        {
            _activity.Publish(
                ActivityEventType.GoalCardChallenged,
                entity.RoomId,
                entity.AgentId,
                entity.TaskId,
                $"Goal card {entity.Id} challenged — work should stop until resolved");
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Goal card {GoalCardId} status: {OldStatus} → {NewStatus}",
            id, currentStatus, newStatus);

        return ToSnapshot(entity);
    }

    public async Task<GoalCardSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var rows = await _db.GoalCards
            .AsNoTracking()
            .GroupBy(g => new { g.Status, g.Verdict })
            .Select(grp => new { grp.Key.Status, grp.Key.Verdict, Count = grp.Count() })
            .ToListAsync(ct);

        int active = 0, challenged = 0, completed = 0, abandoned = 0;
        int vProceed = 0, vCaveat = 0, vChallenge = 0;

        foreach (var r in rows)
        {
            var count = r.Count;
            switch (r.Status)
            {
                case nameof(GoalCardStatus.Active): active += count; break;
                case nameof(GoalCardStatus.Challenged): challenged += count; break;
                case nameof(GoalCardStatus.Completed): completed += count; break;
                case nameof(GoalCardStatus.Abandoned): abandoned += count; break;
            }
            switch (r.Verdict)
            {
                case nameof(GoalCardVerdict.Proceed): vProceed += count; break;
                case nameof(GoalCardVerdict.ProceedWithCaveat): vCaveat += count; break;
                case nameof(GoalCardVerdict.Challenge): vChallenge += count; break;
            }
        }

        var total = active + challenged + completed + abandoned;
        return new GoalCardSummary(total, active, challenged, completed, abandoned, vProceed, vCaveat, vChallenge);
    }

    private static GoalCard ToSnapshot(GoalCardEntity e) => new(
        Id: e.Id,
        AgentId: e.AgentId,
        AgentName: e.AgentName,
        RoomId: e.RoomId,
        TaskId: e.TaskId,
        TaskDescription: e.TaskDescription,
        Intent: e.Intent,
        Divergence: e.Divergence,
        Steelman: e.Steelman,
        Strawman: e.Strawman,
        Verdict: Enum.Parse<GoalCardVerdict>(e.Verdict),
        FreshEyes1: e.FreshEyes1,
        FreshEyes2: e.FreshEyes2,
        FreshEyes3: e.FreshEyes3,
        PromptVersion: e.PromptVersion,
        Status: Enum.Parse<GoalCardStatus>(e.Status),
        CreatedAt: e.CreatedAt,
        UpdatedAt: e.UpdatedAt
    );
}
