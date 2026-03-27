using AgentAcademy.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Fallback executor that returns canned, role-based responses when
/// the Copilot SDK is unavailable. Allows the rest of the system to
/// operate without a live LLM connection.
/// </summary>
public sealed class StubExecutor : IAgentExecutor
{
    private static readonly Random Rng = Random.Shared;
    private readonly ILogger<StubExecutor> _logger;

    public StubExecutor(ILogger<StubExecutor> logger)
    {
        _logger = logger;
        _logger.LogWarning("StubExecutor active — agents will return canned responses");
    }

    public bool IsFullyOperational => false;

    public Task<string> RunAsync(
        AgentDefinition agent,
        string prompt,
        string? roomId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var response = GenerateRoleResponse(agent, prompt);
        _logger.LogDebug(
            "Stub response for {AgentId} ({Role}) in room {RoomId}: {Length} chars",
            agent.Id, agent.Role, roomId ?? "none", response.Length);

        return Task.FromResult(response);
    }

    public Task InvalidateSessionAsync(string agentId, string? roomId)
    {
        // No sessions to invalidate in stub mode.
        return Task.CompletedTask;
    }

    public Task InvalidateRoomSessionsAsync(string roomId)
    {
        // No sessions to invalidate in stub mode.
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        // Nothing to dispose.
        return Task.CompletedTask;
    }

    private static string GenerateRoleResponse(AgentDefinition agent, string prompt)
    {
        var templates = GetTemplatesForRole(agent.Role);
        var template = templates[Rng.Next(templates.Length)];

        var taskLine = prompt
            .Split('\n')
            .FirstOrDefault(l =>
                l.StartsWith("Title:", StringComparison.OrdinalIgnoreCase) ||
                l.StartsWith("Description:", StringComparison.OrdinalIgnoreCase))
            ?? "";

        return template
            .Replace("{name}", agent.Name)
            .Replace("{role}", agent.Role)
            .Replace("{context}", taskLine.Trim());
    }

    private static string[] GetTemplatesForRole(string role) => role switch
    {
        "Planner" => new[]
        {
            "Looking at this task, I suggest we break it into three phases: (1) define the contracts and interfaces, (2) implement the core logic, and (3) add tests and validation. Let me know if anyone sees gaps in this sequencing.",
            "Based on what we know so far, the key dependencies are clear. I propose we start with the interface definitions so Architect and SoftwareEngineer can work in parallel. Reviewer and Validator — please flag any scope concerns early.",
            "Let me summarize where we are: the design direction is set, implementation steps are outlined, and we need validation criteria before we can call this complete. Validator — can you define what 'done' looks like here?",
            "I see a dependency risk: we should confirm the API contract before starting implementation. Architect-1, can you propose the interface shape? SoftwareEngineer-1, please hold on implementation until we have that.",
        },
        "Architect" => new[]
        {
            "From an architecture perspective, I recommend keeping this simple: a single service class behind an interface, with dependency injection for any external checks. Avoid over-abstracting at this stage.",
            "I'd suggest we use the standard ASP.NET health check pattern here — implement `IHealthCheck` for each dependency, register them in DI, and expose via `/healthz`. This gives us a clean, extensible foundation.",
            "One concern: if we couple the health check logic directly to specific services, it becomes hard to test. I recommend an abstraction layer so we can mock dependencies in unit tests. SoftwareEngineer — thoughts on this approach?",
            "The proposed design looks solid. I'd add one consideration: we should think about caching health check results with a short TTL to avoid hammering downstream services on every request.",
        },
        "SoftwareEngineer" => new[]
        {
            "I can implement this as follows: create an `IHealthCheckService` interface with a `CheckHealthAsync` method, implement it with individual dependency checks, and wire it into the DI container. I'll expose the results through a minimal API endpoint.",
            "For the implementation, I'll use `System.Diagnostics.Stopwatch` for uptime tracking, a `ConcurrentDictionary` for dependency status caching, and return structured JSON from the health endpoint. Should be straightforward.",
            "Building on the Architect's recommendation, I'll implement the `IHealthCheck` pattern. Each check will have a timeout and return a structured result with status, duration, and optional error details.",
            "I've outlined the code structure: one model class for the health response, one service for aggregating checks, and one endpoint. I'll keep it minimal and testable. Reviewer — any concerns before I proceed?",
        },
        "Reviewer" => new[]
        {
            "A few concerns: (1) make sure health check timeouts don't block the main request pipeline, (2) avoid leaking internal service details in the response, and (3) consider what happens when a dependency check itself throws. Error handling needs to be explicit.",
            "The approach looks reasonable, but I'd flag one risk: if we're checking multiple dependencies sequentially, the endpoint latency will be the sum of all checks. Consider running them in parallel with `Task.WhenAll`.",
            "I reviewed the proposed structure. It's clean, but I want to make sure we handle the edge case where the health check endpoint itself becomes a performance bottleneck under load. A caching layer or rate limit would help.",
            "One thing I'd push back on: the current plan doesn't mention logging. If a health check fails, we should log it with structured fields so operators can diagnose issues. SoftwareEngineer — can you add that?",
        },
        "TechnicalWriter" => new[]
        {
            "SPEC CHANGE PROPOSAL:\nTask: Health Check Implementation\nAffected Sections:\n- specs/005-services/spec.md: HealthCheckService behavior\n- specs/004-api-contracts/spec.md: /healthz endpoint contract\nChange Type: NEW_CAPABILITY\nProposed Changes:\n- 005-services: Add HealthCheckService section documenting dependency checks and degraded status\n- 004-api-contracts: Add /healthz endpoint with response shape and status codes\nVerification Plan: Confirm endpoint exists, returns expected shape, handles timeouts",
            "I've reviewed the current spec against the proposed changes. The following sections need updates:\n- specs/002-collaboration-lifecycle: Phase transition logic needs to reflect the new quality gate behavior\n- specs/CHANGELOG.md: Entry for this task\nAll other spec sections remain accurate. I'll update the affected files during implementation.",
            "Spec verification complete. I compared the delivered implementation against specs/005-services/spec.md and confirmed: all service contracts match the actual code. No spec-code divergences found. CHANGELOG.md updated with task reference.",
        },
        _ => new[]
        {
            "I've reviewed the current state and I think we're on the right track. Let me know if there's anything specific I should focus on.",
            "PASS",
        },
    };
}
