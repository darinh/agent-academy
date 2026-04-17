using AgentAcademy.Shared.Models;
using AgentAcademy.Server.Services.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Maps agent <c>EnabledTools</c> group names to <see cref="AIFunction"/>
/// instances that the Copilot SDK can register in session configs.
///
/// Tool groups:
/// <list type="bullet">
/// <item><c>task-state</c> — list_tasks, list_rooms, show_agents (read-only, shared)</item>
/// <item><c>code</c> — read_file, search_code (read-only, shared)</item>
/// <item><c>code-write</c> — write_file (per-agent, SoftwareEngineer only; writes restricted to <c>src/</c>)</item>
/// <item><c>spec-write</c> — write_file (per-agent, TechnicalWriter only; writes restricted to <c>specs/</c>)</item>
/// <item><c>task-write</c> — create_task, update_task_status, add_task_comment (per-agent)</item>
/// <item><c>memory</c> — remember, recall (per-agent)</item>
/// </list>
///
/// The <c>chat</c> group is a platform concept (not an SDK tool) and is ignored.
/// </summary>
public sealed class AgentToolRegistry : IAgentToolRegistry
{
    private readonly IAgentToolFunctions _toolFunctions;
    private readonly IAgentCatalog _catalog;
    private readonly Dictionary<string, IReadOnlyList<AIFunction>> _staticGroups;
    private readonly IReadOnlyList<string> _allToolNames;
    private readonly ILogger<AgentToolRegistry> _logger;

    // Groups that require agent context (created per-agent session)
    private static readonly HashSet<string> ContextualGroups =
        new(StringComparer.OrdinalIgnoreCase) { "task-write", "memory", "code-write", "spec-write" };

    public AgentToolRegistry(
        IAgentToolFunctions toolFunctions,
        IAgentCatalog catalog,
        ILogger<AgentToolRegistry> logger)
    {
        _toolFunctions = toolFunctions;
        _catalog = catalog;
        _logger = logger;

        var taskStateTools = toolFunctions.CreateTaskStateTools();
        var codeTools = toolFunctions.CreateCodeTools();

        _staticGroups = new Dictionary<string, IReadOnlyList<AIFunction>>(StringComparer.OrdinalIgnoreCase)
        {
            ["task-state"] = taskStateTools,
            ["code"] = codeTools,
        };

        // Build the complete list including contextual tool names for diagnostics
        var contextualNames = new List<string>
        {
            "create_task", "update_task_status", "add_task_comment",
            "remember", "recall",
            "write_file", "commit_changes"
        };

        _allToolNames = _staticGroups.Values
            .SelectMany(g => g)
            .Select(f => f.Name)
            .Concat(contextualNames)
            .Distinct()
            .ToList();

        _logger.LogInformation(
            "AgentToolRegistry initialized with {GroupCount} groups ({ContextualCount} contextual), {ToolCount} tools: {Names}",
            _staticGroups.Count + ContextualGroups.Count,
            ContextualGroups.Count,
            _allToolNames.Count,
            string.Join(", ", _allToolNames));
    }

    public IReadOnlyList<AIFunction> GetToolsForAgent(
        IEnumerable<string> enabledTools,
        string? agentId = null,
        string? agentName = null,
        string? roomId = null)
    {
        var tools = new List<AIFunction>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in enabledTools)
        {
            // Static (read-only) groups
            if (_staticGroups.TryGetValue(group, out var groupTools))
            {
                foreach (var tool in groupTools)
                {
                    if (seen.Add(tool.Name))
                        tools.Add(tool);
                }
                continue;
            }

            // Contextual (write) groups — require agent identity
            if (ContextualGroups.Contains(group))
            {
                if (string.IsNullOrEmpty(agentId))
                {
                    _logger.LogWarning(
                        "Contextual tool group '{Group}' requested but no agentId provided — skipping",
                        group);
                    continue;
                }

                var contextualTools = CreateContextualTools(group, agentId, agentName ?? agentId, roomId);
                foreach (var tool in contextualTools)
                {
                    if (seen.Add(tool.Name))
                        tools.Add(tool);
                }
            }
        }

        if (tools.Count > 0)
        {
            _logger.LogDebug(
                "Resolved {Count} tools for agent {AgentId}, groups [{Groups}]: {Names}",
                tools.Count,
                agentId ?? "(none)",
                string.Join(", ", enabledTools),
                string.Join(", ", tools.Select(t => t.Name)));
        }

        return tools;
    }

    public IReadOnlyList<string> GetAllToolNames() => _allToolNames;

    private IReadOnlyList<AIFunction> CreateContextualTools(
        string group, string agentId, string agentName, string? roomId)
    {
        return group.ToLowerInvariant() switch
        {
            "task-write" => _toolFunctions.CreateTaskWriteTools(agentId, agentName),
            "memory" => _toolFunctions.CreateMemoryTools(agentId),
            "code-write" => _toolFunctions.CreateCodeWriteTools(agentId, agentName,
                _catalog.Agents.FirstOrDefault(a => a.Id == agentId)?.GitIdentity, roomId),
            "spec-write" => _toolFunctions.CreateSpecWriteTools(agentId, agentName,
                _catalog.Agents.FirstOrDefault(a => a.Id == agentId)?.GitIdentity, roomId),
            _ => []
        };
    }
}
