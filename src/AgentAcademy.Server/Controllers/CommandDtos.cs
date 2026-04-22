using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace AgentAcademy.Server.Controllers;

public sealed record ExecuteCommandRequest(
    [Required, MinLength(1), StringLength(10_000)] string Command,
    Dictionary<string, JsonElement>? Args);

public sealed record ExecuteCommandResponse(
    string Command,
    string Status,
    object? Result,
    string? Error,
    string? ErrorCode,
    string CorrelationId,
    DateTime Timestamp,
    string ExecutedBy);

public sealed record AuditLogEntry(
    string Id,
    string CorrelationId,
    string AgentId,
    string? Source,
    string Command,
    string Status,
    string? ErrorMessage,
    string? ErrorCode,
    string? RoomId,
    DateTime Timestamp);

public sealed record AuditLogResponse(
    List<AuditLogEntry> Records,
    int Total,
    int Limit,
    int Offset);

public sealed record AuditStatsResponse(
    int TotalCommands,
    Dictionary<string, int> ByStatus,
    Dictionary<string, int> ByAgent,
    Dictionary<string, int> ByCommand,
    int? WindowHours);
