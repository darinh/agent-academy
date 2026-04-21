using System.Text.RegularExpressions;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles COMPARE_SPEC_TO_CODE — reads a spec section, extracts all verifiable
/// claims (file paths, handler names, command names), and cross-references them
/// against the actual codebase. Returns a detailed comparison report.
/// </summary>
public sealed class CompareSpecToCodeHandler : ICommandHandler
{
    public string CommandName => "COMPARE_SPEC_TO_CODE";
    public bool IsRetrySafe => true;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("id", out var idObj) || idObj is not string id || string.IsNullOrWhiteSpace(id))
        {
            if (!command.Args.TryGetValue("value", out idObj) || idObj is not string val || string.IsNullOrWhiteSpace(val))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Validation,
                    Error = "Missing required argument: id (spec section number or directory name)"
                };
            }
            id = val;
        }

        id = id.Trim();
        var specManager = context.Services.GetRequiredService<ISpecManager>();
        var sections = await specManager.GetSpecSectionsAsync();

        var match = ResolveSection(sections, id);
        if (match is null)
        {
            var available = string.Join(", ", sections.Select(s => s.Id));
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Spec section \"{id}\" not found. Available: {available}"
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

        var projectRoot = FindProjectRoot();
        var claims = new List<Dictionary<string, object?>>();

        // 1. File path claims
        var extractedPaths = SpecReferenceExtractor.ExtractFilePaths(content);
        var pathValidations = SpecReferenceExtractor.ValidatePaths(extractedPaths, projectRoot);
        foreach (var v in pathValidations)
        {
            claims.Add(new Dictionary<string, object?>
            {
                ["type"] = "file_path",
                ["claim"] = v.Path,
                ["verified"] = v.Exists,
                ["detail"] = v.Exists ? "File exists" : "File not found"
            });
        }

        // 2. Handler class claims (e.g., RememberHandler.cs)
        var handlers = SpecReferenceExtractor.ExtractHandlerNames(content);
        foreach (var handler in handlers)
        {
            var handlerFile = FindHandlerFile(projectRoot, handler);
            claims.Add(new Dictionary<string, object?>
            {
                ["type"] = "handler_class",
                ["claim"] = handler,
                ["verified"] = handlerFile is not null,
                ["detail"] = handlerFile is not null
                    ? $"Found at {handlerFile}"
                    : "Handler class not found in project"
            });
        }

        // 3. Command name claims (SCREAMING_SNAKE in backticks)
        var commandNames = ExtractCommandNames(content);
        var knownCommands = CommandParser.KnownCommands;
        foreach (var cmd in commandNames)
        {
            var isKnown = knownCommands.Contains(cmd);
            claims.Add(new Dictionary<string, object?>
            {
                ["type"] = "command_name",
                ["claim"] = cmd,
                ["verified"] = isKnown,
                ["detail"] = isKnown
                    ? "Registered in KnownCommands"
                    : "Not found in KnownCommands"
            });
        }

        // 4. Spec status claim
        var statusMatch = Regex.Match(content, @">\s*\*\*Status:\s*(\w+)\*\*");
        string? declaredStatus = statusMatch.Success ? statusMatch.Groups[1].Value : null;

        var verifiedCount = claims.Count(c => c["verified"] is true);
        var brokenCount = claims.Count(c => c["verified"] is false);

        var result = new Dictionary<string, object?>
        {
            ["sectionId"] = match.Id,
            ["heading"] = match.Heading,
            ["declaredStatus"] = declaredStatus,
            ["totalClaims"] = claims.Count,
            ["verified"] = verifiedCount,
            ["broken"] = brokenCount,
            ["accuracy"] = claims.Count > 0
                ? Math.Round((double)verifiedCount / claims.Count * 100, 1)
                : 100.0,
            ["claims"] = claims
        };

        if (brokenCount > 0)
        {
            result["summary"] = $"{brokenCount} of {claims.Count} claims could not be verified. " +
                                "The spec may have drifted from the codebase.";
        }
        else
        {
            result["summary"] = $"All {claims.Count} claims verified successfully.";
        }

        return command with
        {
            Status = CommandStatus.Success,
            Result = result
        };
    }

    /// <summary>
    /// Extracts SCREAMING_SNAKE command names from backticks in spec content.
    /// Includes multi-word commands (with underscores) and single-word tokens
    /// that are actually registered in KnownCommands (e.g., DM, SHELL, WHOAMI).
    /// </summary>
    private static List<string> ExtractCommandNames(string content)
    {
        var regex = new Regex(@"`([A-Z][A-Z0-9_]{2,})`", RegexOptions.Compiled);
        var candidates = regex.Matches(content)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var knownCommands = CommandParser.KnownCommands;

        // Include multi-word commands (contain underscore) — high confidence these are commands.
        // Include single-word tokens only if they're actually in KnownCommands.
        // This avoids false positives from common acronyms like JSON, HTTP, API.
        return candidates
            .Where(c => c.Contains('_')
                ? !c.StartsWith("ASPNET") && !c.StartsWith("HTTP_")
                : knownCommands.Contains(c))
            .ToList();
    }

    private static string? FindHandlerFile(string projectRoot, string handlerName)
    {
        var handlersDir = Path.Combine(projectRoot, "src", "AgentAcademy.Server", "Commands", "Handlers");
        if (!Directory.Exists(handlersDir)) return null;

        var fileName = $"{handlerName}.cs";
        var fullPath = Path.Combine(handlersDir, fileName);

        if (File.Exists(fullPath))
            return $"src/AgentAcademy.Server/Commands/Handlers/{fileName}";

        // Search recursively in case handlers are in subdirectories
        var found = Directory.GetFiles(handlersDir, fileName, SearchOption.AllDirectories)
            .FirstOrDefault();

        return found is not null
            ? Path.GetRelativePath(projectRoot, found)
            : null;
    }

    private static SpecSection? ResolveSection(List<SpecSection> sections, string id)
    {
        var exact = sections.FirstOrDefault(s =>
            s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var prefixMatches = sections
            .Where(s => s.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return prefixMatches.Count == 1 ? prefixMatches[0] : null;
    }

    private static string FindProjectRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "AgentAcademy.sln")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
