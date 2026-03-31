using System.ComponentModel.DataAnnotations;

namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Records each server lifecycle event for crash detection and
/// instance identity tracking. See spec 011 (State Recovery).
/// </summary>
public class ServerInstanceEntity
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ShutdownAt { get; set; }

    public int? ExitCode { get; set; }

    public bool CrashDetected { get; set; }

    [Required]
    public string Version { get; set; } = "";
}
