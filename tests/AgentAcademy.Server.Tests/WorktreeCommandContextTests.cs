using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests that WorkingDirectory flows through CommandContext to handlers,
/// and that handlers use it instead of FindProjectRoot() when set.
/// </summary>
public class WorktreeCommandContextTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly SqliteConnection _connection;
    private readonly CommandPipeline _pipeline;

    private static AgentDefinition TestAgent(CommandPermissionSet? permissions = null) =>
        new("agent-1", "TestAgent", "SoftwareEngineer", "Test", "prompt", null,
            new List<string>(), new List<string>(), true, null,
            permissions ?? new CommandPermissionSet(Allowed: new List<string> { "COMMIT_CHANGES" }, Denied: new List<string>()));

    public WorktreeCommandContextTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt =>
            opt.UseSqlite(_connection));

        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();

        var handlers = Array.Empty<ICommandHandler>();

        _pipeline = new CommandPipeline(
            handlers,
            NullLogger<CommandPipeline>.Instance);
    }

    [Fact]
    public async Task ProcessResponseAsync_WithWorkingDirectory_SetsContextWorkingDirectory()
    {
        CommandContext? capturedContext = null;
        var capturingHandler = new ContextCapturingHandler("LIST_ROOMS", ctx => capturedContext = ctx);

        var agent = TestAgent(new CommandPermissionSet(Allowed: new List<string> { "LIST_ROOMS" }, Denied: new List<string>()));
        var worktreePath = "/tmp/test-worktree";

        var handlers = new ICommandHandler[] { capturingHandler };
        var pipeline = new CommandPipeline(
            handlers,
            NullLogger<CommandPipeline>.Instance);

        using var scope = _serviceProvider.CreateScope();
        await pipeline.ProcessResponseAsync(
            "agent-1",
            "LIST_ROOMS:\nscope: all",
            "room-1",
            agent,
            scope.ServiceProvider,
            workingDirectory: worktreePath);

        Assert.NotNull(capturedContext);
        Assert.Equal(worktreePath, capturedContext!.WorkingDirectory);
    }

    [Fact]
    public async Task ProcessResponseAsync_WithoutWorkingDirectory_ContextWorkingDirectoryIsNull()
    {
        CommandContext? capturedContext = null;
        var capturingHandler = new ContextCapturingHandler("LIST_ROOMS", ctx => capturedContext = ctx);

        var agent = TestAgent(new CommandPermissionSet(Allowed: new List<string> { "LIST_ROOMS" }, Denied: new List<string>()));

        var handlers = new ICommandHandler[] { capturingHandler };
        var pipeline = new CommandPipeline(
            handlers,
            NullLogger<CommandPipeline>.Instance);

        using var scope = _serviceProvider.CreateScope();
        await pipeline.ProcessResponseAsync(
            "agent-1",
            "LIST_ROOMS:\nscope: all",
            "room-1",
            agent,
            scope.ServiceProvider);

        Assert.NotNull(capturedContext);
        Assert.Null(capturedContext!.WorkingDirectory);
    }

    [Fact]
    public void CommandContext_WorkingDirectory_DefaultsToNull()
    {
        var context = new CommandContext(
            AgentId: "agent-1",
            AgentName: "Test",
            AgentRole: "SoftwareEngineer",
            RoomId: "room-1",
            BreakoutRoomId: null,
            Services: _serviceProvider);

        Assert.Null(context.WorkingDirectory);
    }

    [Fact]
    public void CommandContext_WorkingDirectory_CanBeSet()
    {
        var context = new CommandContext(
            AgentId: "agent-1",
            AgentName: "Test",
            AgentRole: "SoftwareEngineer",
            RoomId: "room-1",
            BreakoutRoomId: null,
            Services: _serviceProvider,
            WorkingDirectory: "/tmp/worktree");

        Assert.Equal("/tmp/worktree", context.WorkingDirectory);
    }

    [Fact]
    public void CommandContext_With_PreservesWorkingDirectory()
    {
        var context = new CommandContext(
            AgentId: "agent-1",
            AgentName: "Test",
            AgentRole: "SoftwareEngineer",
            RoomId: "room-1",
            BreakoutRoomId: null,
            Services: _serviceProvider,
            WorkingDirectory: "/tmp/worktree");

        var modified = context with { AgentName = "Modified" };

        Assert.Equal("/tmp/worktree", modified.WorkingDirectory);
        Assert.Equal("Modified", modified.AgentName);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// A test handler that captures the CommandContext for assertion.
    /// </summary>
    private sealed class ContextCapturingHandler : ICommandHandler
    {
        private readonly Action<CommandContext> _capture;
        public string CommandName { get; }

        public ContextCapturingHandler(string commandName, Action<CommandContext> capture)
        {
            CommandName = commandName;
            _capture = capture;
        }

        public Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
        {
            _capture(context);
            return Task.FromResult(command with { Status = CommandStatus.Success });
        }
    }
}
