using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentAcademy.Server.HealthChecks;

/// <summary>
/// Writes a detailed JSON response for the /health endpoint.
/// </summary>
public static class HealthCheckResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var response = new HealthResponse(
            Status: report.Status.ToString(),
            TotalDuration: report.TotalDuration.TotalMilliseconds,
            Timestamp: DateTime.UtcNow,
            Checks: report.Entries.Select(e => new HealthCheckEntry(
                Name: e.Key,
                Status: e.Value.Status.ToString(),
                Description: e.Value.Description,
                Duration: e.Value.Duration.TotalMilliseconds,
                Data: e.Value.Data?.Count > 0
                    ? e.Value.Data.ToDictionary(d => d.Key, d => d.Value)
                    : null,
                Exception: null // Never leak internal exception details on an anonymous endpoint
            )).ToList()
        );

        await context.Response.WriteAsJsonAsync(response, JsonOptions);
    }

    private record HealthResponse(
        string Status,
        double TotalDuration,
        DateTime Timestamp,
        List<HealthCheckEntry> Checks);

    private record HealthCheckEntry(
        string Name,
        string Status,
        string? Description,
        double Duration,
        Dictionary<string, object>? Data,
        string? Exception);
}
