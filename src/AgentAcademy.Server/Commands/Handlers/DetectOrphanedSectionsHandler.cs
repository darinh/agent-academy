using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles DETECT_ORPHANED_SECTIONS — scans all spec sections, extracts file
/// path references, and reports sections with broken references. Helps agents
/// identify spec drift across the entire specification corpus.
/// </summary>
public sealed class DetectOrphanedSectionsHandler : ICommandHandler
{
    public string CommandName => "DETECT_ORPHANED_SECTIONS";
    public bool IsRetrySafe => true;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var specManager = context.Services.GetRequiredService<ISpecManager>();
        var sections = await specManager.GetSpecSectionsAsync();

        if (sections.Count == 0)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = "No spec sections found."
            };
        }

        var projectRoot = context.WorkingDirectory ?? FindProjectRoot();

        // Optional: filter to specific section by "id" arg
        string? filterSectionId = null;
        if (command.Args.TryGetValue("id", out var idObj) && idObj is string id && !string.IsNullOrWhiteSpace(id))
        {
            filterSectionId = id.Trim();
        }
        else if (command.Args.TryGetValue("value", out var valObj) && valObj is string val && !string.IsNullOrWhiteSpace(val))
        {
            filterSectionId = val.Trim();
        }

        var sectionReports = new List<Dictionary<string, object?>>();
        var totalOrphaned = 0;
        var totalChecked = 0;
        var matchedSections = 0;

        foreach (var section in sections)
        {
            if (filterSectionId is not null &&
                !section.Id.StartsWith(filterSectionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matchedSections++;

            var content = await specManager.GetSpecContentAsync(section.Id);
            if (content is null) continue;

            var extractedPaths = SpecReferenceExtractor.ExtractFilePaths(content);
            if (extractedPaths.Count == 0) continue;

            var validations = SpecReferenceExtractor.ValidatePaths(extractedPaths, projectRoot);
            var broken = validations.Where(v => !v.Exists).ToList();

            totalChecked += validations.Count;
            totalOrphaned += broken.Count;

            if (broken.Count > 0)
            {
                sectionReports.Add(new Dictionary<string, object?>
                {
                    ["sectionId"] = section.Id,
                    ["heading"] = section.Heading,
                    ["totalReferences"] = validations.Count,
                    ["orphanedCount"] = broken.Count,
                    ["orphanedPaths"] = broken.Select(b => b.Path).ToList()
                });
            }
        }

        // If a filter was provided but matched nothing, report not-found
        if (filterSectionId is not null && matchedSections == 0)
        {
            var available = string.Join(", ", sections.Select(s => s.Id));
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"No spec sections matched filter \"{filterSectionId}\". Available: {available}"
            };
        }

        var result = new Dictionary<string, object?>
        {
            ["sectionsScanned"] = filterSectionId is not null ? matchedSections : sections.Count,
            ["totalReferencesChecked"] = totalChecked,
            ["totalOrphaned"] = totalOrphaned,
            ["status"] = totalOrphaned == 0 ? "CLEAN" : "ORPHANS_DETECTED",
            ["sectionsWithOrphans"] = sectionReports
        };

        if (totalOrphaned > 0)
        {
            result["summary"] = $"{totalOrphaned} orphaned file reference(s) found across " +
                                $"{sectionReports.Count} section(s). These spec sections reference " +
                                "files that no longer exist in the codebase.";
        }
        else
        {
            result["summary"] = "All file references across all spec sections are valid.";
        }

        return command with
        {
            Status = CommandStatus.Success,
            Result = result
        };
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
