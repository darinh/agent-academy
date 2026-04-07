using System.Text;
using System.Text.RegularExpressions;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Commands;

/// <summary>
/// Extracts structured commands from agent text responses.
/// Commands use COMMAND_NAME: syntax. Coexists with legacy
/// TASK ASSIGNMENT/WORK REPORT/REVIEW blocks.
/// </summary>
public sealed class CommandParser
{
    // Matches: COMMAND_NAME: single-line args
    // Or:      COMMAND_NAME:\n  Key: value\n  Key: value
    private static readonly Regex CommandPattern = new(
        @"^([A-Z][A-Z0-9_]+):\s*(.*?)(?=\n[A-Z][A-Z0-9_]+:|\n\n|\z)",
        RegexOptions.Multiline | RegexOptions.Singleline
    );

    // Multi-line arg: Key: value (indented under a command)
    private static readonly Regex ArgLinePattern = new(
        @"^\s{2,}([A-Za-z_][A-Za-z0-9_]*):\s*(.*)",
        RegexOptions.Multiline
    );

    // Legacy blocks that should NOT be parsed as commands
    private static readonly HashSet<string> LegacyBlocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "TASK ASSIGNMENT",
        "WORK REPORT",
        "REVIEW"
    };

    // Commands whose inline value is always raw text, never key=value pairs.
    // Prevents ParseInlineArgs from splitting commit messages like "fix: set timeout=30s".
    private static readonly HashSet<string> RawValueCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "COMMIT_CHANGES"
    };

    // Known command names — prevents false positives from random UPPERCASE: text
    private static readonly HashSet<string> KnownCommands = new(StringComparer.OrdinalIgnoreCase)
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
        "COMMIT_CHANGES"
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

                // Skip legacy blocks
                if (LegacyBlocks.Contains(commandName.Replace("_", " ")))
                {
                    remaining.AppendLine(line);
                    i++;
                    continue;
                }

                // Skip unknown commands (prevents false positives)
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
