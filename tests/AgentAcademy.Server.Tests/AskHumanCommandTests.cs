using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public class AskHumanCommandTests : IDisposable
{
    private readonly AskHumanHandler _handler = new();
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public AskHumanCommandTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddScoped<WorkspaceRuntime>();
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    #region Handler basics

    [Fact]
    public void CommandName_IsAskHuman()
    {
        Assert.Equal("ASK_HUMAN", _handler.CommandName);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenQuestionMissing()
    {
        var (command, context) = CreateCommandAndContext(new Dictionary<string, string>());

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("question", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenQuestionEmpty()
    {
        var (command, context) = CreateCommandAndContext(
            new Dictionary<string, string> { ["question"] = "   " });

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("question", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsSuccess_WhenQuestionSent()
    {
        var (command, context) = CreateCommandAndContext(
            new Dictionary<string, string> { ["question"] = "What database should I use?" },
            sendResult: true);

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.NotNull(result.Result);
        Assert.Equal("sent", result.Result["status"]);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenNoProviderConnected()
    {
        var (command, context) = CreateCommandAndContext(
            new Dictionary<string, string> { ["question"] = "What database?" },
            sendResult: false);

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Discord", result.Error!);
    }

    [Fact]
    public async Task ExecuteAsync_IncludesAgentNameInResult()
    {
        var (command, context) = CreateCommandAndContext(
            new Dictionary<string, string> { ["question"] = "Need help!" },
            sendResult: true,
            agentName: "Hephaestus");

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("Hephaestus", result.Result!["agentName"]);
    }

    #endregion

    #region Parser integration

    [Fact]
    public void CommandParser_RecognizesAskHuman()
    {
        var parser = new CommandParser();
        var text = "I need help with this.\n\nASK_HUMAN:\n  Question: What database should we use?";

        var parseResult = parser.Parse(text);

        Assert.Single(parseResult.Commands);
        Assert.Equal("ASK_HUMAN", parseResult.Commands[0].Command);
        Assert.Equal("What database should we use?", parseResult.Commands[0].Args["Question"]);
    }

    [Fact]
    public void CommandParser_RecognizesAskHuman_InlineFormat()
    {
        var parser = new CommandParser();
        var text = "ASK_HUMAN: question=What database?";

        var parseResult = parser.Parse(text);

        Assert.Single(parseResult.Commands);
        Assert.Equal("ASK_HUMAN", parseResult.Commands[0].Command);
    }

    #endregion

    #region Pipeline integration

    [Fact]
    public async Task Pipeline_ProcessesAskHumanCommand()
    {
        var notificationManager = CreateMockNotificationManager(sendResult: true);

        using var scope = _serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;

        // Build a service collection that includes our mock NotificationManager
        var pipelineServices = new ServiceCollection();
        pipelineServices.AddSingleton(notificationManager);
        pipelineServices.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        pipelineServices.AddSingleton<ActivityBroadcaster>();
        pipelineServices.AddScoped<WorkspaceRuntime>();
        pipelineServices.AddLogging();
        using var pipelineSp = pipelineServices.BuildServiceProvider();

        var handlers = new ICommandHandler[] { new AskHumanHandler() };
        var pipeline = new CommandPipeline(
            handlers,
            NullLogger<CommandPipeline>.Instance);

        var agentText = "Let me check with the human.\n\nASK_HUMAN:\n  Question: Should we use Redis or Memcached?";

        var agent = new AgentDefinition(
            "software-engineer-1", "Hephaestus", "SoftwareEngineer", "Test", "prompt",
            null, new List<string>(), new List<string>(), true, null,
            new CommandPermissionSet(new List<string> { "*" }, new List<string>()));

        using var pipelineScope = pipelineSp.CreateScope();
        var result = await pipeline.ProcessResponseAsync(
            "software-engineer-1", agentText, "room-1", agent, pipelineScope.ServiceProvider);

        Assert.True(result.Results.Count > 0);
        Assert.Equal(CommandStatus.Success, result.Results[0].Status);
    }

    #endregion

    #region Helpers

    private (CommandEnvelope command, CommandContext context) CreateCommandAndContext(
        Dictionary<string, string> args,
        bool sendResult = false,
        string agentId = "test-agent",
        string agentName = "TestAgent")
    {
        var notificationManager = CreateMockNotificationManager(sendResult);

        // Build a service provider that includes NotificationManager + WorkspaceRuntime
        var services = new ServiceCollection();
        services.AddSingleton(notificationManager);
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddScoped<WorkspaceRuntime>();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var scope = sp.CreateScope();

        var command = new CommandEnvelope(
            Command: "ASK_HUMAN",
            Args: args.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: $"cmd-{Guid.NewGuid():N}",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: agentId
        );

        var context = new CommandContext(
            AgentId: agentId,
            AgentName: agentName,
            AgentRole: "SoftwareEngineer",
            RoomId: "room-1",
            BreakoutRoomId: null,
            Services: scope.ServiceProvider
        );

        return (command, context);
    }

    private static NotificationManager CreateMockNotificationManager(bool sendResult)
    {
        var logger = Substitute.For<ILogger<NotificationManager>>();
        var manager = new NotificationManager(logger);

        if (sendResult)
        {
            var mockProvider = Substitute.For<INotificationProvider>();
            mockProvider.ProviderId.Returns("discord");
            mockProvider.DisplayName.Returns("Discord");
            mockProvider.IsConfigured.Returns(true);
            mockProvider.IsConnected.Returns(true);
            mockProvider.SendAgentQuestionAsync(Arg.Any<AgentQuestion>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(true));
            manager.RegisterProvider(mockProvider);
        }

        return manager;
    }

    #endregion
}
