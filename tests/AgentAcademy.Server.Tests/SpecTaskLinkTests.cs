using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for spec-task linking: WorkspaceRuntime methods, command handlers, and SpecManager filtering.
/// </summary>
public class SpecTaskLinkTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly string _specsDir;

    public SpecTaskLinkTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _specsDir = Path.Combine(Path.GetTempPath(), $"spec-link-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_specsDir);

        var catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Collaboration Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true),
                new AgentDefinition(
                    Id: "reviewer-1", Name: "Socrates", Role: "Reviewer",
                    Summary: "Reviewer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: false),
                new AgentDefinition(
                    Id: "planner-1", Name: "Aristotle", Role: "Planner",
                    Summary: "Planner", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: false)
            ]
        );

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton(catalog);
        services.AddScoped<TaskQueryService>();
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<WorkspaceRuntime>();
        services.AddScoped<SystemSettingsService>();
        services.AddSingleton<IAgentExecutor>(NSubstitute.Substitute.For<IAgentExecutor>());
        services.AddScoped<ConversationSessionService>();
        services.AddSingleton(new SpecManager(_specsDir));
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_specsDir))
            Directory.Delete(_specsDir, recursive: true);
    }

    // ── WorkspaceRuntime.LinkTaskToSpecAsync ───────────────────

    [Fact]
    public async Task LinkTaskToSpec_CreatesLink()
    {
        var taskId = await CreateTestTask();

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        var link = await runtime.LinkTaskToSpecAsync(
            taskId, "003-agent-system", "engineer-1", "Hephaestus");

        Assert.Equal(taskId, link.TaskId);
        Assert.Equal("003-agent-system", link.SpecSectionId);
        Assert.Equal(SpecLinkType.Implements, link.LinkType);
        Assert.Equal("Hephaestus", link.LinkedByAgentName);
    }

    [Fact]
    public async Task LinkTaskToSpec_CustomLinkType()
    {
        var taskId = await CreateTestTask();

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        var link = await runtime.LinkTaskToSpecAsync(
            taskId, "001-domain-model", "engineer-1", "Hephaestus",
            linkType: "Modifies", note: "Added new field to TaskEntity");

        Assert.Equal(SpecLinkType.Modifies, link.LinkType);
        Assert.Equal("Added new field to TaskEntity", link.Note);
    }

    [Fact]
    public async Task LinkTaskToSpec_InvalidLinkType_Throws()
    {
        var taskId = await CreateTestTask();

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            runtime.LinkTaskToSpecAsync(taskId, "003-agent-system", "engineer-1", "Hephaestus",
                linkType: "InvalidType"));
        Assert.Contains("Invalid link type", ex.Message);
    }

    [Fact]
    public async Task LinkTaskToSpec_TaskNotFound_Throws()
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.LinkTaskToSpecAsync("nonexistent", "003-agent-system", "engineer-1", "Hephaestus"));
    }

    [Fact]
    public async Task LinkTaskToSpec_Upsert_UpdatesExistingLink()
    {
        var taskId = await CreateTestTask();

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        var link1 = await runtime.LinkTaskToSpecAsync(
            taskId, "003-agent-system", "engineer-1", "Hephaestus",
            linkType: "Implements");

        var link2 = await runtime.LinkTaskToSpecAsync(
            taskId, "003-agent-system", "reviewer-1", "Socrates",
            linkType: "Fixes", note: "Corrected known gap");

        // Should be same link updated, not duplicate
        Assert.Equal(link1.Id, link2.Id);
        Assert.Equal(SpecLinkType.Fixes, link2.LinkType);
        Assert.Equal("Corrected known gap", link2.Note);
        Assert.Equal("Socrates", link2.LinkedByAgentName);
    }

    [Fact]
    public async Task LinkTaskToSpec_MultipleSpecsPerTask()
    {
        var taskId = await CreateTestTask();

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        await runtime.LinkTaskToSpecAsync(taskId, "003-agent-system", "engineer-1", "Hephaestus");
        await runtime.LinkTaskToSpecAsync(taskId, "007-agent-commands", "engineer-1", "Hephaestus");
        await runtime.LinkTaskToSpecAsync(taskId, "010-task-management", "engineer-1", "Hephaestus");

        var links = await runtime.GetSpecLinksForTaskAsync(taskId);
        Assert.Equal(3, links.Count);
    }

    [Fact]
    public async Task LinkTaskToSpec_EmptyArgs_Throws()
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            runtime.LinkTaskToSpecAsync("", "003-agent-system", "engineer-1", "Hephaestus"));

        var taskId = await CreateTestTask();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            runtime.LinkTaskToSpecAsync(taskId, "", "engineer-1", "Hephaestus"));
    }

    // ── WorkspaceRuntime.UnlinkTaskFromSpecAsync ──────────────

    [Fact]
    public async Task UnlinkTaskFromSpec_RemovesLink()
    {
        var taskId = await CreateTestTask();

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        await runtime.LinkTaskToSpecAsync(taskId, "003-agent-system", "engineer-1", "Hephaestus");
        await runtime.UnlinkTaskFromSpecAsync(taskId, "003-agent-system");

        var links = await runtime.GetSpecLinksForTaskAsync(taskId);
        Assert.Empty(links);
    }

    [Fact]
    public async Task UnlinkTaskFromSpec_NonexistentLink_Throws()
    {
        var taskId = await CreateTestTask();

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.UnlinkTaskFromSpecAsync(taskId, "003-agent-system"));
    }

    // ── WorkspaceRuntime.GetSpecLinksForTaskAsync ─────────────

    [Fact]
    public async Task GetSpecLinksForTask_ReturnsOrderedBySection()
    {
        var taskId = await CreateTestTask();

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        await runtime.LinkTaskToSpecAsync(taskId, "010-task-management", "engineer-1", "Hephaestus");
        await runtime.LinkTaskToSpecAsync(taskId, "003-agent-system", "engineer-1", "Hephaestus");

        var links = await runtime.GetSpecLinksForTaskAsync(taskId);
        Assert.Equal("003-agent-system", links[0].SpecSectionId);
        Assert.Equal("010-task-management", links[1].SpecSectionId);
    }

    [Fact]
    public async Task GetSpecLinksForTask_EmptyForUnlinkedTask()
    {
        var taskId = await CreateTestTask();

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        var links = await runtime.GetSpecLinksForTaskAsync(taskId);
        Assert.Empty(links);
    }

    // ── WorkspaceRuntime.GetTasksForSpecAsync ─────────────────

    [Fact]
    public async Task GetTasksForSpec_ReturnsMultipleTasks()
    {
        var task1 = await CreateTestTask(title: "Task A");
        var task2 = await CreateTestTask(title: "Task B");

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        await runtime.LinkTaskToSpecAsync(task1, "003-agent-system", "engineer-1", "Hephaestus");
        await runtime.LinkTaskToSpecAsync(task2, "003-agent-system", "engineer-1", "Hephaestus");

        var links = await runtime.GetTasksForSpecAsync("003-agent-system");
        Assert.Equal(2, links.Count);
    }

    [Fact]
    public async Task GetTasksForSpec_EmptyForUnlinkedSection()
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        var links = await runtime.GetTasksForSpecAsync("999-nonexistent");
        Assert.Empty(links);
    }

    // ── WorkspaceRuntime.GetUnlinkedTasksAsync ────────────────

    [Fact]
    public async Task GetUnlinkedTasks_ExcludesLinkedTasks()
    {
        var linked = await CreateTestTask(title: "Linked Task");
        var unlinked = await CreateTestTask(title: "Unlinked Task");

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        await runtime.LinkTaskToSpecAsync(linked, "003-agent-system", "engineer-1", "Hephaestus");

        var unlinkedTasks = await runtime.GetUnlinkedTasksAsync();
        Assert.Single(unlinkedTasks);
        Assert.Equal("Unlinked Task", unlinkedTasks[0].Title);
    }

    [Fact]
    public async Task GetUnlinkedTasks_ExcludesCompletedAndCancelled()
    {
        await CreateTestTask(title: "Completed", status: "Completed");
        await CreateTestTask(title: "Cancelled", status: "Cancelled");
        var active = await CreateTestTask(title: "Active Unlinked");

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        var unlinkedTasks = await runtime.GetUnlinkedTasksAsync();
        Assert.Single(unlinkedTasks);
        Assert.Equal("Active Unlinked", unlinkedTasks[0].Title);
    }

    // ── LINK_TASK_TO_SPEC command handler ─────────────────────

    [Fact]
    public async Task LinkTaskToSpecHandler_Success()
    {
        var taskId = await CreateTestTask();
        CreateSpecDir("003-agent-system", "# Agent System\n\n## Purpose\nAgent execution.\n");

        var handler = new LinkTaskToSpecHandler();
        var (cmd, ctx) = MakeCommand("LINK_TASK_TO_SPEC", new()
        {
            ["taskId"] = taskId,
            ["specSectionId"] = "003-agent-system",
            ["linkType"] = "Implements"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("003-agent-system", result.Result!["specSectionId"]!.ToString());
    }

    [Fact]
    public async Task LinkTaskToSpecHandler_MissingTaskId_Error()
    {
        var handler = new LinkTaskToSpecHandler();
        var (cmd, ctx) = MakeCommand("LINK_TASK_TO_SPEC", new()
        {
            ["specSectionId"] = "003-agent-system"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("taskId", result.Error!);
    }

    [Fact]
    public async Task LinkTaskToSpecHandler_MissingSpecSectionId_Error()
    {
        var taskId = await CreateTestTask();
        var handler = new LinkTaskToSpecHandler();
        var (cmd, ctx) = MakeCommand("LINK_TASK_TO_SPEC", new()
        {
            ["taskId"] = taskId
        });

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("specSectionId", result.Error!);
    }

    [Fact]
    public async Task LinkTaskToSpecHandler_NonexistentSpec_Error()
    {
        var taskId = await CreateTestTask();
        var handler = new LinkTaskToSpecHandler();
        var (cmd, ctx) = MakeCommand("LINK_TASK_TO_SPEC", new()
        {
            ["taskId"] = taskId,
            ["specSectionId"] = "999-nonexistent"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("not found", result.Error!);
    }

    [Fact]
    public async Task LinkTaskToSpecHandler_NonexistentTask_Error()
    {
        CreateSpecDir("003-agent-system", "# Agent System\n\n## Purpose\nAgent execution.\n");
        var handler = new LinkTaskToSpecHandler();
        var (cmd, ctx) = MakeCommand("LINK_TASK_TO_SPEC", new()
        {
            ["taskId"] = "nonexistent",
            ["specSectionId"] = "003-agent-system"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Error, result.Status);
    }

    [Fact]
    public async Task LinkTaskToSpecHandler_WithNote()
    {
        var taskId = await CreateTestTask();
        CreateSpecDir("003-agent-system", "# Agent System\n\n## Purpose\nAgent execution.\n");

        var handler = new LinkTaskToSpecHandler();
        var (cmd, ctx) = MakeCommand("LINK_TASK_TO_SPEC", new()
        {
            ["taskId"] = taskId,
            ["specSectionId"] = "003-agent-system",
            ["linkType"] = "Modifies",
            ["note"] = "Adding spec-task linking section"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("Modifies", result.Result!["linkType"]!.ToString());
    }

    [Fact]
    public async Task LinkTaskToSpecHandler_InvalidLinkType_Error()
    {
        var taskId = await CreateTestTask();
        CreateSpecDir("003-agent-system", "# Agent System\n\n## Purpose\nAgent execution.\n");

        var handler = new LinkTaskToSpecHandler();
        var (cmd, ctx) = MakeCommand("LINK_TASK_TO_SPEC", new()
        {
            ["taskId"] = taskId,
            ["specSectionId"] = "003-agent-system",
            ["linkType"] = "InvalidType"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Invalid link type", result.Error!);
    }

    // ── SHOW_UNLINKED_CHANGES command handler ─────────────────

    [Fact]
    public async Task ShowUnlinkedChanges_NoUnlinked()
    {
        var taskId = await CreateTestTask();

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        await runtime.LinkTaskToSpecAsync(taskId, "003-agent-system", "engineer-1", "Hephaestus");

        var handler = new ShowUnlinkedChangesHandler();
        var (cmd, ctx) = MakeCommand("SHOW_UNLINKED_CHANGES", new());

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(0, result.Result!["count"]);
    }

    [Fact]
    public async Task ShowUnlinkedChanges_HasUnlinked()
    {
        await CreateTestTask(title: "Unlinked Task");

        var handler = new ShowUnlinkedChangesHandler();
        var (cmd, ctx) = MakeCommand("SHOW_UNLINKED_CHANGES", new());

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(1, result.Result!["count"]);
    }

    [Fact]
    public async Task ShowUnlinkedChanges_ExcludesCompleted()
    {
        await CreateTestTask(title: "Completed", status: "Completed");
        await CreateTestTask(title: "Active Unlinked");

        var handler = new ShowUnlinkedChangesHandler();
        var (cmd, ctx) = MakeCommand("SHOW_UNLINKED_CHANGES", new());

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(1, result.Result!["count"]);
    }

    // ── SpecManager.LoadSpecContextForTaskAsync ───────────────

    [Fact]
    public async Task LoadSpecContextForTask_MarksLinkedSections()
    {
        CreateSpecDir("001-domain-model", "# Domain Model\n\n## Purpose\nAll domain types.\n");
        CreateSpecDir("003-agent-system", "# Agent System\n\n## Purpose\nAgent execution.\n");

        var manager = new SpecManager(_specsDir);
        var result = await manager.LoadSpecContextForTaskAsync(["003-agent-system"]);

        Assert.NotNull(result);
        Assert.Contains("[★]", result);
        Assert.Contains("[ ]", result);
        // The linked one should have the star
        Assert.Contains("[★] specs/003-agent-system", result);
        Assert.Contains("[ ] specs/001-domain-model", result);
    }

    [Fact]
    public async Task LoadSpecContextForTask_EmptyIds_FallsBackToAll()
    {
        CreateSpecDir("001-domain-model", "# Domain Model\n\n## Purpose\nAll domain types.\n");

        var manager = new SpecManager(_specsDir);
        var fullResult = await manager.LoadSpecContextAsync();
        var taskResult = await manager.LoadSpecContextForTaskAsync([]);

        // Should fall back to the same as LoadSpecContextAsync
        Assert.Equal(fullResult, taskResult);
    }

    [Fact]
    public async Task LoadSpecContextForTask_AllLinked()
    {
        CreateSpecDir("001-domain-model", "# Domain Model\n\n## Purpose\nAll domain types.\n");
        CreateSpecDir("003-agent-system", "# Agent System\n\n## Purpose\nAgent execution.\n");

        var manager = new SpecManager(_specsDir);
        var result = await manager.LoadSpecContextForTaskAsync(
            ["001-domain-model", "003-agent-system"]);

        Assert.NotNull(result);
        Assert.DoesNotContain("[ ]", result);
        // Both should be starred
        var lines = result!.Split('\n');
        Assert.All(lines, line => Assert.Contains("[★]", line));
    }

    [Fact]
    public async Task LoadSpecContextForTask_NoSpecsDir_ReturnsNull()
    {
        var manager = new SpecManager(Path.Combine(_specsDir, "nonexistent"));
        var result = await manager.LoadSpecContextForTaskAsync(["003-agent-system"]);
        Assert.Null(result);
    }

    // ── Activity events ───────────────────────────────────────

    [Fact]
    public async Task LinkTaskToSpec_PublishesActivityEvent()
    {
        var taskId = await CreateTestTask();

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var activityBus = scope.ServiceProvider.GetRequiredService<ActivityBroadcaster>();

        ActivityEvent? captured = null;
        activityBus.Subscribe(e =>
        {
            if (e.Type == ActivityEventType.SpecTaskLinked) captured = e;
        });

        await runtime.LinkTaskToSpecAsync(taskId, "003-agent-system", "engineer-1", "Hephaestus");

        Assert.NotNull(captured);
        Assert.Contains("Hephaestus", captured!.Message);
        Assert.Contains("003-agent-system", captured.Message);
    }

    // ── All link types ────────────────────────────────────────

    [Theory]
    [InlineData("Implements")]
    [InlineData("Modifies")]
    [InlineData("Fixes")]
    [InlineData("References")]
    public async Task LinkTaskToSpec_AllValidLinkTypes(string linkType)
    {
        var taskId = await CreateTestTask();

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        var link = await runtime.LinkTaskToSpecAsync(
            taskId, "003-agent-system", "engineer-1", "Hephaestus", linkType: linkType);

        var expectedEnum = Enum.Parse<SpecLinkType>(linkType);
        Assert.Equal(expectedEnum, link.LinkType);
    }

    // ── Cascade delete ────────────────────────────────────────

    [Fact]
    public async Task SpecTaskLinks_CascadeDeleteWithTask()
    {
        var taskId = await CreateTestTask();

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        await runtime.LinkTaskToSpecAsync(taskId, "003-agent-system", "engineer-1", "Hephaestus");

        // Delete the task
        var task = await db.Tasks.FindAsync(taskId);
        db.Tasks.Remove(task!);
        await db.SaveChangesAsync();

        // Links should be gone
        var links = await db.SpecTaskLinks.Where(l => l.TaskId == taskId).ToListAsync();
        Assert.Empty(links);
    }

    // ── Unique constraint ─────────────────────────────────────

    [Fact]
    public async Task SpecTaskLinks_UniqueConstraint()
    {
        var taskId = await CreateTestTask();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        db.SpecTaskLinks.Add(new SpecTaskLinkEntity
        {
            Id = "link-1",
            TaskId = taskId,
            SpecSectionId = "003-agent-system",
            LinkType = "Implements",
            LinkedByAgentId = "engineer-1",
            LinkedByAgentName = "Hephaestus",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        db.SpecTaskLinks.Add(new SpecTaskLinkEntity
        {
            Id = "link-2",
            TaskId = taskId,
            SpecSectionId = "003-agent-system",
            LinkType = "Modifies",
            LinkedByAgentId = "engineer-1",
            LinkedByAgentName = "Hephaestus",
            CreatedAt = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ── Helpers ───────────────────────────────────────────────

    private async Task<string> CreateTestTask(
        string status = "Active",
        string title = "Test Task",
        string roomId = "room-1")
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var room = await db.Rooms.FindAsync(roomId);
        if (room == null)
        {
            db.Rooms.Add(new RoomEntity
            {
                Id = roomId,
                Name = "Test Room",
                Status = "Active",
                CurrentPhase = "Implementation",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        var taskId = $"task-{Guid.NewGuid():N}"[..20];
        db.Tasks.Add(new TaskEntity
        {
            Id = taskId,
            Title = title,
            Description = "Test task",
            SuccessCriteria = "",
            Status = status,
            Type = "Feature",
            CurrentPhase = "Implementation",
            CurrentPlan = "",
            ValidationStatus = "NotStarted",
            ValidationSummary = "",
            ImplementationStatus = "NotStarted",
            ImplementationSummary = "",
            PreferredRoles = "[]",
            RoomId = roomId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            FleetModels = "[]",
            TestsCreated = "[]"
        });
        await db.SaveChangesAsync();
        return taskId;
    }

    private void CreateSpecDir(string name, string content)
    {
        var dir = Path.Combine(_specsDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "spec.md"), content);
    }

    private (CommandEnvelope command, CommandContext context) MakeCommand(
        string commandName,
        Dictionary<string, string> args,
        string agentId = "engineer-1",
        string agentName = "Hephaestus",
        string agentRole = "SoftwareEngineer")
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
