using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public class AgentToolRegistryTests
{
    [Fact]
    public void GetToolsForAgent_TaskStateGroup_ReturnsThreeTools()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["task-state"]);

        Assert.Equal(3, tools.Count);
        Assert.Contains(tools, t => t.Name == "list_tasks");
        Assert.Contains(tools, t => t.Name == "list_rooms");
        Assert.Contains(tools, t => t.Name == "show_agents");
    }

    [Fact]
    public void GetToolsForAgent_CodeGroup_ReturnsTwoTools()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["code"]);

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Name == "read_file");
        Assert.Contains(tools, t => t.Name == "search_code");
    }

    [Fact]
    public void GetToolsForAgent_BothGroups_ReturnsFiveTools()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["task-state", "code"]);

        Assert.Equal(5, tools.Count);
    }

    [Fact]
    public void GetToolsForAgent_ChatGroup_ReturnsNoTools()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["chat"]);

        Assert.Empty(tools);
    }

    [Fact]
    public void GetToolsForAgent_EmptyGroups_ReturnsNoTools()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent([]);

        Assert.Empty(tools);
    }

    [Fact]
    public void GetToolsForAgent_UnknownGroup_ReturnsNoTools()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["nonexistent"]);

        Assert.Empty(tools);
    }

    [Fact]
    public void GetToolsForAgent_DuplicateGroups_NoDuplicateTools()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["task-state", "task-state"]);

        Assert.Equal(3, tools.Count);
        Assert.Equal(3, tools.Select(t => t.Name).Distinct().Count());
    }

    [Fact]
    public void GetToolsForAgent_CaseInsensitiveGroupNames()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["TASK-STATE", "Code"]);

        Assert.Equal(5, tools.Count);
    }

    [Fact]
    public void GetAllToolNames_ReturnsAllRegisteredNames()
    {
        var registry = CreateRegistry();
        var names = registry.GetAllToolNames();

        // 5 static + 7 contextual = 12 total
        Assert.Equal(12, names.Count);
        Assert.Contains("list_tasks", names);
        Assert.Contains("list_rooms", names);
        Assert.Contains("show_agents", names);
        Assert.Contains("read_file", names);
        Assert.Contains("search_code", names);
        Assert.Contains("create_task", names);
        Assert.Contains("update_task_status", names);
        Assert.Contains("add_task_comment", names);
        Assert.Contains("remember", names);
        Assert.Contains("recall", names);
        Assert.Contains("write_file", names);
        Assert.Contains("commit_changes", names);
    }

    [Fact]
    public void GetToolsForAgent_ToolsHaveDescriptions()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["task-state", "code"]);

        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description),
                $"Tool '{tool.Name}' should have a description");
        }
    }

    [Fact]
    public void GetToolsForAgent_MatchesTypicalEngineerAgent()
    {
        // Engineers have: chat, task-state, code, code-write, task-write, memory
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(
            ["chat", "task-state", "code", "code-write", "task-write", "memory"],
            "eng-1", "Engineer");

        // 5 static + 2 code-write + 3 task-write + 2 memory = 12
        Assert.Equal(12, tools.Count);
    }

    [Fact]
    public void GetToolsForAgent_MatchesTypicalPlannerAgent()
    {
        // Planners have: chat, task-state, task-write, memory (no code)
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(
            ["chat", "task-state", "task-write", "memory"],
            "planner-1", "Planner");

        // 3 static + 3 task-write + 2 memory = 8
        Assert.Equal(8, tools.Count);
        Assert.DoesNotContain(tools, t => t.Name == "read_file");
        Assert.DoesNotContain(tools, t => t.Name == "search_code");
    }

    [Fact]
    public void GetToolsForAgent_TaskWriteGroup_ReturnsThreeTools()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["task-write"], "agent-1", "Alpha");

        Assert.Equal(3, tools.Count);
        Assert.Contains(tools, t => t.Name == "create_task");
        Assert.Contains(tools, t => t.Name == "update_task_status");
        Assert.Contains(tools, t => t.Name == "add_task_comment");
    }

    [Fact]
    public void GetToolsForAgent_MemoryGroup_ReturnsTwoTools()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["memory"], "agent-1", "Alpha");

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Name == "remember");
        Assert.Contains(tools, t => t.Name == "recall");
    }

    [Fact]
    public void GetToolsForAgent_CodeWriteGroup_ReturnsWriteAndCommitTools()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["code-write"], "agent-1", "Alpha");

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Name == "write_file");
        Assert.Contains(tools, t => t.Name == "commit_changes");
    }

    [Fact]
    public void GetToolsForAgent_ContextualGroupWithoutAgentId_ReturnsNoTools()
    {
        var registry = CreateRegistry();
        // No agentId provided — contextual groups should be skipped
        var tools = registry.GetToolsForAgent(["task-write", "memory"]);

        Assert.Empty(tools);
    }

    [Fact]
    public void GetToolsForAgent_AllGroups_NoDuplicates()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(
            ["task-state", "code", "code-write", "task-write", "memory", "task-write", "memory"],
            "agent-1", "Alpha");

        var names = tools.Select(t => t.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(12, tools.Count);
    }

    private static AgentToolRegistry CreateRegistry()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var toolFunctions = new AgentToolFunctions(
            scopeFactory,
            NullLogger<AgentToolFunctions>.Instance);
        var catalog = new AgentCatalogOptions("main", "Main", []);
        return new AgentToolRegistry(
            toolFunctions,
            catalog,
            NullLogger<AgentToolRegistry>.Instance);
    }
}

public class AgentToolFunctionsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentToolFunctions _toolFunctions;

    public AgentToolFunctionsTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton(new AgentCatalogOptions(
            "main", "Main Room", new List<AgentDefinition>
            {
                new("agent-1", "Alpha", "Engineer", "Test agent", "prompt",
                    "gpt-5", ["planning"], ["chat", "task-state", "code"],
                    true),
                new("agent-2", "Beta", "Reviewer", "Test agent 2", "prompt",
                    "gpt-5", ["review"], ["chat", "task-state"],
                    true),
            }));
        services.AddSingleton<ILogger<TaskQueryService>>(NullLogger<TaskQueryService>.Instance);
        services.AddSingleton<ILogger<WorkspaceRuntime>>(NullLogger<WorkspaceRuntime>.Instance);
        services.AddScoped<TaskQueryService>();
        services.AddScoped<WorkspaceRuntime>();
        services.AddScoped<SystemSettingsService>();
        services.AddSingleton<IAgentExecutor>(Substitute.For<IAgentExecutor>());
        services.AddSingleton<ILogger<ConversationSessionService>>(NullLogger<ConversationSessionService>.Instance);
        services.AddScoped<ConversationSessionService>();

        _serviceProvider = services.BuildServiceProvider();

        // Initialize the workspace (creates DB tables and seeds default room)
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Database.EnsureCreated();
            var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
            runtime.InitializeAsync().GetAwaiter().GetResult();
        }

        _toolFunctions = new AgentToolFunctions(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AgentToolFunctions>.Instance);
    }

    [Fact]
    public void CreateTaskStateTools_ReturnsThreeTools()
    {
        var tools = _toolFunctions.CreateTaskStateTools();
        Assert.Equal(3, tools.Count);
        Assert.Contains(tools, t => t.Name == "list_tasks");
        Assert.Contains(tools, t => t.Name == "list_rooms");
        Assert.Contains(tools, t => t.Name == "show_agents");
    }

    [Fact]
    public void CreateCodeTools_ReturnsTwoTools()
    {
        var tools = _toolFunctions.CreateCodeTools();
        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Name == "read_file");
        Assert.Contains(tools, t => t.Name == "search_code");
    }

    [Fact]
    public async Task ListTasks_EmptyWorkspace_ReturnsNoTasks()
    {
        var tools = _toolFunctions.CreateTaskStateTools();
        var listTasks = tools.Single(t => t.Name == "list_tasks");

        var result = await listTasks.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());
        var text = result?.ToString() ?? "";

        Assert.Contains("No tasks found", text);
    }

    [Fact]
    public async Task ListRooms_ReturnsDefaultRoom()
    {
        var tools = _toolFunctions.CreateTaskStateTools();
        var listRooms = tools.Single(t => t.Name == "list_rooms");

        var result = await listRooms.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());
        var text = result?.ToString() ?? "";

        Assert.Contains("Rooms", text);
        Assert.Contains("Main", text);
    }

    [Fact]
    public async Task ListAgents_ReturnsCatalogAgents()
    {
        var tools = _toolFunctions.CreateTaskStateTools();
        var listAgents = tools.Single(t => t.Name == "show_agents");

        var result = await listAgents.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());
        var text = result?.ToString() ?? "";

        Assert.Contains("Alpha", text);
        Assert.Contains("Beta", text);
        Assert.Contains("Engineer", text);
        Assert.Contains("Reviewer", text);
    }

    [Fact]
    public async Task ReadFile_ExistingFile_ReturnsContent()
    {
        var tools = _toolFunctions.CreateCodeTools();
        var readFile = tools.Single(t => t.Name == "read_file");

        var result = await readFile.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>()
        {
            ["path"] = "AgentAcademy.sln"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("AgentAcademy", text);
        Assert.Contains("File:", text);
    }

    [Fact]
    public async Task ReadFile_NonexistentFile_ReturnsError()
    {
        var tools = _toolFunctions.CreateCodeTools();
        var readFile = tools.Single(t => t.Name == "read_file");

        var result = await readFile.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>()
        {
            ["path"] = "nonexistent/file.txt"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Error", text);
        Assert.Contains("not found", text);
    }

    [Fact]
    public async Task ReadFile_PathTraversal_ReturnsDenied()
    {
        var tools = _toolFunctions.CreateCodeTools();
        var readFile = tools.Single(t => t.Name == "read_file");

        var result = await readFile.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>()
        {
            ["path"] = "../../etc/passwd"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("denied", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadFile_Directory_ListsEntries()
    {
        var tools = _toolFunctions.CreateCodeTools();
        var readFile = tools.Single(t => t.Name == "read_file");

        var result = await readFile.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>()
        {
            ["path"] = "src"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Directory", text);
        Assert.Contains("AgentAcademy.Server", text);
    }

    [Fact]
    public async Task ReadFile_WithLineRange_ReturnsSubset()
    {
        var tools = _toolFunctions.CreateCodeTools();
        var readFile = tools.Single(t => t.Name == "read_file");

        var result = await readFile.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>()
        {
            ["path"] = "AgentAcademy.sln",
            ["startLine"] = 1,
            ["endLine"] = 3,
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("showing 1-3", text);
    }

    [Fact]
    public async Task SearchCode_FindsResults()
    {
        var tools = _toolFunctions.CreateCodeTools();
        var searchCode = tools.Single(t => t.Name == "search_code");

        var result = await searchCode.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>()
        {
            ["query"] = "IAgentExecutor"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("IAgentExecutor", text);
        Assert.Contains("Search results", text);
    }

    [Fact]
    public async Task SearchCode_NoResults_ReturnsMessage()
    {
        var tools = _toolFunctions.CreateCodeTools();
        var searchCode = tools.Single(t => t.Name == "search_code");

        // Search a narrow path to avoid matching this test file itself
        var result = await searchCode.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>()
        {
            ["query"] = "zzz_no_such_pattern_ever_zzz",
            ["path"] = "docs"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("No results found", text);
    }

    [Fact]
    public async Task SearchCode_WithGlob_FiltersResults()
    {
        var tools = _toolFunctions.CreateCodeTools();
        var searchCode = tools.Single(t => t.Name == "search_code");

        var result = await searchCode.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>()
        {
            ["query"] = "namespace",
            ["glob"] = "*.cs"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Search results", text);
        Assert.Contains(".cs", text);
    }

    [Fact]
    public async Task SearchCode_PathTraversal_ReturnsDenied()
    {
        var tools = _toolFunctions.CreateCodeTools();
        var searchCode = tools.Single(t => t.Name == "search_code");

        var result = await searchCode.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>()
        {
            ["query"] = "root",
            ["path"] = "../../etc"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("denied", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindProjectRoot_ReturnsValidDirectory()
    {
        var root = AgentToolFunctions.FindProjectRoot();
        Assert.True(File.Exists(Path.Combine(root, "AgentAcademy.sln")),
            $"Expected AgentAcademy.sln in {root}");
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}

public class AgentWriteToolTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentToolFunctions _toolFunctions;

    public AgentWriteToolTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton(new AgentCatalogOptions(
            "main", "Main Room", new List<AgentDefinition>
            {
                new("agent-1", "Alpha", "Engineer", "Test agent", "prompt",
                    "gpt-5", ["planning"], ["chat", "task-state", "code", "task-write", "memory"],
                    true),
                new("agent-2", "Beta", "Reviewer", "Test agent 2", "prompt",
                    "gpt-5", ["review"], ["chat", "task-state", "task-write", "memory"],
                    true),
            }));
        services.AddSingleton<ILogger<TaskQueryService>>(NullLogger<TaskQueryService>.Instance);
        services.AddSingleton<ILogger<WorkspaceRuntime>>(NullLogger<WorkspaceRuntime>.Instance);
        services.AddScoped<TaskQueryService>();
        services.AddScoped<WorkspaceRuntime>();
        services.AddScoped<SystemSettingsService>();
        services.AddSingleton<IAgentExecutor>(Substitute.For<IAgentExecutor>());
        services.AddSingleton<ILogger<ConversationSessionService>>(NullLogger<ConversationSessionService>.Instance);
        services.AddScoped<ConversationSessionService>();

        _serviceProvider = services.BuildServiceProvider();

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Database.EnsureCreated();
            var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
            runtime.InitializeAsync().GetAwaiter().GetResult();
        }

        _toolFunctions = new AgentToolFunctions(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AgentToolFunctions>.Instance);
    }

    // ── Task-write tool creation tests ──────────────────────────

    [Fact]
    public void CreateTaskWriteTools_ReturnsThreeTools()
    {
        var tools = _toolFunctions.CreateTaskWriteTools("agent-1", "Alpha");
        Assert.Equal(3, tools.Count);
        Assert.Contains(tools, t => t.Name == "create_task");
        Assert.Contains(tools, t => t.Name == "update_task_status");
        Assert.Contains(tools, t => t.Name == "add_task_comment");
    }

    [Fact]
    public void CreateMemoryTools_ReturnsTwoTools()
    {
        var tools = _toolFunctions.CreateMemoryTools("agent-1");
        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Name == "remember");
        Assert.Contains(tools, t => t.Name == "recall");
    }

    [Fact]
    public void WriteTools_AllHaveDescriptions()
    {
        var taskWrite = _toolFunctions.CreateTaskWriteTools("agent-1", "Alpha");
        var memory = _toolFunctions.CreateMemoryTools("agent-1");

        foreach (var tool in taskWrite.Concat(memory))
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description),
                $"Tool '{tool.Name}' should have a description");
        }
    }

    // ── create_task ─────────────────────────────────────────────

    [Fact]
    public async Task CreateTask_ValidInput_ReturnsSuccess()
    {
        var tools = _toolFunctions.CreateTaskWriteTools("agent-1", "Alpha");
        var createTask = tools.Single(t => t.Name == "create_task");

        var result = await createTask.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["title"] = "Test Task",
            ["description"] = "A test task description",
            ["successCriteria"] = "Tests pass"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Task created successfully", text);
        Assert.Contains("Test Task", text);
        Assert.Contains("ID:", text);
    }

    [Fact]
    public async Task CreateTask_MissingTitle_ReturnsError()
    {
        var tools = _toolFunctions.CreateTaskWriteTools("agent-1", "Alpha");
        var createTask = tools.Single(t => t.Name == "create_task");

        var result = await createTask.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["title"] = "",
            ["description"] = "desc",
            ["successCriteria"] = "criteria"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Error", text);
        Assert.Contains("title", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateTask_InvalidType_ReturnsError()
    {
        var tools = _toolFunctions.CreateTaskWriteTools("agent-1", "Alpha");
        var createTask = tools.Single(t => t.Name == "create_task");

        var result = await createTask.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["title"] = "Test",
            ["description"] = "desc",
            ["successCriteria"] = "criteria",
            ["type"] = "InvalidType"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Error", text);
        Assert.Contains("Invalid task type", text);
    }

    [Fact]
    public async Task CreateTask_WithType_UsesSpecifiedType()
    {
        var tools = _toolFunctions.CreateTaskWriteTools("agent-1", "Alpha");
        var createTask = tools.Single(t => t.Name == "create_task");

        var result = await createTask.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["title"] = "Fix Login Bug",
            ["description"] = "Login fails on empty password",
            ["successCriteria"] = "Login works",
            ["type"] = "Bug"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Task created successfully", text);
        Assert.Contains("Bug", text);
    }

    // ── update_task_status ──────────────────────────────────────

    [Fact]
    public async Task UpdateTaskStatus_ValidStatus_ReturnsSuccess()
    {
        // First create a task to update
        var taskId = await CreateTestTask();

        var tools = _toolFunctions.CreateTaskWriteTools("agent-1", "Alpha");
        var updateStatus = tools.Single(t => t.Name == "update_task_status");

        var result = await updateStatus.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["taskId"] = taskId,
            ["status"] = "InReview"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("updated", text);
        Assert.Contains("InReview", text);
    }

    [Fact]
    public async Task UpdateTaskStatus_InvalidStatus_ReturnsError()
    {
        var taskId = await CreateTestTask();

        var tools = _toolFunctions.CreateTaskWriteTools("agent-1", "Alpha");
        var updateStatus = tools.Single(t => t.Name == "update_task_status");

        var result = await updateStatus.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["taskId"] = taskId,
            ["status"] = "Completed" // Not in allowed statuses
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Error", text);
        Assert.Contains("Invalid status", text);
    }

    [Fact]
    public async Task UpdateTaskStatus_Blocker_SetsBlockedStatus()
    {
        var taskId = await CreateTestTask();

        var tools = _toolFunctions.CreateTaskWriteTools("agent-1", "Alpha");
        var updateStatus = tools.Single(t => t.Name == "update_task_status");

        var result = await updateStatus.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["taskId"] = taskId,
            ["blocker"] = "Waiting for API key"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Blocked", text);
        Assert.Contains("Waiting for API key", text);
    }

    [Fact]
    public async Task UpdateTaskStatus_BlockerAndStatus_ReturnsError()
    {
        var taskId = await CreateTestTask();

        var tools = _toolFunctions.CreateTaskWriteTools("agent-1", "Alpha");
        var updateStatus = tools.Single(t => t.Name == "update_task_status");

        var result = await updateStatus.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["taskId"] = taskId,
            ["status"] = "Active",
            ["blocker"] = "Something"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Error", text);
        Assert.Contains("Cannot specify both", text);
    }

    [Fact]
    public async Task UpdateTaskStatus_NoteOnly_PostsNote()
    {
        var taskId = await CreateTestTask();

        var tools = _toolFunctions.CreateTaskWriteTools("agent-1", "Alpha");
        var updateStatus = tools.Single(t => t.Name == "update_task_status");

        var result = await updateStatus.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["taskId"] = taskId,
            ["note"] = "Making good progress"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("note posted", text);
    }

    [Fact]
    public async Task UpdateTaskStatus_NonexistentTask_ReturnsError()
    {
        var tools = _toolFunctions.CreateTaskWriteTools("agent-1", "Alpha");
        var updateStatus = tools.Single(t => t.Name == "update_task_status");

        var result = await updateStatus.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["taskId"] = "nonexistent-task-id",
            ["status"] = "Active"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Error", text);
        Assert.Contains("not found", text);
    }

    [Fact]
    public async Task UpdateTaskStatus_NoArguments_ReturnsError()
    {
        var tools = _toolFunctions.CreateTaskWriteTools("agent-1", "Alpha");
        var updateStatus = tools.Single(t => t.Name == "update_task_status");

        var result = await updateStatus.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["taskId"] = "some-id"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Error", text);
        Assert.Contains("At least one", text);
    }

    // ── add_task_comment ────────────────────────────────────────

    [Fact]
    public async Task AddTaskComment_ValidInput_ReturnsSuccess()
    {
        var taskId = await CreateTestTask();

        var tools = _toolFunctions.CreateTaskWriteTools("agent-1", "Alpha");
        var addComment = tools.Single(t => t.Name == "add_task_comment");

        var result = await addComment.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["taskId"] = taskId,
            ["content"] = "Found a race condition in the handler"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Comment added", text);
        Assert.Contains("Comment", text);
    }

    [Fact]
    public async Task AddTaskComment_WithFindingType_ReturnsCorrectType()
    {
        var taskId = await CreateTestTask();

        var tools = _toolFunctions.CreateTaskWriteTools("agent-1", "Alpha");
        var addComment = tools.Single(t => t.Name == "add_task_comment");

        var result = await addComment.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["taskId"] = taskId,
            ["content"] = "Memory leak in connection pool",
            ["commentType"] = "Finding"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Finding", text);
    }

    [Fact]
    public async Task AddTaskComment_InvalidType_ReturnsError()
    {
        var tools = _toolFunctions.CreateTaskWriteTools("agent-1", "Alpha");
        var addComment = tools.Single(t => t.Name == "add_task_comment");

        var result = await addComment.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["taskId"] = "some-id",
            ["content"] = "test",
            ["commentType"] = "InvalidType"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Error", text);
        Assert.Contains("Invalid comment type", text);
    }

    [Fact]
    public async Task AddTaskComment_NonexistentTask_ReturnsError()
    {
        var tools = _toolFunctions.CreateTaskWriteTools("agent-1", "Alpha");
        var addComment = tools.Single(t => t.Name == "add_task_comment");

        var result = await addComment.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["taskId"] = "nonexistent",
            ["content"] = "test"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Error", text);
        Assert.Contains("not found", text);
    }

    // ── remember ────────────────────────────────────────────────

    [Fact]
    public async Task Remember_ValidInput_CreatesMemory()
    {
        var tools = _toolFunctions.CreateMemoryTools("agent-1");
        var remember = tools.Single(t => t.Name == "remember");

        var result = await remember.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["key"] = "auth-pattern",
            ["value"] = "Use JWT tokens with 1h expiry",
            ["category"] = "pattern"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Memory created", text);
        Assert.Contains("auth-pattern", text);
    }

    [Fact]
    public async Task Remember_Upsert_UpdatesExisting()
    {
        var tools = _toolFunctions.CreateMemoryTools("agent-1");
        var remember = tools.Single(t => t.Name == "remember");

        // Create
        await remember.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["key"] = "db-convention",
            ["value"] = "Use snake_case",
            ["category"] = "pattern"
        }));

        // Update
        var result = await remember.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["key"] = "db-convention",
            ["value"] = "Use PascalCase for entity names",
            ["category"] = "decision"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Memory updated", text);
    }

    [Fact]
    public async Task Remember_WithTtl_SetsExpiry()
    {
        var tools = _toolFunctions.CreateMemoryTools("agent-1");
        var remember = tools.Single(t => t.Name == "remember");

        var result = await remember.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["key"] = "temp-note",
            ["value"] = "Server down for maintenance",
            ["category"] = "incident",
            ["ttl"] = 24
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("expires", text);
    }

    [Fact]
    public async Task Remember_InvalidCategory_ReturnsError()
    {
        var tools = _toolFunctions.CreateMemoryTools("agent-1");
        var remember = tools.Single(t => t.Name == "remember");

        var result = await remember.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["key"] = "test",
            ["value"] = "test",
            ["category"] = "invalid-category"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Error", text);
        Assert.Contains("Invalid category", text);
    }

    [Fact]
    public async Task Remember_InvalidTtl_ReturnsError()
    {
        var tools = _toolFunctions.CreateMemoryTools("agent-1");
        var remember = tools.Single(t => t.Name == "remember");

        var result = await remember.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["key"] = "test",
            ["value"] = "test",
            ["category"] = "pattern",
            ["ttl"] = -1
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("Error", text);
        Assert.Contains("ttl", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remember_PermanentFlag_ClearsTtl()
    {
        var tools = _toolFunctions.CreateMemoryTools("agent-1");
        var remember = tools.Single(t => t.Name == "remember");
        var recall = tools.Single(t => t.Name == "recall");

        // Create with TTL
        await remember.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["key"] = "temp-to-perm",
            ["value"] = "Was temporary",
            ["category"] = "decision",
            ["ttl"] = 1
        }));

        // Promote to permanent
        var result = await remember.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["key"] = "temp-to-perm",
            ["value"] = "Now permanent",
            ["category"] = "decision",
            ["permanent"] = true
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("updated", text);
        Assert.Contains("permanent", text);

        // Verify it shows up without expiry warning
        var recallResult = await recall.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["key"] = "temp-to-perm"
        }));
        var recallText = recallResult?.ToString() ?? "";
        Assert.Contains("Now permanent", recallText);
        Assert.DoesNotContain("expires", recallText);
    }

    [Fact]
    public async Task Recall_CategoryCaseInsensitive_FindsResults()
    {
        var tools = _toolFunctions.CreateMemoryTools("agent-1");
        var remember = tools.Single(t => t.Name == "remember");
        var recall = tools.Single(t => t.Name == "recall");

        // Store with lowercase (remember normalizes)
        await remember.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["key"] = "case-test",
            ["value"] = "Case test value",
            ["category"] = "Pattern" // Will be stored as "pattern"
        }));

        // Recall with mixed case — should still find it
        var result = await recall.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["category"] = "PATTERN" // RecallAsync normalizes to "pattern"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("case-test", text);
    }

    // ── recall ──────────────────────────────────────────────────

    [Fact]
    public async Task Recall_EmptyMemories_ReturnsNoResults()
    {
        var tools = _toolFunctions.CreateMemoryTools("agent-1");
        var recall = tools.Single(t => t.Name == "recall");

        var result = await recall.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()));
        var text = result?.ToString() ?? "";

        Assert.Contains("No memories found", text);
    }

    [Fact]
    public async Task Recall_AfterRemember_FindsMemory()
    {
        var tools = _toolFunctions.CreateMemoryTools("agent-1");
        var remember = tools.Single(t => t.Name == "remember");
        var recall = tools.Single(t => t.Name == "recall");

        // Store a memory
        await remember.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["key"] = "api-convention",
            ["value"] = "All endpoints return ProblemDetails on error",
            ["category"] = "pattern"
        }));

        // Recall all
        var result = await recall.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()));
        var text = result?.ToString() ?? "";

        Assert.Contains("api-convention", text);
        Assert.Contains("ProblemDetails", text);
    }

    [Fact]
    public async Task Recall_ByCategoryFilter_FiltersCorrectly()
    {
        var tools = _toolFunctions.CreateMemoryTools("agent-1");
        var remember = tools.Single(t => t.Name == "remember");
        var recall = tools.Single(t => t.Name == "recall");

        await remember.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["key"] = "my-pattern",
            ["value"] = "Pattern value",
            ["category"] = "pattern"
        }));
        await remember.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["key"] = "my-decision",
            ["value"] = "Decision value",
            ["category"] = "decision"
        }));

        var result = await recall.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["category"] = "decision"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("my-decision", text);
        Assert.DoesNotContain("my-pattern", text);
    }

    [Fact]
    public async Task Recall_ByExactKey_ReturnsMatch()
    {
        var tools = _toolFunctions.CreateMemoryTools("agent-1");
        var remember = tools.Single(t => t.Name == "remember");
        var recall = tools.Single(t => t.Name == "recall");

        await remember.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["key"] = "specific-key",
            ["value"] = "Specific value",
            ["category"] = "lesson"
        }));

        var result = await recall.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["key"] = "specific-key"
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("specific-key", text);
        Assert.Contains("Specific value", text);
    }

    [Fact]
    public async Task Recall_AgentIsolation_DoesNotSeeOtherAgentMemories()
    {
        // Agent 1 stores a memory
        var tools1 = _toolFunctions.CreateMemoryTools("agent-1");
        var remember1 = tools1.Single(t => t.Name == "remember");
        await remember1.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["key"] = "agent1-secret",
            ["value"] = "Only agent 1 should see this",
            ["category"] = "decision"
        }));

        // Agent 2 recalls — should not see agent 1's memory
        var tools2 = _toolFunctions.CreateMemoryTools("agent-2");
        var recall2 = tools2.Single(t => t.Name == "recall");
        var result = await recall2.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()));
        var text = result?.ToString() ?? "";

        Assert.DoesNotContain("agent1-secret", text);
    }

    [Fact]
    public async Task Recall_SharedCategory_VisibleToOtherAgents()
    {
        // Agent 1 stores a shared memory
        var tools1 = _toolFunctions.CreateMemoryTools("agent-1");
        var remember1 = tools1.Single(t => t.Name == "remember");
        await remember1.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["key"] = "team-convention",
            ["value"] = "Always use async/await",
            ["category"] = "shared"
        }));

        // Agent 2 recalls — should see shared memories
        var tools2 = _toolFunctions.CreateMemoryTools("agent-2");
        var recall2 = tools2.Single(t => t.Name == "recall");
        var result = await recall2.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()));
        var text = result?.ToString() ?? "";

        Assert.Contains("team-convention", text);
        Assert.Contains("from agent-1", text);
    }

    [Fact]
    public async Task Recall_ExpiredMemory_ExcludedByDefault()
    {
        // Directly insert an expired memory
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.AgentMemories.Add(new AgentMemoryEntity
            {
                AgentId = "agent-1",
                Key = "expired-info",
                Category = "incident",
                Value = "This expired yesterday",
                CreatedAt = DateTime.UtcNow.AddDays(-2),
                ExpiresAt = DateTime.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }

        var tools = _toolFunctions.CreateMemoryTools("agent-1");
        var recall = tools.Single(t => t.Name == "recall");

        var result = await recall.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()));
        var text = result?.ToString() ?? "";

        Assert.DoesNotContain("expired-info", text);
    }

    [Fact]
    public async Task Recall_ExpiredMemory_IncludedWhenRequested()
    {
        // Directly insert an expired memory
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.AgentMemories.Add(new AgentMemoryEntity
            {
                AgentId = "agent-1",
                Key = "expired-but-requested",
                Category = "incident",
                Value = "This expired but user wants to see it",
                CreatedAt = DateTime.UtcNow.AddDays(-2),
                ExpiresAt = DateTime.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }

        var tools = _toolFunctions.CreateMemoryTools("agent-1");
        var recall = tools.Single(t => t.Name == "recall");

        var result = await recall.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["includeExpired"] = true
        }));
        var text = result?.ToString() ?? "";

        Assert.Contains("expired-but-requested", text);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private async Task<string> CreateTestTask()
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var result = await runtime.CreateTaskAsync(new TaskAssignmentRequest(
            Title: "Test Task",
            Description: "Test task for write tool tests",
            SuccessCriteria: "Tests pass",
            RoomId: null,
            PreferredRoles: ["Engineer"]
        ));
        return result.Task.Id;
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
