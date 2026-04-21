using System.Net.Http.Headers;
using AgentAcademy.Forge;
using AgentAcademy.Forge.Llm;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Services;

namespace AgentAcademy.Server.Startup;

/// <summary>
/// DI registration for the Forge Pipeline Engine integration.
/// </summary>
public static class ForgeServiceRegistration
{
    /// <summary>
    /// Register the Forge engine and its dependencies.
    /// When <see cref="ForgeOptions.Enabled"/> is false, no services are registered.
    /// When <see cref="ForgeOptions.ExecutionAvailable"/> is false, the engine is
    /// registered for read-only access (list runs, get artifacts) but execution
    /// endpoints will return 503.
    /// </summary>
    public static IServiceCollection AddForge(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var options = configuration
            .GetSection(ForgeOptions.SectionName)
            .Get<ForgeOptions>() ?? new ForgeOptions();

        if (!options.Enabled)
        {
            services.AddSingleton(options);
            return services;
        }

        // Resolve runs directory relative to content root
        var runsDir = Path.IsPathRooted(options.RunsDirectory)
            ? options.RunsDirectory
            : Path.Combine(environment.ContentRootPath, options.RunsDirectory);

        Directory.CreateDirectory(runsDir);

        services.AddSingleton(options);

        // TimeProvider is required by PipelineRunner
        services.AddSingleton(TimeProvider.System);

        // Register the Forge engine core (stores, schemas, executors)
        services.AddForgeEngine(runsDir);

        // Register LLM client
        if (options.ExecutionAvailable)
        {
            services.AddSingleton<ILlmClient>(_ =>
            {
                var http = new HttpClient();
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", options.OpenAiApiKey);

                return new OpenAiLlmClient(options.OpenAiBaseUrl, http);
            });
        }
        else
        {
            // No LLM client — execution endpoints return 503
            services.AddSingleton<ILlmClient, UnavailableLlmClient>();
        }

        // Background service for processing forge runs
        services.AddSingleton<ForgeRunService>();
        services.AddHostedService(sp => sp.GetRequiredService<ForgeRunService>());

        return services;
    }
}

/// <summary>
/// LLM client that always throws — used when no API key is configured.
/// Prevents accidental execution; read-only endpoints still work.
/// </summary>
internal sealed class UnavailableLlmClient : ILlmClient
{
    public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default)
        => throw new InvalidOperationException(
            "Forge LLM execution is unavailable — no OpenAI API key configured. " +
            "Set Forge:OpenAiApiKey in appsettings.json or user-secrets.");
}
