using System.Security.Claims;
using System.Text.Json;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class CommandControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public CommandControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(options => options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Execute_SyncCommand_ReturnsResultAndAuditsHumanUi()
    {
        var handler = new CapturingHandler("GIT_LOG");
        var controller = CreateController(handler);

        var result = await controller.Execute(new ExecuteCommandRequest(
            Command: "GIT_LOG",
            Args: ParseArgs("""{"count":5,"since":"2 days ago"}""")));

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ExecuteCommandResponse>(ok.Value);

        Assert.Equal("completed", payload.Status);
        Assert.Equal("human", payload.ExecutedBy);
        Assert.Equal("5", handler.LastEnvelope!.Args["count"]);
        Assert.Equal("2 days ago", handler.LastEnvelope.Args["since"]);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var audit = await db.CommandAudits.SingleAsync();
        Assert.Equal("human-ui", audit.Source);
        Assert.Equal("GIT_LOG", audit.Command);
        Assert.Equal("Success", audit.Status);
    }

    [Fact]
    public async Task Execute_WhenUnauthenticated_ReturnsUnauthorized()
    {
        var controller = CreateController(new CapturingHandler("LIST_ROOMS"), authenticated: false);

        var result = await controller.Execute(new ExecuteCommandRequest("LIST_ROOMS", null));

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Execute_AsyncCommand_ReturnsAcceptedAndPollingTracksCompletion()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingHandler(gate);
        var controller = CreateController(handler);

        var executeResult = await controller.Execute(new ExecuteCommandRequest(
            Command: "RUN_BUILD",
            Args: null));

        var accepted = Assert.IsType<AcceptedResult>(executeResult);
        var acceptedPayload = Assert.IsType<ExecuteCommandResponse>(accepted.Value);
        Assert.Equal("pending", acceptedPayload.Status);

        var pendingResult = await controller.GetStatus(acceptedPayload.CorrelationId);
        var pendingOk = Assert.IsType<OkObjectResult>(pendingResult);
        var pendingPayload = Assert.IsType<ExecuteCommandResponse>(pendingOk.Value);
        Assert.Equal("pending", pendingPayload.Status);

        gate.SetResult();

        ExecuteCommandResponse? completedPayload = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(25);
            var statusResult = await controller.GetStatus(acceptedPayload.CorrelationId);
            var statusOk = Assert.IsType<OkObjectResult>(statusResult);
            var payload = Assert.IsType<ExecuteCommandResponse>(statusOk.Value);
            if (payload.Status != "pending")
            {
                completedPayload = payload;
                break;
            }
        }

        Assert.NotNull(completedPayload);
        Assert.Equal("completed", completedPayload!.Status);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var audit = await db.CommandAudits.SingleAsync(a => a.CorrelationId == acceptedPayload.CorrelationId);
        Assert.Equal("Success", audit.Status);
        Assert.Equal("human-ui", audit.Source);
    }

    [Fact]
    public void GetMetadata_WhenAuthenticated_ReturnsAllowlistedCommands()
    {
        var handlers = new ICommandHandler[]
        {
            new CapturingHandler("GIT_LOG"),
            new CapturingHandler("READ_FILE"),
            new CapturingHandler("RUN_BUILD"),
            new CapturingHandler("LIST_ROOMS"),
        };
        var controller = CreateController(handlers);

        var result = controller.GetMetadata();

        var ok = Assert.IsType<OkObjectResult>(result);
        var metadata = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value);
        var list = metadata.ToList();

        // Must return only commands that are in the allowlist
        Assert.True(list.Count > 0, "Metadata should not be empty");

        // Verify well-known commands are present
        var commands = list.Cast<AgentAcademy.Shared.Models.HumanCommandMetadata>().ToList();
        Assert.Contains(commands, m => m.Command == "READ_FILE");
        Assert.Contains(commands, m => m.Command == "RUN_BUILD");
        Assert.Contains(commands, m => m.Command == "GIT_LOG");

        // Verify field metadata is populated
        var readFile = commands.Single(m => m.Command == "READ_FILE");
        Assert.Equal("Read file", readFile.Title);
        Assert.Equal("code", readFile.Category);
        Assert.False(readFile.IsAsync);
        Assert.True(readFile.Fields.Count > 0, "READ_FILE should have fields");
        Assert.Contains(readFile.Fields, f => f.Name == "path" && f.Required);

        // Verify async commands are flagged
        var runBuild = commands.Single(m => m.Command == "RUN_BUILD");
        Assert.True(runBuild.IsAsync);
    }

    [Fact]
    public void GetMetadata_WhenUnauthenticated_ReturnsUnauthorized()
    {
        var controller = CreateController(new CapturingHandler("GIT_LOG"), authenticated: false);

        var result = controller.GetMetadata();

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public void GetMetadata_OnlyReturnsAllowlistedCommands()
    {
        var controller = CreateController(new CapturingHandler("GIT_LOG"));

        var result = controller.GetMetadata();

        var ok = Assert.IsType<OkObjectResult>(result);
        var commands = Assert.IsAssignableFrom<List<AgentAcademy.Shared.Models.HumanCommandMetadata>>(ok.Value);

        // None of the agent-only commands should appear
        Assert.DoesNotContain(commands, m => m.Command == "SHELL");
        Assert.DoesNotContain(commands, m => m.Command == "REMEMBER");
        Assert.DoesNotContain(commands, m => m.Command == "DM");
        Assert.DoesNotContain(commands, m => m.Command == "MOVE_TO_ROOM");
    }

    [Fact]
    public void GetMetadata_ExcludesCommandsWithoutRegisteredHandler()
    {
        // Only register a handler for GIT_LOG — metadata should not include
        // any allowlisted command that lacks a handler.
        var controller = CreateController(new CapturingHandler("GIT_LOG"));

        var result = controller.GetMetadata();

        var ok = Assert.IsType<OkObjectResult>(result);
        var commands = Assert.IsAssignableFrom<List<AgentAcademy.Shared.Models.HumanCommandMetadata>>(ok.Value);

        // Only GIT_LOG has a handler registered
        Assert.Single(commands);
        Assert.Equal("GIT_LOG", commands[0].Command);
    }

    private CommandController CreateController(ICommandHandler handler, bool authenticated = true) =>
        CreateController(new[] { handler }, authenticated);

    private CommandController CreateController(IEnumerable<ICommandHandler> handlers, bool authenticated = true)
    {
        var controller = new CommandController(
            handlers,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CommandController>.Instance);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = authenticated ? CreateAuthenticatedUser() : new ClaimsPrincipal(new ClaimsIdentity())
            }
        };

        return controller;
    }

    private static ClaimsPrincipal CreateAuthenticatedUser()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "hephaestus"),
            new Claim("urn:github:name", "Hephaestus"),
        ], "Cookies");

        return new ClaimsPrincipal(identity);
    }

    private static Dictionary<string, JsonElement> ParseArgs(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CapturingHandler(string commandName) : ICommandHandler
    {
        public string CommandName => commandName;
        public CommandEnvelope? LastEnvelope { get; private set; }

        public Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
        {
            LastEnvelope = command;
            return Task.FromResult(command with
            {
                Result = new Dictionary<string, object?>
                {
                    ["echo"] = command.Args
                }
            });
        }
    }

    private sealed class BlockingHandler(TaskCompletionSource gate) : ICommandHandler
    {
        public string CommandName => "RUN_BUILD";

        public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
        {
            await gate.Task;
            return command with
            {
                Result = new Dictionary<string, object?>
                {
                    ["success"] = true
                }
            };
        }
    }
}
