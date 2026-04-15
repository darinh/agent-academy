using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Handles task verification evidence recording and gate checks.
/// Extracted from TaskLifecycleService to isolate the evidence ledger concern.
/// </summary>
public sealed class TaskEvidenceService : ITaskEvidenceService
{
    /// <summary>
    /// Valid evidence phases.
    /// </summary>
    public static readonly HashSet<string> ValidEvidencePhases = new(StringComparer.OrdinalIgnoreCase)
    {
        "Baseline", "After", "Review"
    };

    private readonly AgentAcademyDbContext _db;
    private readonly ActivityPublisher _activity;

    public TaskEvidenceService(
        AgentAcademyDbContext db,
        ActivityPublisher activity)
    {
        _db = db;
        _activity = activity;
    }

    /// <summary>
    /// Records a structured verification check against a task.
    /// </summary>
    public async Task<TaskEvidence> RecordEvidenceAsync(
        string taskId, string agentId, string agentName,
        EvidencePhase phase, string checkName, string tool,
        string? command, int? exitCode, string? outputSnippet, bool passed)
    {
        var task = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        var entity = new TaskEvidenceEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            TaskId = taskId,
            Phase = phase.ToString(),
            CheckName = checkName,
            Tool = tool,
            Command = command,
            ExitCode = exitCode,
            OutputSnippet = outputSnippet?.Length > 500 ? outputSnippet[..500] : outputSnippet,
            Passed = passed,
            AgentId = agentId,
            AgentName = agentName,
            CreatedAt = DateTime.UtcNow
        };
        _db.TaskEvidence.Add(entity);

        _activity.Publish(ActivityEventType.EvidenceRecorded, task.RoomId, agentId, taskId,
            $"{agentName} recorded {phase.ToString().ToLower()} evidence: {checkName} — {(passed ? "passed" : "FAILED")}");

        await _db.SaveChangesAsync();
        return TaskSnapshotFactory.BuildTaskEvidence(entity);
    }

    /// <summary>
    /// Checks whether a task meets the minimum evidence requirements for a phase transition.
    /// Gate definitions (based on task status):
    /// - Active → AwaitingValidation: ≥1 "After" check passed
    /// - AwaitingValidation → InReview: ≥2 "After" checks passed
    /// - InReview → Approved: ≥1 "Review" check passed
    /// </summary>
    public async Task<GateCheckResult> CheckGatesAsync(string taskId)
    {
        var task = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        var allEvidence = await _db.TaskEvidence
            .Where(e => e.TaskId == taskId)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync();

        var evidenceModels = allEvidence.Select(TaskSnapshotFactory.BuildTaskEvidence).ToList();
        var currentStatus = task.Status;
        string targetStatus;
        int requiredChecks;
        string requiredPhaseFilter;
        List<string> suggestedChecks;

        switch (currentStatus)
        {
            case "Active":
                targetStatus = "AwaitingValidation";
                requiredChecks = 1;
                requiredPhaseFilter = "After";
                suggestedChecks = new List<string> { "build", "tests", "type-check" };
                break;
            case "AwaitingValidation":
                targetStatus = "InReview";
                requiredChecks = 2;
                requiredPhaseFilter = "After";
                suggestedChecks = new List<string> { "build", "tests", "type-check", "lint" };
                break;
            case "InReview":
                targetStatus = "Approved";
                requiredChecks = 1;
                requiredPhaseFilter = "Review";
                suggestedChecks = new List<string> { "code-review" };
                break;
            default:
                targetStatus = "N/A";
                requiredChecks = 0;
                requiredPhaseFilter = "After";
                suggestedChecks = new List<string>();
                break;
        }

        var relevantPassed = allEvidence
            .Where(e => e.Phase == requiredPhaseFilter && e.Passed)
            .Select(e => e.CheckName)
            .Distinct()
            .ToList();

        var missingChecks = suggestedChecks
            .Where(s => !relevantPassed.Contains(s, StringComparer.OrdinalIgnoreCase))
            .ToList();

        _activity.Publish(ActivityEventType.GateChecked, task.RoomId, null, taskId,
            $"Gate check for {currentStatus} → {targetStatus}: {relevantPassed.Count}/{requiredChecks} checks passed" +
            (missingChecks.Count > 0 ? $". Missing: {string.Join(", ", missingChecks)}" : ""));

        return new GateCheckResult(
            TaskId: taskId,
            CurrentPhase: currentStatus,
            TargetPhase: targetStatus,
            Met: relevantPassed.Count >= requiredChecks,
            RequiredChecks: requiredChecks,
            PassedChecks: relevantPassed.Count,
            MissingChecks: missingChecks,
            Evidence: evidenceModels
        );
    }
}
