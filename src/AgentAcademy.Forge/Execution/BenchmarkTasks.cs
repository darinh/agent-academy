using AgentAcademy.Forge.Models;

namespace AgentAcademy.Forge.Execution;

/// <summary>
/// Frozen task briefs for the three Forge spike benchmark tasks.
/// Descriptions are kept concise — they're the LLM's input, not the acceptance criteria.
/// Acceptance criteria live in forge-spike/benchmarks/*.md (human reference only).
/// </summary>
public static class BenchmarkTasks
{
    /// <summary>
    /// T1: Build a small MCP server with two tools (code_search + file_read).
    /// </summary>
    public static readonly TaskBrief T1 = new()
    {
        TaskId = "T1",
        Title = "Build a Small MCP Server",
        Description = """
            Build a small MCP (Model Context Protocol) server with exactly two tools:

            1. **code_search** — Search for code patterns in repository files.
               - Input: `pattern` (string, required), `path` (string, optional — directory to search within).
               - Output: array of `{file, line, content}` matches.
               - Case-insensitive by default. Excludes node_modules/, .git/, bin/, obj/.
               - Max 100 results. Empty results return []. Invalid path returns error.

            2. **file_read** — Read the contents of a file.
               - Input: `path` (string, required), `startLine` (number, optional), `endLine` (number, optional).
               - Returns full file or specified line range (1-indexed, inclusive).
               - File not found and invalid ranges return errors. Max 10MB.

            The server must implement MCP protocol (JSON-RPC 2.0 over stdio), respond to `initialize`
            within 5 seconds, handle malformed requests gracefully, log diagnostics to stderr,
            and include TypeScript types or Python type hints.
            """
    };

    /// <summary>
    /// T2: Write a technical spec for the NotificationManager module.
    /// </summary>
    public static readonly TaskBrief T2 = new()
    {
        TaskId = "T2",
        Title = "Write Technical Spec for NotificationManager",
        Description = """
            Write an 800-1200 word technical specification for the NotificationManager module
            in this repository (src/AgentAcademy.Server/Notifications/NotificationManager.cs).

            Required sections:
            1. Overview / Purpose
            2. Architecture / Design
            3. API Reference (all 9 public methods with full signatures)
            4. Error Handling & Retry Logic
            5. Thread Safety & Concurrency
            6. Integration Points

            All class names, method signatures, types, and behaviors must match the actual code.
            The spec must reference the ConcurrentDictionary<string, INotificationProvider> storage,
            StringComparer.OrdinalIgnoreCase for lookups, NotificationRetryPolicy.ExecuteAsync usage,
            optional NotificationDeliveryTracker, and dependency injection setup in ServiceCollectionExtensions.cs.

            Document all 9 public methods: RegisterProvider, GetProvider, GetAllProviders, SendToAllAsync,
            RequestInputFromAnyAsync, SendAgentQuestionAsync, SendDirectMessageDisplayAsync,
            NotifyRoomRenamedAsync, NotifyRoomClosedAsync.

            Every claim must be verifiable from the source code. No aspirational statements.
            Output as Markdown.
            """
    };

    /// <summary>
    /// T3: Refactor NotificationManager.cs — adversarial test case.
    /// </summary>
    public static readonly TaskBrief T3 = new()
    {
        TaskId = "T3",
        Title = "Refactor NotificationManager (Adversarial Case)",
        Description = """
            Refactor src/AgentAcademy.Server/Notifications/NotificationManager.cs with these extractions:

            1. **Extract retry logic**: Create private helper `ExecuteWithRetryAsync<T>` that wraps
               `NotificationRetryPolicy.ExecuteAsync`. Replace all 5 inline retry calls
               (SendToAllAsync, SendAgentQuestionAsync, SendDirectMessageDisplayAsync,
               NotifyRoomRenamedAsync, NotifyRoomClosedAsync).

            2. **Simplify provider lookup**: Extract a `ConnectedProviders` property/method returning
               `IEnumerable<INotificationProvider>` filtered to `.Where(p => p.IsConnected)`.
               Replace all 5 inline filter calls. Must not cache (always fresh).

            3. **Consolidate error tracking**: Extract private helpers `RecordDeliveryIfTracked`,
               `RecordSkippedIfTracked`, `RecordFailureIfTracked` that check `_tracker is not null`
               internally. Replace all inline tracker calls.

            4. **Code quality**: Remove unnecessary null checks, add XML doc comments to new methods.

            Constraints:
            - No changes to public API surface.
            - All existing tests must pass without modification.
            - Preserve exact retry, logging, tracking, and cancellation behavior.
            - Thread safety must not be compromised.
            - Only NotificationManager.cs should change.
            """
    };
}
