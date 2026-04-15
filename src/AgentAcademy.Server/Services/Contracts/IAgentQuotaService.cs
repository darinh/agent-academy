using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Enforces per-agent resource quotas (requests/hour, tokens/hour, cost/hour).
/// Throws <see cref="AgentQuotaExceededException"/> when a limit is breached.
/// </summary>
public interface IAgentQuotaService
{
    /// <summary>
    /// Checks whether the agent is allowed to make another LLM call.
    /// If allowed, atomically records the request in the sliding window.
    /// Throws <see cref="AgentQuotaExceededException"/> if denied.
    /// </summary>
    Task EnforceQuotaAsync(string agentId);

    /// <summary>
    /// Returns the current quota status for an agent (for API/UI display).
    /// </summary>
    Task<QuotaStatus> GetStatusAsync(string agentId);

    /// <summary>
    /// Invalidates the cached quota config for an agent. Call after
    /// quota settings are updated via the API.
    /// </summary>
    void InvalidateCache(string agentId);
}
