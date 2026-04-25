using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Default <see cref="ICostGuard"/> — never halts. Holds the DI slot for the
/// real cost-anomaly-detection implementation described in
/// <c>specs/100-product-vision/cost-tracking-design.md</c>.
/// </summary>
public sealed class NoOpCostGuard : ICostGuard
{
    public Task<bool> ShouldHaltAsync(SprintEntity sprint, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
