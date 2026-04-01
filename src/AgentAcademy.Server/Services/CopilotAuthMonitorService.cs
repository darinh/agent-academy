namespace AgentAcademy.Server.Services;

internal sealed class CopilotAuthMonitorService : BackgroundService
{
    internal static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(5);

    private readonly ICopilotAuthProbe _probe;
    private readonly IAgentExecutor _executor;
    private readonly ILogger<CopilotAuthMonitorService> _logger;

    public CopilotAuthMonitorService(
        ICopilotAuthProbe probe,
        IAgentExecutor executor,
        ILogger<CopilotAuthMonitorService> logger)
    {
        _probe = probe;
        _executor = executor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ProbeInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            await ProbeOnceAsync(stoppingToken);

            if (!await timer.WaitForNextTickAsync(stoppingToken))
                break;
        }
    }

    internal async Task ProbeOnceAsync(CancellationToken ct = default)
    {
        var result = await _probe.ProbeAsync(ct);
        switch (result)
        {
            case CopilotAuthProbeResult.Healthy:
                await _executor.MarkAuthOperationalAsync(ct);
                break;
            case CopilotAuthProbeResult.AuthFailed:
                await _executor.MarkAuthDegradedAsync(ct);
                break;
            case CopilotAuthProbeResult.TransientFailure:
                _logger.LogDebug("Ignoring transient Copilot auth probe failure");
                break;
            case CopilotAuthProbeResult.Skipped:
                _logger.LogDebug("Skipping Copilot auth probe transition because no token is available");
                break;
        }
    }
}
