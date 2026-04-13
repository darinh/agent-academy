using System.Text.Json;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Config;

/// <summary>
/// Loads the agent catalog from agents.json and registers it as
/// <see cref="IAgentCatalog"/> in the DI container.
/// </summary>
public static class AgentCatalogLoader
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Reads a specific agents.json file and returns the deserialized catalog.
    /// Used by the watcher when the file path is already known.
    /// </summary>
    public static AgentCatalogOptions LoadFromPath(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var wrapper = JsonSerializer.Deserialize<AgentCatalogWrapper>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize agents.json");

        var catalog = wrapper.AgentCatalog
            ?? throw new InvalidOperationException("agents.json is missing 'AgentCatalog' property");

        var sortedAgents = catalog.Agents
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return catalog with { Agents = sortedAgents };
    }

    /// <summary>
    /// Reads agents.json from the Config directory and returns the deserialized catalog.
    /// </summary>
    public static AgentCatalogOptions Load(string? basePath = null)
    {
        var (options, _) = LoadWithPath(basePath);
        return options;
    }

    /// <summary>
    /// Reads agents.json, returns the deserialized catalog plus the resolved file path.
    /// </summary>
    public static (AgentCatalogOptions Options, string FilePath) LoadWithPath(string? basePath = null)
    {
        basePath ??= AppContext.BaseDirectory;

        // Try several known locations for the config file
        var candidates = new[]
        {
            Path.Combine(basePath, "Config", "agents.json"),
            Path.Combine(basePath, "..", "..", "..", "Config", "agents.json"), // dev-time: bin/Debug/net8.0 → project root
            Path.Combine(Directory.GetCurrentDirectory(), "Config", "agents.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "AgentAcademy.Server", "Config", "agents.json"),
        };

        string? configPath = candidates.FirstOrDefault(File.Exists);

        if (configPath is null)
        {
            throw new FileNotFoundException(
                $"Could not find agents.json. Searched: {string.Join(", ", candidates)}");
        }

        var json = File.ReadAllText(configPath);
        var wrapper = JsonSerializer.Deserialize<AgentCatalogWrapper>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize agents.json");

        var catalog = wrapper.AgentCatalog
            ?? throw new InvalidOperationException("agents.json is missing 'AgentCatalog' property");

        // Sort agents by name (case-insensitive) to match v1 behavior
        var sortedAgents = catalog.Agents
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (catalog with { Agents = sortedAgents }, Path.GetFullPath(configPath));
    }

    /// <summary>
    /// Registers the agent catalog as a singleton in the DI container.
    /// Both <see cref="AgentCatalog"/> (concrete, for the watcher) and
    /// <see cref="IAgentCatalog"/> (interface, for consumers) resolve
    /// to the same instance. Also registers <see cref="AgentCatalogWatcher"/>
    /// as a hosted service for file-change detection.
    /// </summary>
    public static IServiceCollection AddAgentCatalog(
        this IServiceCollection services,
        string? basePath = null)
    {
        var (options, filePath) = LoadWithPath(basePath);
        var catalog = new AgentCatalog(options);
        services.AddSingleton(catalog);
        services.AddSingleton<IAgentCatalog>(sp => sp.GetRequiredService<AgentCatalog>());
        services.AddSingleton(new AgentCatalogFileInfo(filePath));
        services.AddSingleton<AgentCatalogWatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<AgentCatalogWatcher>());
        return services;
    }

    /// <summary>
    /// Wrapper to match the JSON structure: { "AgentCatalog": { ... } }
    /// </summary>
    private record AgentCatalogWrapper(AgentCatalogOptions? AgentCatalog);
}

/// <summary>
/// Holds the resolved path to agents.json for the file watcher.
/// </summary>
public record AgentCatalogFileInfo(string FilePath);
