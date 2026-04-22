using AgentAcademy.Forge.Execution;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Tests;

public sealed class ForgeActivityEventAdapterTests
{
    private readonly ActivityBroadcaster _bus = new();
    private readonly ForgeActivityEventAdapter _sut;

    public ForgeActivityEventAdapterTests()
    {
        _sut = new ForgeActivityEventAdapter(_bus);
    }

    private ActivityEvent LastBroadcast() => _bus.GetRecentActivity()[^1];

    // ── PublishJobQueued ─────────────────────────────────────────────

    [Fact]
    public void PublishJobQueued_BroadcastsCorrectType()
    {
        _sut.PublishJobQueued("job-1", "Queued for execution");

        var evt = LastBroadcast();
        Assert.Equal(ActivityEventType.ForgeJobQueued, evt.Type);
        Assert.Equal("Queued for execution", evt.Message);
        Assert.Equal("job-1", evt.CorrelationId);
    }

    [Fact]
    public void PublishJobQueued_MetadataContainsJobId()
    {
        _sut.PublishJobQueued("job-2", "msg");

        var evt = LastBroadcast();
        Assert.NotNull(evt.Metadata);
        Assert.Equal("job-2", evt.Metadata!["jobId"]);
    }

    // ── PublishJobStarted ────────────────────────────────────────────

    [Fact]
    public void PublishJobStarted_BroadcastsCorrectType()
    {
        _sut.PublishJobStarted("job-3", "Starting pipeline");

        var evt = LastBroadcast();
        Assert.Equal(ActivityEventType.ForgeJobStarted, evt.Type);
        Assert.Equal("Starting pipeline", evt.Message);
    }

    // ── PublishJobFinished ───────────────────────────────────────────

    [Fact]
    public void PublishJobFinished_Succeeded_BroadcastsCompleted()
    {
        _sut.PublishJobFinished("job-4", "Done", "succeeded", "run-abc");

        var evt = LastBroadcast();
        Assert.Equal(ActivityEventType.ForgeJobCompleted, evt.Type);
        Assert.Equal("run-abc", evt.Metadata!["runId"]);
        Assert.Equal("succeeded", evt.Metadata!["outcome"]);
    }

    [Fact]
    public void PublishJobFinished_Failed_BroadcastsFailed()
    {
        _sut.PublishJobFinished("job-5", "Error occurred", "failed", "run-def");

        var evt = LastBroadcast();
        Assert.Equal(ActivityEventType.ForgeJobFailed, evt.Type);
        Assert.Equal("failed", evt.Metadata!["outcome"]);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("timeout")]
    [InlineData("cancelled")]
    public void PublishJobFinished_NonSucceeded_MapToFailed(string outcome)
    {
        _sut.PublishJobFinished("job-6", "msg", outcome, "run-x");

        Assert.Equal(ActivityEventType.ForgeJobFailed, LastBroadcast().Type);
    }

    // ── PublishProgress ──────────────────────────────────────────────

    [Fact]
    public void PublishProgress_WaveStarted_BroadcastsPhaseStarted()
    {
        var progress = new ForgeProgressEvent
        {
            RunId = "run-1",
            Kind = ForgeProgressKind.WaveStarted,
            PhaseId = "design",
            Message = "Wave 1 started",
            Wave = 1
        };

        _sut.PublishProgress("job-7", progress);

        var evt = LastBroadcast();
        Assert.Equal(ActivityEventType.ForgePhaseStarted, evt.Type);
        Assert.Equal("Wave 1 started", evt.Message);
        Assert.Equal("run-1", evt.Metadata!["runId"]);
        Assert.Equal("design", evt.Metadata!["phaseId"]);
        Assert.Equal(1, evt.Metadata!["wave"]);
    }

    [Fact]
    public void PublishProgress_PhaseCompleted_BroadcastsPhaseCompleted()
    {
        var progress = new ForgeProgressEvent
        {
            RunId = "run-2",
            Kind = ForgeProgressKind.PhaseCompleted,
            PhaseId = "review"
        };

        _sut.PublishProgress("job-8", progress);

        var evt = LastBroadcast();
        Assert.Equal(ActivityEventType.ForgePhaseCompleted, evt.Type);
        Assert.Contains("review", evt.Message);
    }

    [Fact]
    public void PublishProgress_PhaseFailed_BroadcastsPhaseFailed()
    {
        var progress = new ForgeProgressEvent
        {
            RunId = "run-3",
            Kind = ForgeProgressKind.PhaseFailed,
            PhaseId = "implement"
        };

        _sut.PublishProgress("job-9", progress);

        Assert.Equal(ActivityEventType.ForgePhaseFailed, LastBroadcast().Type);
    }

    [Fact]
    public void PublishProgress_PhaseStarted_BroadcastsPhaseStarted()
    {
        var progress = new ForgeProgressEvent
        {
            RunId = "run-4",
            Kind = ForgeProgressKind.PhaseStarted,
            PhaseId = "design"
        };

        _sut.PublishProgress("job-10", progress);

        Assert.Equal(ActivityEventType.ForgePhaseStarted, LastBroadcast().Type);
    }

    [Theory]
    [InlineData(ForgeProgressKind.RunStarted)]
    [InlineData(ForgeProgressKind.FidelityCompleted)]
    [InlineData(ForgeProgressKind.ControlCompleted)]
    [InlineData(ForgeProgressKind.RunCompleted)]
    [InlineData(ForgeProgressKind.RunFailed)]
    public void PublishProgress_UnmappedKind_DoesNotBroadcast(ForgeProgressKind kind)
    {
        var progress = new ForgeProgressEvent
        {
            RunId = "run-5",
            Kind = kind,
            PhaseId = "any"
        };

        _sut.PublishProgress("job-11", progress);

        Assert.Empty(_bus.GetRecentActivity());
    }

    [Fact]
    public void PublishProgress_NullPhaseId_UsesUnknownFallback()
    {
        var progress = new ForgeProgressEvent
        {
            RunId = "run-6",
            Kind = ForgeProgressKind.PhaseCompleted,
            PhaseId = null
        };

        _sut.PublishProgress("job-12", progress);

        var evt = LastBroadcast();
        Assert.Contains("unknown", evt.Message);
    }

    // ── Common event properties ──────────────────────────────────────

    [Fact]
    public void AllEvents_HaveUniqueIds()
    {
        _sut.PublishJobQueued("j1", "m1");
        _sut.PublishJobStarted("j2", "m2");

        var events = _bus.GetRecentActivity();
        Assert.Equal(2, events.Count);
        Assert.NotEqual(events[0].Id, events[1].Id);
    }

    [Fact]
    public void AllEvents_HaveInfoSeverity()
    {
        _sut.PublishJobQueued("j1", "m1");
        Assert.Equal(ActivitySeverity.Info, LastBroadcast().Severity);
    }

    [Fact]
    public void AllEvents_HaveNullRoomAndActor()
    {
        _sut.PublishJobQueued("j1", "m1");
        var evt = LastBroadcast();
        Assert.Null(evt.RoomId);
        Assert.Null(evt.ActorId);
    }
}
