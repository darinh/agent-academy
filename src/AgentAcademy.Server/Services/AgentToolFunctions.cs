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
public sealed class AgentToolFunctions
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
    /// </summary>
    public IReadOnlyList<AIFunction> CreateCodeTools()
    {
        return
        [
            AIFunctionFactory.Create(ReadFileAsync, "read_file",
                "Read a file's contents from the project. Supports optional line range. Paths are relative to the project root."),
            AIFunctionFactory.Create(SearchCodeAsync, "search_code",
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
        var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
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

    // ── Code tools ──────────────────────────────────────────────

    [Description("Read a file's contents from the project. Paths are relative to the project root.")]
    private async Task<string> ReadFileAsync(
        [Description("File path relative to the project root (e.g., src/AgentAcademy.Server/Program.cs)")]
        string path,
        [Description("Start line number (1-based, default 1)")]
        int startLine = 1,
        [Description("End line number (default: end of file)")]
        int? endLine = null)
    {
        _logger.LogDebug("Tool call: read_file (path={Path}, startLine={Start})", path, startLine);

        var projectRoot = FindProjectRoot();
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, path));

        // Security: path must be within the project directory
        var rootWithSep = projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSep, StringComparison.Ordinal) &&
            !fullPath.Equals(projectRoot, StringComparison.Ordinal))
        {
            return "Error: Path traversal denied — file must be within the project directory.";
        }

        if (Directory.Exists(fullPath))
        {
            var entries = Directory.GetFileSystemEntries(fullPath)
                .Select(e => Path.GetRelativePath(projectRoot, e))
                .OrderBy(e => e)
                .ToList();
            return $"Directory: {path}\nEntries ({entries.Count}):\n{string.Join('\n', entries)}";
        }

        if (!File.Exists(fullPath))
            return $"Error: File not found: {path}";

        var lines = await File.ReadAllLinesAsync(fullPath);
        var totalLines = lines.Length;

        startLine = Math.Max(1, startLine);
        var end = endLine ?? totalLines;
        end = Math.Min(totalLines, end);

        var selected = lines.Skip(startLine - 1).Take(end - startLine + 1).ToArray();
        var content = string.Join('\n', selected);

        // Truncate to prevent huge responses
        const int maxLen = 12_000;
        var truncated = false;
        if (content.Length > maxLen)
        {
            truncated = true;
            content = content[..maxLen];
            var lastNewline = content.LastIndexOf('\n');
            if (lastNewline > 0)
                content = content[..lastNewline];
        }

        var header = $"File: {path} ({totalLines} lines, showing {startLine}-{end})";
        if (truncated)
            header += " [TRUNCATED — use startLine/endLine to read more]";
        return $"{header}\n\n{content}";
    }

    [Description("Search for text patterns in the project codebase using git grep.")]
    private async Task<string> SearchCodeAsync(
        [Description("Search query (text pattern to find)")]
        string query,
        [Description("Subdirectory path to restrict search to (e.g., src/AgentAcademy.Server)")]
        string? path = null,
        [Description("Glob pattern to filter files (e.g., *.cs, *.ts)")]
        string? glob = null,
        [Description("Case-insensitive search (default false)")]
        bool ignoreCase = false)
    {
        _logger.LogDebug("Tool call: search_code (query={Query}, path={Path})", query, path);

        var projectRoot = FindProjectRoot();

        // Validate path is within project before building the command
        if (path is not null)
        {
            var fullSubPath = Path.GetFullPath(Path.Combine(projectRoot, path));
            var rootWithSep = projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullSubPath.StartsWith(rootWithSep, StringComparison.Ordinal) &&
                !fullSubPath.Equals(projectRoot, StringComparison.Ordinal))
            {
                return "Error: Path traversal denied — path must be within the project directory.";
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = projectRoot
        };
        psi.ArgumentList.Add("--no-pager");
        psi.ArgumentList.Add("grep");
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("--color=never");
        psi.ArgumentList.Add("-I"); // skip binary files
        psi.ArgumentList.Add("-F"); // fixed-string search (not regex)

        if (ignoreCase)
            psi.ArgumentList.Add("-i");

        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(query);

        // Path and glob filtering
        if (glob is not null || path is not null)
        {
            psi.ArgumentList.Add("--");
            if (glob is not null && path is not null)
            {
                var relPath = Path.GetRelativePath(projectRoot,
                    Path.GetFullPath(Path.Combine(projectRoot, path)));
                psi.ArgumentList.Add($":(glob){relPath}/**/{glob}");
            }
            else if (path is not null)
            {
                psi.ArgumentList.Add(path);
            }
            else
            {
                psi.ArgumentList.Add(glob!);
            }
        }

        const int maxResults = 50;

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return "Error: Failed to start search process.";

            // Read stdout line-by-line with a global cap to prevent
            // unbounded memory usage (--max-count is per-file in git grep).
            var lines = new List<string>(maxResults);
            while (lines.Count < maxResults)
            {
                var line = await process.StandardOutput.ReadLineAsync();
                if (line is null) break;
                lines.Add(line);
            }

            // Drain stderr concurrently to prevent deadlock
            // (filled stderr pipe buffer blocks the process).
            var stderr = await process.StandardError.ReadToEndAsync();

            // Kill the process if it's still running (we have enough results)
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            }

            // Use a timeout to avoid hanging forever
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { /* process already killed above */ }

            // git grep exit codes: 0 = matches found, 1 = no matches, >1 = error
            if (process.ExitCode > 1 && lines.Count == 0)
            {
                var errMsg = stderr.Length > 200 ? stderr[..200] : stderr;
                return $"Error: Search failed (exit {process.ExitCode}): {errMsg.Trim()}";
            }

            if (lines.Count == 0)
                return $"No results found for: {query}";

            var truncated = lines.Count >= maxResults;
            var header = $"Search results for \"{query}\" ({lines.Count} matches)";
            if (truncated)
                header += $" [capped at {maxResults} — narrow with path/glob]";

            return $"{header}:\n\n{string.Join('\n', lines)}";
        }
        catch (Exception ex)
        {
            return $"Error: Search failed: {ex.Message}";
        }
    }

    // ── Code-Write Tools ────────────────────────────────────────

    /// <summary>
    /// Creates <see cref="AIFunction"/> instances for the "code-write" tool group.
    /// These tools write files to the project directory and are scoped to the
    /// calling agent. Only agents with <c>code-write</c> in their
    /// <c>EnabledTools</c> (typically SoftwareEngineer role) receive these tools.
    /// </summary>
    public IReadOnlyList<AIFunction> CreateCodeWriteTools(string agentId, string agentName, AgentGitIdentity? gitIdentity = null, string? roomId = null)
    {
        var wrapper = new CodeWriteToolWrapper(_scopeFactory, _logger, agentId, agentName, gitIdentity, roomId);
        return
        [
            AIFunctionFactory.Create(wrapper.WriteFileAsync, "write_file",
                "Write content to a file in the project. Creates the file if it doesn't exist, overwrites if it does. " +
                "The file is automatically staged for commit. Paths must be within src/ and relative to the project root."),
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
}
