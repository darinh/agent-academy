using System.Net.Http.Headers;
using System.Text.Json;
using AgentAcademy.Forge;
using AgentAcademy.Forge.Llm;
using AgentAcademy.Forge.Models;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;

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

        // Resolve methodologies directory relative to content root
        var methodologiesDir = Path.IsPathRooted(options.MethodologiesDirectory)
            ? options.MethodologiesDirectory
            : Path.Combine(environment.ContentRootPath, options.MethodologiesDirectory);

        Directory.CreateDirectory(methodologiesDir);

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
        services.AddSingleton<ForgeRunService>(sp => new ForgeRunService(
            sp.GetRequiredService<AgentAcademy.Forge.Execution.PipelineRunner>(),
            sp.GetRequiredService<ForgeOptions>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<ForgeRunService>>()));
        services.AddSingleton<IForgeJobService>(sp => sp.GetRequiredService<ForgeRunService>());
        services.AddHostedService(sp => sp.GetRequiredService<ForgeRunService>());

        // Methodology catalog
        services.AddSingleton<IMethodologyCatalog>(sp =>
            new DiskMethodologyCatalog(
                methodologiesDir,
                sp.GetRequiredService<ILogger<DiskMethodologyCatalog>>()));

        return services;
    }

    /// <summary>
    /// Seed the default methodology into the catalog if it doesn't already exist.
    /// Called during app initialization.
    /// </summary>
    public static async Task SeedDefaultMethodologyAsync(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<ForgeOptions>();
        if (!options.Enabled)
            return;

        var catalog = app.Services.GetRequiredService<IMethodologyCatalog>();
        if (catalog is not DiskMethodologyCatalog diskCatalog)
            return;

        var defaultMethodology = LoadDefaultMethodology();
        if (defaultMethodology is not null)
        {
            await diskCatalog.SeedAsync(defaultMethodology);
        }
    }

    /// <summary>
    /// Recover forge jobs from a previous server lifecycle.
    /// Called during app initialization, after DB migration.
    /// </summary>
    public static async Task RecoverForgeJobsAsync(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<ForgeOptions>();
        if (!options.Enabled)
            return;

        try
        {
            var runService = app.Services.GetRequiredService<ForgeRunService>();
            await runService.RecoverJobsAsync();
        }
        catch (Exception ex)
        {
            var logger = app.Services.GetRequiredService<ILogger<ForgeRunService>>();
            logger.LogWarning(ex, "Forge job recovery failed — continuing startup without recovery");
        }
    }

    private static MethodologyDefinition? LoadDefaultMethodology()
    {
        try
        {
            return new MethodologyDefinition
            {
                Id = "spike-default-v1",
                Description = "Five-phase software engineering pipeline",
                MaxAttemptsDefault = 3,
                ModelDefaults = new ModelDefaults
                {
                    Generation = "gpt-4o",
                    Judge = "gpt-4o-mini"
                },
                Phases =
                [
                    new PhaseDefinition
                    {
                        Id = "requirements",
                        Goal = "Decompose the task brief into testable functional and non-functional requirements.",
                        Inputs = [],
                        OutputSchema = "requirements/v1",
                        Instructions = "Read the task brief carefully. Produce a complete requirements artifact. Make assumptions explicit in open_questions[].assumed_answer rather than refusing."
                    },
                    new PhaseDefinition
                    {
                        Id = "contract",
                        Goal = "Define the external interface the implementation must satisfy.",
                        Inputs = ["requirements"],
                        OutputSchema = "contract/v1",
                        Instructions = "Treat the requirements as ground truth. Every must-priority FR must be satisfied by at least one interface. Provide concrete signatures."
                    },
                    new PhaseDefinition
                    {
                        Id = "function_design",
                        Goal = "Decompose the contract into internal components, responsibilities, and data flow.",
                        Inputs = ["requirements", "contract"],
                        OutputSchema = "function_design/v1",
                        Instructions = "Produce a DAG of components. Each component must have a single, narrow responsibility. Do not write code — only structural design."
                    },
                    new PhaseDefinition
                    {
                        Id = "implementation",
                        Goal = "Produce the file contents that realize the function design.",
                        Inputs = ["contract", "function_design"],
                        OutputSchema = "implementation/v1",
                        Instructions = "Output complete, runnable file contents — no placeholders, no TODOs. Use the contract signatures verbatim."
                    },
                    new PhaseDefinition
                    {
                        Id = "review",
                        Goal = "Adversarially review the implementation against requirements, contract, and design.",
                        Inputs = ["requirements", "contract", "function_design", "implementation"],
                        OutputSchema = "review/v1",
                        Instructions = "Adopt an adversarial stance: assume the implementation is wrong until proven otherwise. Surface every defect."
                    }
                ]
            };
        }
        catch
        {
            return null;
        }
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
