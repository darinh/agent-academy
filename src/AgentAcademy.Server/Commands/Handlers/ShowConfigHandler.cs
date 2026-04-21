using AgentAcademy.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles SHOW_CONFIG — displays current configuration for an allowlisted
/// set of safe sections. Sensitive values are masked.
/// </summary>
public sealed class ShowConfigHandler : ICommandHandler
{
    public string CommandName => "SHOW_CONFIG";
    public bool IsRetrySafe => true;

    private static readonly HashSet<string> AllowedSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Logging",
        "Cors",
        "AllowedHosts",
        "Copilot",
    };

    private static readonly string[] SensitivePatterns =
    [
        "secret", "password", "key", "token", "credential",
        "connectionstring", "certificate", "passphrase", "signing"
    ];

    public Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var config = context.Services.GetService<IConfiguration>();
        if (config is null)
        {
            return Task.FromResult(command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = "Configuration service is not available."
            });
        }

        // Parse optional section filter
        string? requestedSection = null;
        if (command.Args.TryGetValue("section", out var sectionObj) && sectionObj is string s && !string.IsNullOrWhiteSpace(s))
            requestedSection = s;

        if (requestedSection is not null && !AllowedSections.Contains(requestedSection))
        {
            return Task.FromResult(command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = $"Section '{requestedSection}' is not in the allowed list. Allowed: {string.Join(", ", AllowedSections)}"
            });
        }

        var sections = requestedSection is not null
            ? [requestedSection]
            : AllowedSections.ToArray();

        var result = new Dictionary<string, object?>();
        foreach (var section in sections)
        {
            var configSection = config.GetSection(section);
            if (!configSection.Exists()) continue;

            var values = new Dictionary<string, string>();

            // Handle scalar sections (e.g., AllowedHosts = "*")
            if (configSection.Value is not null)
            {
                var key = configSection.Key;
                values[key] = IsSensitiveKey(key) ? "***" : configSection.Value;
            }
            else
            {
                foreach (var child in configSection.GetChildren())
                    FlattenSection(child, "", values);
            }

            result[section] = values;
        }

        return Task.FromResult(command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["sections"] = result,
                ["allowedSections"] = AllowedSections.ToList()
            }
        });
    }

    private static void FlattenSection(IConfigurationSection section, string prefix, Dictionary<string, string> values)
    {
        var children = section.GetChildren().ToList();
        var fullKey = string.IsNullOrEmpty(prefix) ? section.Key : $"{prefix}:{section.Key}";

        if (children.Count == 0 && section.Value is not null)
        {
            values[fullKey] = IsSensitiveKey(fullKey) ? "***" : section.Value;
            return;
        }

        foreach (var child in children)
            FlattenSection(child, fullKey, values);
    }

    private static bool IsSensitiveKey(string key) =>
        SensitivePatterns.Any(p => key.Contains(p, StringComparison.OrdinalIgnoreCase));
}
