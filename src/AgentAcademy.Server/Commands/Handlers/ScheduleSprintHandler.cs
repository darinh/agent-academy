using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles SCHEDULE_SPRINT — manage the cron-based sprint schedule for the active workspace.
/// Actions: get (default), set, delete. Wraps the same logic as the REST endpoints
/// in <see cref="Controllers.SprintController"/>.
/// </summary>
public sealed class ScheduleSprintHandler : ICommandHandler
{
    public string CommandName => "SCHEDULE_SPRINT";

    public bool IsDestructive => false;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var action = "get";
        if (command.Args.TryGetValue("action", out var actionObj) && actionObj is string a
            && !string.IsNullOrWhiteSpace(a))
        {
            action = a.Trim().ToLowerInvariant();
        }

        var roomService = context.Services.GetRequiredService<IRoomService>();
        var workspacePath = await roomService.GetActiveWorkspacePathAsync();
        if (string.IsNullOrEmpty(workspacePath))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "No active workspace. Open a workspace before managing sprint schedules."
            };
        }

        return action switch
        {
            "get" => await HandleGetAsync(command, context, workspacePath),
            "set" => await HandleSetAsync(command, context, workspacePath),
            "delete" => await HandleDeleteAsync(command, context, workspacePath),
            _ => command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = $"Unknown action '{action}'. Use: get, set, or delete."
            }
        };
    }

    private static async Task<CommandEnvelope> HandleGetAsync(
        CommandEnvelope command, CommandContext context, string workspacePath)
    {
        var db = context.Services.GetRequiredService<AgentAcademyDbContext>();
        var entity = await db.SprintSchedules
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkspacePath == workspacePath);

        if (entity is null)
        {
            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["hasSchedule"] = false,
                    ["message"] = "No sprint schedule configured for this workspace."
                }
            };
        }

        return command with
        {
            Status = CommandStatus.Success,
            Result = ScheduleToResult(entity)
        };
    }

    private static async Task<CommandEnvelope> HandleSetAsync(
        CommandEnvelope command, CommandContext context, string workspacePath)
    {
        if (!command.Args.TryGetValue("cron", out var cronObj) || cronObj is not string cron
            || string.IsNullOrWhiteSpace(cron))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required arg 'cron'. Provide a 5-field cron expression (e.g. '0 9 * * 1' for every Monday at 9 AM)."
            };
        }

        if (!SprintSchedulerService.IsValidCron(cron))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = $"Invalid cron expression: '{cron}'. Use standard 5-field format (minute hour day month weekday)."
            };
        }

        var timeZoneId = "UTC";
        if (command.Args.TryGetValue("timezone", out var tzObj) && tzObj is string tz
            && !string.IsNullOrWhiteSpace(tz))
        {
            timeZoneId = tz.Trim();
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = $"Unknown timezone: '{timeZoneId}'. Use an IANA timezone ID (e.g. 'America/New_York', 'UTC')."
            };
        }

        var enabled = true;
        if (command.Args.TryGetValue("enabled", out var enabledObj))
        {
            enabled = enabledObj switch
            {
                bool b => b,
                string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
                _ => true
            };
        }

        var db = context.Services.GetRequiredService<AgentAcademyDbContext>();
        var now = DateTime.UtcNow;
        var entity = await db.SprintSchedules
            .FirstOrDefaultAsync(s => s.WorkspacePath == workspacePath);

        var isNew = entity is null;
        if (isNew)
        {
            entity = new SprintScheduleEntity
            {
                Id = Guid.NewGuid().ToString(),
                WorkspacePath = workspacePath,
                CreatedAt = now
            };
            db.SprintSchedules.Add(entity);
        }

        entity!.CronExpression = cron;
        entity.TimeZoneId = timeZoneId;
        entity.Enabled = enabled;
        entity.UpdatedAt = now;

        // Precompute next run time
        entity.NextRunAtUtc = SprintSchedulerService.ComputeNextRun(cron, timeZoneId, now);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (isNew)
        {
            // Race: another request inserted first. Reload and update instead.
            db.ChangeTracker.Clear();
            entity = await db.SprintSchedules
                .FirstOrDefaultAsync(s => s.WorkspacePath == workspacePath);
            if (entity is null)
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Conflict,
                    Error = "Concurrent schedule modification. Please retry."
                };
            }

            entity.CronExpression = cron;
            entity.TimeZoneId = timeZoneId;
            entity.Enabled = enabled;
            entity.UpdatedAt = now;
            entity.NextRunAtUtc = SprintSchedulerService.ComputeNextRun(cron, timeZoneId, now);
            isNew = false;
            await db.SaveChangesAsync();
        }

        var result = ScheduleToResult(entity);
        result["message"] = isNew
            ? $"Sprint schedule created: '{cron}' ({timeZoneId}), enabled={enabled}"
            : $"Sprint schedule updated: '{cron}' ({timeZoneId}), enabled={enabled}";

        return command with
        {
            Status = CommandStatus.Success,
            Result = result
        };
    }

    private static async Task<CommandEnvelope> HandleDeleteAsync(
        CommandEnvelope command, CommandContext context, string workspacePath)
    {
        var db = context.Services.GetRequiredService<AgentAcademyDbContext>();
        var entity = await db.SprintSchedules
            .FirstOrDefaultAsync(s => s.WorkspacePath == workspacePath);

        if (entity is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = "No sprint schedule exists for this workspace."
            };
        }

        db.SprintSchedules.Remove(entity);
        await db.SaveChangesAsync();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["deleted"] = true,
                ["message"] = "Sprint schedule deleted."
            }
        };
    }

    private static Dictionary<string, object?> ScheduleToResult(SprintScheduleEntity entity) => new()
    {
        ["hasSchedule"] = true,
        ["scheduleId"] = entity.Id,
        ["cronExpression"] = entity.CronExpression,
        ["timeZoneId"] = entity.TimeZoneId,
        ["enabled"] = entity.Enabled,
        ["nextRunAtUtc"] = entity.NextRunAtUtc?.ToString("O"),
        ["lastTriggeredAt"] = entity.LastTriggeredAt?.ToString("O"),
        ["lastOutcome"] = entity.LastOutcome
    };
}
