using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles OPEN_SPEC — reads a spec section by ID, resolving numeric prefixes
/// (e.g., "007") or full directory names (e.g., "007-agent-commands").
/// Delegates to ISpecManager for resolution and path-traversal-safe reads.
/// </summary>
public sealed class OpenSpecHandler : ICommandHandler
{
    public string CommandName => "OPEN_SPEC";
    public bool IsRetrySafe => true;

    private const int MaxContentLength = 12_000;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("id", out var idObj) || idObj is not string id || string.IsNullOrWhiteSpace(id))
        {
            // Also accept positional "value" arg
            if (!command.Args.TryGetValue("value", out idObj) || idObj is not string val || string.IsNullOrWhiteSpace(val))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Validation,
                    Error = "Missing required argument: id (spec section number or directory name, e.g. \"007\" or \"007-agent-commands\")"
                };
            }
            id = val;
        }

        id = id.Trim();
        var specManager = context.Services.GetRequiredService<ISpecManager>();

        // Resolve the section — try exact match first, then numeric prefix
        var sections = await specManager.GetSpecSectionsAsync();

        SpecSection? match = null;
        var exactMatch = sections.FirstOrDefault(s =>
            s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (exactMatch is not null)
        {
            match = exactMatch;
        }
        else
        {
            // Try numeric prefix match (e.g., "007" → "007-agent-commands")
            var prefixMatches = sections
                .Where(s => s.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (prefixMatches.Count == 1)
            {
                match = prefixMatches[0];
            }
            else if (prefixMatches.Count > 1)
            {
                var listing = string.Join(", ", prefixMatches.Select(s => s.Id));
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Validation,
                    Error = $"Ambiguous spec ID \"{id}\" — matches: {listing}. Use the full directory name."
                };
            }
        }

        if (match is null)
        {
            var available = string.Join(", ", sections.Select(s => s.Id));
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Spec section \"{id}\" not found. Available sections: {available}"
            };
        }

        var content = await specManager.GetSpecContentAsync(match.Id);
        if (content is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = $"Failed to read spec section \"{match.Id}\""
            };
        }

        var lines = content.Split('\n');
        var totalLines = lines.Length;

        // Apply optional line range
        int startLine = 1, endLine = totalLines;
        if (command.Args.TryGetValue("startLine", out var startObj))
            int.TryParse(startObj?.ToString(), out startLine);
        if (command.Args.TryGetValue("endLine", out var endObj))
            int.TryParse(endObj?.ToString(), out endLine);

        startLine = Math.Max(1, startLine);
        endLine = Math.Min(totalLines, endLine);

        var selectedLines = lines.Skip(startLine - 1).Take(endLine - startLine + 1).ToArray();
        var result = string.Join('\n', selectedLines);

        // Truncate if content exceeds max size
        var truncated = false;
        var truncatedAtLine = endLine;
        if (result.Length > MaxContentLength)
        {
            truncated = true;
            result = result[..MaxContentLength];
            var lastNewline = result.LastIndexOf('\n');
            if (lastNewline > 0)
                result = result[..lastNewline];
            truncatedAtLine = startLine + result.Split('\n').Length - 1;
        }

        var dict = new Dictionary<string, object?>
        {
            ["sectionId"] = match.Id,
            ["heading"] = match.Heading,
            ["summary"] = match.Summary,
            ["path"] = match.FilePath,
            ["content"] = result,
            ["totalLines"] = totalLines,
            ["startLine"] = startLine,
            ["endLine"] = truncated ? truncatedAtLine : endLine
        };

        if (truncated)
        {
            dict["truncated"] = true;
            dict["hint"] = $"Content truncated at line {truncatedAtLine} of {totalLines}. " +
                           $"Use startLine: {truncatedAtLine + 1} to continue reading.";
        }

        return command with
        {
            Status = CommandStatus.Success,
            Result = dict
        };
    }
}
