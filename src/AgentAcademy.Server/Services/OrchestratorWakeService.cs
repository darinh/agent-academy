using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Default <see cref="IOrchestratorWakeService"/>. Mirrors the wake pattern
/// originally inlined in <c>SprintController.TryWakeOrchestratorForSprintAsync</c>
/// (lines 613-650 prior to extraction): query non-Archived, non-Completed rooms
/// in the sprint's workspace, then call
/// <see cref="IAgentOrchestrator.HandleHumanMessage"/> for each with per-room
/// try/catch and warning logs. Both the API endpoint AND the terminal-stage
/// driver share this implementation. See
/// <c>specs/100-product-vision/sprint-terminal-stage-handler-design.md §4.2.1</c>.
/// </summary>
public sealed class OrchestratorWakeService : IOrchestratorWakeService
{
    private readonly AgentAcademyDbContext _db;
    private readonly ISprintService _sprintService;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly ILogger<OrchestratorWakeService> _logger;

    public OrchestratorWakeService(
        AgentAcademyDbContext db,
        ISprintService sprintService,
        IAgentOrchestrator orchestrator,
        ILogger<OrchestratorWakeService> logger)
    {
        _db = db;
        _sprintService = sprintService;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    public async Task WakeWorkspaceRoomsForSprintAsync(string sprintId, CancellationToken ct = default)
    {
        try
        {
            var sprint = await _sprintService.GetSprintByIdAsync(sprintId);
            if (sprint is null || string.IsNullOrEmpty(sprint.WorkspacePath))
                return;

            var archived = nameof(RoomStatus.Archived);
            var completed = nameof(RoomStatus.Completed);
            var roomIds = await _db.Rooms
                .Where(r => r.WorkspacePath == sprint.WorkspacePath
                    && r.Status != archived
                    && r.Status != completed)
                .Select(r => r.Id)
                .ToListAsync(ct);

            foreach (var roomId in roomIds)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    _orchestrator.HandleHumanMessage(roomId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to wake orchestrator for room {RoomId} (sprint {SprintId}); " +
                        "agents will pick up new state on the next round.",
                        roomId, sprintId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to enumerate rooms to wake for sprint {SprintId}.", sprintId);
        }
    }
}
