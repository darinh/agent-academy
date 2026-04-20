using AgentAcademy.Forge.Artifacts;
using AgentAcademy.Forge.Costs;
using AgentAcademy.Forge.Execution;
using AgentAcademy.Forge.Prompt;
using AgentAcademy.Forge.Schemas;
using AgentAcademy.Forge.Storage;
using AgentAcademy.Forge.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Forge;

/// <summary>
/// DI registration for the Forge pipeline engine.
/// </summary>
public static class ForgeServiceExtensions
{
    /// <summary>
    /// Register Forge engine services. The engine uses the file system for all state.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="forgeRunsRoot">Root directory for forge runs (default: ./forge-runs).</param>
    public static IServiceCollection AddForgeEngine(this IServiceCollection services, string? forgeRunsRoot = null)
    {
        var root = forgeRunsRoot ?? Path.Combine(Directory.GetCurrentDirectory(), "forge-runs");

        services.AddSingleton<IArtifactStore>(sp =>
            new DiskArtifactStore(
                Path.Combine(root, "artifacts"),
                sp.GetRequiredService<ILogger<DiskArtifactStore>>()));

        services.AddSingleton<IRunStore>(sp =>
            new DiskRunStore(
                root,
                sp.GetRequiredService<ILogger<DiskRunStore>>()));

        services.AddSingleton<SchemaRegistry>();
        services.AddSingleton<PromptBuilder>();
        services.AddSingleton<StructuralValidator>();
        services.AddSingleton<SemanticValidator>();
        services.AddSingleton<CrossArtifactValidator>();
        services.AddSingleton<ValidatorPipeline>();
        services.AddSingleton<CostCalculator>();
        services.AddSingleton<PhaseExecutor>();
        services.AddSingleton<PipelineRunner>();

        return services;
    }
}
