using AgentAcademy.Server.Data;
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
        Assert.Contains(tools, t => t.Name == "list_agents");
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

        Assert.Equal(5, names.Count);
        Assert.Contains("list_tasks", names);
        Assert.Contains("list_rooms", names);
        Assert.Contains("list_agents", names);
        Assert.Contains("read_file", names);
        Assert.Contains("search_code", names);
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
        // Engineers have: chat, task-state, code
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["chat", "task-state", "code"]);

        Assert.Equal(5, tools.Count);
    }

    [Fact]
    public void GetToolsForAgent_MatchesTypicalPlannerAgent()
    {
        // Planners have: chat, task-state (no code)
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["chat", "task-state"]);

        Assert.Equal(3, tools.Count);
        Assert.DoesNotContain(tools, t => t.Name == "read_file");
        Assert.DoesNotContain(tools, t => t.Name == "search_code");
    }

    private static AgentToolRegistry CreateRegistry()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var toolFunctions = new AgentToolFunctions(
            scopeFactory,
            NullLogger<AgentToolFunctions>.Instance);
        return new AgentToolRegistry(
            toolFunctions,
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
        services.AddSingleton<ILogger<WorkspaceRuntime>>(NullLogger<WorkspaceRuntime>.Instance);
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
        Assert.Contains(tools, t => t.Name == "list_agents");
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
        var listAgents = tools.Single(t => t.Name == "list_agents");

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

public class AgentPermissionHandlerTests
{
    [Fact]
    public async Task Create_WithNoTools_ApprovesAll()
    {
        var handler = AgentPermissionHandler.Create(
            new HashSet<string>(),
            NullLogger.Instance);

        var result = await handler(
            CreatePermissionRequest("shell"),
            CreatePermissionInvocation("session-1"));

        // No tools = approve everything (same as ApproveAll)
        Assert.Equal(
            GitHub.Copilot.SDK.PermissionRequestResultKind.Approved,
            result.Kind);
    }

    [Fact]
    public async Task Create_WithRegisteredTools_ApprovesToolCall()
    {
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "list_tasks", "read_file" },
            NullLogger.Instance);

        var result = await handler(
            CreatePermissionRequest("custom-tool"),
            CreatePermissionInvocation("session-1"));

        Assert.Equal(
            GitHub.Copilot.SDK.PermissionRequestResultKind.Approved,
            result.Kind);
    }

    [Fact]
    public async Task Create_WithRegisteredTools_ApprovesReadKind()
    {
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "read_file" },
            NullLogger.Instance);

        var result = await handler(
            CreatePermissionRequest("read"),
            CreatePermissionInvocation("session-1"));

        Assert.Equal(
            GitHub.Copilot.SDK.PermissionRequestResultKind.Approved,
            result.Kind);
    }

    [Fact]
    public async Task Create_WithRegisteredTools_DeniesShellKind()
    {
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "list_tasks" },
            NullLogger.Instance);

        var result = await handler(
            CreatePermissionRequest("shell"),
            CreatePermissionInvocation("session-1"));

        Assert.Equal(
            GitHub.Copilot.SDK.PermissionRequestResultKind.DeniedByRules,
            result.Kind);
    }

    [Fact]
    public async Task Create_WithRegisteredTools_DeniesWriteKind()
    {
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "list_tasks" },
            NullLogger.Instance);

        var result = await handler(
            CreatePermissionRequest("write"),
            CreatePermissionInvocation("session-1"));

        Assert.Equal(
            GitHub.Copilot.SDK.PermissionRequestResultKind.DeniedByRules,
            result.Kind);
    }

    [Fact]
    public async Task Create_WithRegisteredTools_DeniesUrlKind()
    {
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "list_tasks" },
            NullLogger.Instance);

        var result = await handler(
            CreatePermissionRequest("url"),
            CreatePermissionInvocation("session-1"));

        Assert.Equal(
            GitHub.Copilot.SDK.PermissionRequestResultKind.DeniedByRules,
            result.Kind);
    }

    [Fact]
    public async Task Create_MultipleSafeKinds_AllApproved()
    {
        var handler = AgentPermissionHandler.Create(
            new HashSet<string> { "search_code" },
            NullLogger.Instance);

        foreach (var kind in new[] { "custom-tool", "read", "tool" })
        {
            var result = await handler(
                CreatePermissionRequest(kind),
                CreatePermissionInvocation("session-1"));

            Assert.Equal(
                GitHub.Copilot.SDK.PermissionRequestResultKind.Approved,
                result.Kind);
        }
    }

    private static GitHub.Copilot.SDK.PermissionRequest CreatePermissionRequest(string kind)
    {
        return new GitHub.Copilot.SDK.PermissionRequest { Kind = kind };
    }

    private static GitHub.Copilot.SDK.PermissionInvocation CreatePermissionInvocation(string sessionId)
    {
        return new GitHub.Copilot.SDK.PermissionInvocation { SessionId = sessionId };
    }
}
