using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for GENERATE_DIGEST command handler.
/// Verifies manual digest triggering via the command system.
/// </summary>
public sealed class GenerateDigestHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly IAgentExecutor _executor;
    private readonly LearningDigestService _digestService;
    private readonly GenerateDigestHandler _handler = new();

    private static readonly AgentCatalogOptions TestCatalog = new(
        DefaultRoomId: "main",
        DefaultRoomName: "Main Room",
        Agents: new List<AgentDefinition>
        {
            new("planner-1", "Aristotle", "Planner", "Planner", "prompt", null,
                new(), new(), true,
                Permissions: new CommandPermissionSet(["REMEMBER", "LIST_*", "GENERATE_DIGEST"], [])),
            new("engineer-1", "Hephaestus", "SoftwareEngineer", "Engineer", "prompt", null,
                new(), new(), true,
                Permissions: new CommandPermissionSet(["REMEMBER"], []))
        }
    );

    public GenerateDigestHandlerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _executor = Substitute.For<IAgentExecutor>();
        _executor.IsFullyOperational.Returns(true);

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton(TestCatalog);
        services.AddSingleton<IAgentCatalog>(TestCatalog);
        services.AddSingleton(_executor);
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<IActivityBroadcaster>(sp => sp.GetRequiredService<ActivityBroadcaster>());
        services.AddSingleton<MessageBroadcaster>();
        services.AddSingleton<IMessageBroadcaster>(sp => sp.GetRequiredService<MessageBroadcaster>());
        services.AddScoped<ActivityPublisher>();
        services.AddScoped<IActivityPublisher>(sp => sp.GetRequiredService<ActivityPublisher>());
        services.AddScoped<TaskQueryService>();
        services.AddScoped<ITaskQueryService>(sp => sp.GetRequiredService<TaskQueryService>());
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<ITaskLifecycleService>(sp => sp.GetRequiredService<TaskLifecycleService>());
        services.AddScoped<TaskDependencyService>();
        services.AddScoped<ITaskDependencyService>(sp => sp.GetRequiredService<TaskDependencyService>());
        services.AddScoped<MessageService>();
        services.AddScoped<IMessageService>(sp => sp.GetRequiredService<MessageService>());
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<ISystemSettingsService>(sp => sp.GetRequiredService<SystemSettingsService>());
        services.AddSingleton<CommandRateLimiter>();
        services.AddSingleton<CommandPipeline>();
        services.AddSingleton<CommandParser>();
        services.AddSingleton<ICommandHandler, RememberHandler>();
        services.AddSingleton<CommandAuthorizer>();
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        _digestService = new LearningDigestService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            TestCatalog,
            _executor, new TestDoubles.NoOpWatchdogAgentRunner(_executor),
            _serviceProvider.GetRequiredService<CommandPipeline>(),
            NullLogger<LearningDigestService>.Instance);

        services.AddSingleton(_digestService);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

        SeedRoom(db);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private static void SeedRoom(AgentAcademyDbContext db)
    {
        db.Rooms.Add(new RoomEntity
        {
            Id = "main",
            Name = "Main Room",
            Status = "Idle",
            CurrentPhase = "Intake",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private CommandContext MakeContext()
    {
        var scope = _serviceProvider.CreateScope();
        // Register the digest service in the scope so the handler can resolve it
        return new CommandContext(
            AgentId: "planner-1",
            AgentName: "Aristotle",
            AgentRole: "Planner",
            RoomId: "main",
            BreakoutRoomId: null,
            Services: new DigestServiceProvider(_serviceProvider.CreateScope().ServiceProvider, _digestService));
    }

    private static CommandEnvelope MakeEnvelope(Dictionary<string, object?>? args = null)
        => new(
            Command: "GENERATE_DIGEST",
            Args: args ?? new Dictionary<string, object?>(),
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: $"cmd-{Guid.NewGuid():N}",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: "planner-1");

    [Fact]
    public void CommandName_Is_GENERATE_DIGEST()
    {
        Assert.Equal("GENERATE_DIGEST", _handler.CommandName);
    }

    [Fact]
    public void IsRetrySafe_Is_False()
    {
        Assert.False(_handler.IsRetrySafe);
    }

    [Fact]
    public void IsDestructive_Is_False()
    {
        ICommandHandler handler = _handler;
        Assert.False(handler.IsDestructive);
    }

    [Fact]
    public async Task Returns_Success_With_Generated_False_When_Below_Threshold()
    {
        var context = MakeContext();
        var result = await _handler.ExecuteAsync(MakeEnvelope(), context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(false, dict["generated"]);
        Assert.Contains("threshold", dict["message"]!.ToString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Force_True_With_No_Retrospectives_Returns_Generated_False()
    {
        var context = MakeContext();
        var args = new Dictionary<string, object?> { ["force"] = true };
        var result = await _handler.ExecuteAsync(MakeEnvelope(args), context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(false, dict["generated"]);
    }

    [Fact]
    public async Task Force_True_String_Parsed_Correctly()
    {
        var context = MakeContext();
        var args = new Dictionary<string, object?> { ["force"] = "true" };
        var result = await _handler.ExecuteAsync(MakeEnvelope(args), context);

        // Should succeed (force=true parsed) even if no retrospectives
        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task Force_False_String_Parsed_Correctly()
    {
        var context = MakeContext();
        var args = new Dictionary<string, object?> { ["force"] = "false" };
        var result = await _handler.ExecuteAsync(MakeEnvelope(args), context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(false, dict["generated"]);
        Assert.Contains("threshold", dict["message"]!.ToString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Force_With_Retrospectives_Triggers_Digest()
    {
        // Seed retrospective comments
        SeedRetrospectives(3);

        _executor.RunAsync(Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("REMEMBER:\n  Category: shared\n  Key: test-learning\n  Value: Tests are important");

        var context = MakeContext();
        var args = new Dictionary<string, object?> { ["force"] = true };
        var result = await _handler.ExecuteAsync(MakeEnvelope(args), context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(true, dict["generated"]);
        Assert.NotNull(dict["digestId"]);
    }

    [Fact]
    public async Task Without_Force_Requires_Threshold_Met()
    {
        // Seed below default threshold (5)
        SeedRetrospectives(3);

        var context = MakeContext();
        var result = await _handler.ExecuteAsync(MakeEnvelope(), context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(false, dict["generated"]);
    }

    [Fact]
    public async Task Without_Force_Generates_When_Threshold_Met()
    {
        // Seed at default threshold (5)
        SeedRetrospectives(5);

        _executor.RunAsync(Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("Learning: build patterns are key");

        var context = MakeContext();
        var result = await _handler.ExecuteAsync(MakeEnvelope(), context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(true, dict["generated"]);
    }

    [Fact]
    public async Task Result_Contains_DigestId_When_Generated()
    {
        SeedRetrospectives(5);

        _executor.RunAsync(Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("Some summary response");

        var context = MakeContext();
        var result = await _handler.ExecuteAsync(MakeEnvelope(), context);

        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.True(dict.ContainsKey("digestId"));
        var digestId = Assert.IsType<int>(dict["digestId"]);
        Assert.True(digestId > 0);
    }

    [Fact]
    public async Task No_Args_Defaults_Force_To_False()
    {
        // No retrospectives, no force arg → should hit threshold check
        var context = MakeContext();
        var result = await _handler.ExecuteAsync(MakeEnvelope(new Dictionary<string, object?>()), context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(false, dict["generated"]);
        Assert.Contains("threshold", dict["message"]!.ToString()!, StringComparison.OrdinalIgnoreCase);
    }

    private void SeedRetrospectives(int count)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        for (int i = 0; i < count; i++)
        {
            var taskId = $"T-retro-{i}";
            db.Tasks.Add(new TaskEntity
            {
                Id = taskId,
                Title = $"Test Task {i}",
                Description = "Test",
                Status = TaskStatus.Completed.ToString(),
                Type = "Feature",
                RoomId = "main",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            db.TaskComments.Add(new TaskCommentEntity
            {
                Id = $"retro-comment-{i}",
                TaskId = taskId,
                AgentId = "engineer-1",
                AgentName = "Hephaestus",
                CommentType = nameof(TaskCommentType.Retrospective),
                Content = $"Retrospective #{i}: Learned that testing is crucial for task {i}.",
                CreatedAt = DateTime.UtcNow.AddMinutes(-count + i)
            });
        }
        db.SaveChanges();
    }

    /// <summary>
    /// Wraps an IServiceProvider to inject LearningDigestService into resolution.
    /// Command handlers use context.Services.GetRequiredService, so we need
    /// the digest service available there.
    /// </summary>
    private sealed class DigestServiceProvider : IServiceProvider
    {
        private readonly IServiceProvider _inner;
        private readonly LearningDigestService _digestService;

        public DigestServiceProvider(IServiceProvider inner, LearningDigestService digestService)
        {
            _inner = inner;
            _digestService = digestService;
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ILearningDigestService) || serviceType == typeof(LearningDigestService))
                return _digestService;
            return _inner.GetService(serviceType);
        }
    }
}
