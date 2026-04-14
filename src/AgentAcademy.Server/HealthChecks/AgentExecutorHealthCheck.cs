using AgentAcademy.Server.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentAcademy.Server.HealthChecks;

/// <summary>
/// Reports the operational status of the agent executor (Copilot SDK connection).
/// Auth failure is degraded (not unhealthy) — the server still functions, just without
/// LLM capability.
/// </summary>
public sealed class AgentExecutorHealthCheck : IHealthCheck
{
    private readonly IAgentExecutor _executor;

    public AgentExecutorHealthCheck(IAgentExecutor executor) => _executor = executor;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        var data = new Dictionary<string, object>
        {
            ["operational"] = _executor.IsFullyOperational,
            ["authFailed"] = _executor.IsAuthFailed,
            ["circuitBreaker"] = _executor.CircuitBreakerState.ToString(),
        };

        if (_executor.IsAuthFailed)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "Agent executor authentication failed. LLM features unavailable.", data: data));
        }

        if (!_executor.IsFullyOperational)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "Agent executor is not fully operational (stub or initializing).", data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            "Agent executor is operational.", data: data));
    }
}
