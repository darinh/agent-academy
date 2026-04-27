using System.ComponentModel;
using System.Diagnostics;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Contains the C# methods that the Copilot SDK exposes as callable tools
/// for agents. Each method is wrapped by <see cref="AIFunctionFactory.Create"/>
/// and passed to <see cref="GitHub.Copilot.SDK.SessionConfig.Tools"/>.
///
/// Tool functions use <see cref="IServiceScopeFactory"/> to resolve scoped
/// services (TaskQueryService, RoomService, etc.) at invocation time.
///
/// Read-only tools (task-state, code) are agent-agnostic and created once.
/// Write tools (task-write, memory) capture the calling agent's identity
/// via closures and are created per-agent session.
/// </summary>
public sealed class AgentToolFunctions : IAgentToolFunctions
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAgentCatalog _catalog;
    private readonly ILogger<AgentToolFunctions> _logger;

    public AgentToolFunctions(
        IServiceScopeFactory scopeFactory,
        IAgentCatalog catalog,
        ILogger<AgentToolFunctions> logger)
    {
        _scopeFactory = scopeFactory;
        _catalog = catalog;
        _logger = logger;
    }

    /// <summary>
    /// Creates all <see cref="AIFunction"/> instances for the "task-state" tool group.
    /// </summary>
    public IReadOnlyList<AIFunction> CreateTaskStateTools()
    {
        return
        [
            AIFunctionFactory.Create(ListTasksAsync, "list_tasks",
                "List all tasks in the workspace with their status, assignee, and metadata."),
            AIFunctionFactory.Create(ListRoomsAsync, "list_rooms",
                "List all collaboration rooms in the workspace with their status and participants."),
            AIFunctionFactory.Create(ListAgentsAsync, "show_agents",
                "List all agents in the workspace with their current location and state."),
        ];
    }

    /// <summary>
    /// Creates all <see cref="AIFunction"/> instances for the "code" tool group.
    /// When <paramref name="workspacePath"/> is provided the read tools resolve
    /// paths and run searches inside that worktree; otherwise they fall back to
    /// the develop checkout via <see cref="FindProjectRoot"/>.
    /// </summary>
    public IReadOnlyList<AIFunction> CreateCodeTools(string? workspacePath = null)
    {
        var wrapper = new CodeReadToolWrapper(_logger, workspacePath);
        return
        [
            AIFunctionFactory.Create(wrapper.ReadFileAsync, "read_file",
                "Read a file's contents from the project. Supports optional line range. Paths are relative to the project root."),
            AIFunctionFactory.Create(wrapper.SearchCodeAsync, "search_code",
                "Search for text patterns in the project codebase using git grep. Returns matching lines with file paths and line numbers."),
        ];
    }

    /// <summary>
    /// Creates <see cref="AIFunction"/> instances for the "task-write" tool group.
    /// These tools mutate task state and are scoped to the calling agent.
    /// </summary>
    public IReadOnlyList<AIFunction> CreateTaskWriteTools(string agentId, string agentName)
    {
        var wrapper = new TaskWriteToolWrapper(_scopeFactory, _logger, agentId, agentName);
        return
        [
            AIFunctionFactory.Create(wrapper.CreateTaskAsync, "create_task",
                "Create a new task in the workspace. Returns the created task ID and room assignment."),
            AIFunctionFactory.Create(wrapper.UpdateTaskStatusAsync, "update_task_status",
                "Update a task's status, report a blocker, or post a note. At least one of status, blocker, or note is required."),
            AIFunctionFactory.Create(wrapper.AddTaskCommentAsync, "add_task_comment",
                "Add a comment, finding, evidence, or blocker to a task."),
        ];
    }

    /// <summary>
    /// Creates <see cref="AIFunction"/> instances for the "memory" tool group.
    /// Memory tools are scoped to the calling agent — each agent has its own
    /// memory store but can read shared memories from other agents.
    /// </summary>
    public IReadOnlyList<AIFunction> CreateMemoryTools(string agentId)
    {
        var wrapper = new MemoryToolWrapper(_scopeFactory, _logger, agentId);
        return
        [
            AIFunctionFactory.Create(wrapper.RememberAsync, "remember",
                "Store a memory that persists across sessions. Use for decisions, lessons learned, patterns, and project knowledge. Use category 'shared' to make visible to all agents."),
            AIFunctionFactory.Create(wrapper.RecallAsync, "recall",
                "Search and retrieve memories. Supports free-text search, category filter, and key lookup. Returns matching memories ranked by relevance."),
        ];
    }

    // ── Task-state tools ────────────────────────────────────────

    [Description("List all tasks in the workspace with their status, assignee, and metadata.")]
    private async Task<string> ListTasksAsync(
        [Description("Optional status filter: Active, Completed, Cancelled, Blocked, InReview")]
        string? status = null)
    {
        _logger.LogDebug("Tool call: list_tasks (status={Status})", status);

        using var scope = _scopeFactory.CreateScope();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
        var tasks = await taskQueries.GetTasksAsync();

        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<Shared.Models.TaskStatus>(status, ignoreCase: true, out var parsed))
        {
            tasks = tasks.Where(t => t.Status == parsed).ToList();
        }

        if (tasks.Count == 0)
            return "No tasks found.";

        var lines = tasks.Select(t =>
            $"- [{t.Status}] {t.Title} (ID: {t.Id})" +
            (t.AssignedAgentName is not null ? $" → {t.AssignedAgentName}" : "") +
            (t.BranchName is not null ? $" branch: {t.BranchName}" : ""));

        return $"Tasks ({tasks.Count}):\n{string.Join('\n', lines)}";
    }

    [Description("List all collaboration rooms in the workspace.")]
    private async Task<string> ListRoomsAsync(
        [Description("Include archived rooms (default false)")]
        bool includeArchived = false)
    {
        _logger.LogDebug("Tool call: list_rooms (includeArchived={IncludeArchived})", includeArchived);

        using var scope = _scopeFactory.CreateScope();
        var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
        var rooms = await roomService.GetRoomsAsync(includeArchived);

        if (rooms.Count == 0)
            return "No rooms found.";

        var lines = rooms.Select(r =>
            $"- [{r.Status}] {r.Name} (ID: {r.Id})" +
            (r.Topic is not null ? $" — {r.Topic}" : "") +
            $" ({r.Participants.Count} participants)");

        return $"Rooms ({rooms.Count}):\n{string.Join('\n', lines)}";
    }

    [Description("List all agents with their current location and state.")]
    private async Task<string> ListAgentsAsync()
    {
        _logger.LogDebug("Tool call: show_agents");

        using var scope = _scopeFactory.CreateScope();
        var agentLocations = scope.ServiceProvider.GetRequiredService<IAgentLocationService>();
        var locations = await agentLocations.GetAgentLocationsAsync();

        var agentLines = new List<string>();
        foreach (var agent in _catalog.Agents)
        {
            var location = locations.FirstOrDefault(l => l.AgentId == agent.Id);
            var line = $"- {agent.Name} ({agent.Role})";
            if (location is not null)
                line += $" in room {location.RoomId}, state: {location.State}";
            agentLines.Add(line);
        }

        return $"Agents ({_catalog.Agents.Count}):\n{string.Join('\n', agentLines)}";
    }

    // ── Code-Write Tools ────────────────────────────────────────

    /// <summary>
    /// Creates <see cref="AIFunction"/> instances for the "code-write" tool group.
    /// These tools write files to the project directory and are scoped to the
    /// calling agent. Only agents with <c>code-write</c> in their
    /// <c>EnabledTools</c> (typically SoftwareEngineer role) receive these tools.
    /// Writes are permitted under <c>src/</c> (production code) and <c>tests/</c>
    /// (test fixtures and assertions that ship alongside production changes).
    /// When <paramref name="workspacePath"/> is provided the wrapper writes and
    /// commits inside that worktree instead of the develop checkout.
    /// </summary>
    public IReadOnlyList<AIFunction> CreateCodeWriteTools(string agentId, string agentName, AgentGitIdentity? gitIdentity = null, string? roomId = null, string? workspacePath = null)
    {
        var wrapper = new CodeWriteToolWrapper(
            _scopeFactory, _logger, agentId, agentName, gitIdentity, roomId,
            allowedRoots: new[] { "src", "tests" },
            protectedPaths: CodeWriteToolWrapper.CodeWriteProtectedPaths,
            scopeRoot: workspacePath,
            requireWorktree: true);
        return
        [
            AIFunctionFactory.Create(wrapper.WriteFileAsync, "write_file",
                "Write content to a file in the project. Creates the file if it doesn't exist, overwrites if it does. " +
                "The file is automatically staged for commit. Paths must be within src/ or tests/ and relative to the project root."),
            AIFunctionFactory.Create(wrapper.CommitChangesAsync, "commit_changes",
                "Commit all staged changes with a conventional commit message. Use after write_file to persist your changes. " +
                "Returns the commit SHA on success."),
        ];
    }

    /// <summary>
    /// Creates <see cref="AIFunction"/> instances for the "spec-write" tool group.
    /// These tools write files to the <c>specs/</c> and <c>docs/</c> directories
    /// only and are scoped to the calling agent. Typically granted to the Technical
    /// Writer role (Thucydides) so the spec corpus and documentation tree can be
    /// maintained by its owner without granting general code-write access.
    /// When <paramref name="workspacePath"/> is provided the wrapper writes and
    /// commits inside that worktree instead of the develop checkout.
    /// </summary>
    public IReadOnlyList<AIFunction> CreateSpecWriteTools(string agentId, string agentName, AgentGitIdentity? gitIdentity = null, string? roomId = null, string? workspacePath = null)
    {
        var wrapper = new CodeWriteToolWrapper(
            _scopeFactory, _logger, agentId, agentName, gitIdentity, roomId,
            allowedRoots: new[] { "specs", "docs" },
            protectedPaths: CodeWriteToolWrapper.SpecWriteProtectedPaths,
            scopeRoot: workspacePath);
        return
        [
            AIFunctionFactory.Create(wrapper.WriteFileAsync, "write_file",
                "Write content to a file in the project. Creates the file if it doesn't exist, overwrites if it does. " +
                "The file is automatically staged for commit. Paths must be within specs/ or docs/ and relative to the project root."),
            AIFunctionFactory.Create(wrapper.CommitChangesAsync, "commit_changes",
                "Commit all staged changes with a conventional commit message. Use after write_file to persist your changes. " +
                "Returns the commit SHA on success."),
        ];
    }

    // ── Helpers ──────────────────────────────────────────────────

    internal static string FindProjectRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "AgentAcademy.sln")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException(
            "AgentAcademy.sln not found — cannot determine project root for tool sandboxing.");
    }

    /// <summary>
    /// Resolves any symlinks in <paramref name="fullPath"/> and reports whether
    /// the final canonical target lies within <paramref name="projectRoot"/>.
    /// Walks the path so an intermediate-segment symlink (e.g. a subdir that
    /// links outside the repo) is also caught. Returns true when the path
    /// does not exist (caller will report "not found"), so this method only
    /// blocks confirmed escapes.
    /// </summary>
    internal static bool IsResolvedPathInsideRoot(string fullPath, string projectRoot)
    {
        try
        {
            var rootCanonical = ResolveCanonical(projectRoot);
            if (rootCanonical is null) return true; // can't validate; defer to other checks

            // If the file/dir doesn't exist yet, fall back to the deepest existing
            // ancestor for symlink resolution. This still catches "ancestor is a
            // symlink" escapes while not blocking legitimate not-found responses.
            string? probe = fullPath;
            while (probe is not null && !File.Exists(probe) && !Directory.Exists(probe))
            {
                probe = Path.GetDirectoryName(probe);
            }
            if (probe is null) return true;

            var targetCanonical = ResolveCanonical(probe);
            if (targetCanonical is null) return true;

            var rootWithSep = rootCanonical.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return targetCanonical.Equals(rootCanonical, StringComparison.Ordinal)
                || targetCanonical.StartsWith(rootWithSep, StringComparison.Ordinal);
        }
        catch
        {
            // On any unexpected I/O error, deny rather than allow.
            return false;
        }
    }

    private static string? ResolveCanonical(string path)
    {
        // FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true) follows
        // an arbitrarily long symlink chain. Path.GetFullPath then normalizes
        // any relative target. Returns null if the path doesn't refer to an
        // existing entry (caller decides what to do).
        FileSystemInfo? info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : File.Exists(path) ? new FileInfo(path) : null;
        if (info is null) return null;

        var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
        var finalPath = resolved?.FullName ?? info.FullName;
        return Path.GetFullPath(finalPath);
    }
}
