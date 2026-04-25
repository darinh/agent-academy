using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Posts a one-shot "stage changed" coordination message into every active
/// room of a sprint's workspace and wakes the orchestrator so agents pick up
/// the new stage without further human input.
///
/// This makes Phase 1 P1.3 of <c>specs/100-product-vision</c> observable:
/// a stage advance triggers an agent message reflecting the new stage's
/// intent. Without it, stage transitions are silent presentation-layer
/// updates and agents only see the new preamble on their next round —
/// which never fires because nobody woke the orchestrator.
///
/// The full stage preamble (with workflow rules, advancement instructions,
/// etc.) is injected into the agent's prompt by <see cref="SprintPreambles"/>
/// when the round runs, so the chat message stays short.
/// </summary>
public sealed class SprintStageAdvanceAnnouncer : ISprintStageAdvanceAnnouncer
{
    private readonly AgentAcademyDbContext _db;
    private readonly IMessageService _messageService;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly ILogger<SprintStageAdvanceAnnouncer> _logger;

    public SprintStageAdvanceAnnouncer(
        AgentAcademyDbContext db,
        IMessageService messageService,
        IAgentOrchestrator orchestrator,
        ILogger<SprintStageAdvanceAnnouncer> logger)
    {
        _db = db;
        _messageService = messageService;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> AnnounceAsync(
        SprintEntity sprint,
        string previousStage,
        string? trigger = null,
        IReadOnlyCollection<string>? targetRoomIds = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sprint);
        ArgumentException.ThrowIfNullOrEmpty(previousStage);

        try
        {
            List<string> rooms;
            if (targetRoomIds is not null)
            {
                // Caller pre-captured the room set — used by SprintStageService
                // for the Implementation → FinalSynthesis transition where the
                // stage sync flips rooms to Completed before announce runs and
                // would otherwise hide them from the workspace query below.
                rooms = targetRoomIds.ToList();
            }
            else
            {
                var archived = nameof(RoomStatus.Archived);
                var completed = nameof(RoomStatus.Completed);

                rooms = await _db.Rooms
                    .Where(r => r.WorkspacePath == sprint.WorkspacePath
                        && r.Status != archived
                        && r.Status != completed)
                    .Select(r => r.Id)
                    .ToListAsync(ct);
            }

            if (rooms.Count == 0)
            {
                _logger.LogWarning(
                    "Sprint stage announce: no active rooms in workspace '{Workspace}' for sprint #{Number} ({Id}); " +
                    "agents will not see the {Previous} → {Current} transition until a room exists.",
                    sprint.WorkspacePath, sprint.Number, sprint.Id, previousStage, sprint.CurrentStage);
                return 0;
            }

            var content = BuildAnnouncementMessage(sprint, previousStage, trigger);
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
                        "Sprint stage announce: failed to post message in room {RoomId} for sprint #{Number} ({Id}) " +
                        "({Previous} → {Current}); continuing with remaining rooms.",
                        roomId, sprint.Number, sprint.Id, previousStage, sprint.CurrentStage);
                    continue;
                }

                // The orchestrator wake is what triggers the agent's next round; if
                // it throws, the message is already persisted, so the post is still
                // a success — log the wake failure separately so an operator can see
                // the room won't get an agent response without a manual nudge.
                try
                {
                    _orchestrator.HandleHumanMessage(roomId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Sprint stage announce: posted message in room {RoomId} for sprint #{Number} ({Id}) " +
                        "({Previous} → {Current}) but failed to wake the orchestrator; agents will not " +
                        "respond until the next human message.",
                        roomId, sprint.Number, sprint.Id, previousStage, sprint.CurrentStage);
                }
            }

            if (successful < rooms.Count)
            {
                _logger.LogWarning(
                    "Sprint stage announce: only {Successful}/{Total} room(s) received the announcement for " +
                    "sprint #{Number} ({Id}) {Previous} → {Current} in workspace '{Workspace}' (trigger={Trigger})",
                    successful, rooms.Count, sprint.Number, sprint.Id, previousStage, sprint.CurrentStage,
                    sprint.WorkspacePath, trigger ?? "advance");
            }
            else
            {
                _logger.LogInformation(
                    "Sprint stage announce: posted to {Count} room(s) for sprint #{Number} ({Id}) " +
                    "{Previous} → {Current} in workspace '{Workspace}' (trigger={Trigger})",
                    successful, sprint.Number, sprint.Id, previousStage, sprint.CurrentStage,
                    sprint.WorkspacePath, trigger ?? "advance");
            }

            return successful;
        }
        catch (Exception ex)
        {
            // Announcement is best-effort — stage advancement must not fail because of it.
            _logger.LogError(ex,
                "Sprint stage announce failed for sprint #{Number} ({Id}) {Previous} → {Current} in workspace '{Workspace}'",
                sprint.Number, sprint.Id, previousStage, sprint.CurrentStage, sprint.WorkspacePath);
            return 0;
        }
    }

    private static string BuildAnnouncementMessage(SprintEntity sprint, string previousStage, string? trigger)
    {
        var triggerLabel = trigger switch
        {
            "approved" => " (user-approved)",
            "forced" => " (forced)",
            null or "" => "",
            _ => $" ({trigger})",
        };

        var stageIntent = StageIntent(sprint.CurrentStage);

        return
            $"➡️ **Sprint #{sprint.Number} advanced{triggerLabel}: {previousStage} → {sprint.CurrentStage}**\n\n" +
            stageIntent +
            "\n\nAristotle, please facilitate this stage. Full stage instructions are in your prompt preamble.";
    }

    /// <summary>
    /// One-line summary of what the team should focus on at the new stage.
    /// The full stage preamble (with workflow + advancement instructions) is
    /// injected into the agent prompt by <see cref="SprintPreambles"/>; this
    /// chat message is the human-readable nudge that something changed.
    /// </summary>
    private static string StageIntent(string stage) => stage switch
    {
        "Intake" => "Focus: gather requirements. Produce a RequirementsDocument.",
        "Planning" => "Focus: break the requirements into tasks with owners and risks. Produce a SprintPlan.",
        "Discussion" => "Focus: open debate on the plan — surface trade-offs and risks before implementation begins.",
        "Validation" => "Focus: validate the plan for completeness and feasibility. Produce a ValidationReport.",
        "Implementation" => "Focus: build the plan. Create tasks, work in branches, open PRs, review, and merge.",
        "FinalSynthesis" => "Focus: wrap the sprint. Produce a SprintReport (and OverflowRequirements if work remains), then COMPLETE_SPRINT.",
        _ => $"Focus: stage **{stage}** — see your prompt preamble for instructions.",
    };
}
