using System.Text.Json;
using AgentAcademy.Server.Commands;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Tests;

public class CommandAuthorizerTests
{
    private readonly CommandAuthorizer _authorizer = new();

    private static AgentDefinition MakeAgent(
        string id = "test-1",
        string name = "TestAgent",
        CommandPermissionSet? permissions = null) =>
        new(id, name, "SoftwareEngineer", "Test", "prompt", null,
            new List<string>(), new List<string>(), true, null, permissions);

    private static CommandEnvelope MakeCommand(string command) =>
        new(command, new Dictionary<string, object?>(), CommandStatus.Success,
            null, null, "test-corr", DateTime.UtcNow, "test-1");

    // ── No Permissions ─────────────────────────────────────────

    [Fact]
    public void Authorize_NoPermissions_Denies()
    {
        var agent = MakeAgent(permissions: null);
        var command = MakeCommand("LIST_ROOMS");

        var result = _authorizer.Authorize(command, agent);

        Assert.NotNull(result);
        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Contains("no command permissions", result.Error);
    }

    // ── Exact Match ────────────────────────────────────────────

    [Fact]
    public void Authorize_ExactMatch_Allows()
    {
        var agent = MakeAgent(permissions: new CommandPermissionSet(
            Allowed: new List<string> { "LIST_ROOMS" },
            Denied: new List<string>()));
        var command = MakeCommand("LIST_ROOMS");

        var result = _authorizer.Authorize(command, agent);

        Assert.Null(result); // null = authorized
    }

    [Fact]
    public void Authorize_ExactMatch_CaseInsensitive()
    {
        var agent = MakeAgent(permissions: new CommandPermissionSet(
            Allowed: new List<string> { "list_rooms" },
            Denied: new List<string>()));
        var command = MakeCommand("LIST_ROOMS");

        Assert.Null(_authorizer.Authorize(command, agent));
    }

    // ── Wildcard Match ─────────────────────────────────────────

    [Fact]
    public void Authorize_WildcardPrefix_Allows()
    {
        var agent = MakeAgent(permissions: new CommandPermissionSet(
            Allowed: new List<string> { "LIST_*" },
            Denied: new List<string>()));

        Assert.Null(_authorizer.Authorize(MakeCommand("LIST_ROOMS"), agent));
        Assert.Null(_authorizer.Authorize(MakeCommand("LIST_AGENTS"), agent));
        Assert.Null(_authorizer.Authorize(MakeCommand("LIST_TASKS"), agent));
    }

    [Fact]
    public void Authorize_FullWildcard_AllowsAll()
    {
        var agent = MakeAgent(permissions: new CommandPermissionSet(
            Allowed: new List<string> { "*" },
            Denied: new List<string>()));

        Assert.Null(_authorizer.Authorize(MakeCommand("ANY_COMMAND"), agent));
    }

    [Fact]
    public void Authorize_WildcardDoesNotMatchDifferentPrefix()
    {
        var agent = MakeAgent(permissions: new CommandPermissionSet(
            Allowed: new List<string> { "LIST_*" },
            Denied: new List<string>()));

        var result = _authorizer.Authorize(MakeCommand("READ_FILE"), agent);

        Assert.NotNull(result);
        Assert.Equal(CommandStatus.Denied, result.Status);
    }

    // ── Deny Takes Priority ────────────────────────────────────

    [Fact]
    public void Authorize_DeniedOverridesAllowed()
    {
        var agent = MakeAgent(permissions: new CommandPermissionSet(
            Allowed: new List<string> { "*" },
            Denied: new List<string> { "RUN_BUILD" }));

        var result = _authorizer.Authorize(MakeCommand("RUN_BUILD"), agent);

        Assert.NotNull(result);
        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Contains("explicitly denied", result.Error);
    }

    [Fact]
    public void Authorize_DeniedWildcard()
    {
        var agent = MakeAgent(permissions: new CommandPermissionSet(
            Allowed: new List<string> { "*" },
            Denied: new List<string> { "RUN_*" }));

        Assert.NotNull(_authorizer.Authorize(MakeCommand("RUN_BUILD"), agent));
        Assert.NotNull(_authorizer.Authorize(MakeCommand("RUN_TESTS"), agent));
        Assert.Null(_authorizer.Authorize(MakeCommand("LIST_ROOMS"), agent));
    }

    // ── Default Deny ───────────────────────────────────────────

    [Fact]
    public void Authorize_NotInAllowList_Denies()
    {
        var agent = MakeAgent(permissions: new CommandPermissionSet(
            Allowed: new List<string> { "LIST_ROOMS" },
            Denied: new List<string>()));

        var result = _authorizer.Authorize(MakeCommand("READ_FILE"), agent);

        Assert.NotNull(result);
        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Contains("not authorized", result.Error);
    }

    // ── Agent-Config Permission Matrix ──────────────────────────
    // Mirrors agents.json: verifies each role gets the correct
    // grant/deny outcome for PR workflow and task dependency commands.

    private static AgentDefinition MakeAgentWithRole(
        string id,
        string role,
        List<string> allowed,
        List<string>? denied = null) =>
        new(id, $"Agent-{id}", role, "Test", "prompt", null,
            new List<string>(), new List<string>(), true, null,
            new CommandPermissionSet(allowed, denied ?? new List<string>()));

    // ── Planner (planner-1): All 6 new commands granted ─────────

    [Theory]
    [InlineData("CREATE_PR")]
    [InlineData("POST_PR_REVIEW")]
    [InlineData("GET_PR_REVIEWS")]
    [InlineData("MERGE_PR")]
    [InlineData("ADD_TASK_DEPENDENCY")]
    [InlineData("REMOVE_TASK_DEPENDENCY")]
    public void Planner_Granted_AllPrAndDependencyCommands(string command)
    {
        var agent = MakeAgentWithRole("planner-1", "Planner",
            new List<string>
            {
                "LIST_*", "CREATE_PR", "POST_PR_REVIEW", "GET_PR_REVIEWS",
                "MERGE_PR", "ADD_TASK_DEPENDENCY", "REMOVE_TASK_DEPENDENCY"
            });

        Assert.Null(_authorizer.Authorize(MakeCommand(command), agent));
    }

    // ── Reviewer (reviewer-1): PR commands granted, no dependency commands ──

    [Theory]
    [InlineData("CREATE_PR")]
    [InlineData("POST_PR_REVIEW")]
    [InlineData("GET_PR_REVIEWS")]
    [InlineData("MERGE_PR")]
    public void Reviewer_Granted_PrCommands(string command)
    {
        var agent = MakeAgentWithRole("reviewer-1", "Reviewer",
            new List<string>
            {
                "LIST_*", "CREATE_PR", "POST_PR_REVIEW", "GET_PR_REVIEWS", "MERGE_PR"
            });

        Assert.Null(_authorizer.Authorize(MakeCommand(command), agent));
    }

    [Theory]
    [InlineData("ADD_TASK_DEPENDENCY")]
    [InlineData("REMOVE_TASK_DEPENDENCY")]
    public void Reviewer_Denied_TaskDependencyCommands(string command)
    {
        var agent = MakeAgentWithRole("reviewer-1", "Reviewer",
            new List<string>
            {
                "LIST_*", "CREATE_PR", "POST_PR_REVIEW", "GET_PR_REVIEWS", "MERGE_PR"
            });

        var result = _authorizer.Authorize(MakeCommand(command), agent);

        Assert.NotNull(result);
        Assert.Equal(CommandStatus.Denied, result.Status);
    }

    // ── SoftwareEngineer (SWE-1/SWE-2): CREATE_PR, GET_PR_REVIEWS,
    //    dependency commands granted; POST_PR_REVIEW and MERGE_PR NOT granted ──

    [Theory]
    [InlineData("CREATE_PR")]
    [InlineData("GET_PR_REVIEWS")]
    [InlineData("ADD_TASK_DEPENDENCY")]
    [InlineData("REMOVE_TASK_DEPENDENCY")]
    public void SoftwareEngineer_Granted_SubsetOfPrAndDependencyCommands(string command)
    {
        var agent = MakeAgentWithRole("software-engineer-1", "SoftwareEngineer",
            new List<string>
            {
                "LIST_*", "CREATE_PR", "GET_PR_REVIEWS",
                "ADD_TASK_DEPENDENCY", "REMOVE_TASK_DEPENDENCY"
            },
            denied: new List<string> { "APPROVE_TASK", "REQUEST_CHANGES", "RESTART_SERVER" });

        Assert.Null(_authorizer.Authorize(MakeCommand(command), agent));
    }

    [Theory]
    [InlineData("POST_PR_REVIEW")]
    [InlineData("MERGE_PR")]
    public void SoftwareEngineer_NotGranted_ReviewAndMergeCommands(string command)
    {
        var agent = MakeAgentWithRole("software-engineer-1", "SoftwareEngineer",
            new List<string>
            {
                "LIST_*", "CREATE_PR", "GET_PR_REVIEWS",
                "ADD_TASK_DEPENDENCY", "REMOVE_TASK_DEPENDENCY"
            },
            denied: new List<string> { "APPROVE_TASK", "REQUEST_CHANGES", "RESTART_SERVER" });

        var result = _authorizer.Authorize(MakeCommand(command), agent);

        Assert.NotNull(result);
        Assert.Equal(CommandStatus.Denied, result.Status);
    }

    // ── Architect: GET_PR_REVIEWS + dependency commands only ─────

    [Theory]
    [InlineData("GET_PR_REVIEWS")]
    [InlineData("ADD_TASK_DEPENDENCY")]
    [InlineData("REMOVE_TASK_DEPENDENCY")]
    public void Architect_Granted_ReadOnlyPrAndDependencyCommands(string command)
    {
        var agent = MakeAgentWithRole("architect-1", "Architect",
            new List<string>
            {
                "LIST_*", "GET_PR_REVIEWS", "ADD_TASK_DEPENDENCY", "REMOVE_TASK_DEPENDENCY"
            },
            denied: new List<string> { "RESTART_SERVER" });

        Assert.Null(_authorizer.Authorize(MakeCommand(command), agent));
    }

    [Theory]
    [InlineData("CREATE_PR")]
    [InlineData("POST_PR_REVIEW")]
    [InlineData("MERGE_PR")]
    public void Architect_NotGranted_MutatePrCommands(string command)
    {
        var agent = MakeAgentWithRole("architect-1", "Architect",
            new List<string>
            {
                "LIST_*", "GET_PR_REVIEWS", "ADD_TASK_DEPENDENCY", "REMOVE_TASK_DEPENDENCY"
            },
            denied: new List<string> { "RESTART_SERVER" });

        var result = _authorizer.Authorize(MakeCommand(command), agent);

        Assert.NotNull(result);
        Assert.Equal(CommandStatus.Denied, result.Status);
    }

    // ── TechnicalWriter: No PR or dependency commands ───────────

    [Theory]
    [InlineData("CREATE_PR")]
    [InlineData("POST_PR_REVIEW")]
    [InlineData("GET_PR_REVIEWS")]
    [InlineData("MERGE_PR")]
    [InlineData("ADD_TASK_DEPENDENCY")]
    [InlineData("REMOVE_TASK_DEPENDENCY")]
    public void TechnicalWriter_NotGranted_AnyNewCommands(string command)
    {
        var agent = MakeAgentWithRole("tech-writer-1", "TechnicalWriter",
            new List<string> { "LIST_*", "READ_FILE", "SEARCH_CODE" },
            denied: new List<string> { "APPROVE_TASK", "REQUEST_CHANGES", "RESTART_SERVER" });

        var result = _authorizer.Authorize(MakeCommand(command), agent);

        Assert.NotNull(result);
        Assert.Equal(CommandStatus.Denied, result.Status);
    }

    // ── Deny list takes priority even for new commands ──────────

    [Fact]
    public void DenyList_Overrides_ExplicitGrant_ForNewCommands()
    {
        var agent = MakeAgentWithRole("custom-1", "SoftwareEngineer",
            new List<string> { "CREATE_PR", "MERGE_PR" },
            denied: new List<string> { "MERGE_PR" });

        Assert.Null(_authorizer.Authorize(MakeCommand("CREATE_PR"), agent));

        var denied = _authorizer.Authorize(MakeCommand("MERGE_PR"), agent);
        Assert.NotNull(denied);
        Assert.Equal(CommandStatus.Denied, denied.Status);
        Assert.Contains("explicitly denied", denied.Error);
    }

    // ── Wildcard grant includes new commands ─────────────────────

    [Theory]
    [InlineData("CREATE_PR")]
    [InlineData("POST_PR_REVIEW")]
    [InlineData("GET_PR_REVIEWS")]
    [InlineData("MERGE_PR")]
    [InlineData("ADD_TASK_DEPENDENCY")]
    [InlineData("REMOVE_TASK_DEPENDENCY")]
    public void WildcardGrant_Includes_AllNewCommands(string command)
    {
        var agent = MakeAgentWithRole("wildcard-1", "Planner",
            new List<string> { "*" });

        Assert.Null(_authorizer.Authorize(MakeCommand(command), agent));
    }

    // ── Cross-role verification: SWE with deny on APPROVE_TASK ──

    [Fact]
    public void SoftwareEngineer_ExplicitDeny_StillBlocks_EvenWithWildcard()
    {
        var agent = MakeAgentWithRole("swe-deny-test", "SoftwareEngineer",
            new List<string> { "*" },
            denied: new List<string> { "APPROVE_TASK", "REQUEST_CHANGES" });

        // Wildcard allows CREATE_PR
        Assert.Null(_authorizer.Authorize(MakeCommand("CREATE_PR"), agent));

        // But explicit deny still blocks APPROVE_TASK
        var denied = _authorizer.Authorize(MakeCommand("APPROVE_TASK"), agent);
        Assert.NotNull(denied);
        Assert.Equal(CommandStatus.Denied, denied.Status);
    }

    // ── RestrictedRoles gate (SHELL is role-restricted) ─────────

    [Fact]
    public void RestrictedRoles_Shell_AllowedForPlanner()
    {
        var agent = MakeAgentWithRole("planner-shell", "Planner",
            new List<string> { "SHELL" });

        Assert.Null(_authorizer.Authorize(MakeCommand("SHELL"), agent));
    }

    [Fact]
    public void RestrictedRoles_Shell_DeniedForSweEvenIfGranted()
    {
        var agent = MakeAgentWithRole("swe-shell", "SoftwareEngineer",
            new List<string> { "SHELL" });

        var result = _authorizer.Authorize(MakeCommand("SHELL"), agent);

        Assert.NotNull(result);
        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Contains("restricted to roles", result.Error);
    }

    // ── Second SWE has same permissions as first ────────────────

    [Theory]
    [InlineData("CREATE_PR")]
    [InlineData("GET_PR_REVIEWS")]
    [InlineData("ADD_TASK_DEPENDENCY")]
    [InlineData("REMOVE_TASK_DEPENDENCY")]
    public void SoftwareEngineer2_SameGrants_AsSoftwareEngineer1(string command)
    {
        var agent = MakeAgentWithRole("software-engineer-2", "SoftwareEngineer",
            new List<string>
            {
                "LIST_*", "CREATE_PR", "GET_PR_REVIEWS",
                "ADD_TASK_DEPENDENCY", "REMOVE_TASK_DEPENDENCY"
            },
            denied: new List<string> { "APPROVE_TASK", "REQUEST_CHANGES", "RESTART_SERVER" });

        Assert.Null(_authorizer.Authorize(MakeCommand(command), agent));
    }

    // ── Config Drift Guard ──────────────────────────────────────
    // Reads agents.json and validates the actual permission grants match
    // the expected matrix. If someone changes the config, this fails.

    private static readonly string[] PrWorkflowCommands =
        { "CREATE_PR", "POST_PR_REVIEW", "GET_PR_REVIEWS", "MERGE_PR" };

    private static readonly string[] TaskDependencyCommands =
        { "ADD_TASK_DEPENDENCY", "REMOVE_TASK_DEPENDENCY" };

    /// <summary>
    /// Expected grants per agent role for the 6 new commands.
    /// Key: agent ID, Value: set of commands that MUST be in Allowed.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> ExpectedGrants = new()
    {
        ["planner-1"] = new(PrWorkflowCommands.Concat(TaskDependencyCommands)),
        ["reviewer-1"] = new(PrWorkflowCommands),
        ["software-engineer-1"] = new(new[] { "CREATE_PR", "GET_PR_REVIEWS", "ADD_TASK_DEPENDENCY", "REMOVE_TASK_DEPENDENCY" }),
        ["software-engineer-2"] = new(new[] { "CREATE_PR", "GET_PR_REVIEWS", "ADD_TASK_DEPENDENCY", "REMOVE_TASK_DEPENDENCY" }),
        ["architect-1"] = new(new[] { "GET_PR_REVIEWS", "ADD_TASK_DEPENDENCY", "REMOVE_TASK_DEPENDENCY" }),
        ["tech-writer-1"] = new(),
    };

    /// <summary>
    /// Commands that MUST NOT appear in the agent's Allowed list.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> ExpectedDenials = new()
    {
        ["software-engineer-1"] = new(new[] { "POST_PR_REVIEW", "MERGE_PR" }),
        ["software-engineer-2"] = new(new[] { "POST_PR_REVIEW", "MERGE_PR" }),
        ["architect-1"] = new(new[] { "CREATE_PR", "POST_PR_REVIEW", "MERGE_PR" }),
        ["tech-writer-1"] = new(PrWorkflowCommands.Concat(TaskDependencyCommands)),
    };

    [Fact]
    public void AgentsJson_PermissionMatrix_MatchesExpectedGrants()
    {
        var agents = LoadAgentsFromConfig();

        var errors = new List<string>();

        foreach (var (agentId, expectedCommands) in ExpectedGrants)
        {
            var agent = agents.FirstOrDefault(a => a.Id == agentId);
            Assert.NotNull(agent);

            var allowed = agent.Permissions?.Allowed ?? new List<string>();

            foreach (var cmd in expectedCommands)
            {
                if (!allowed.Contains(cmd, StringComparer.OrdinalIgnoreCase))
                    errors.Add($"{agentId} missing grant for {cmd}");
            }
        }

        Assert.True(errors.Count == 0,
            $"Config drift detected in agents.json:\n  {string.Join("\n  ", errors)}");
    }

    [Fact]
    public void AgentsJson_PermissionMatrix_DeniesExpectedCommands()
    {
        var agents = LoadAgentsFromConfig();

        var errors = new List<string>();

        foreach (var (agentId, deniedCommands) in ExpectedDenials)
        {
            var agent = agents.FirstOrDefault(a => a.Id == agentId);
            Assert.NotNull(agent);

            var allowed = agent.Permissions?.Allowed ?? new List<string>();

            foreach (var cmd in deniedCommands)
            {
                if (allowed.Contains(cmd, StringComparer.OrdinalIgnoreCase))
                    errors.Add($"{agentId} should NOT have {cmd} but it's in the Allowed list");
            }
        }

        Assert.True(errors.Count == 0,
            $"Config drift detected in agents.json:\n  {string.Join("\n  ", errors)}");
    }

    [Fact]
    public void AgentsJson_Authorizer_GrantsMatchRealConfig()
    {
        var agents = LoadAgentsFromConfig();

        var errors = new List<string>();

        foreach (var agent in agents)
        {
            if (!ExpectedGrants.TryGetValue(agent.Id, out var expected))
                continue;

            foreach (var cmd in expected)
            {
                var result = _authorizer.Authorize(MakeCommand(cmd), agent);
                if (result != null)
                    errors.Add($"{agent.Id} denied {cmd} (expected: allowed). Error: {result.Error}");
            }

            if (ExpectedDenials.TryGetValue(agent.Id, out var denied))
            {
                foreach (var cmd in denied)
                {
                    var result = _authorizer.Authorize(MakeCommand(cmd), agent);
                    if (result == null)
                        errors.Add($"{agent.Id} allowed {cmd} (expected: denied)");
                }
            }
        }

        Assert.True(errors.Count == 0,
            $"Authorizer + config mismatch:\n  {string.Join("\n  ", errors)}");
    }

    private static List<AgentDefinition> LoadAgentsFromConfig()
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "src", "AgentAcademy.Server", "Config", "agents.json");
        Assert.True(File.Exists(path), $"agents.json not found at {path}");

        using var stream = File.OpenRead(path);
        var doc = JsonDocument.Parse(stream);
        var catalog = doc.RootElement.GetProperty("AgentCatalog");
        var agentsArray = catalog.GetProperty("Agents");

        var agents = new List<AgentDefinition>();
        foreach (var a in agentsArray.EnumerateArray())
        {
            var perms = a.TryGetProperty("Permissions", out var permProp)
                ? new CommandPermissionSet(
                    Allowed: permProp.TryGetProperty("Allowed", out var al)
                        ? al.EnumerateArray().Select(e => e.GetString()!).ToList()
                        : new List<string>(),
                    Denied: permProp.TryGetProperty("Denied", out var dl)
                        ? dl.EnumerateArray().Select(e => e.GetString()!).ToList()
                        : new List<string>())
                : null;

            agents.Add(new AgentDefinition(
                Id: a.GetProperty("Id").GetString()!,
                Name: a.GetProperty("Name").GetString()!,
                Role: a.GetProperty("Role").GetString()!,
                Summary: a.GetProperty("Summary").GetString()!,
                StartupPrompt: "",
                Model: null,
                CapabilityTags: new List<string>(),
                EnabledTools: new List<string>(),
                AutoJoinDefaultRoom: true,
                Permissions: perms));
        }

        return agents;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "AgentAcademy.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("Could not find repo root (AgentAcademy.sln)");
    }
}
