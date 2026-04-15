using Microsoft.Extensions.Logging;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Immutable snapshot of shared per-round context. Data only — no services.
/// Each field soft-fails to null independently so one failure cannot cascade.
/// </summary>
internal record RoundContext(
    string? SpecContext,
    string? SpecVersion,
    string? SessionSummary,
    string? SprintPreamble,
    string? ActiveSprintStage);

/// <summary>
/// Loads the shared context needed by conversation rounds and DM rounds.
/// Extracted from AgentOrchestrator to enable independent testing and
/// reduce the orchestrator's responsibility surface.
/// </summary>
public sealed class RoundContextLoader
{
    private readonly SpecManager _specManager;
    private readonly IConversationSessionQueryService _sessionQuery;
    private readonly IRoomService _roomService;
    private readonly ISprintService _sprintService;
    private readonly ISprintArtifactService _artifactService;
    private readonly ILogger<RoundContextLoader> _logger;

    public RoundContextLoader(
        SpecManager specManager,
        IConversationSessionQueryService sessionQuery,
        IRoomService roomService,
        ISprintService sprintService,
        ISprintArtifactService artifactService,
        ILogger<RoundContextLoader> logger)
    {
        _specManager = specManager;
        _sessionQuery = sessionQuery;
        _roomService = roomService;
        _sprintService = sprintService;
        _artifactService = artifactService;
        _logger = logger;
    }

    /// <summary>
    /// Loads the shared context needed by both conversation rounds and DM rounds.
    /// Each field fails independently to null with a logged warning.
    /// </summary>
    internal async Task<RoundContext> LoadAsync(string roomId)
    {
        string? specContext = null;
        string? specVersion = null;
        string? sessionSummary = null;
        string? sprintPreamble = null;
        string? activeSprintStage = null;

        try
        {
            specContext = await _specManager.LoadSpecContextAsync();
            var versionInfo = await _specManager.GetSpecVersionAsync();
            specVersion = versionInfo?.Version;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load spec context for room {RoomId}", roomId);
        }

        try
        {
            sessionSummary = await _sessionQuery.GetSessionContextAsync(roomId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load session context for room {RoomId}", roomId);
        }

        try
        {
            var (preamble, stage) = await LoadSprintContextAsync(roomId);
            sprintPreamble = preamble;
            activeSprintStage = stage;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load sprint context for room {RoomId}", roomId);
        }

        return new(specContext, specVersion, sessionSummary, sprintPreamble, activeSprintStage);
    }

    private async Task<(string? Preamble, string? ActiveStage)> LoadSprintContextAsync(string roomId)
    {
        var workspacePath = await _roomService.GetActiveWorkspacePathAsync();
        if (workspacePath is null) return (null, null);

        var sprint = await _sprintService.GetActiveSprintAsync(workspacePath);
        if (sprint is null) return (null, null);

        var priorContext = await _sessionQuery.GetSprintContextAsync(sprint.Id);

        string? overflowContent = null;
        if (sprint.CurrentStage == "Intake" && sprint.OverflowFromSprintId is not null)
        {
            var overflowArtifacts = await _artifactService.GetSprintArtifactsAsync(sprint.Id);
            var overflow = overflowArtifacts.FirstOrDefault(a => a.Type == "OverflowRequirements");
            overflowContent = overflow?.Content;
        }

        var preamble = SprintPreambles.BuildPreamble(
            sprint.Number, sprint.CurrentStage, priorContext, overflowContent);

        return (preamble, sprint.CurrentStage);
    }
}
