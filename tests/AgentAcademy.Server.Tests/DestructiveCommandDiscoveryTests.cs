using System.Reflection;
using System.Text.Json;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Discovery tests that verify all destructive command handlers are properly
/// marked and that the set of destructive commands stays intentional.
/// If a new handler is added with IsDestructive = true, it must be added to
/// the expected set below — forcing a conscious decision.
/// </summary>
public class DestructiveCommandDiscoveryTests
{
    /// <summary>
    /// The canonical set of commands that MUST be destructive.
    /// Adding or removing from this set is intentional and requires updating this test.
    /// </summary>
    private static readonly HashSet<string> ExpectedDestructiveCommands = new(StringComparer.Ordinal)
    {
        "CANCEL_TASK",
        "CLEANUP_ROOMS",
        "CLEANUP_WORKTREES",
        "CLOSE_ROOM",
        "FORGET",
        "MERGE_TASK",
        "REJECT_TASK",
        "RESTART_SERVER",
        "RUN_MIGRATIONS",
    };

    private static IReadOnlyList<ICommandHandler> DiscoverHandlers()
    {
        // Use the same assembly-scanning logic as ServiceCollectionExtensions
        var handlerTypes = typeof(ICommandHandler).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && typeof(ICommandHandler).IsAssignableFrom(t));

        var handlers = new List<ICommandHandler>();
        var failures = new List<string>();
        foreach (var type in handlerTypes)
        {
            // Create with null-forgiving constructor args for discovery only —
            // we only read CommandName / IsDestructive / DestructiveWarning,
            // we never call ExecuteAsync.
            var ctors = type.GetConstructors();
            var ctor = ctors.OrderBy(c => c.GetParameters().Length).First();
            var args = ctor.GetParameters()
                .Select(p => (object?)null!)
                .ToArray();

            try
            {
                var handler = (ICommandHandler)ctor.Invoke(args);
                handlers.Add(handler);
            }
            catch (Exception ex)
            {
                failures.Add($"{type.Name}: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"Failed to instantiate handlers (discovery incomplete): {string.Join("; ", failures)}");

        return handlers;
    }

    [Fact]
    public void AllExpectedHandlersAreMarkedDestructive()
    {
        var handlers = DiscoverHandlers();
        var actualDestructive = handlers
            .Where(h => h.IsDestructive)
            .Select(h => h.CommandName)
            .ToHashSet(StringComparer.Ordinal);

        var missing = ExpectedDestructiveCommands.Except(actualDestructive).ToList();
        Assert.True(missing.Count == 0,
            $"Expected these commands to be destructive but they are not: {string.Join(", ", missing)}");
    }

    [Fact]
    public void NoUnexpectedHandlersAreMarkedDestructive()
    {
        var handlers = DiscoverHandlers();
        var actualDestructive = handlers
            .Where(h => h.IsDestructive)
            .Select(h => h.CommandName)
            .ToHashSet(StringComparer.Ordinal);

        var unexpected = actualDestructive.Except(ExpectedDestructiveCommands).ToList();
        Assert.True(unexpected.Count == 0,
            $"These commands are marked destructive but not in the expected set (add them if intentional): {string.Join(", ", unexpected)}");
    }

    [Fact]
    public void DestructiveHandlersHaveCustomWarnings()
    {
        var handlers = DiscoverHandlers();
        var destructive = handlers.Where(h => h.IsDestructive).ToList();

        foreach (var handler in destructive)
        {
            Assert.False(string.IsNullOrWhiteSpace(handler.DestructiveWarning),
                $"{handler.CommandName} is destructive but has no warning message");

            // Ensure it's not just the default interface warning
            var defaultWarning = $"{handler.CommandName} performs a destructive action.";
            Assert.NotEqual(defaultWarning, handler.DestructiveWarning,
                StringComparer.Ordinal);
        }
    }

    [Fact]
    public void NonDestructiveHandlersUseDefaultWarningOrEmpty()
    {
        var handlers = DiscoverHandlers();
        var nonDestructive = handlers.Where(h => !h.IsDestructive).ToList();

        // Non-destructive handlers shouldn't have IsDestructive = true.
        // This is a sanity check that the default interface implementation works.
        foreach (var handler in nonDestructive)
        {
            Assert.False(handler.IsDestructive,
                $"{handler.CommandName} should not be destructive");
        }
    }

    [Theory]
    [InlineData(typeof(ForgetHandler), "FORGET")]
    [InlineData(typeof(CancelTaskHandler), "CANCEL_TASK")]
    [InlineData(typeof(CleanupRoomsHandler), "CLEANUP_ROOMS")]
    [InlineData(typeof(CloseRoomHandler), "CLOSE_ROOM")]
    [InlineData(typeof(MergeTaskHandler), "MERGE_TASK")]
    [InlineData(typeof(RejectTaskHandler), "REJECT_TASK")]
    [InlineData(typeof(RestartServerHandler), "RESTART_SERVER")]
    public void SpecificHandler_IsDestructive(Type handlerType, string expectedCommandName)
    {
        var ctor = handlerType.GetConstructors()
            .OrderBy(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters()
            .Select(p => (object?)null!)
            .ToArray();

        var handler = (ICommandHandler)ctor.Invoke(args);

        Assert.Equal(expectedCommandName, handler.CommandName);
        Assert.True(handler.IsDestructive,
            $"{expectedCommandName} must be destructive");
        Assert.False(string.IsNullOrWhiteSpace(handler.DestructiveWarning),
            $"{expectedCommandName} must have a warning message");
    }

    [Fact]
    public void AllHandlersAreDiscoverable()
    {
        var handlers = DiscoverHandlers();

        // We should discover a reasonable number of handlers
        Assert.True(handlers.Count >= 20,
            $"Expected at least 20 handlers but discovered {handlers.Count}. " +
            "Auto-discovery may be broken.");

        // Ensure no duplicate command names
        var duplicates = handlers
            .GroupBy(h => h.CommandName)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0,
            $"Duplicate command names found: {string.Join(", ", duplicates)}");
    }

    /// <summary>
    /// Verifies that every agent's StartupPrompt in agents.json documents
    /// destructive command behavior (added in feat: add destructive command
    /// confirmation docs to all agent prompts).
    /// </summary>
    [Fact]
    public void AllAgentPromptsDocumentDestructiveCommandBehavior()
    {
        // Find agents.json relative to the test assembly
        var repoRoot = FindRepoRoot();
        var agentsJsonPath = Path.Combine(repoRoot, "src", "AgentAcademy.Server", "Config", "agents.json");
        Assert.True(File.Exists(agentsJsonPath), $"agents.json not found at {agentsJsonPath}");

        using var stream = File.OpenRead(agentsJsonPath);
        var doc = JsonDocument.Parse(stream);
        var catalog = doc.RootElement.GetProperty("AgentCatalog");
        var agents = catalog.GetProperty("Agents");

        var missingDocs = new List<string>();
        foreach (var agent in agents.EnumerateArray())
        {
            var name = agent.GetProperty("Name").GetString()!;
            var prompt = agent.GetProperty("StartupPrompt").GetString() ?? "";

            // Every agent should document destructive commands and confirm=true
            var hasDestructiveDocs = prompt.Contains("destructive", StringComparison.OrdinalIgnoreCase)
                                    || prompt.Contains("confirm=true", StringComparison.OrdinalIgnoreCase);

            if (!hasDestructiveDocs)
                missingDocs.Add(name);
        }

        Assert.True(missingDocs.Count == 0,
            $"These agents lack destructive command documentation in their StartupPrompt: {string.Join(", ", missingDocs)}");
    }

    /// <summary>
    /// Verifies that agents with destructive commands in their allowed list
    /// have those specific commands mentioned in their startup prompt.
    /// </summary>
    [Fact]
    public void AgentPromptsListTheirSpecificDestructiveCommands()
    {
        var repoRoot = FindRepoRoot();
        var agentsJsonPath = Path.Combine(repoRoot, "src", "AgentAcademy.Server", "Config", "agents.json");
        Assert.True(File.Exists(agentsJsonPath), $"agents.json not found at {agentsJsonPath}");

        using var stream = File.OpenRead(agentsJsonPath);
        var doc = JsonDocument.Parse(stream);
        var catalog = doc.RootElement.GetProperty("AgentCatalog");
        var agents = catalog.GetProperty("Agents");

        foreach (var agent in agents.EnumerateArray())
        {
            var name = agent.GetProperty("Name").GetString()!;
            var prompt = agent.GetProperty("StartupPrompt").GetString() ?? "";

            // Check if agent has Permissions.Allowed that includes destructive commands
            if (agent.TryGetProperty("Permissions", out var perms)
                && perms.TryGetProperty("Allowed", out var allowed))
            {
                foreach (var cmd in allowed.EnumerateArray())
                {
                    var cmdName = cmd.GetString()!;
                    if (ExpectedDestructiveCommands.Contains(cmdName))
                    {
                        Assert.True(prompt.Contains(cmdName, StringComparison.OrdinalIgnoreCase),
                            $"Agent {name} has {cmdName} in allowed commands but doesn't mention it in StartupPrompt");
                    }
                }
            }
        }
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
