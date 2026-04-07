namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for a structured verification check attached to a task.
/// Maps to the "task_evidence" table.
/// </summary>
public class TaskEvidenceEntity
{
    public string Id { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;

    /// <summary>Phase of the check: baseline, after, or review.</summary>
    public string Phase { get; set; } = "after";

    /// <summary>Descriptive name of the check (e.g. "build", "tests", "type-check", "review-socrates").</summary>
    public string CheckName { get; set; } = string.Empty;

    /// <summary>Tool or method used (e.g. "bash", "manual", "ide-diagnostics").</summary>
    public string Tool { get; set; } = string.Empty;

    /// <summary>Command that was run, if applicable.</summary>
    public string? Command { get; set; }

    /// <summary>Exit code of the command, if applicable.</summary>
    public int? ExitCode { get; set; }

    /// <summary>Truncated output or summary of the check result.</summary>
    public string? OutputSnippet { get; set; }

    /// <summary>Whether the check passed (true) or failed (false).</summary>
    public bool Passed { get; set; }

    /// <summary>Agent who recorded this evidence.</summary>
    public string AgentId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    // Navigation
    public TaskEntity? Task { get; set; }
}
