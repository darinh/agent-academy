using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for Tier 3C Frontend/UX command handlers: ShowRoutesHandler.
/// </summary>
public sealed class Tier3FrontendUxCommandTests
{
    // ==================== Test Infrastructure ====================

    private sealed class TestEndpointDataSource : EndpointDataSource
    {
        private readonly List<Endpoint> _endpoints;

        public TestEndpointDataSource(List<Endpoint> endpoints)
        {
            _endpoints = endpoints;
        }

        public override IReadOnlyList<Endpoint> Endpoints => _endpoints;

        public override IChangeToken GetChangeToken() =>
            NullChangeToken.Singleton;

        private sealed class NullChangeToken : IChangeToken
        {
            public static readonly NullChangeToken Singleton = new();
            public bool HasChanged => false;
            public bool ActiveChangeCallbacks => false;
            public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) =>
                EmptyDisposable.Instance;
        }

        private sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private static Endpoint MakeEndpoint(string path, string[] methods, string displayName)
    {
        var pattern = RoutePatternFactory.Parse(path);
        var metadata = new EndpointMetadataCollection(new HttpMethodMetadata(methods));
        return new RouteEndpoint(
            requestDelegate: _ => Task.CompletedTask,
            routePattern: pattern,
            order: 0,
            metadata: metadata,
            displayName: displayName);
    }

    private static (CommandEnvelope command, CommandContext context) MakeCommand(
        Dictionary<string, object?> args,
        List<Endpoint>? endpoints = null)
    {
        var services = new ServiceCollection();

        var testEndpoints = endpoints ?? new List<Endpoint>
        {
            MakeEndpoint("/api/rooms", ["GET"], "AgentAcademy.Server.Controllers.RoomController.GetRooms (AgentAcademy.Server)"),
            MakeEndpoint("/api/rooms/{roomId}", ["GET"], "AgentAcademy.Server.Controllers.RoomController.GetRoom (AgentAcademy.Server)"),
            MakeEndpoint("/api/rooms/{roomId}/messages", ["GET"], "AgentAcademy.Server.Controllers.RoomController.GetMessages (AgentAcademy.Server)"),
            MakeEndpoint("/api/rooms/{roomId}/human", ["POST"], "AgentAcademy.Server.Controllers.RoomController.PostHuman (AgentAcademy.Server)"),
            MakeEndpoint("/api/tasks", ["GET"], "AgentAcademy.Server.Controllers.TaskController.GetTasks (AgentAcademy.Server)"),
            MakeEndpoint("/api/tasks/{taskId}", ["GET"], "AgentAcademy.Server.Controllers.TaskController.GetTask (AgentAcademy.Server)"),
            MakeEndpoint("/api/auth/status", ["GET"], "AgentAcademy.Server.Controllers.AuthController.GetStatus (AgentAcademy.Server)"),
            MakeEndpoint("/api/commands/execute", ["POST"], "AgentAcademy.Server.Controllers.CommandController.Execute (AgentAcademy.Server)"),
        };

        services.AddSingleton<EndpointDataSource>(new TestEndpointDataSource(testEndpoints));

        var sp = services.BuildServiceProvider();
        var command = new CommandEnvelope(
            Command: "SHOW_ROUTES",
            Args: args,
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: $"cmd-{Guid.NewGuid():N}",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: "engineer-1"
        );
        var context = new CommandContext(
            AgentId: "engineer-1",
            AgentName: "Hephaestus",
            AgentRole: "SoftwareEngineer",
            RoomId: "main",
            BreakoutRoomId: null,
            Services: sp
        );
        return (command, context);
    }

    // ==================== Handler Properties ====================

    [Fact]
    public void CommandName_IsShowRoutes()
    {
        var handler = new ShowRoutesHandler();
        Assert.Equal("SHOW_ROUTES", handler.CommandName);
    }

    [Fact]
    public void IsRetrySafe_IsTrue()
    {
        var handler = new ShowRoutesHandler();
        Assert.True(handler.IsRetrySafe);
    }

    [Fact]
    public void IsDestructive_IsFalse()
    {
        ICommandHandler handler = new ShowRoutesHandler();
        Assert.False(handler.IsDestructive);
    }

    // ==================== Basic Execution ====================

    [Fact]
    public async Task Execute_ReturnsAllRoutes()
    {
        var handler = new ShowRoutesHandler();
        var (cmd, ctx) = MakeCommand(new());

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var routes = (List<Dictionary<string, object?>>)dict["routes"]!;
        Assert.Equal(8, (int)dict["count"]!);
        Assert.Equal(8, routes.Count);
    }

    [Fact]
    public async Task Execute_RoutesAreSortedByPath()
    {
        var handler = new ShowRoutesHandler();
        var (cmd, ctx) = MakeCommand(new());

        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var routes = (List<Dictionary<string, object?>>)dict["routes"]!;
        var paths = routes.Select(r => r["path"]?.ToString()).ToList();
        var sorted = paths.OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, paths);
    }

    [Fact]
    public async Task Execute_IncludesMethodsAndControllerInfo()
    {
        var handler = new ShowRoutesHandler();
        var (cmd, ctx) = MakeCommand(new());

        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var routes = (List<Dictionary<string, object?>>)dict["routes"]!;

        var roomsRoute = routes.First(r => r["path"]?.ToString() == "/api/rooms");
        Assert.Equal(new List<string> { "GET" }, roomsRoute["methods"]);
        Assert.Equal("Room", roomsRoute["controller"]);
        Assert.Equal("GetRooms", roomsRoute["action"]);
    }

    // ==================== Prefix Filter ====================

    [Fact]
    public async Task Execute_PrefixFilter_FiltersRoutes()
    {
        var handler = new ShowRoutesHandler();
        var (cmd, ctx) = MakeCommand(new Dictionary<string, object?>
        {
            ["prefix"] = "/api/rooms"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var routes = (List<Dictionary<string, object?>>)dict["routes"]!;
        Assert.Equal(4, routes.Count);
        Assert.All(routes, r =>
            Assert.StartsWith("/api/rooms", r["path"]?.ToString()));
    }

    [Fact]
    public async Task Execute_PrefixFilter_CaseInsensitive()
    {
        var handler = new ShowRoutesHandler();
        var (cmd, ctx) = MakeCommand(new Dictionary<string, object?>
        {
            ["prefix"] = "/API/ROOMS"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var routes = (List<Dictionary<string, object?>>)dict["routes"]!;
        Assert.Equal(4, routes.Count);
    }

    [Fact]
    public async Task Execute_PrefixFilter_NoMatch_ReturnsEmpty()
    {
        var handler = new ShowRoutesHandler();
        var (cmd, ctx) = MakeCommand(new Dictionary<string, object?>
        {
            ["prefix"] = "/api/nonexistent"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var routes = (List<Dictionary<string, object?>>)dict["routes"]!;
        Assert.Empty(routes);
        Assert.Equal(0, (int)dict["count"]!);
    }

    // ==================== Method Filter ====================

    [Fact]
    public async Task Execute_MethodFilter_FiltersRoutes()
    {
        var handler = new ShowRoutesHandler();
        var (cmd, ctx) = MakeCommand(new Dictionary<string, object?>
        {
            ["method"] = "POST"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var routes = (List<Dictionary<string, object?>>)dict["routes"]!;
        Assert.Equal(2, routes.Count);
        Assert.All(routes, r =>
        {
            var methods = (List<string>)r["methods"]!;
            Assert.Contains("POST", methods);
        });
    }

    [Fact]
    public async Task Execute_MethodFilter_CaseInsensitive()
    {
        var handler = new ShowRoutesHandler();
        var (cmd, ctx) = MakeCommand(new Dictionary<string, object?>
        {
            ["method"] = "post"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var routes = (List<Dictionary<string, object?>>)dict["routes"]!;
        Assert.Equal(2, routes.Count);
    }

    // ==================== Combined Filters ====================

    [Fact]
    public async Task Execute_CombinedFilters_ApplyBoth()
    {
        var handler = new ShowRoutesHandler();
        var (cmd, ctx) = MakeCommand(new Dictionary<string, object?>
        {
            ["prefix"] = "/api/rooms",
            ["method"] = "POST"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var routes = (List<Dictionary<string, object?>>)dict["routes"]!;
        Assert.Single(routes);
        Assert.Equal("/api/rooms/{roomId}/human", routes[0]["path"]?.ToString());
    }

    // ==================== Edge Cases ====================

    [Fact]
    public async Task Execute_NoEndpointSources_ReturnsEmpty()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var command = new CommandEnvelope(
            Command: "SHOW_ROUTES",
            Args: new(),
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: "cmd-test",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: "engineer-1"
        );
        var context = new CommandContext(
            AgentId: "engineer-1",
            AgentName: "Hephaestus",
            AgentRole: "SoftwareEngineer",
            RoomId: "main",
            BreakoutRoomId: null,
            Services: sp
        );

        var handler = new ShowRoutesHandler();
        var result = await handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(0, (int)dict["count"]!);
    }

    [Fact]
    public async Task Execute_NonRouteEndpoints_AreSkipped()
    {
        var endpoints = new List<Endpoint>
        {
            // Non-RouteEndpoint (plain Endpoint)
            new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "NonRoute"),
            // Valid RouteEndpoint
            MakeEndpoint("/api/test", ["GET"], "TestController.Test"),
        };

        var handler = new ShowRoutesHandler();
        var (cmd, ctx) = MakeCommand(new(), endpoints);

        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(1, (int)dict["count"]!);
    }

    [Fact]
    public async Task Execute_EndpointWithNoHttpMethodMetadata_ReturnsEmptyMethods()
    {
        var pattern = RoutePatternFactory.Parse("/api/nomethod");
        var endpoint = new RouteEndpoint(
            _ => Task.CompletedTask,
            pattern,
            order: 0,
            metadata: new EndpointMetadataCollection(),
            displayName: "NoMethodEndpoint");

        var handler = new ShowRoutesHandler();
        var (cmd, ctx) = MakeCommand(new(), new List<Endpoint> { endpoint });

        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var routes = (List<Dictionary<string, object?>>)dict["routes"]!;
        Assert.Single(routes);
        var methods = (List<string>)routes[0]["methods"]!;
        Assert.Empty(methods);
    }

    // ==================== DisplayName Parsing ====================

    [Theory]
    [InlineData(
        "AgentAcademy.Server.Controllers.RoomController.GetRooms (AgentAcademy.Server)",
        "Room", "GetRooms")]
    [InlineData(
        "AgentAcademy.Server.Controllers.CommandController.Execute (AgentAcademy.Server)",
        "Command", "Execute")]
    [InlineData(
        "MyApp.ApiController.Index",
        "Api", "Index")]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData("SingleSegment", null, null)]
    public void ParseDisplayName_ExtractsControllerAndAction(
        string? displayName, string? expectedController, string? expectedAction)
    {
        var (controller, action) = ShowRoutesHandler.ParseDisplayName(displayName);
        Assert.Equal(expectedController, controller);
        Assert.Equal(expectedAction, action);
    }

    // ==================== KnownCommands Registration ====================

    [Fact]
    public void KnownCommands_IncludesShowRoutes()
    {
        Assert.Contains("SHOW_ROUTES", CommandParser.KnownCommands);
    }

    // ==================== Multiple EndpointDataSources ====================

    [Fact]
    public async Task Execute_MultipleEndpointSources_MergesAll()
    {
        var services = new ServiceCollection();
        services.AddSingleton<EndpointDataSource>(new TestEndpointDataSource(new List<Endpoint>
        {
            MakeEndpoint("/api/alpha", ["GET"], "AlphaController.Get"),
        }));
        services.AddSingleton<EndpointDataSource>(new TestEndpointDataSource(new List<Endpoint>
        {
            MakeEndpoint("/api/beta", ["POST"], "BetaController.Post"),
        }));

        var sp = services.BuildServiceProvider();
        var command = new CommandEnvelope(
            Command: "SHOW_ROUTES",
            Args: new(),
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: "cmd-test",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: "engineer-1"
        );
        var context = new CommandContext(
            AgentId: "engineer-1",
            AgentName: "Hephaestus",
            AgentRole: "SoftwareEngineer",
            RoomId: "main",
            BreakoutRoomId: null,
            Services: sp
        );

        var handler = new ShowRoutesHandler();
        var result = await handler.ExecuteAsync(command, context);

        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(2, (int)dict["count"]!);
        var routes = (List<Dictionary<string, object?>>)dict["routes"]!;
        Assert.Contains(routes, r => r["path"]?.ToString() == "/api/alpha");
        Assert.Contains(routes, r => r["path"]?.ToString() == "/api/beta");
    }
}
