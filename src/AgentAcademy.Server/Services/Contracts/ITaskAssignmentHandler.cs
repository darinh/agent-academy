using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Handles the full task assignment pipeline: permission gating, branch
/// creation, worktree setup, breakout room creation, task item creation,
/// and cleanup on failure.
///
/// Registered as a singleton. Resolves scoped services from
/// <c>IServiceScopeFactory</c> per operation.
/// </summary>
/// <remarks>
/// <c>ParsedTaskAssignment</c> is currently internal to the Services namespace.
/// During Phase 2 migration, consider making it public or defining the
/// assignment contract on this interface directly.
/// </remarks>
public interface ITaskAssignmentHandler
{
    /// <summary>
    /// Gates and processes a full task assignment from an agent response.
    /// Validates agent permissions (Planners can create any type; non-Planners
    /// only Bug; others become proposals), then delegates to the assignment
    /// pipeline for branch/worktree/breakout creation.
    /// </summary>
    /// <param name="scope">Service scope for resolving scoped dependencies.</param>
    /// <param name="requestedBy">Agent definition of the requesting agent.</param>
    /// <param name="roomId">Room where the assignment was requested.</param>
    /// <param name="assignment">Parsed task assignment from the agent response.</param>
    Task ProcessAssignmentAsync(
        IServiceScope scope,
        AgentDefinition requestedBy,
        string roomId,
        ParsedTaskAssignment assignment);
}
