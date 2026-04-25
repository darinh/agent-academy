using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Posts a one-time "sprint started" coordination message into every active
/// room of a sprint's workspace and wakes the orchestrator so agents pick up
/// the new sprint without further human input.
///
/// This is the kickoff that makes Phase 1 of <c>specs/100-product-vision</c>
/// observable: <c>CreateSprintAsync</c> succeeds → message appears in the room
/// → orchestrator queues a round → an agent responds. Without it, sprint
/// creation is a silent state change and the autonomy loop never starts.
/// </summary>
public sealed class SprintKickoffService : ISprintKickoffService
{
    private readonly AgentAcademyDbContext _db;
    private readonly IMessageService _messageService;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly ILogger<SprintKickoffService> _logger;

    public SprintKickoffService(
        AgentAcademyDbContext db,
        IMessageService messageService,
        IAgentOrchestrator orchestrator,
        ILogger<SprintKickoffService> logger)
    {
        _db = db;
        _messageService = messageService;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> PostKickoffAsync(
        SprintEntity sprint, string? trigger = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sprint);

        try
        {
            var archived = nameof(RoomStatus.Archived);
            var completed = nameof(RoomStatus.Completed);

            var rooms = await _db.Rooms
                .Where(r => r.WorkspacePath == sprint.WorkspacePath
                    && r.Status != archived
                    && r.Status != completed)
                .Select(r => r.Id)
                .ToListAsync(ct);

            if (rooms.Count == 0)
            {
                _logger.LogWarning(
                    "Sprint kickoff: no active rooms in workspace '{Workspace}' for sprint #{Number} ({Id}); " +
                    "agents will not see the sprint start until a room exists.",
                    sprint.WorkspacePath, sprint.Number, sprint.Id);
                return 0;
            }

            var content = BuildKickoffMessage(sprint, trigger);
            var successful = 0;

            foreach (var roomId in rooms)
            {
                try
                {
                    await _messageService.PostSystemMessageAsync(roomId, content);
                    successful++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Sprint kickoff: failed to post message in room {RoomId} for sprint #{Number} ({Id}); " +
                        "continuing with remaining rooms.",
                        roomId, sprint.Number, sprint.Id);
                    continue;
                }

                // The orchestrator wake is what makes the kickoff observable; if it
                // throws, the message is already persisted, so the post is still a
                // success — log the wake failure separately so an operator can see
                // the room won't get an agent response without a manual retry.
                try
                {
                    _orchestrator.HandleHumanMessage(roomId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Sprint kickoff: posted message in room {RoomId} for sprint #{Number} ({Id}) " +
                        "but failed to wake the orchestrator; agents will not respond until the next " +
                        "human message.",
                        roomId, sprint.Number, sprint.Id);
                }
            }

            if (successful < rooms.Count)
            {
                _logger.LogWarning(
                    "Sprint kickoff: only {Successful}/{Total} room(s) received the kickoff for sprint #{Number} ({Id}) in workspace '{Workspace}' (trigger={Trigger})",
                    successful, rooms.Count, sprint.Number, sprint.Id, sprint.WorkspacePath, trigger ?? "manual");
            }
            else
            {
                _logger.LogInformation(
                    "Sprint kickoff: posted to {Count} room(s) for sprint #{Number} ({Id}) in workspace '{Workspace}' (trigger={Trigger})",
                    successful, sprint.Number, sprint.Id, sprint.WorkspacePath, trigger ?? "manual");
            }

            return successful;
        }
        catch (Exception ex)
        {
            // Kickoff is best-effort — sprint creation must not fail because of it.
            _logger.LogError(ex,
                "Sprint kickoff failed for sprint #{Number} ({Id}) in workspace '{Workspace}'",
                sprint.Number, sprint.Id, sprint.WorkspacePath);
            return 0;
        }
    }

    private static string BuildKickoffMessage(SprintEntity sprint, string? trigger)
    {
        var triggerLabel = trigger switch
        {
            "auto" => " (auto-started after previous sprint)",
            "scheduled" => " (scheduled)",
            null or "" => "",
            _ => $" ({trigger})",
        };

        return
            $"🟢 **Sprint #{sprint.Number} started**{triggerLabel} for `{sprint.WorkspacePath}`. " +
            $"Current stage: **{sprint.CurrentStage}**.\n\n" +
            "Aristotle, please facilitate the kickoff: confirm the sprint goal, list any " +
            "overflow requirements carried over, and propose the acceptance criteria you'll track. " +
            "When the team is ready, advance to **Planning**.";
    }
}
