using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public class AgentToolRegistryComprehensiveTests
{
    private const string AgentId = "eng-1";
    private const string AgentName = "Engineer";

    private static readonly AgentDefinition TestAgent = new(
        AgentId, AgentName, "SoftwareEngineer", "Test engineer", "prompt", null,
        ["coding"],
        ["task-state", "code", "task-write", "memory", "code-write"],
        true,
        new AgentGitIdentity("Engineer", "eng@test.com"));

    private static readonly AgentCatalogOptions DefaultCatalog = new(
        "main", "Main Room", [TestAgent]);

    private static AgentToolRegistry CreateRegistry(AgentCatalogOptions? catalog = null)
    {
        catalog ??= DefaultCatalog;
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var toolFunctions = new AgentToolFunctions(
            scopeFactory,
            catalog,
            NullLogger<AgentToolFunctions>.Instance);
        return new AgentToolRegistry(
            toolFunctions,
            catalog,
            NullLogger<AgentToolRegistry>.Instance);
    }

    // ── Constructor / initialization ─────────────────────────────

    [Fact]
    public void Constructor_InitializesStaticGroups()
    {
        var registry = CreateRegistry();

        // Static groups are eagerly created — requesting them without an
        // agentId must succeed and return the correct tool count.
        var taskStateTools = registry.GetToolsForAgent(["task-state"]);
        var codeTools = registry.GetToolsForAgent(["code"]);

        Assert.Equal(3, taskStateTools.Count);
        Assert.Equal(2, codeTools.Count);
    }

    // ── GetAllToolNames ──────────────────────────────────────────

    [Fact]
    public void GetAllToolNames_ContainsStaticAndContextualNames()
    {
        var registry = CreateRegistry();
        var names = registry.GetAllToolNames();

        // 5 static (list_tasks, list_rooms, show_agents, read_file, search_code)
        // + 7 contextual (create_task, update_task_status, add_task_comment,
        //                  remember, recall, write_file, commit_changes)
        // = 12 total
        Assert.Equal(12, names.Count);
    }

    [Fact]
    public void GetAllToolNames_ContainsExpectedStaticTools()
    {
        var registry = CreateRegistry();
        var names = registry.GetAllToolNames();

        Assert.Contains("list_tasks", names);
        Assert.Contains("list_rooms", names);
        Assert.Contains("show_agents", names);
        Assert.Contains("read_file", names);
        Assert.Contains("search_code", names);
    }

    [Fact]
    public void GetAllToolNames_ContainsExpectedContextualNames()
    {
        var registry = CreateRegistry();
        var names = registry.GetAllToolNames();

        Assert.Contains("create_task", names);
        Assert.Contains("update_task_status", names);
        Assert.Contains("add_task_comment", names);
        Assert.Contains("remember", names);
        Assert.Contains("recall", names);
        Assert.Contains("write_file", names);
        Assert.Contains("commit_changes", names);
    }

    // ── GetToolsForAgent — static groups ─────────────────────────

    [Fact]
    public void GetToolsForAgent_TaskStateGroup_ReturnsReadOnlyTools()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["task-state"]);

        Assert.Equal(3, tools.Count);
        Assert.Contains(tools, t => t.Name == "list_tasks");
        Assert.Contains(tools, t => t.Name == "list_rooms");
        Assert.Contains(tools, t => t.Name == "show_agents");
    }

    [Fact]
    public void GetToolsForAgent_CodeGroup_ReturnsCodeTools()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["code"]);

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Name == "read_file");
        Assert.Contains(tools, t => t.Name == "search_code");
    }

    [Fact]
    public void GetToolsForAgent_MultipleStaticGroups_ReturnsCombined()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["task-state", "code"]);

        Assert.Equal(5, tools.Count);
        Assert.Contains(tools, t => t.Name == "list_tasks");
        Assert.Contains(tools, t => t.Name == "read_file");
    }

    [Fact]
    public void GetToolsForAgent_EmptyGroups_ReturnsEmpty()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent([]);

        Assert.Empty(tools);
    }

    [Fact]
    public void GetToolsForAgent_UnknownGroup_Ignored()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["nonexistent", "chat", "bogus"]);

        Assert.Empty(tools);
    }

    // ── GetToolsForAgent — contextual groups ─────────────────────

    [Fact]
    public void GetToolsForAgent_ContextualGroup_WithAgentId_ReturnsTools()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["task-write"], AgentId, AgentName);

        Assert.NotEmpty(tools);
        Assert.Equal(3, tools.Count);
    }

    [Fact]
    public void GetToolsForAgent_ContextualGroup_WithoutAgentId_SkipsGroup()
    {
        var registry = CreateRegistry();

        // No agentId — contextual groups should be silently skipped.
        var tools = registry.GetToolsForAgent(["task-write", "memory", "code-write"]);

        Assert.Empty(tools);
    }

    [Fact]
    public void GetToolsForAgent_ContextualGroup_WithEmptyAgentId_SkipsGroup()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["task-write"], agentId: "");

        Assert.Empty(tools);
    }

    [Fact]
    public void GetToolsForAgent_TaskWriteGroup_ReturnsWriteTools()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["task-write"], AgentId, AgentName);

        Assert.Equal(3, tools.Count);
        Assert.Contains(tools, t => t.Name == "create_task");
        Assert.Contains(tools, t => t.Name == "update_task_status");
        Assert.Contains(tools, t => t.Name == "add_task_comment");
    }

    [Fact]
    public void GetToolsForAgent_MemoryGroup_ReturnsMemoryTools()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["memory"], AgentId, AgentName);

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Name == "remember");
        Assert.Contains(tools, t => t.Name == "recall");
    }

    [Fact]
    public void GetToolsForAgent_CodeWriteGroup_ReturnsCodeWriteTools()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["code-write"], AgentId, AgentName);

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Name == "write_file");
        Assert.Contains(tools, t => t.Name == "commit_changes");
    }

    // ── Deduplication ────────────────────────────────────────────

    [Fact]
    public void GetToolsForAgent_DuplicateGroupNames_NoDuplicateTools()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(
            ["task-state", "task-state", "code", "code"],
            AgentId, AgentName);

        Assert.Equal(5, tools.Count);
        var names = tools.Select(t => t.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void GetToolsForAgent_DuplicateContextualGroups_NoDuplicateTools()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(
            ["task-write", "memory", "task-write", "memory"],
            AgentId, AgentName);

        Assert.Equal(5, tools.Count);
        var names = tools.Select(t => t.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    // ── Mixed static + contextual ────────────────────────────────

    [Fact]
    public void GetToolsForAgent_MixedStaticAndContextual_ReturnsBoth()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(
            ["task-state", "code", "task-write", "memory", "code-write"],
            AgentId, AgentName);

        // 3 task-state + 2 code + 3 task-write + 2 memory + 2 code-write = 12
        Assert.Equal(12, tools.Count);

        // Verify at least one from each group
        Assert.Contains(tools, t => t.Name == "list_tasks");     // task-state
        Assert.Contains(tools, t => t.Name == "read_file");       // code
        Assert.Contains(tools, t => t.Name == "create_task");     // task-write
        Assert.Contains(tools, t => t.Name == "remember");        // memory
        Assert.Contains(tools, t => t.Name == "write_file");      // code-write
    }

    [Fact]
    public void GetToolsForAgent_MixedStaticAndContextual_WithoutAgentId_ReturnsOnlyStatic()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(
            ["task-state", "code", "task-write", "memory", "code-write"]);

        // Only static groups resolve without an agentId.
        Assert.Equal(5, tools.Count);
        Assert.Contains(tools, t => t.Name == "list_tasks");
        Assert.Contains(tools, t => t.Name == "read_file");
        Assert.DoesNotContain(tools, t => t.Name == "create_task");
        Assert.DoesNotContain(tools, t => t.Name == "remember");
        Assert.DoesNotContain(tools, t => t.Name == "write_file");
    }

    // ── Case insensitivity ───────────────────────────────────────

    [Fact]
    public void GetToolsForAgent_CaseInsensitiveGroupNames_Static()
    {
        var registry = CreateRegistry();

        var tools = registry.GetToolsForAgent(["TASK-STATE", "Code"]);

        Assert.Equal(5, tools.Count);
    }

    [Fact]
    public void GetToolsForAgent_CaseInsensitiveGroupNames_Contextual()
    {
        var registry = CreateRegistry();

        var tools = registry.GetToolsForAgent(
            ["Task-Write", "MEMORY", "Code-Write"],
            AgentId, AgentName);

        Assert.Equal(7, tools.Count); // 3 + 2 + 2
    }

    // ── Agent name defaulting ────────────────────────────────────

    [Fact]
    public void GetToolsForAgent_AgentNameDefaultsToAgentId_WhenNull()
    {
        var registry = CreateRegistry();

        // When agentName is null, CreateContextualTools receives agentId as fallback.
        // The tools should still be created successfully.
        var tools = registry.GetToolsForAgent(
            ["task-write", "memory", "code-write"],
            AgentId,
            agentName: null);

        Assert.Equal(7, tools.Count);
        Assert.Contains(tools, t => t.Name == "create_task");
        Assert.Contains(tools, t => t.Name == "remember");
        Assert.Contains(tools, t => t.Name == "write_file");
    }

    // ── Tools have descriptions ──────────────────────────────────

    [Fact]
    public void GetToolsForAgent_AllToolsHaveDescriptions()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(
            ["task-state", "code", "task-write", "memory", "code-write"],
            AgentId, AgentName);

        foreach (var tool in tools)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(tool.Description),
                $"Tool '{tool.Name}' should have a non-empty description");
        }
    }

    // ── Catalog interaction ──────────────────────────────────────

    [Fact]
    public void GetToolsForAgent_CodeWriteGroup_UsesGitIdentityFromCatalog()
    {
        // The agent in DefaultCatalog has a GitIdentity — code-write tools
        // should resolve successfully for that agent.
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["code-write"], AgentId, AgentName);

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Name == "commit_changes");
    }

    [Fact]
    public void GetToolsForAgent_CodeWriteGroup_AgentNotInCatalog_StillReturnsTools()
    {
        // An agent ID not in the catalog — GitIdentity will be null but
        // CreateCodeWriteTools should still return tools.
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["code-write"], "unknown-agent", "Unknown");

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Name == "write_file");
        Assert.Contains(tools, t => t.Name == "commit_changes");
    }

    [Fact]
    public void GetToolsForAgent_SpecWriteGroup_ReturnsWriteTools()
    {
        // spec-write is a separate contextual group scoped to specs/.
        // It exposes the same tool names (write_file, commit_changes) as code-write
        // but is resolved via CreateSpecWriteTools.
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["spec-write"], AgentId, AgentName);

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Name == "write_file");
        Assert.Contains(tools, t => t.Name == "commit_changes");
    }

    [Fact]
    public void GetToolsForAgent_SpecWriteGroup_RequiresAgentId()
    {
        // Contextual groups silently skip when agentId is missing.
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["spec-write"]);

        Assert.Empty(tools);
    }

    [Fact]
    public void GetToolsForAgent_SpecWriteAndCodeWriteTogether_DeDupesToolNames()
    {
        // Both groups expose write_file and commit_changes. The registry
        // deduplicates by tool name — an agent holding both should see each
        // tool once (the first one wins).
        var registry = CreateRegistry();
        var tools = registry.GetToolsForAgent(["code-write", "spec-write"], AgentId, AgentName);

        Assert.Equal(2, tools.Count);
        Assert.Equal(tools.Select(t => t.Name).Distinct().Count(), tools.Count);
    }

    [Fact]
    public void Constructor_WithEmptyCatalog_StillInitializes()
    {
        var emptyCatalog = new AgentCatalogOptions("main", "Main", []);
        var registry = CreateRegistry(emptyCatalog);

        var names = registry.GetAllToolNames();
        Assert.Equal(12, names.Count);
    }
}
