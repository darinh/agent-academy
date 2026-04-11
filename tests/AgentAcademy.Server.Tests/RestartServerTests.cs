using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public class RestartServerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _sp;
    private readonly RestartServerHandler _handler;
    private readonly IHostApplicationLifetime _lifetime;

    public RestartServerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _lifetime = Substitute.For<IHostApplicationLifetime>();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(new AgentCatalogOptions("main", "Main Room",
            new List<AgentDefinition>()));
        services.AddSingleton<ILogger<TaskQueryService>>(NullLogger<TaskQueryService>.Instance);
        services.AddSingleton<ILogger<TaskLifecycleService>>(NullLogger<TaskLifecycleService>.Instance);
        services.AddSingleton<ILogger<WorkspaceRuntime>>(NullLogger<WorkspaceRuntime>.Instance);
        services.AddScoped<TaskQueryService>();
        services.AddScoped<TaskLifecycleService>();
        services.AddSingleton<ILogger<MessageService>>(NullLogger<MessageService>.Instance);
        services.AddScoped<MessageService>();
        services.AddSingleton<ILogger<BreakoutRoomService>>(NullLogger<BreakoutRoomService>.Instance);
        services.AddScoped<BreakoutRoomService>();
        services.AddSingleton<ILogger<TaskItemService>>(NullLogger<TaskItemService>.Instance);
        services.AddSingleton<ILogger<RoomService>>(NullLogger<RoomService>.Instance);
        services.AddScoped<TaskItemService>();
        services.AddScoped<RoomService>();
        services.AddScoped<WorkspaceRuntime>();
        services.AddScoped<SystemSettingsService>();
        services.AddSingleton<IAgentExecutor>(NSubstitute.Substitute.For<IAgentExecutor>());
        services.AddSingleton<ILogger<ConversationSessionService>>(NullLogger<ConversationSessionService>.Instance);
        services.AddScoped<ConversationSessionService>();
        _sp = services.BuildServiceProvider();

        // Ensure DB schema
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Database.EnsureCreated();
        }

        _handler = new RestartServerHandler(
            _lifetime,
            NullLogger<RestartServerHandler>.Instance);
    }

    public void Dispose()
    {
        _sp.Dispose();
        _connection.Dispose();
    }

    private CommandContext MakeContext(string agentId, string agentName, string role) =>
        new(agentId, agentName, role, "main", null, _sp.CreateScope().ServiceProvider);

    private static CommandEnvelope MakeEnvelope(Dictionary<string, object?>? args = null) =>
        new(
            Command: "RESTART_SERVER",
            Args: args ?? new Dictionary<string, object?> { ["reason"] = "Config update needed" },
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: $"cmd-{Guid.NewGuid():N}",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: "planner-1");

    [Fact]
    public async Task ExecuteAsync_PlannerRole_Succeeds()
    {
        var context = MakeContext("planner-1", "Aristotle", "Planner");
        var envelope = MakeEnvelope();

        var result = await _handler.ExecuteAsync(envelope, context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.NotNull(result.Result);
        Assert.Equal(75, result.Result!["exitCode"]);
        Assert.Equal("Config update needed", result.Result["reason"]);
    }

    [Theory]
    [InlineData("SoftwareEngineer")]
    [InlineData("Architect")]
    [InlineData("Reviewer")]
    [InlineData("TechnicalWriter")]
    public async Task ExecuteAsync_NonPlannerRole_Denied(string role)
    {
        var context = MakeContext("agent-1", "Agent", role);
        var envelope = MakeEnvelope();

        var result = await _handler.ExecuteAsync(envelope, context);

        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Contains("Planner", result.Error!);
    }

    [Fact]
    public async Task ExecuteAsync_MissingReason_ReturnsError()
    {
        var context = MakeContext("planner-1", "Aristotle", "Planner");
        var envelope = MakeEnvelope(new Dictionary<string, object?>());

        var result = await _handler.ExecuteAsync(envelope, context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("reason", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_SetsExitCode75()
    {
        var context = MakeContext("planner-1", "Aristotle", "Planner");
        var envelope = MakeEnvelope();

        // Save original exit code to restore after test
        var originalExitCode = Environment.ExitCode;
        try
        {
            await _handler.ExecuteAsync(envelope, context);

            Assert.Equal(RestartServerHandler.RestartExitCode, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public void RestartExitCode_Is75()
    {
        Assert.Equal(75, RestartServerHandler.RestartExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_UnderRateLimit_Succeeds()
    {
        // Seed a few recent intentional restarts (under the limit)
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            for (int i = 0; i < RestartServerHandler.MaxRestartsPerWindow - 1; i++)
            {
                db.ServerInstances.Add(new Data.Entities.ServerInstanceEntity
                {
                    StartedAt = DateTime.UtcNow.AddMinutes(-(i + 1) * 5),
                    ShutdownAt = DateTime.UtcNow.AddMinutes(-i * 5),
                    ExitCode = RestartServerHandler.RestartExitCode,
                    Version = "1.0.0"
                });
            }
            await db.SaveChangesAsync();
        }

        var context = MakeContext("planner-1", "Aristotle", "Planner");
        var envelope = MakeEnvelope();

        var originalExitCode = Environment.ExitCode;
        try
        {
            var result = await _handler.ExecuteAsync(envelope, context);
            Assert.Equal(CommandStatus.Success, result.Status);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task ExecuteAsync_AtRateLimit_Denied()
    {
        // Seed exactly MaxRestartsPerWindow intentional restarts
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            for (int i = 0; i < RestartServerHandler.MaxRestartsPerWindow; i++)
            {
                db.ServerInstances.Add(new Data.Entities.ServerInstanceEntity
                {
                    StartedAt = DateTime.UtcNow.AddMinutes(-(i + 1) * 5),
                    ShutdownAt = DateTime.UtcNow.AddMinutes(-i * 5),
                    ExitCode = RestartServerHandler.RestartExitCode,
                    Version = "1.0.0"
                });
            }
            await db.SaveChangesAsync();
        }

        var context = MakeContext("planner-1", "Aristotle", "Planner");
        var envelope = MakeEnvelope();

        var result = await _handler.ExecuteAsync(envelope, context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.RateLimit, result.ErrorCode);
        Assert.Contains("rate limit", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_OldRestartsOutsideWindow_NotCounted()
    {
        // Seed restarts that are OUTSIDE the window (older than RestartWindowHours)
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            for (int i = 0; i < RestartServerHandler.MaxRestartsPerWindow + 5; i++)
            {
                db.ServerInstances.Add(new Data.Entities.ServerInstanceEntity
                {
                    StartedAt = DateTime.UtcNow.AddHours(-(RestartServerHandler.RestartWindowHours + 1)),
                    ShutdownAt = DateTime.UtcNow.AddHours(-(RestartServerHandler.RestartWindowHours + 1)),
                    ExitCode = RestartServerHandler.RestartExitCode,
                    Version = "1.0.0"
                });
            }
            await db.SaveChangesAsync();
        }

        var context = MakeContext("planner-1", "Aristotle", "Planner");
        var envelope = MakeEnvelope();

        var originalExitCode = Environment.ExitCode;
        try
        {
            var result = await _handler.ExecuteAsync(envelope, context);
            Assert.Equal(CommandStatus.Success, result.Status);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CrashRestartsNotCounted()
    {
        // Seed crash restarts (exit code != 75) — these should NOT count toward the limit
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            for (int i = 0; i < RestartServerHandler.MaxRestartsPerWindow + 5; i++)
            {
                db.ServerInstances.Add(new Data.Entities.ServerInstanceEntity
                {
                    StartedAt = DateTime.UtcNow.AddMinutes(-(i + 1) * 2),
                    ShutdownAt = DateTime.UtcNow.AddMinutes(-i * 2),
                    ExitCode = -1, // crash
                    CrashDetected = true,
                    Version = "1.0.0"
                });
            }
            await db.SaveChangesAsync();
        }

        var context = MakeContext("planner-1", "Aristotle", "Planner");
        var envelope = MakeEnvelope();

        var originalExitCode = Environment.ExitCode;
        try
        {
            var result = await _handler.ExecuteAsync(envelope, context);
            Assert.Equal(CommandStatus.Success, result.Status);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task ExecuteAsync_SuccessResult_IncludesRestartCount()
    {
        var context = MakeContext("planner-1", "Aristotle", "Planner");
        var envelope = MakeEnvelope();

        var originalExitCode = Environment.ExitCode;
        try
        {
            var result = await _handler.ExecuteAsync(envelope, context);

            Assert.Equal(CommandStatus.Success, result.Status);
            Assert.Equal(1, result.Result!["restartsInWindow"]);
            Assert.Equal(RestartServerHandler.MaxRestartsPerWindow, result.Result["maxRestartsPerWindow"]);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }
}

public class RestartServerCommandTests
{
    [Fact]
    public void TryParse_ValidArgs_Succeeds()
    {
        var args = new Dictionary<string, object?> { ["reason"] = "Need to reload config" };

        var result = RestartServerCommand.TryParse(args, out var command, out var error);

        Assert.True(result);
        Assert.NotNull(command);
        Assert.Equal("Need to reload config", command!.Reason);
        Assert.Null(error);
    }

    [Fact]
    public void TryParse_MissingReason_Fails()
    {
        var args = new Dictionary<string, object?>();

        var result = RestartServerCommand.TryParse(args, out var command, out var error);

        Assert.False(result);
        Assert.Null(command);
        Assert.Contains("reason", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_WhitespaceReason_Fails()
    {
        var args = new Dictionary<string, object?> { ["reason"] = "   " };

        var result = RestartServerCommand.TryParse(args, out var command, out var error);

        Assert.False(result);
        Assert.Null(command);
    }

    [Fact]
    public void TryParse_TrimsReason()
    {
        var args = new Dictionary<string, object?> { ["reason"] = "  Config update  " };

        RestartServerCommand.TryParse(args, out var command, out _);

        Assert.Equal("Config update", command!.Reason);
    }
}

public class CommandParserRestartServerTests
{
    private readonly CommandParser _parser = new();

    [Fact]
    public void Parse_RestartServerCommand_Recognized()
    {
        var text = "RESTART_SERVER:\n  Reason: Need to reload configuration";

        var result = _parser.Parse(text);

        Assert.Single(result.Commands);
        Assert.Equal("RESTART_SERVER", result.Commands[0].Command);
        Assert.Equal("Need to reload configuration", result.Commands[0].Args["reason"]);
    }
}
