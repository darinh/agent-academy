using AgentAcademy.Server.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentAcademy.Server.HealthChecks;

/// <summary>
/// Verifies the SQLite database is reachable by executing a lightweight query.
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly AgentAcademyDbContext _db;

    public DatabaseHealthCheck(AgentAcademyDbContext db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var canConnect = await _db.Database.CanConnectAsync(ct);
            return canConnect
                ? HealthCheckResult.Healthy("Database is reachable.")
                : HealthCheckResult.Unhealthy("Database connection failed.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database check threw an exception.", ex);
        }
    }
}
