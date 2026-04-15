using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public sealed class TaskWriteToolWrapperTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _sp;
    private readonly TaskWriteToolWrapper _wrapper;
    private readonly AgentCatalogOptions _catalog;

    public TaskWriteToolWrapperTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _catalog = new AgentCatalogOptions("main", "Main Room",
        [
            new AgentDefinition(
                Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                Summary: "Backend engineer", StartupPrompt: "prompt", Model: null,
                CapabilityTags: [], EnabledTools: ["code-write"],
                AutoJoinDefaultRoom: true,
                Permissions: new CommandPermissionSet(["*"], []))
        ]);

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt =>
            opt.UseSqlite(_connection));

        services.AddSingleton(_catalog);
        services.AddSingleton<IAgentCatalog>(_catalog);
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<IActivityBroadcaster>(sp => sp.GetRequiredService<ActivityBroadcaster>());
        services.AddSingleton<MessageBroadcaster>();
        services.AddSingleton<IMessageBroadcaster>(sp => sp.GetRequiredService<MessageBroadcaster>());
        services.AddScoped<ActivityPublisher>();
        services.AddScoped<IActivityPublisher>(sp => sp.GetRequiredService<ActivityPublisher>());
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton(Substitute.For<IAgentExecutor>());
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<ConversationSessionService>();
        services.AddScoped<IConversationSessionService>(sp => sp.GetRequiredService<ConversationSessionService>());
        services.AddScoped<MessageService>();
        services.AddScoped<IMessageService>(sp => sp.GetRequiredService<MessageService>());
        services.AddScoped<AgentLocationService>();
        services.AddScoped<IAgentLocationService>(sp => sp.GetRequiredService<AgentLocationService>());
        services.AddScoped<PlanService>();
        services.AddScoped<TaskDependencyService>();
        services.AddScoped<ITaskDependencyService>(sp => sp.GetRequiredService<TaskDependencyService>());
        services.AddSingleton<ILogger<TaskDependencyService>>(NullLogger<TaskDependencyService>.Instance);
        services.AddScoped<TaskQueryService>();
        services.AddScoped<ITaskQueryService>(sp => sp.GetRequiredService<TaskQueryService>());
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<ITaskLifecycleService>(sp => sp.GetRequiredService<TaskLifecycleService>());
        services.AddScoped<PhaseTransitionValidator>();
        services.AddScoped<RoomService>();
        services.AddScoped<IRoomService>(sp => sp.GetRequiredService<RoomService>());
        services.AddScoped<RoomSnapshotBuilder>();

        services.AddScoped<IRoomSnapshotBuilder>(sp => sp.GetRequiredService<RoomSnapshotBuilder>());
        services.AddSingleton<ILogger<WorkspaceRoomService>>(NullLogger<WorkspaceRoomService>.Instance);
        services.AddScoped<WorkspaceRoomService>();

        services.AddScoped<IWorkspaceRoomService>(sp => sp.GetRequiredService<WorkspaceRoomService>());
        services.AddScoped<RoomLifecycleService>();
        services.AddScoped<IRoomLifecycleService>(sp => sp.GetRequiredService<RoomLifecycleService>());
        services.AddScoped<BreakoutRoomService>();
        services.AddScoped<IBreakoutRoomService>(sp => sp.GetRequiredService<BreakoutRoomService>());
        services.AddScoped<TaskOrchestrationService>();
        services.AddScoped<ITaskOrchestrationService>(sp => sp.GetRequiredService<TaskOrchestrationService>());
        services.AddScoped<TaskItemService>();
        services.AddScoped<ITaskItemService>(sp => sp.GetRequiredService<TaskItemService>());
        services.AddScoped<CrashRecoveryService>();
        services.AddScoped<ICrashRecoveryService>(sp => sp.GetRequiredService<CrashRecoveryService>());
        services.AddScoped<InitializationService>();

        _sp = services.BuildServiceProvider();

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

        // Create default room so tasks can be assigned
        db.Rooms.Add(new Data.Entities.RoomEntity
        {
            Id = "main",
            Name = "Main Room",
            Status = "Active",
            WorkspacePath = "/test"
        });
        db.SaveChanges();

        _wrapper = new TaskWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "engineer-1", "Hephaestus");
    }

    public void Dispose()
    {
        _sp.Dispose();
        _connection.Dispose();
    }

    // ── CreateTaskAsync: Input validation ───────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateTaskAsync_EmptyTitle_ReturnsError(string? title)
    {
        var result = await _wrapper.CreateTaskAsync(title!, "desc", "criteria");
        Assert.StartsWith("Error: title is required", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateTaskAsync_EmptyDescription_ReturnsError(string? desc)
    {
        var result = await _wrapper.CreateTaskAsync("Title", desc!, "criteria");
        Assert.StartsWith("Error: description is required", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateTaskAsync_EmptySuccessCriteria_ReturnsError(string? criteria)
    {
        var result = await _wrapper.CreateTaskAsync("Title", "Desc", criteria!);
        Assert.StartsWith("Error: successCriteria is required", result);
    }

    [Fact]
    public async Task CreateTaskAsync_InvalidType_ReturnsError()
    {
        var result = await _wrapper.CreateTaskAsync("Title", "Desc", "Criteria",
            type: "InvalidType");
        Assert.Contains("Invalid task type", result);
        Assert.Contains("InvalidType", result);
    }

    [Theory]
    [InlineData("Feature")]
    [InlineData("Bug")]
    [InlineData("Chore")]
    [InlineData("Spike")]
    public async Task CreateTaskAsync_ValidTypes_Succeeds(string type)
    {
        var result = await _wrapper.CreateTaskAsync(
            $"Task {type}", "Description", "Criteria", type: type);
        Assert.Contains("Task created successfully", result);
        Assert.Contains($"Type: {type}", result);
    }

    [Fact]
    public async Task CreateTaskAsync_DefaultType_IsFeature()
    {
        var result = await _wrapper.CreateTaskAsync("Title", "Desc", "Criteria");
        Assert.Contains("Task created successfully", result);
        Assert.Contains("Type: Feature", result);
    }

    [Fact]
    public async Task CreateTaskAsync_ReturnsTaskDetails()
    {
        var result = await _wrapper.CreateTaskAsync(
            "Add validation", "Add input validation", "All inputs validated");

        Assert.Contains("Task created successfully", result);
        Assert.Contains("ID:", result);
        Assert.Contains("Title: Add validation", result);
        Assert.Contains("Status:", result);
        Assert.Contains("Room:", result);
    }

    [Fact]
    public async Task CreateTaskAsync_CaseInsensitiveType()
    {
        var result = await _wrapper.CreateTaskAsync(
            "Case test", "Desc", "Criteria", type: "feature");
        Assert.Contains("Task created successfully", result);
    }

    // ── UpdateTaskStatusAsync: Input validation ─────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateTaskStatusAsync_EmptyTaskId_ReturnsError(string? taskId)
    {
        var result = await _wrapper.UpdateTaskStatusAsync(taskId!, status: "Active");
        Assert.StartsWith("Error: taskId is required", result);
    }

    [Fact]
    public async Task UpdateTaskStatusAsync_NoFields_ReturnsError()
    {
        var result = await _wrapper.UpdateTaskStatusAsync("T-1");
        Assert.Contains("At least one of status, blocker, or note is required", result);
    }

    [Fact]
    public async Task UpdateTaskStatusAsync_BlockerAndStatus_ReturnsError()
    {
        var result = await _wrapper.UpdateTaskStatusAsync("T-1",
            status: "Active", blocker: "Something is broken");
        Assert.Contains("Cannot specify both", result);
    }

    [Fact]
    public async Task UpdateTaskStatusAsync_InvalidStatus_ReturnsError()
    {
        var result = await _wrapper.UpdateTaskStatusAsync("T-1",
            status: "InvalidStatus");
        Assert.Contains("Invalid status", result);
        Assert.Contains("InvalidStatus", result);
    }

    [Fact]
    public async Task UpdateTaskStatusAsync_NonexistentTask_ReturnsError()
    {
        var result = await _wrapper.UpdateTaskStatusAsync("nonexistent",
            status: "Active");
        Assert.Contains("not found", result);
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("Blocked")]
    [InlineData("AwaitingValidation")]
    [InlineData("InReview")]
    [InlineData("Queued")]
    public async Task UpdateTaskStatusAsync_ValidStatuses_DoNotReturnValidationError(string status)
    {
        // These won't return "Invalid status" — they might return "not found"
        // but that's fine, the validation passed.
        var result = await _wrapper.UpdateTaskStatusAsync("T-1", status: status);
        Assert.DoesNotContain("Invalid status", result);
    }

    [Fact]
    public async Task UpdateTaskStatusAsync_WithNote_PostsNote()
    {
        // Create a real task first
        var createResult = await _wrapper.CreateTaskAsync(
            "Note test", "Desc", "Criteria");
        var taskId = ExtractTaskId(createResult);

        var result = await _wrapper.UpdateTaskStatusAsync(taskId,
            note: "This is a progress note");
        Assert.Contains("note posted", result);
    }

    [Fact]
    public async Task UpdateTaskStatusAsync_WithBlocker_SetsBlockedStatus()
    {
        var createResult = await _wrapper.CreateTaskAsync(
            "Blocker test", "Desc", "Criteria");
        var taskId = ExtractTaskId(createResult);

        var result = await _wrapper.UpdateTaskStatusAsync(taskId,
            blocker: "Waiting on dependency");
        Assert.Contains("Blocked", result);
        Assert.Contains("blocker:", result);
    }

    [Fact]
    public async Task UpdateTaskStatusAsync_StatusChange_UpdatesTask()
    {
        var createResult = await _wrapper.CreateTaskAsync(
            "Status test", "Desc", "Criteria");
        var taskId = ExtractTaskId(createResult);

        var result = await _wrapper.UpdateTaskStatusAsync(taskId,
            status: "AwaitingValidation");
        Assert.Contains("AwaitingValidation", result);
    }

    // ── AddTaskCommentAsync: Input validation ───────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddTaskCommentAsync_EmptyTaskId_ReturnsError(string? taskId)
    {
        var result = await _wrapper.AddTaskCommentAsync(taskId!, "content");
        Assert.StartsWith("Error: taskId is required", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddTaskCommentAsync_EmptyContent_ReturnsError(string? content)
    {
        var result = await _wrapper.AddTaskCommentAsync("T-1", content!);
        Assert.StartsWith("Error: content is required", result);
    }

    [Fact]
    public async Task AddTaskCommentAsync_InvalidType_ReturnsError()
    {
        var result = await _wrapper.AddTaskCommentAsync("T-1", "content",
            commentType: "InvalidType");
        Assert.Contains("Invalid comment type", result);
    }

    [Theory]
    [InlineData("Comment")]
    [InlineData("Finding")]
    [InlineData("Evidence")]
    [InlineData("Blocker")]
    public async Task AddTaskCommentAsync_ValidTypes_Succeeds(string commentType)
    {
        var createResult = await _wrapper.CreateTaskAsync(
            $"Comment {commentType}", "Desc", "Criteria");
        var taskId = ExtractTaskId(createResult);

        var result = await _wrapper.AddTaskCommentAsync(taskId, "Test content",
            commentType: commentType);
        Assert.Contains("Comment added", result);
        Assert.Contains($"Type: {commentType}", result);
    }

    [Fact]
    public async Task AddTaskCommentAsync_DefaultType_IsComment()
    {
        var createResult = await _wrapper.CreateTaskAsync(
            "Default comment type", "Desc", "Criteria");
        var taskId = ExtractTaskId(createResult);

        var result = await _wrapper.AddTaskCommentAsync(taskId, "Some comment");
        Assert.Contains("Type: Comment", result);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static string ExtractTaskId(string createResult)
    {
        // Extract "ID: T-xxx" from the create result
        var line = createResult.Split('\n')
            .FirstOrDefault(l => l.TrimStart().StartsWith("- ID:"));
        return line?.Replace("- ID:", "").Trim() ?? throw new Exception(
            $"Could not extract task ID from: {createResult}");
    }
}
