using AgentAcademy.Forge.Execution;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Adapts forge lifecycle/progress signals into server activity events.
/// </summary>
public interface IForgeActivityEventAdapter
{
    void PublishJobQueued(string jobId, string message);
    void PublishJobStarted(string jobId, string message);
    void PublishJobFinished(string jobId, string message, string outcome, string runId);
    void PublishProgress(string jobId, ForgeProgressEvent evt);
}
