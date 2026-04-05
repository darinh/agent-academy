using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Maps agent <c>EnabledTools</c> group names to <see cref="AIFunction"/>
/// instances that the Copilot SDK can register in session configs.
///
/// Tool groups:
/// <list type="bullet">
/// <item><c>task-state</c> — list_tasks, list_rooms, list_agents</item>
/// <item><c>code</c> — read_file, search_code</item>
/// </list>
///
/// The <c>chat</c> group is a platform concept (not an SDK tool) and is ignored.
/// </summary>
public sealed class AgentToolRegistry : IAgentToolRegistry
{
    private readonly Dictionary<string, IReadOnlyList<AIFunction>> _toolGroups;
    private readonly IReadOnlyList<string> _allToolNames;
    private readonly ILogger<AgentToolRegistry> _logger;

    public AgentToolRegistry(
        AgentToolFunctions toolFunctions,
        ILogger<AgentToolRegistry> logger)
    {
        _logger = logger;

        var taskStateTools = toolFunctions.CreateTaskStateTools();
        var codeTools = toolFunctions.CreateCodeTools();

        _toolGroups = new Dictionary<string, IReadOnlyList<AIFunction>>(StringComparer.OrdinalIgnoreCase)
        {
            ["task-state"] = taskStateTools,
            ["code"] = codeTools,
        };

        _allToolNames = _toolGroups.Values
            .SelectMany(g => g)
            .Select(f => f.Name)
            .Distinct()
            .ToList();

        _logger.LogInformation(
            "AgentToolRegistry initialized with {GroupCount} groups, {ToolCount} tools: {Names}",
            _toolGroups.Count,
            _allToolNames.Count,
            string.Join(", ", _allToolNames));
    }

    public IReadOnlyList<AIFunction> GetToolsForAgent(IEnumerable<string> enabledTools)
    {
        var tools = new List<AIFunction>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in enabledTools)
        {
            if (_toolGroups.TryGetValue(group, out var groupTools))
            {
                foreach (var tool in groupTools)
                {
                    if (seen.Add(tool.Name))
                        tools.Add(tool);
                }
            }
        }

        if (tools.Count > 0)
        {
            _logger.LogDebug(
                "Resolved {Count} tools for groups [{Groups}]: {Names}",
                tools.Count,
                string.Join(", ", enabledTools),
                string.Join(", ", tools.Select(t => t.Name)));
        }

        return tools;
    }

    public IReadOnlyList<string> GetAllToolNames() => _allToolNames;
}
