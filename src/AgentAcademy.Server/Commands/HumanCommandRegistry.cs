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
            ]),

        new("CLEANUP_ROOMS", "Cleanup rooms", "operations",
            "Archive all stale rooms where every task is complete.",
            "Batch cleanup for rooms that have no remaining active work. Keeps the workspace tidy.",
            IsAsync: false, Fields: []),

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
