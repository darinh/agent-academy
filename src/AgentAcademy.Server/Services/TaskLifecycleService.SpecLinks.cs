using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Spec–task linking: creates and updates links between tasks and spec sections.
/// </summary>
public sealed partial class TaskLifecycleService
{
    /// <summary>
    /// Valid spec-task link types.
    /// </summary>
    public static readonly HashSet<string> ValidLinkTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Implements", "Modifies", "Fixes", "References"
    };

    /// <summary>
    /// Links a task to a spec section. Idempotent — updates link type if the pair already exists.
    /// </summary>
    public async Task<SpecTaskLink> LinkTaskToSpecAsync(
        string taskId, string specSectionId, string agentId, string agentName,
        string linkType = "Implements", string? note = null)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("taskId is required");
        if (string.IsNullOrWhiteSpace(specSectionId))
            throw new ArgumentException("specSectionId is required");
        if (!ValidLinkTypes.Contains(linkType))
            throw new ArgumentException(
                $"Invalid link type '{linkType}'. Valid types: {string.Join(", ", ValidLinkTypes)}");

        var task = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        // Upsert with retry: catch unique constraint violation on concurrent insert
        try
        {
            return await UpsertSpecLinkCoreAsync(
                task, taskId, specSectionId, agentId, agentName, linkType, note);
        }
        catch (DbUpdateException)
        {
            // Concurrent insert hit unique constraint — reload and update
            _db.ChangeTracker.Clear();
            return await UpsertSpecLinkCoreAsync(
                task, taskId, specSectionId, agentId, agentName, linkType, note);
        }
    }

    private async Task<SpecTaskLink> UpsertSpecLinkCoreAsync(
        TaskEntity task, string taskId, string specSectionId,
        string agentId, string agentName, string linkType, string? note)
    {
        var existing = await _db.SpecTaskLinks
            .FirstOrDefaultAsync(l => l.TaskId == taskId && l.SpecSectionId == specSectionId);

        if (existing is not null)
        {
            existing.LinkType = linkType;
            existing.Note = note ?? existing.Note;
            existing.LinkedByAgentId = agentId;
            existing.LinkedByAgentName = agentName;

            Publish(ActivityEventType.SpecTaskLinked, task.RoomId, agentId, taskId,
                $"{agentName} updated spec link: {specSectionId} → {task.Title}");
            await _db.SaveChangesAsync();

            return TaskSnapshotFactory.BuildSpecTaskLink(existing);
        }

        var entity = new SpecTaskLinkEntity
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            TaskId = taskId,
            SpecSectionId = specSectionId,
            LinkType = linkType,
            LinkedByAgentId = agentId,
            LinkedByAgentName = agentName,
            Note = note,
            CreatedAt = DateTime.UtcNow
        };

        _db.SpecTaskLinks.Add(entity);
        Publish(ActivityEventType.SpecTaskLinked, task.RoomId, agentId, taskId,
            $"{agentName} linked spec {specSectionId} to task: {Truncate(task.Title, 60)}");
        await _db.SaveChangesAsync();

        return TaskSnapshotFactory.BuildSpecTaskLink(entity);
    }
}
