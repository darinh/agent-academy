using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles VERIFY_SPEC_SECTION — reads a spec section, extracts all file path
/// references, and checks each against the filesystem. Returns a verification
/// report showing which references are valid and which are broken.
/// </summary>
public sealed class VerifySpecSectionHandler : ICommandHandler
{
    public string CommandName => "VERIFY_SPEC_SECTION";
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
                    Error = "Missing required argument: id (spec section number or directory name, e.g. \"007\" or \"007-agent-commands\")"
                };
            }
            id = val;
        }

        id = id.Trim();
        var specManager = context.Services.GetRequiredService<ISpecManager>();

        // Resolve the section
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

        var projectRoot = context.WorkingDirectory ?? FindProjectRoot();
        var extractedPaths = SpecReferenceExtractor.ExtractFilePaths(content);
        var validations = SpecReferenceExtractor.ValidatePaths(extractedPaths, projectRoot);

        var verified = validations.Where(v => v.Exists).ToList();
        var broken = validations.Where(v => !v.Exists).ToList();

        var result = new Dictionary<string, object?>
        {
            ["sectionId"] = match.Id,
            ["heading"] = match.Heading,
            ["totalReferences"] = validations.Count,
            ["verified"] = verified.Count,
            ["broken"] = broken.Count,
            ["status"] = broken.Count == 0 ? "CLEAN" : "DRIFT_DETECTED",
            ["verifiedPaths"] = verified.Select(v => v.Path).ToList(),
            ["brokenPaths"] = broken.Select(v => new Dictionary<string, object?>
            {
                ["path"] = v.Path,
                ["reason"] = v.Reason
            }).ToList()
        };

        if (broken.Count > 0)
        {
            result["hint"] = $"{broken.Count} file reference(s) in spec section \"{match.Id}\" point to " +
                             "files that no longer exist. The spec may need updating.";
        }

        return command with
        {
            Status = CommandStatus.Success,
            Result = result
        };
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
