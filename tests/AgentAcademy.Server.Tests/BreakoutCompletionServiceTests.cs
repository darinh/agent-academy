using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentAcademy.Server.Tests;

public class BreakoutCompletionServiceTests
{
    private readonly IAgentExecutor _executor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentCatalogOptions _catalog;
    private readonly BreakoutCompletionService _service;

    private static AgentDefinition TestAgent(string id = "agent-1", string name = "TestAgent") =>
        new(Id: id, Name: name, Role: "SoftwareEngineer",
            Summary: "Test agent", StartupPrompt: "Go", Model: null,
            CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true);

    private static AgentDefinition ReviewerAgent(string id = "reviewer-1", string name = "Athena") =>
        new(Id: id, Name: name, Role: "Reviewer",
            Summary: "Reviewer agent", StartupPrompt: "Review", Model: null,
            CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true);

    public BreakoutCompletionServiceTests()
    {
        _executor = Substitute.For<IAgentExecutor>();
        _scopeFactory = Substitute.For<IServiceScopeFactory>();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Room",
            Agents:
            [
                TestAgent("engineer-1", "Hephaestus"),
                ReviewerAgent()
            ]);

        _service = new BreakoutCompletionService(
            _scopeFactory,
            _catalog,
            _executor,
            null!, // SpecManager — not reached in tested methods
            null!, // CommandPipeline — exception path fires before it's used
            null!, // AgentMemoryLoader — not reached in tested methods
            NullLogger<BreakoutCompletionService>.Instance);
    }

    // ── RunAgentAsync ────────────────────────────────────────────

    [Fact]
    public async Task RunAgentAsync_Success_ReturnsResponse()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, "do stuff", "room-1", null, Arg.Any<CancellationToken>())
            .Returns("all done");

        var result = await _service.RunAgentAsync(agent, "do stuff", "room-1");

        Assert.Equal("all done", result);
    }

    [Fact]
    public async Task RunAgentAsync_WithWorkspacePath_PassesThroughToExecutor()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, "build", "room-1", "/ws/tree", Arg.Any<CancellationToken>())
            .Returns("built");

        var result = await _service.RunAgentAsync(agent, "build", "room-1", "/ws/tree");

        Assert.Equal("built", result);
        await _executor.Received(1).RunAsync(agent, "build", "room-1", "/ws/tree", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAgentAsync_EmptyResponse_ReturnedAsIs()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, "ping", "room-1", null, Arg.Any<CancellationToken>())
            .Returns("");

        var result = await _service.RunAgentAsync(agent, "ping", "room-1");

        Assert.Equal("", result);
    }

    [Fact]
    public async Task RunAgentAsync_QuotaExceeded_ReturnsWarningMessage()
    {
        var agent = TestAgent(name: "Apollo");
        _executor.RunAsync(agent, "work", "room-1", null, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AgentQuotaExceededException(
                agent.Id, "requests_per_hour", "Rate limit exceeded", 60));

        var result = await _service.RunAgentAsync(agent, "work", "room-1");

        Assert.Contains("⚠️", result);
        Assert.Contains("paused", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAgentAsync_QuotaExceeded_WarningContainsAgentName()
    {
        var agent = TestAgent(name: "Apollo");
        _executor.RunAsync(agent, "work", "room-1", null, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AgentQuotaExceededException(
                agent.Id, "tokens_per_hour", "Token limit hit", 120));

        var result = await _service.RunAgentAsync(agent, "work", "room-1");

        Assert.Contains("Apollo", result);
    }

    [Fact]
    public async Task RunAgentAsync_QuotaExceeded_WarningContainsOriginalMessage()
    {
        var agent = TestAgent(name: "Apollo");
        var errorMessage = "Token limit hit — back off for a bit";
        _executor.RunAsync(agent, "work", "room-1", null, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AgentQuotaExceededException(
                agent.Id, "tokens_per_hour", errorMessage, 120));

        var result = await _service.RunAgentAsync(agent, "work", "room-1");

        Assert.Contains(errorMessage, result);
    }

    [Fact]
    public async Task RunAgentAsync_Cancelled_ReturnsEmptyString()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, "long task", "room-1", null, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var result = await _service.RunAgentAsync(agent, "long task", "room-1");

        Assert.Equal("", result);
    }

    [Fact]
    public async Task RunAgentAsync_TaskCancelled_ReturnsEmptyString()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, "long task", "room-1", null, Arg.Any<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException());

        var result = await _service.RunAgentAsync(agent, "long task", "room-1");

        Assert.Equal("", result);
    }

    [Fact]
    public async Task RunAgentAsync_OtherException_Propagates()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, "bad call", "room-1", null, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("something broke"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RunAgentAsync(agent, "bad call", "room-1"));
    }

    [Fact]
    public async Task RunAgentAsync_TimeoutException_Propagates()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, "slow", "room-1", null, Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("timed out"));

        await Assert.ThrowsAsync<TimeoutException>(
            () => _service.RunAgentAsync(agent, "slow", "room-1"));
    }

    // ── ProcessCommandsAsync ─────────────────────────────────────

    [Fact]
    public async Task ProcessCommandsAsync_WhenScopeCreationFails_ReturnsProcessingFailed()
    {
        var agent = TestAgent();
        _scopeFactory.CreateScope().Throws(new InvalidOperationException("DI disposed"));

        var result = await _service.ProcessCommandsAsync(agent, "response text", "room-1");

        Assert.True(result.ProcessingFailed);
    }

    [Fact]
    public async Task ProcessCommandsAsync_WhenScopeCreationFails_ReturnsOriginalResponseText()
    {
        var agent = TestAgent();
        const string original = "Here is my detailed response with code changes.";
        _scopeFactory.CreateScope().Throws(new ObjectDisposedException("scope"));

        var result = await _service.ProcessCommandsAsync(agent, original, "room-1");

        Assert.Equal(original, result.RemainingText);
    }

    [Fact]
    public async Task ProcessCommandsAsync_WhenScopeCreationFails_ReturnsEmptyCommandList()
    {
        var agent = TestAgent();
        _scopeFactory.CreateScope().Throws(new InvalidOperationException("boom"));

        var result = await _service.ProcessCommandsAsync(agent, "text", "room-1");

        Assert.NotNull(result.Results);
        Assert.Empty(result.Results);
    }

    [Fact]
    public async Task ProcessCommandsAsync_WhenScopeCreationFails_WithWorkingDirectory_StillReturnsFailedResult()
    {
        var agent = TestAgent();
        _scopeFactory.CreateScope().Throws(new InvalidOperationException("nope"));

        var result = await _service.ProcessCommandsAsync(agent, "text", "room-1", "/ws/tree");

        Assert.True(result.ProcessingFailed);
        Assert.Equal("text", result.RemainingText);
    }

    // ── Stopped flag ─────────────────────────────────────────────

    [Fact]
    public void Stopped_InitiallyFalse()
    {
        Assert.False(_service.Stopped);
    }

    [Fact]
    public void Stopped_CanBeSetToTrue()
    {
        _service.Stopped = true;

        Assert.True(_service.Stopped);
    }

    [Fact]
    public void Stopped_CanBeToggledBackToFalse()
    {
        _service.Stopped = true;
        _service.Stopped = false;

        Assert.False(_service.Stopped);
    }

    // ── Catalog wiring ───────────────────────────────────────────

    [Fact]
    public async Task RunAgentAsync_DifferentAgentDefinitions_EachCallsExecutorCorrectly()
    {
        var engineer = TestAgent("eng-1", "Hephaestus");
        var reviewer = ReviewerAgent("rev-1", "Athena");

        _executor.RunAsync(engineer, "build", "room-1", null, Arg.Any<CancellationToken>())
            .Returns("code written");
        _executor.RunAsync(reviewer, "review", "room-1", null, Arg.Any<CancellationToken>())
            .Returns("looks good");

        var r1 = await _service.RunAgentAsync(engineer, "build", "room-1");
        var r2 = await _service.RunAgentAsync(reviewer, "review", "room-1");

        Assert.Equal("code written", r1);
        Assert.Equal("looks good", r2);
    }

    [Fact]
    public async Task RunAgentAsync_QuotaExceeded_DifferentQuotaTypes_AllHandled()
    {
        var agent = TestAgent(name: "Hermes");
        var quotaTypes = new[] { "requests_per_hour", "tokens_per_hour", "cost_per_hour" };

        foreach (var quotaType in quotaTypes)
        {
            _executor.RunAsync(agent, "work", "room-1", null, Arg.Any<CancellationToken>())
                .ThrowsAsync(new AgentQuotaExceededException(
                    agent.Id, quotaType, $"Exceeded {quotaType}", 60));

            var result = await _service.RunAgentAsync(agent, "work", "room-1");

            Assert.Contains("Hermes", result);
            Assert.Contains("⚠️", result);
        }
    }
}
