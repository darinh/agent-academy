using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Services;

/// <summary>
/// CRUD operations for sprint schedule configuration.
/// Validation and persistence only — the background scheduler
/// (<see cref="SprintSchedulerService"/>) handles cron evaluation.
/// </summary>
public sealed class SprintScheduleService
{
    private readonly AgentAcademyDbContext _db;

    public SprintScheduleService(AgentAcademyDbContext db) => _db = db;

    /// <summary>
    /// Gets the sprint schedule for a workspace, or null if none exists.
    /// </summary>
    public async Task<SprintScheduleResponse?> GetScheduleAsync(string workspacePath)
    {
        var entity = await _db.SprintSchedules
            .FirstOrDefaultAsync(s => s.WorkspacePath == workspacePath);
        return entity is null ? null : ToResponse(entity);
    }

    /// <summary>
    /// Creates or updates the sprint schedule for a workspace.
    /// </summary>
    /// <exception cref="ArgumentException">Invalid cron expression or timezone.</exception>
    public async Task<SprintScheduleResponse> UpsertScheduleAsync(
        string workspacePath, string cronExpression, string timeZoneId, bool enabled)
    {
        if (!SprintSchedulerService.IsValidCron(cronExpression))
            throw new ArgumentException(
                "Invalid cron expression. Use standard 5-field format (minute hour day month weekday).");

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new ArgumentException($"Unknown timezone: {timeZoneId}");
        }

        var now = DateTime.UtcNow;
        var entity = await _db.SprintSchedules
            .FirstOrDefaultAsync(s => s.WorkspacePath == workspacePath);

        if (entity is null)
        {
            entity = new SprintScheduleEntity
            {
                Id = Guid.NewGuid().ToString(),
                WorkspacePath = workspacePath,
                CreatedAt = now,
            };
            _db.SprintSchedules.Add(entity);
        }

        entity.CronExpression = cronExpression;
        entity.TimeZoneId = timeZoneId;
        entity.Enabled = enabled;
        entity.NextRunAtUtc = enabled
            ? SprintSchedulerService.ComputeNextRun(cronExpression, timeZoneId, now)
            : null;
        entity.UpdatedAt = now;

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (entity.CreatedAt == now)
        {
            // Concurrent insert race — reload and update
            _db.ChangeTracker.Clear();
            entity = await _db.SprintSchedules
                .FirstAsync(s => s.WorkspacePath == workspacePath);
            entity.CronExpression = cronExpression;
            entity.TimeZoneId = timeZoneId;
            entity.Enabled = enabled;
            entity.NextRunAtUtc = enabled
                ? SprintSchedulerService.ComputeNextRun(cronExpression, timeZoneId, now)
                : null;
            entity.UpdatedAt = now;
            await _db.SaveChangesAsync();
        }

        return ToResponse(entity);
    }

    /// <summary>
    /// Deletes the sprint schedule for a workspace.
    /// Returns false if no schedule existed.
    /// </summary>
    public async Task<bool> DeleteScheduleAsync(string workspacePath)
    {
        var entity = await _db.SprintSchedules
            .FirstOrDefaultAsync(s => s.WorkspacePath == workspacePath);
        if (entity is null)
            return false;

        _db.SprintSchedules.Remove(entity);
        await _db.SaveChangesAsync();
        return true;
    }

    private static SprintScheduleResponse ToResponse(SprintScheduleEntity e) => new(
        e.Id, e.WorkspacePath, e.CronExpression, e.TimeZoneId, e.Enabled,
        e.NextRunAtUtc, e.LastTriggeredAt, e.LastEvaluatedAt, e.LastOutcome,
        e.CreatedAt, e.UpdatedAt);
}
