using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Contains the C# methods that the Copilot SDK exposes as callable tools
/// for agents. Each method is wrapped by <see cref="AIFunctionFactory.Create"/>
/// and passed to <see cref="GitHub.Copilot.SDK.SessionConfig.Tools"/>.
///
/// Tool functions use <see cref="IServiceScopeFactory"/> to resolve scoped
/// services (WorkspaceRuntime, DbContext) at invocation time.
///
/// Read-only tools (task-state, code) are agent-agnostic and created once.
/// Write tools (task-write, memory) capture the calling agent's identity
/// via closures and are created per-agent session.
/// </summary>
public sealed class AgentToolFunctions
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentToolFunctions> _logger;

    public AgentToolFunctions(
        IServiceScopeFactory scopeFactory,
        ILogger<AgentToolFunctions> logger)
    {
        _scopeFactory = scopeFactory;
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
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var tasks = await runtime.GetTasksAsync();

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
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var rooms = await runtime.GetRoomsAsync(includeArchived);

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
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var overview = await runtime.GetOverviewAsync();

        var agentLines = new List<string>();
        foreach (var agent in overview.ConfiguredAgents)
        {
            var location = overview.AgentLocations.FirstOrDefault(l => l.AgentId == agent.Id);
            var line = $"- {agent.Name} ({agent.Role})";
            if (location is not null)
                line += $" in room {location.RoomId}, state: {location.State}";
            agentLines.Add(line);
        }

        return $"Agents ({overview.ConfiguredAgents.Count}):\n{string.Join('\n', agentLines)}";
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

    // ── Inner wrapper classes for contextual (per-agent) tools ──

    /// <summary>
    /// Wrapper that captures agent identity for task-write tool functions.
    /// Methods have proper default parameter values so <see cref="AIFunctionFactory"/>
    /// treats nullable parameters as optional.
    /// </summary>
    internal sealed class TaskWriteToolWrapper
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger _logger;
        private readonly string _agentId;
        private readonly string _agentName;

        internal TaskWriteToolWrapper(
            IServiceScopeFactory scopeFactory, ILogger logger,
            string agentId, string agentName)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _agentId = agentId;
            _agentName = agentName;
        }

        private static readonly HashSet<string> AllowedTaskStatuses = new(StringComparer.OrdinalIgnoreCase)
        {
            nameof(Shared.Models.TaskStatus.Active),
            nameof(Shared.Models.TaskStatus.Blocked),
            nameof(Shared.Models.TaskStatus.AwaitingValidation),
            nameof(Shared.Models.TaskStatus.InReview),
            nameof(Shared.Models.TaskStatus.Queued),
        };

        [Description("Create a new task in the workspace.")]
        internal async Task<string> CreateTaskAsync(
            [Description("Task title")] string title,
            [Description("Detailed description of the task")] string description,
            [Description("Success criteria — what must be true for the task to be considered done")] string successCriteria,
            [Description("Preferred agent roles (e.g., SoftwareEngineer, Reviewer)")] string[]? preferredRoles = null,
            [Description("Task type: Feature, Bug, Refactor, Documentation, Test (default: Feature)")] string? type = null)
        {
            _logger.LogDebug("Tool call: create_task by {AgentId} (title={Title})", _agentId, title);

            if (string.IsNullOrWhiteSpace(title))
                return "Error: title is required.";
            if (string.IsNullOrWhiteSpace(description))
                return "Error: description is required.";
            if (string.IsNullOrWhiteSpace(successCriteria))
                return "Error: successCriteria is required.";

            var taskType = TaskType.Feature;
            if (!string.IsNullOrWhiteSpace(type) &&
                !Enum.TryParse<TaskType>(type, ignoreCase: true, out taskType))
            {
                return $"Error: Invalid task type '{type}'. Valid: Feature, Bug, Refactor, Documentation, Test";
            }

            var roles = preferredRoles?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList()
                ?? new List<string>();

            using var scope = _scopeFactory.CreateScope();
            var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

            try
            {
                var request = new TaskAssignmentRequest(
                    Title: title,
                    Description: description,
                    SuccessCriteria: successCriteria,
                    RoomId: null,
                    PreferredRoles: roles,
                    Type: taskType
                );

                var result = await runtime.CreateTaskAsync(request);
                return $"Task created successfully.\n" +
                       $"- ID: {result.Task.Id}\n" +
                       $"- Title: {result.Task.Title}\n" +
                       $"- Status: {result.Task.Status}\n" +
                       $"- Room: {result.Room.Name} (ID: {result.Room.Id})\n" +
                       $"- Type: {taskType}";
            }
            catch (Exception ex)
            {
                return $"Error creating task: {ex.Message}";
            }
        }

        [Description("Update a task's status, report a blocker, or post a note.")]
        internal async Task<string> UpdateTaskStatusAsync(
            [Description("ID of the task to update")] string taskId,
            [Description("New status: Active, Blocked, AwaitingValidation, InReview, Queued")] string? status = null,
            [Description("Blocker description (implies Blocked status — cannot be combined with status)")] string? blocker = null,
            [Description("Note to post on the task")] string? note = null)
        {
            _logger.LogDebug("Tool call: update_task_status by {AgentId} (taskId={TaskId})", _agentId, taskId);

            if (string.IsNullOrWhiteSpace(taskId))
                return "Error: taskId is required.";

            var hasStatus = !string.IsNullOrWhiteSpace(status);
            var hasBlocker = !string.IsNullOrWhiteSpace(blocker);
            var hasNote = !string.IsNullOrWhiteSpace(note);

            if (!hasStatus && !hasBlocker && !hasNote)
                return "Error: At least one of status, blocker, or note is required.";

            if (hasBlocker && hasStatus)
                return "Error: Cannot specify both 'blocker' and 'status' — blocker implies Blocked status.";

            if (hasStatus && !AllowedTaskStatuses.Contains(status!))
                return $"Error: Invalid status '{status}'. Allowed: {string.Join(", ", AllowedTaskStatuses.Order())}";

            using var scope = _scopeFactory.CreateScope();
            var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

            try
            {
                var task = await runtime.GetTaskAsync(taskId);
                if (task is null)
                    return $"Error: Task '{taskId}' not found.";

                var actions = new List<string>();

                if (hasBlocker)
                {
                    await runtime.UpdateTaskStatusAsync(taskId, Shared.Models.TaskStatus.Blocked);
                    await runtime.PostTaskNoteAsync(taskId,
                        $"🚫 Blocked by {_agentName}: {blocker}");
                    actions.Add($"status → Blocked (blocker: {blocker})");
                }
                else if (hasStatus)
                {
                    var parsed = Enum.Parse<Shared.Models.TaskStatus>(status!, ignoreCase: true);
                    await runtime.UpdateTaskStatusAsync(taskId, parsed);
                    actions.Add($"status → {parsed}");
                }

                if (hasNote)
                {
                    await runtime.PostTaskNoteAsync(taskId,
                        $"📝 Note from {_agentName}: {note}");
                    actions.Add("note posted");
                }

                var finalTask = await runtime.GetTaskAsync(taskId);
                var title = finalTask?.Title ?? task.Title;
                return $"Task '{title}' updated: {string.Join("; ", actions)}\n" +
                       $"- ID: {taskId}\n" +
                       $"- Status: {finalTask?.Status.ToString() ?? task.Status.ToString()}";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [Description("Add a comment, finding, evidence, or blocker to a task.")]
        internal async Task<string> AddTaskCommentAsync(
            [Description("ID of the task to comment on")] string taskId,
            [Description("Comment content")] string content,
            [Description("Comment type: Comment, Finding, Evidence, Blocker (default: Comment)")] string? commentType = null)
        {
            _logger.LogDebug("Tool call: add_task_comment by {AgentId} (taskId={TaskId})", _agentId, taskId);

            if (string.IsNullOrWhiteSpace(taskId))
                return "Error: taskId is required.";
            if (string.IsNullOrWhiteSpace(content))
                return "Error: content is required.";

            var parsedType = TaskCommentType.Comment;
            if (!string.IsNullOrWhiteSpace(commentType) &&
                !Enum.TryParse<TaskCommentType>(commentType, ignoreCase: true, out parsedType))
            {
                return $"Error: Invalid comment type '{commentType}'. Valid: Comment, Finding, Evidence, Blocker";
            }

            using var scope = _scopeFactory.CreateScope();
            var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

            try
            {
                var comment = await runtime.AddTaskCommentAsync(
                    taskId, _agentId, _agentName, parsedType, content);
                return $"Comment added to task '{taskId}'.\n" +
                       $"- Type: {parsedType}\n" +
                       $"- ID: {comment.Id}";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// Wrapper that captures agent identity for memory tool functions.
    /// </summary>
    internal sealed class MemoryToolWrapper
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger _logger;
        private readonly string _agentId;

        internal MemoryToolWrapper(
            IServiceScopeFactory scopeFactory, ILogger logger, string agentId)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _agentId = agentId;
        }

        [Description("Store a memory that persists across sessions.")]
        internal async Task<string> RememberAsync(
            [Description("Unique key for this memory (e.g., 'auth-pattern', 'deploy-gotcha')")] string key,
            [Description("The knowledge to remember")] string value,
            [Description("Category: decision, lesson, pattern, preference, invariant, risk, gotcha, incident, constraint, finding, spec-drift, mapping, verification, gap-pattern, shared")] string category,
            [Description("Optional time-to-live in hours (max 87600). Memory expires after this. Omit for permanent.")] int? ttl = null,
            [Description("Set to true to remove any existing TTL and make the memory permanent")] bool permanent = false)
        {
            _logger.LogDebug("Tool call: remember by {AgentId} (key={Key}, category={Category})",
                _agentId, key, category);

            if (string.IsNullOrWhiteSpace(key))
                return "Error: key is required.";
            if (string.IsNullOrWhiteSpace(value))
                return "Error: value is required.";
            if (string.IsNullOrWhiteSpace(category))
                return "Error: category is required.";

            if (!RememberHandler.ValidCategories.Contains(category))
                return $"Error: Invalid category '{category}'. Valid: {string.Join(", ", RememberHandler.ValidCategories.Order())}";

            if (ttl.HasValue && (ttl.Value <= 0 || ttl.Value > 87600))
                return "Error: ttl must be between 1 and 87600 hours.";

            category = category.ToLowerInvariant();

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

                var existing = await db.AgentMemories.FindAsync(_agentId, key);
                var now = DateTime.UtcNow;
                DateTime? expiresAt = ttl.HasValue ? now.AddHours(ttl.Value) : null;

                if (existing != null)
                {
                    existing.Category = category;
                    existing.Value = value;
                    existing.UpdatedAt = now;
                    if (permanent)
                        existing.ExpiresAt = null;
                    else if (ttl.HasValue)
                        existing.ExpiresAt = expiresAt;
                }
                else
                {
                    db.AgentMemories.Add(new AgentMemoryEntity
                    {
                        AgentId = _agentId,
                        Key = key,
                        Category = category,
                        Value = value,
                        CreatedAt = now,
                        ExpiresAt = expiresAt
                    });
                }

                try
                {
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateException) when (existing == null)
                {
                    // Concurrent insert race — retry as update
                    db.ChangeTracker.Clear();
                    var conflict = await db.AgentMemories.FindAsync(_agentId, key);
                    if (conflict != null)
                    {
                        conflict.Category = category;
                        conflict.Value = value;
                        conflict.UpdatedAt = now;
                        if (permanent)
                            conflict.ExpiresAt = null;
                        else if (ttl.HasValue)
                            conflict.ExpiresAt = expiresAt;
                        await db.SaveChangesAsync();
                        existing = conflict; // for the action message
                    }
                }

                var action = existing != null ? "updated" : "created";
                var result = $"Memory {action}: [{category}] {key}";
                if (permanent)
                    result += " (permanent)";
                else if (expiresAt.HasValue)
                    result += $" (expires: {expiresAt.Value:u})";
                return result;
            }
            catch (Exception ex)
            {
                return $"Error storing memory: {ex.Message}";
            }
        }

        [Description("Search and retrieve memories.")]
        internal async Task<string> RecallAsync(
            [Description("Free-text search query (uses full-text search with BM25 ranking)")] string? query = null,
            [Description("Filter by category")] string? category = null,
            [Description("Filter by exact key")] string? key = null,
            [Description("Include expired memories (default: false)")] bool includeExpired = false)
        {
            _logger.LogDebug("Tool call: recall by {AgentId} (query={Query}, category={Category})",
                _agentId, query, category);

            // Normalize category to match remember's lowercase storage
            if (!string.IsNullOrWhiteSpace(category))
                category = category.ToLowerInvariant();

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

                var now = DateTime.UtcNow;
                List<AgentMemoryEntity> memories;

                if (!string.IsNullOrWhiteSpace(query))
                {
                    memories = await RecallHandler.SearchWithFts5Async(
                        db, _agentId, query, category, key);
                }
                else
                {
                    var q = db.AgentMemories.Where(m => m.AgentId == _agentId || m.Category == "shared");

                    if (!string.IsNullOrWhiteSpace(category))
                        q = q.Where(m => m.Category == category);
                    if (!string.IsNullOrWhiteSpace(key))
                        q = q.Where(m => m.Key == key);

                    memories = await q.OrderBy(m => m.Category).ThenBy(m => m.Key).ToListAsync();
                }

                if (!includeExpired)
                    memories = memories.Where(m => m.ExpiresAt == null || m.ExpiresAt > now).ToList();

                // Update LastAccessedAt for staleness tracking (best-effort, matching RecallHandler)
                if (memories.Count > 0)
                {
                    try
                    {
                        foreach (var group in memories.GroupBy(m => m.AgentId))
                        {
                            var keyList = group.Select(g => g.Key).Distinct().ToList();
                            var placeholders = string.Join(", ", keyList.Select((_, i) => $"{{{i + 2}}}"));
                            var sql = $"UPDATE agent_memories SET LastAccessedAt = {{0}} WHERE AgentId = {{1}} AND Key IN ({placeholders})";
                            var parameters = new List<object> { now, group.Key };
                            parameters.AddRange(keyList);
                            await db.Database.ExecuteSqlRawAsync(sql, parameters.ToArray());
                        }
                    }
                    catch { /* LastAccessedAt update is best-effort */ }
                }

                if (memories.Count == 0)
                    return "No memories found.";

                var lines = memories.Select(m =>
                {
                    var line = $"- [{m.Category}] {m.Key}: {m.Value}";
                    if (m.Category == "shared" && m.AgentId != _agentId)
                        line += $" (from {m.AgentId})";
                    if (RecallHandler.IsStale(m, now))
                        line += " ⚠️ stale";
                    if (m.ExpiresAt.HasValue)
                        line += $" (expires: {m.ExpiresAt.Value:u})";
                    return line;
                });

                return $"Memories ({memories.Count}):\n{string.Join('\n', lines)}";
            }
            catch (Exception ex)
            {
                return $"Error recalling memories: {ex.Message}";
            }
        }
    }

    // ── Code-Write Tools ────────────────────────────────────────

    /// <summary>
    /// Creates <see cref="AIFunction"/> instances for the "code-write" tool group.
    /// These tools write files to the project directory and are scoped to the
    /// calling agent. Only agents with <c>code-write</c> in their
    /// <c>EnabledTools</c> (typically SoftwareEngineer role) receive these tools.
    /// </summary>
    public IReadOnlyList<AIFunction> CreateCodeWriteTools(string agentId, string agentName)
    {
        var wrapper = new CodeWriteToolWrapper(_scopeFactory, _logger, agentId, agentName);
        return
        [
            AIFunctionFactory.Create(wrapper.WriteFileAsync, "write_file",
                "Write content to a file in the project. Creates the file if it doesn't exist, overwrites if it does. " +
                "The file is automatically staged for commit. Paths must be within src/ and relative to the project root."),
        ];
    }

    /// <summary>
    /// Wrapper that captures agent identity for code-write tool functions.
    /// Enforces path restrictions: files must be within <c>src/</c> and cannot
    /// modify protected infrastructure files.
    /// </summary>
    internal sealed class CodeWriteToolWrapper
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger _logger;
        private readonly string _agentId;
        private readonly string _agentName;

        // Files that agents must never modify (core infrastructure).
        private static readonly string[] ProtectedPaths =
        [
            "Services/AgentPermissionHandler.cs",
            "Services/AgentToolFunctions.cs",
            "Services/AgentToolRegistry.cs",
            "Services/IAgentToolRegistry.cs",
            "Services/CopilotExecutor.cs",
            "Services/AgentOrchestrator.cs",
            "Services/GitService.cs",
            "Program.cs",
        ];

        private const int MaxContentLength = 100_000; // 100 KB

        internal CodeWriteToolWrapper(
            IServiceScopeFactory scopeFactory, ILogger logger,
            string agentId, string agentName)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _agentId = agentId;
            _agentName = agentName;
        }

        [Description("Write content to a file in the project. Creates the file if it doesn't exist, overwrites if it does. " +
                     "The file is automatically staged for commit. Paths must be within src/ and relative to the project root.")]
        internal async Task<string> WriteFileAsync(
            [Description("File path relative to the project root (e.g., src/AgentAcademy.Server/Models/MyModel.cs)")]
            string path,
            [Description("The full content to write to the file")]
            string content)
        {
            _logger.LogInformation("Tool call: write_file by {AgentId} (path={Path}, length={Length})",
                _agentId, path, content?.Length ?? 0);

            if (string.IsNullOrWhiteSpace(path))
                return "Error: path is required.";
            if (content is null)
                return "Error: content is required (use empty string for empty file).";
            if (content.Length > MaxContentLength)
                return $"Error: Content too large ({content.Length:N0} chars). Maximum is {MaxContentLength:N0} chars.";

            // Reject binary content (null bytes)
            if (content.Contains('\0'))
                return "Error: Binary content detected (null bytes). Only text files are supported.";

            var projectRoot = FindProjectRoot();
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, path));

            // Security: path must be within the project directory
            var rootWithSep = projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootWithSep, StringComparison.Ordinal))
                return "Error: Path traversal denied — file must be within the project directory.";

            // Restrict writes to src/ directory only
            var relativePath = Path.GetRelativePath(projectRoot, fullPath);
            if (!relativePath.StartsWith("src" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "Error: Writes are restricted to the src/ directory. Cannot write to: " + relativePath;

            // Block protected infrastructure files
            // Normalize separators to forward slashes for cross-platform comparison
            var normalizedRelative = relativePath.Replace('\\', '/');
            foreach (var protectedPath in ProtectedPaths)
            {
                if (normalizedRelative.EndsWith(protectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Agent {AgentId} attempted to write protected file: {Path}",
                        _agentId, relativePath);
                    return $"Error: {Path.GetFileName(protectedPath)} is a protected infrastructure file and cannot be modified by agents.";
                }
            }

            try
            {
                // Create parent directories if needed
                var directory = Path.GetDirectoryName(fullPath);
                if (directory is not null && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                var isNew = !File.Exists(fullPath);
                await File.WriteAllTextAsync(fullPath, content);

                _logger.LogInformation(
                    "Agent {AgentId} ({AgentName}) wrote file: {Path} ({Length} chars, new={IsNew})",
                    _agentId, _agentName, relativePath, content.Length, isNew);

                // Stage the file for commit
                var staged = await StageFileAsync(projectRoot, relativePath);

                var action = isNew ? "Created" : "Updated";
                var stageStatus = staged ? "staged for commit" : "written but NOT staged (git add failed)";
                return $"{action}: {relativePath} ({content.Length:N0} chars, {stageStatus})";
            }
            catch (UnauthorizedAccessException)
            {
                return $"Error: Permission denied writing to {relativePath}.";
            }
            catch (IOException ex)
            {
                return $"Error writing file: {ex.Message}";
            }
        }

        private async Task<bool> StageFileAsync(string projectRoot, string relativePath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    WorkingDirectory = projectRoot
                };
                psi.ArgumentList.Add("add");
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add(relativePath);

                using var process = Process.Start(psi);
                if (process is not null)
                {
                    await process.WaitForExitAsync();
                    if (process.ExitCode != 0)
                    {
                        var stderr = await process.StandardError.ReadToEndAsync();
                        _logger.LogWarning("git add failed for {Path}: {Error}", relativePath, stderr);
                        return false;
                    }
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stage file {Path} — file was written but not staged", relativePath);
                return false;
            }
        }
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
