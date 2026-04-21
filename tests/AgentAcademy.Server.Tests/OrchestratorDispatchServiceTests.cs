using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public sealed class OrchestratorDispatchServiceTests
{
    [Fact]
    public async Task DispatchAsync_WithTargetAgent_RoutesDirectMessage()
    {
        var roundRunner = Substitute.For<IConversationRoundRunner>();
        var dmRouter = Substitute.For<IDirectMessageRouter>();
        var sut = new OrchestratorDispatchService(roundRunner, dmRouter);

        await sut.DispatchAsync("room-1", "agent-1", CancellationToken.None);

        await dmRouter.Received(1).RouteAsync("agent-1");
        await roundRunner.DidNotReceive().RunRoundsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_WithoutTargetAgent_RunsConversationRounds()
    {
        var roundRunner = Substitute.For<IConversationRoundRunner>();
        var dmRouter = Substitute.For<IDirectMessageRouter>();
        var sut = new OrchestratorDispatchService(roundRunner, dmRouter);
        var ct = new CancellationTokenSource().Token;

        await sut.DispatchAsync("room-1", null, ct);

        await roundRunner.Received(1).RunRoundsAsync("room-1", ct);
        await dmRouter.DidNotReceive().RouteAsync(Arg.Any<string>());
    }
}
