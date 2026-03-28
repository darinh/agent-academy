using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public class StubExecutorTests
{
    private readonly StubExecutor _sut = new(NullLogger<StubExecutor>.Instance);

    private static AgentDefinition MakeAgent(string role, string id = "test-1", string name = "TestBot") =>
        new(
            Id: id,
            Name: name,
            Role: role,
            Summary: "Test agent",
            StartupPrompt: "You are a test agent.",
            Model: null,
            CapabilityTags: new List<string>(),
            EnabledTools: new List<string>(),
            AutoJoinDefaultRoom: true
        );

    [Fact]
    public void IsFullyOperational_ReturnsFalse()
    {
        Assert.False(_sut.IsFullyOperational);
    }

    [Theory]
    [InlineData("Planner")]
    [InlineData("Architect")]
    [InlineData("SoftwareEngineer")]
    [InlineData("Reviewer")]
    [InlineData("TechnicalWriter")]
    public async Task RunAsync_ReturnsNonEmptyResponse_ForKnownRoles(string role)
    {
        var agent = MakeAgent(role);
        var result = await _sut.RunAsync(agent, "Title: Build a widget", "room-1");

        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public async Task RunAsync_ReturnsResponse_ForUnknownRole()
    {
        var agent = MakeAgent("UnknownRole");
        var result = await _sut.RunAsync(agent, "Do something", null);

        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public async Task RunAsync_ThrowsOnCancellation()
    {
        var agent = MakeAgent("Planner");
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _sut.RunAsync(agent, "Hello", "room-1", cts.Token));
    }

    [Fact]
    public async Task InvalidateSessionAsync_DoesNotThrow()
    {
        await _sut.InvalidateSessionAsync("agent-1", "room-1");
        // No exception = pass
    }

    [Fact]
    public async Task InvalidateRoomSessionsAsync_DoesNotThrow()
    {
        await _sut.InvalidateRoomSessionsAsync("room-1");
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        await _sut.DisposeAsync();
    }

    [Fact]
    public async Task RunAsync_WithNullRoomId_ReturnsResponse()
    {
        var agent = MakeAgent("Architect");
        var result = await _sut.RunAsync(agent, "Title: Design API", null);

        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Theory]
    [InlineData("Planner")]
    [InlineData("Architect")]
    [InlineData("SoftwareEngineer")]
    [InlineData("Reviewer")]
    [InlineData("TechnicalWriter")]
    public async Task RunAsync_ResponseVariesBetweenCalls(string role)
    {
        // Run enough times that a random selection from 3-4 templates
        // should produce at least 2 distinct responses.
        var agent = MakeAgent(role);
        var responses = new HashSet<string>();
        for (int i = 0; i < 20; i++)
        {
            var result = await _sut.RunAsync(agent, "Title: test", "room-1");
            responses.Add(result);
        }

        // With 3+ templates and 20 draws, probability of getting only 1
        // unique response is astronomically low.
        Assert.True(responses.Count >= 2,
            $"Expected at least 2 distinct responses for role '{role}', got {responses.Count}");
    }
}

public class AgentExecutorInterfaceTests
{
    [Fact]
    public void StubExecutor_ImplementsIAgentExecutor()
    {
        var executor = new StubExecutor(NullLogger<StubExecutor>.Instance);
        Assert.IsAssignableFrom<IAgentExecutor>(executor);
    }

    [Fact]
    public void CopilotExecutor_ImplementsIAgentExecutor()
    {
        var executor = new CopilotExecutor(
            NullLogger<CopilotExecutor>.Instance,
            NullLogger<StubExecutor>.Instance,
            new ConfigurationBuilder().Build());
        Assert.IsAssignableFrom<IAgentExecutor>(executor);
    }
}
