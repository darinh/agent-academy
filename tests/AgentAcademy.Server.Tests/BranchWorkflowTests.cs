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
using NSubstitute;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for MERGE_TASK command handler and branch workflow authorization.
/// </summary>
public class BranchWorkflowTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentCatalogOptions _catalog;
    private readonly GitService _gitService;

    public BranchWorkflowTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Collaboration Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "planner-1", Name: "Aristotle", Role: "Planner",
                    Summary: "Planner", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["MERGE_TASK", "APPROVE_TASK"], [])),
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["LIST_*"], ["MERGE_TASK"])),
                new AgentDefinition(
                    Id: "reviewer-1", Name: "Socrates", Role: "Reviewer",
                    Summary: "Reviewer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: false,
                    Permissions: new CommandPermissionSet(["MERGE_TASK", "APPROVE_TASK"], []))
            ]
        );

        _gitService = new GitService(NullLogger<GitService>.Instance);

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton(_catalog);
        services.AddScoped<WorkspaceRuntime>();
        services.AddSingleton(_gitService);
        services.AddSingleton<CommandAuthorizer>();
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

    // ── Authorization Tests ─────────────────────────────────────

    [Fact]
    public async Task MergeTask_Reviewer_CanMerge_WhenApproved()
    {
        var taskId = await CreateTestTask(
            status: nameof(TaskStatus.Approved),
            branchName: "task/test-branch-abc123");
        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = taskId }, "reviewer-1", "Socrates", "Reviewer");

        var authorizer = _serviceProvider.GetRequiredService<CommandAuthorizer>();
        var agent = _catalog.Agents.First(a => a.Id == "reviewer-1");
        var denied = authorizer.Authorize(cmd, agent);

        Assert.Null(denied); // NOT Denied
    }

    [Fact]
    public async Task MergeTask_Planner_CanMerge()
    {
        var taskId = await CreateTestTask(
            status: nameof(TaskStatus.Approved),
            branchName: "task/test-branch-abc123");
        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = taskId }, "planner-1", "Aristotle", "Planner");

        var authorizer = _serviceProvider.GetRequiredService<CommandAuthorizer>();
        var agent = _catalog.Agents.First(a => a.Id == "planner-1");
        var denied = authorizer.Authorize(cmd, agent);

        Assert.Null(denied); // NOT Denied
    }

    [Fact]
    public async Task MergeTask_Engineer_Denied()
    {
        var taskId = await CreateTestTask(
            status: nameof(TaskStatus.Approved),
            branchName: "task/test-branch-abc123");
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = taskId }, "engineer-1", "Hephaestus", "SoftwareEngineer");

        var authorizer = _serviceProvider.GetRequiredService<CommandAuthorizer>();
        var agent = _catalog.Agents.First(a => a.Id == "engineer-1");
        var denied = authorizer.Authorize(cmd, agent);

        Assert.NotNull(denied);
        Assert.Equal(CommandStatus.Denied, denied!.Status);
    }

    // ── Validation Tests ────────────────────────────────────────

    [Fact]
    public async Task MergeTask_NotApproved_Blocked()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.Active));
        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = taskId }, "reviewer-1", "Socrates", "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Approved", result.Error!);
    }

    [Fact]
    public async Task MergeTask_NoBranch_Blocked()
    {
        var taskId = await CreateTestTask(
            status: nameof(TaskStatus.Approved),
            branchName: null);
        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = taskId }, "reviewer-1", "Socrates", "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("branch", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MergeTask_MissingTaskId_ReturnsError()
    {
        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new(), "reviewer-1", "Socrates", "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("TaskId", result.Error!);
    }

    [Fact]
    public async Task MergeTask_NonexistentTask_ReturnsError()
    {
        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = "nonexistent-task-id" }, "reviewer-1", "Socrates", "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("not found", result.Error!);
    }

    [Fact]
    public async Task MergeTask_InReview_Blocked()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.InReview));
        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = taskId }, "reviewer-1", "Socrates", "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Approved", result.Error!);
    }

    // ── Parser Test ─────────────────────────────────────────────

    [Fact]
    public void CommandParser_RecognizesMergeTask()
    {
        var parser = new CommandParser();
        var result = parser.Parse("MERGE_TASK: task-123");

        Assert.Single(result.Commands);
        Assert.Equal("MERGE_TASK", result.Commands[0].Command);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private async Task<string> CreateTestTask(
        string status = "Active",
        string? branchName = "task/test-abc123",
        string? assignedAgentId = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await EnsureRoom(db, "room-1");

        var taskId = $"task-{Guid.NewGuid():N}"[..20];
        db.Tasks.Add(new Data.Entities.TaskEntity
        {
            Id = taskId,
            Title = "Test Task",
            Description = "Test task description",
            SuccessCriteria = "It works",
            Status = status,
            CurrentPhase = "Implementation",
            RoomId = "room-1",
            BranchName = branchName,
            AssignedAgentId = assignedAgentId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return taskId;
    }

    private static async Task EnsureRoom(AgentAcademyDbContext db, string roomId)
    {
        var room = await db.Rooms.FindAsync(roomId);
        if (room == null)
        {
            db.Rooms.Add(new Data.Entities.RoomEntity
            {
                Id = roomId,
                Name = "Test Room",
                Status = "Active",
                CurrentPhase = "Implementation",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
    }

    private (CommandEnvelope command, CommandContext context) MakeCommand(
        string commandName,
        Dictionary<string, string> args,
        string agentId = "reviewer-1",
        string agentName = "Socrates",
        string agentRole = "Reviewer")
    {
        var scope = _serviceProvider.CreateScope();

        var command = new CommandEnvelope(
            Command: commandName,
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
            AgentRole: agentRole,
            RoomId: "room-1",
            BreakoutRoomId: null,
            Services: scope.ServiceProvider
        );

        return (command, context);
    }
}
