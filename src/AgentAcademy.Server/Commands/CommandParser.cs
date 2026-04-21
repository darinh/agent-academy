using System.Text;
using System.Text.RegularExpressions;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Commands;

/// <summary>
/// Extracts structured commands from agent text responses.
/// Commands use COMMAND_NAME: syntax. Any uppercase header that is not in
/// <see cref="KnownCommands"/> (including legacy TASK ASSIGNMENT /
/// WORK REPORT / REVIEW blocks) passes through untouched to RemainingText.
/// </summary>
public sealed class CommandParser
{
    // Commands whose inline value is always raw text, never key=value pairs.
    // Prevents ParseInlineArgs from splitting commit messages like "fix: set timeout=30s".
    private static readonly HashSet<string> RawValueCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "COMMIT_CHANGES"
    };

    // Known command names — prevents false positives from random UPPERCASE: text
    internal static readonly HashSet<string> KnownCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "READ_FILE", "SEARCH_CODE", "LIST_ROOMS", "LIST_AGENTS", "LIST_TASKS",
        "REMEMBER", "RECALL", "LIST_MEMORIES", "FORGET", "EXPORT_MEMORIES", "IMPORT_MEMORIES",
        "LINK_TASK_TO_SPEC", "SHOW_UNLINKED_CHANGES",
        "APPROVE_TASK", "REQUEST_CHANGES", "REJECT_TASK", "SHOW_REVIEW_QUEUE",
        "CLAIM_TASK", "RELEASE_TASK", "UPDATE_TASK", "CANCEL_TASK",
        "RUN_BUILD", "RUN_TESTS", "SHOW_DIFF", "GIT_LOG",
        "DM", "ROOM_HISTORY", "MOVE_TO_ROOM", "SET_PLAN",
        "ADD_TASK_COMMENT", "RECALL_AGENT", "CLOSE_ROOM", "CLEANUP_ROOMS", "MERGE_TASK",
        "RESTART_SERVER", "SHELL", "CREATE_ROOM", "REOPEN_ROOM",
        "CREATE_PR", "POST_PR_REVIEW", "GET_PR_REVIEWS", "MERGE_PR",
        "RECORD_EVIDENCE", "QUERY_EVIDENCE", "CHECK_GATES",
        "COMMIT_CHANGES",
        "START_SPRINT", "ADVANCE_STAGE", "STORE_ARTIFACT", "COMPLETE_SPRINT",
        "SCHEDULE_SPRINT",
        "ADD_TASK_DEPENDENCY", "REMOVE_TASK_DEPENDENCY",
        "LIST_WORKTREES", "CLEANUP_WORKTREES", "LIST_AGENT_STATS",
        "RETURN_TO_MAIN", "INVITE_TO_ROOM", "ROOM_TOPIC",
        "REBASE_TASK", "CREATE_TASK_ITEM", "UPDATE_TASK_ITEM",
        "GENERATE_DIGEST",
        // Tier 2A — Task Workflow
        "TASK_STATUS", "SHOW_TASK_HISTORY", "SHOW_DEPENDENCIES", "REQUEST_REVIEW", "WHOAMI",
        // Tier 2 — Goal Cards, Task Items, List Commands
        "CREATE_GOAL_CARD", "UPDATE_GOAL_CARD_STATUS", "LIST_TASK_ITEMS", "LIST_COMMANDS",
        // Tier 2B — Communication
        "MENTION_TASK_OWNER", "BROADCAST_TO_ROOM",
        // Tier 2C — Task Management
        "MARK_BLOCKED", "SHOW_DECISIONS",
        // Tier 2D — Code & Spec
        "OPEN_SPEC", "SEARCH_SPEC", "OPEN_COMPONENT", "FIND_REFERENCES"
    };

    /// <summary>
    /// Parse agent text for structured commands.
    /// Returns extracted commands and the remaining text with commands stripped.
    /// </summary>
    public CommandParseResult Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new CommandParseResult(new List<ParsedCommand>(), text);

        var commands = new List<ParsedCommand>();
        var remaining = new StringBuilder();
        var lines = text.Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];
            var match = Regex.Match(line, @"^([A-Z][A-Z0-9_]+(?:\s[A-Z]+)*):\s*(.*)$");

            if (match.Success)
            {
                var commandName = match.Groups[1].Value.Replace(" ", "_");
                var firstLineValue = match.Groups[2].Value.Trim();

                // Skip unknown commands (prevents false positives).
                // Legacy blocks like "TASK ASSIGNMENT" flow through this path
                // unchanged — they are not known commands, so they go to remaining.
                if (!KnownCommands.Contains(commandName))
                {
                    remaining.AppendLine(line);
                    i++;
                    continue;
                }

                // Parse args — could be single-line or multi-line
                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                i++;

                // Check for multi-line indented args
                var multiLineValue = new StringBuilder();
                string? currentArgKey = null;
                var currentArgValue = new StringBuilder();

                while (i < lines.Length)
                {
                    var argLine = lines[i];

                    // Next command or empty line = end of this command's args
                    if (string.IsNullOrWhiteSpace(argLine) ||
                        Regex.IsMatch(argLine, @"^[A-Z][A-Z0-9_]+(?:\s[A-Z]+)*:"))
                        break;

                    // Indented arg line
                    var argMatch = Regex.Match(argLine, @"^\s{2,}([A-Za-z_][A-Za-z0-9_]*):\s*(.*)");
                    if (argMatch.Success)
                    {
                        // Save previous arg if any
                        if (currentArgKey != null)
                            args[currentArgKey] = currentArgValue.ToString().Trim();

                        currentArgKey = argMatch.Groups[1].Value;
                        currentArgValue = new StringBuilder(argMatch.Groups[2].Value);
                    }
                    else if (currentArgKey != null && argLine.StartsWith("  "))
                    {
                        // Continuation line for current arg
                        currentArgValue.AppendLine();
                        currentArgValue.Append(argLine.TrimStart());
                    }
                    else
                    {
                        break;
                    }

                    i++;
                }

                // Save last arg
                if (currentArgKey != null)
                    args[currentArgKey] = currentArgValue.ToString().Trim();

                // If no structured args were found but there was a first-line value,
                // treat it as a single positional arg
                if (args.Count == 0 && !string.IsNullOrEmpty(firstLineValue))
                {
                    if (RawValueCommands.Contains(commandName))
                    {
                        // Always treat as raw text — don't split on key=value
                        args["value"] = firstLineValue;
                    }
                    else
                    {
                        // Parse key=value pairs from inline args
                        var inlineArgs = ParseInlineArgs(firstLineValue);
                        if (inlineArgs.Count > 0)
                            foreach (var kv in inlineArgs)
                                args[kv.Key] = kv.Value;
                        else
                            args["value"] = firstLineValue;
                    }
                }

                commands.Add(new ParsedCommand(commandName, args));
            }
            else
            {
                remaining.AppendLine(line);
                i++;
            }
        }

        return new CommandParseResult(commands, remaining.ToString().TrimEnd());
    }

    /// <summary>
    /// Parse inline key=value pairs: "category=gotcha key=some-key"
    /// </summary>
    private static Dictionary<string, string> ParseInlineArgs(string text)
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = Regex.Matches(text, @"(\w+)=(\S+)");
        foreach (Match m in matches)
        {
            args[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return args;
    }
}
