using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Commands;

/// <summary>
/// Authoritative registry of human-executable commands with full metadata.
/// Drives the GET /api/commands/metadata endpoint and replaces the frontend's
/// hardcoded command catalog as the single source of truth.
/// </summary>
public static class HumanCommandRegistry
{
    private static readonly IReadOnlyList<HumanCommandMetadata> Commands =
    [
        new("READ_FILE", "Read file", "code",
            "Inspect a repository file with optional line windows.",
            "Best for source spelunking, spec checks, and quick spot reads without leaving the workspace.",
            IsAsync: false,
            Fields:
            [
                new("path", "Path", "text", "Repository-relative file path.",
                    Placeholder: "src/AgentAcademy.Server/Program.cs", Required: true),
                new("startLine", "Start line", "number", "Optional first line to include.",
                    Placeholder: "1"),
                new("endLine", "End line", "number", "Optional final line to include.",
                    Placeholder: "120"),
            ]),

        new("SEARCH_CODE", "Search code", "code",
            "Run a focused repository text search.",
            "Supports optional subpath and glob filters for tighter scans without exposing raw shell access.",
            IsAsync: false,
            Fields:
            [
                new("query", "Query", "text", "Literal or regex-like grep pattern.",
                    Placeholder: "CommandController", Required: true),
                new("path", "Path filter", "text", "Optional subdirectory inside the repo.",
                    Placeholder: "src/agent-academy-client/src"),
                new("glob", "Glob filter", "text", "Optional include glob passed to grep.",
                    Placeholder: "*.tsx"),
            ]),

        new("LIST_ROOMS", "List rooms", "workspace",
            "Snapshot active rooms, phases, status, and participant counts.",
            "Useful when triaging the collaboration state before jumping into a room.",
            IsAsync: false, Fields: []),

        new("LIST_AGENTS", "List agents", "workspace",
            "Inspect agent locations, roles, and current state.",
            "Shows where the team is parked without needing planner-only tooling.",
            IsAsync: false, Fields: []),

        new("LIST_TASKS", "List tasks", "workspace",
            "Review all tasks, or filter by status or assignee.",
            "The fastest way to check the queue from the UI before diving into a branch or room.",
            IsAsync: false,
            Fields:
            [
                new("status", "Status", "text", "Optional task status filter.",
                    Placeholder: "Active"),
                new("assignee", "Assignee", "text", "Optional agent id or name.",
                    Placeholder: "Athena"),
            ]),

        new("LIST_COMMANDS", "List commands", "workspace",
            "List all available commands with their authorization status.",
            "Returns every registered command with a flag indicating whether the requesting agent is authorized to use it.",
            IsAsync: false, Fields: []),

        new("SHOW_DIFF", "Show diff", "git",
            "Inspect uncommitted changes or diff against a branch.",
            "Returns a trimmed git diff summary so humans can review work without opening a terminal.",
            IsAsync: false,
            Fields:
            [
                new("branch", "Branch", "text", "Optional branch to diff against.",
                    Placeholder: "develop"),
            ]),

        new("GIT_LOG", "Git log", "git",
            "Browse recent commits with optional file or date filtering.",
            "Good for reconstructing recent moves before asking an agent to continue the work.",
            IsAsync: false,
            Fields:
            [
                new("count", "Count", "number", "Optional limit, capped by the backend.",
                    Placeholder: "20"),
                new("since", "Since", "text", "Optional git-compatible since expression.",
                    Placeholder: "2 days ago"),
                new("file", "File", "text", "Optional file path filter.",
                    Placeholder: "src/agent-academy-client/src/App.tsx"),
            ]),

        new("SHOW_REVIEW_QUEUE", "Review queue", "workspace",
            "See tasks waiting on review or validation.",
            "A fast reviewer-focused queue without exposing task mutation commands.",
            IsAsync: false, Fields: []),

        new("ROOM_HISTORY", "Room history", "workspace",
            "Load recent messages from any room without navigating there.",
            "Use this to grab context before entering a room or to review archived conversations.",
            IsAsync: false,
            Fields:
            [
                new("roomId", "Room ID", "text", "Target room identifier.",
                    Placeholder: "agent-academy-main", Required: true),
                new("count", "Message count", "number", "Optional number of messages to return.",
                    Placeholder: "20"),
            ]),

        new("ROOM_TOPIC", "Room topic", "workspace",
            "Set or clear the topic description for a room.",
            "Useful for providing context to agents and humans about the room's current focus area.",
            IsAsync: false,
            Fields:
            [
                new("roomId", "Room ID", "text", "Target room identifier.",
                    Placeholder: "agent-academy-main", Required: true),
                new("topic", "Topic", "text", "New topic text, or leave empty to clear.",
                    Placeholder: "Implementing command metadata endpoint"),
            ]),

        new("CREATE_ROOM", "Create room", "workspace",
            "Create a new persistent collaboration room.",
            "Spin up a dedicated room for a new work stream. Agents can be invited after creation.",
            IsAsync: false,
            Fields:
            [
                new("name", "Room name", "text", "Unique identifier for the new room.",
                    Placeholder: "feature-auth-rework", Required: true),
                new("topic", "Topic", "text", "Optional initial topic for the room.",
                    Placeholder: "Rework authentication flow"),
            ]),

        new("REOPEN_ROOM", "Reopen room", "workspace",
            "Reopen an archived room for continued work.",
            "Use when a previously completed work stream needs further changes or follow-up.",
            IsAsync: false,
            Fields:
            [
                new("roomId", "Room ID", "text", "Identifier of the archived room to reopen.",
                    Required: true),
            ]),

        new("CLOSE_ROOM", "Close room", "workspace",
            "Archive a non-main room.",
            "Archives the room and notifies participants. Cannot close the main room.",
            IsAsync: false,
            Fields:
            [
                new("roomId", "Room ID", "text", "Identifier of the room to archive.",
                    Required: true),
            ],
            IsDestructive: true,
            DestructiveWarning: "This will archive the room permanently. Agents in the room will be moved out."),

        new("CLEANUP_ROOMS", "Cleanup rooms", "operations",
            "Archive all stale rooms where every task is complete.",
            "Batch cleanup for rooms that have no remaining active work. Keeps the workspace tidy.",
            IsAsync: false, Fields: [],
            IsDestructive: true,
            DestructiveWarning: "This will archive all stale rooms where tasks are complete. Multiple rooms may be affected."),

        new("INVITE_TO_ROOM", "Invite to room", "workspace",
            "Move another agent to a specified room.",
            "Useful for assembling a team in a breakout room or pulling in a specialist.",
            IsAsync: false,
            Fields:
            [
                new("agentId", "Agent ID", "text", "The agent to invite.",
                    Required: true),
                new("roomId", "Room ID", "text", "Target room identifier.",
                    Required: true),
            ]),

        new("RUN_BUILD", "Run build", "operations",
            "Kick off a backend build and poll for the result.",
            "Async on purpose so the UI stays responsive while the server serializes build access.",
            IsAsync: true, Fields: []),

        new("RUN_TESTS", "Run tests", "operations",
            "Launch the test suite with an optional scope hint.",
            "Supports all, backend, frontend, or custom file filters with the backend polling contract.",
            IsAsync: true,
            Fields:
            [
                new("scope", "Scope", "text",
                    "Optional scope: all, backend, frontend, or file:<filter>.",
                    Placeholder: "frontend", DefaultValue: "all"),
            ]),

        new("EXPORT_MEMORIES", "Export memories", "workspace",
            "Export an agent's stored memories as structured JSON.",
            "Useful for backup, transfer between agents, or inspection of learned knowledge.",
            IsAsync: false,
            Fields:
            [
                new("category", "Category", "text", "Optional category filter.",
                    Placeholder: "pattern"),
            ]),

        new("CREATE_TASK_ITEM", "Create task item", "workspace",
            "Create a work item assigned to yourself or another agent.",
            "Break down complex work into trackable sub-items within a room.",
            IsAsync: false,
            Fields:
            [
                new("title", "Title", "text", "Short title for the work item.",
                    Placeholder: "Implement auth middleware", Required: true),
                new("description", "Description", "text", "Optional details about what the item involves.",
                    Placeholder: "Add JWT validation to all API endpoints"),
                new("assignedTo", "Assigned to", "text", "Agent ID or name. Defaults to current agent.",
                    Placeholder: "Hephaestus"),
                new("roomId", "Room ID", "text", "Target room. Defaults to current room.",
                    Placeholder: "feature-auth-rework"),
            ]),

        new("UPDATE_TASK_ITEM", "Update task item", "workspace",
            "Update a task item's status and optionally attach evidence.",
            "Mark items as Active, Done, or Rejected. Attach evidence when completing items.",
            IsAsync: false,
            Fields:
            [
                new("taskItemId", "Task item ID", "text", "The task item to update.",
                    Required: true),
                new("status", "Status", "text", "New status: Pending, Active, Done, or Rejected.",
                    Placeholder: "Done", Required: true),
                new("evidence", "Evidence", "text", "Optional evidence of completion.",
                    Placeholder: "All tests passing, see commit abc123"),
            ]),

        new("LIST_TASK_ITEMS", "List task items", "workspace",
            "List task items with optional room or status filters.",
            "View all work items in the workspace, or narrow down by room or status.",
            IsAsync: false,
            Fields:
            [
                new("roomId", "Room ID", "text", "Optional room to filter by.",
                    Placeholder: "feature-auth-rework"),
                new("status", "Status", "text", "Optional status filter: Pending, Active, Done, Rejected.",
                    Placeholder: "Active"),
            ]),

        new("REBASE_TASK", "Rebase task branch", "git",
            "Rebase a task's feature branch onto develop to resolve divergence.",
            "Use before MERGE_TASK when the branch has fallen behind develop. Supports dry-run mode to check for conflicts without modifying the branch.",
            IsAsync: false,
            Fields:
            [
                new("taskId", "Task ID", "text", "The task whose branch to rebase.",
                    Required: true),
                new("dryRun", "Dry run", "text", "Set to 'true' to check for conflicts without rebasing.",
                    Placeholder: "false"),
            ]),

        new("CREATE_PR", "Create pull request", "git",
            "Push a task branch to GitHub and open a pull request.",
            "Pushes the task's branch to the remote origin and creates a GitHub PR targeting develop. Updates the task with the PR URL and number.",
            IsAsync: true,
            Fields:
            [
                new("taskId", "Task ID", "text", "The task to create a PR for.",
                    Required: true),
                new("title", "PR title", "text", "Custom PR title. Defaults to the task title.",
                    Placeholder: "feat: add user avatars"),
                new("body", "PR body", "text", "Custom PR body. Defaults to task description + success criteria."),
                new("baseBranch", "Base branch", "text", "Target branch for the PR.",
                    Placeholder: "develop"),
            ]),

        new("POST_PR_REVIEW", "Post PR review", "git",
            "Post a review comment, approval, or change request on a task's pull request.",
            "Posts a review on the task's GitHub PR. Supports APPROVE, REQUEST_CHANGES, and COMMENT actions. Only Planner, Reviewer, and Human roles can post reviews.",
            IsAsync: false,
            Fields:
            [
                new("taskId", "Task ID", "text", "The task whose PR to review.",
                    Required: true),
                new("body", "Review body", "text", "The review comment text.",
                    Placeholder: "LGTM — tests pass and code is clean.", Required: true),
                new("action", "Action", "text", "APPROVE, REQUEST_CHANGES, or COMMENT.",
                    Placeholder: "COMMENT", DefaultValue: "COMMENT"),
            ]),

        new("GET_PR_REVIEWS", "Get PR reviews", "git",
            "Retrieve all reviews on a task's pull request.",
            "Fetches review history from GitHub including author, verdict, body, and timestamp. Useful for checking review status before merging.",
            IsAsync: false,
            Fields:
            [
                new("taskId", "Task ID", "text", "The task whose PR reviews to fetch.",
                    Required: true),
            ]),

        new("MERGE_PR", "Merge pull request", "git",
            "Squash-merge a task's pull request on GitHub.",
            "Merges the task's PR via the GitHub API using squash merge. Updates the task to Completed with the merge commit SHA. Use instead of MERGE_TASK when a PR exists.",
            IsAsync: true,
            Fields:
            [
                new("taskId", "Task ID", "text", "The task whose PR to merge.",
                    Required: true),
                new("deleteBranch", "Delete branch", "text", "Set to 'true' to delete the head branch after merging.",
                    Placeholder: "false", DefaultValue: "false"),
            ]),

        new("LINK_TASK_TO_SPEC", "Link task to spec", "workspace",
            "Create a traceability link between a task and a spec section.",
            "Records which spec sections a task implements, modifies, fixes, or references. Enables filtered spec loading and drift detection.",
            IsAsync: false,
            Fields:
            [
                new("taskId", "Task ID", "text", "The task to link.",
                    Required: true),
                new("specSectionId", "Spec section", "text", "The spec directory name (e.g., '003-agent-system').",
                    Placeholder: "003-agent-system", Required: true),
                new("linkType", "Link type", "text", "Relationship type: Implements, Modifies, Fixes, or References.",
                    Placeholder: "Implements", DefaultValue: "Implements"),
                new("note", "Note", "text", "Optional note describing what the task changes in this spec.",
                    Placeholder: "Adds SDK tool calling section"),
            ]),

        new("SHOW_UNLINKED_CHANGES", "Show unlinked changes", "workspace",
            "List active tasks that have no spec links.",
            "Detects potential spec drift by finding tasks without traceability to spec sections. Use LINK_TASK_TO_SPEC to resolve.",
            IsAsync: false,
            Fields: []),

        new("UPDATE_TASK", "Update task", "workspace",
            "Update a task's status, blocker, or note.",
            "Change task state directly. Allowed statuses: Active, Blocked, AwaitingValidation, InReview, Queued. Use CANCEL_TASK or APPROVE_TASK + MERGE_TASK for terminal states.",
            IsAsync: false,
            Fields:
            [
                new("taskId", "Task ID", "text", "The task to update.",
                    Required: true),
                new("status", "Status", "text", "New status for the task.",
                    Placeholder: "Completed"),
                new("blocker", "Blocker", "text", "Blocker description (implies Blocked status)."),
                new("note", "Note", "text", "Note to attach to the task."),
            ]),

        new("CANCEL_TASK", "Cancel task", "workspace",
            "Cancel a task and optionally delete its branch.",
            "Cancels a task in any non-terminal state. Useful for cleaning up abandoned or duplicate work.",
            IsAsync: false,
            Fields:
            [
                new("taskId", "Task ID", "text", "The task to cancel.",
                    Required: true),
                new("reason", "Reason", "text", "Why the task is being cancelled.",
                    Placeholder: "Work completed via alternate path"),
                new("deleteBranch", "Delete branch", "text", "Set to 'false' to keep the branch.",
                    Placeholder: "true"),
            ],
            IsDestructive: true,
            DestructiveWarning: "This will permanently cancel the task. The task branch may be deleted."),

        new("APPROVE_TASK", "Approve task", "workspace",
            "Approve a task after review.",
            "Marks a task as approved. Only works on tasks in InReview or AwaitingValidation status.",
            IsAsync: false,
            Fields:
            [
                new("taskId", "Task ID", "text", "The task to approve.",
                    Required: true),
                new("findings", "Findings", "text", "Optional review findings or notes."),
            ]),

        // ── Memory management ──

        new("REMEMBER", "Remember", "operations",
            "Store a key-value memory entry.",
            "Persists a fact or preference that agents can recall later. Supports optional category and TTL.",
            IsAsync: true,
            Fields:
            [
                new("key", "Key", "text", "Unique identifier for this memory.",
                    Placeholder: "build-command", Required: true),
                new("value", "Value", "text", "The content to remember.",
                    Placeholder: "dotnet build && npm run build", Required: true),
                new("category", "Category", "text",
                    "Grouping tag: decision, lesson, pattern, preference, invariant, risk, gotcha, incident, constraint, finding, spec-drift, mapping, verification, gap-pattern, shared.",
                    Placeholder: "decision", Required: true),
                new("ttl", "TTL (hours)", "number", "Auto-expire after this many hours. Omit for permanent."),
            ]),

        new("FORGET", "Forget", "operations",
            "Delete a memory entry by key.",
            "Permanently removes a stored memory. Use LIST_MEMORIES to find the key first.",
            IsAsync: true,
            Fields:
            [
                new("key", "Key", "text", "The memory key to delete.",
                    Required: true),
            ]),

        new("LIST_MEMORIES", "List memories", "operations",
            "Browse stored memories with optional category filter.",
            "Shows all persisted memories for the workspace. Filter by category to narrow results.",
            IsAsync: true,
            Fields:
            [
                new("category", "Category", "text", "Optional category filter.",
                    Placeholder: "workflow"),
                new("include_expired", "Include expired", "text", "Set to 'true' to include expired entries.",
                    Placeholder: "false"),
            ]),

        new("RECALL", "Recall", "operations",
            "Search memories by query, category, or key.",
            "Fuzzy-search across all stored memories. More flexible than LIST_MEMORIES for discovery.",
            IsAsync: true,
            Fields:
            [
                new("query", "Query", "text", "Free-text search across memory values.",
                    Placeholder: "build command"),
                new("category", "Category", "text", "Optional category filter."),
                new("key", "Key", "text", "Optional exact key lookup."),
                new("include_expired", "Include expired", "text", "Set to 'true' to include expired entries.",
                    Placeholder: "false"),
            ]),

        new("IMPORT_MEMORIES", "Import memories", "operations",
            "Bulk import memories from JSON.",
            "Accepts a JSON array of memory objects to upsert in batch.",
            IsAsync: true,
            Fields:
            [
                new("memories", "Memories JSON", "text",
                    "JSON array: [{\"category\":\"...\",\"key\":\"...\",\"value\":\"...\",\"ttl\":null}]",
                    Required: true),
            ]),

        // ── Task lifecycle ──

        new("ADD_TASK_COMMENT", "Add task comment", "workspace",
            "Attach a comment, finding, evidence, or blocker note to a task.",
            "Adds structured annotations to a task. Type determines how the comment is displayed.",
            IsAsync: true,
            Fields:
            [
                new("taskId", "Task ID", "text", "The task to comment on.",
                    Required: true),
                new("content", "Content", "text", "The comment text.",
                    Required: true),
                new("type", "Type", "text", "Comment | Finding | Evidence | Blocker",
                    Placeholder: "Comment"),
            ]),

        new("CLAIM_TASK", "Claim task", "workspace",
            "Assign yourself to a task for manual work.",
            "Claims an unassigned or available task. Useful for human-driven implementation.",
            IsAsync: true,
            Fields:
            [
                new("taskId", "Task ID", "text", "The task to claim.",
                    Required: true),
            ]),

        new("RELEASE_TASK", "Release task", "workspace",
            "Unassign yourself from a claimed task.",
            "Releases a task back to the pool so another agent or human can pick it up.",
            IsAsync: true,
            Fields:
            [
                new("taskId", "Task ID", "text", "The task to release.",
                    Required: true),
            ]),

        new("REJECT_TASK", "Reject task", "workspace",
            "Reject a task and send it back for rework.",
            "Reverts an approved or completed task to changes-requested status with a reason.",
            IsAsync: true,
            Fields:
            [
                new("taskId", "Task ID", "text", "The task to reject.",
                    Required: true),
                new("reason", "Reason", "text", "Why the task is being rejected.",
                    Required: true),
            ]),

        new("REQUEST_CHANGES", "Request changes", "workspace",
            "Request changes on a task with specific findings.",
            "Sends a task back for revision with detailed feedback on what needs to change.",
            IsAsync: true,
            Fields:
            [
                new("taskId", "Task ID", "text", "The task to request changes on.",
                    Required: true),
                new("findings", "Findings", "text", "Detailed description of required changes.",
                    Required: true),
            ]),

        new("MERGE_TASK", "Merge task", "workspace",
            "Squash-merge a task branch into develop.",
            "Completes a task by merging its branch. Only works on approved tasks with clean branches.",
            IsAsync: true,
            Fields:
            [
                new("taskId", "Task ID", "text", "The task to merge.",
                    Required: true),
            ]),

        // ── Server operations ──

        new("RESTART_SERVER", "Restart server", "operations",
            "Trigger a graceful server restart.",
            "Initiates a controlled shutdown and restart via the wrapper script. Rate-limited.",
            IsAsync: true,
            Fields:
            [
                new("reason", "Reason", "text", "Why the server is being restarted.",
                    Placeholder: "Applied configuration changes", Required: true),
            ]),

        // ── Evidence ledger ──

        new("RECORD_EVIDENCE", "Record evidence", "workspace",
            "Record a structured verification check against a task.",
            "Captures a verification result (build, tests, code review, etc.) with phase, tool, exit code, and output. Evidence is queryable and used for gate checks before phase transitions.",
            IsAsync: false,
            Fields:
            [
                new("taskId", "Task ID", "text", "The task to record evidence for.",
                    Required: true),
                new("checkName", "Check name", "text", "Name of the check (e.g. 'build', 'tests', 'type-check', 'code-review').",
                    Required: true),
                new("passed", "Passed", "text", "Whether the check passed ('true' or 'false').",
                    Required: true),
                new("phase", "Phase", "text", "Evidence phase: Baseline, After, or Review.",
                    Placeholder: "After"),
                new("tool", "Tool", "text", "Tool used (e.g. 'bash', 'manual').",
                    Placeholder: "manual"),
                new("command", "Command", "text", "Command that was run."),
                new("exitCode", "Exit code", "text", "Exit code of the command."),
                new("output", "Output", "text", "Truncated output or summary (max 500 chars)."),
            ]),

        new("QUERY_EVIDENCE", "Query evidence", "workspace",
            "Query the evidence ledger for a task.",
            "Returns all recorded evidence for a task, optionally filtered by phase (Baseline, After, Review). Shows check names, pass/fail status, tools, and output.",
            IsAsync: false,
            Fields:
            [
                new("taskId", "Task ID", "text", "The task to query evidence for.",
                    Required: true),
                new("phase", "Phase", "text", "Filter by phase: Baseline, After, or Review.",
                    Placeholder: "all"),
            ]),

        new("CHECK_GATES", "Check gates", "workspace",
            "Check if a task meets evidence requirements for phase transition.",
            "Evaluates minimum evidence gates: Implementation→AwaitingValidation (≥1 After check), AwaitingValidation→InReview (≥2 After checks), InReview→Approved (≥1 Review check). Shows what's missing.",
            IsAsync: false,
            Fields:
            [
                new("taskId", "Task ID", "text", "The task to check gates for.",
                    Required: true),
            ]),
    ];

    private static readonly Dictionary<string, HumanCommandMetadata> Index =
        Commands.ToDictionary(c => c.Command, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns all registered human command definitions.</summary>
    public static IReadOnlyList<HumanCommandMetadata> GetAll() => Commands;

    /// <summary>Returns metadata for a single command, or null if not registered.</summary>
    public static HumanCommandMetadata? Get(string command) =>
        Index.TryGetValue(command, out var meta) ? meta : null;

    /// <summary>Returns the set of registered command names.</summary>
    public static IReadOnlyCollection<string> CommandNames => Index.Keys;
}
