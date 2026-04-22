using AgentAcademy.Server.Data;
using AgentAcademy.Server.HealthChecks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Startup;

/// <summary>
/// DI registration for foundational platform infrastructure.
/// Extracted from Program.cs so service wiring churn stays localized.
/// </summary>
public static class ServiceCollectionStartupExtensions
{
    /// <summary>
    /// Registers shared runtime infrastructure (DataProtection, API plumbing,
    /// health checks, SignalR, and CORS policy).
    /// </summary>
    public static IServiceCollection AddCoreInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var dpKeysPath = configuration["DataProtection:KeysPath"]
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentAcademy", "DataProtection-Keys");

        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dpKeysPath));

        services.AddControllers();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();
        services.AddSignalR();

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
            .AddCheck<AgentExecutorHealthCheck>("agent_executor", tags: ["ready"]);

        var configuredOrigins = configuration.GetSection("Cors:Origins").Get<string[]>()?
            .Where(static origin => !string.IsNullOrWhiteSpace(origin))
            .ToArray();
        var corsOrigins = configuredOrigins is { Length: > 0 }
            ? configuredOrigins
            : ["http://localhost:5173"];
        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.WithOrigins(corsOrigins)
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            });
        });

        return services;
    }

    /// <summary>
    /// Registers EF Core persistence for the application database.
    /// </summary>
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<AgentAcademyDbContext>(options =>
            options.UseSqlite(configuration.GetConnectionString("DefaultConnection")
                ?? "Data Source=agent-academy.db"));

        return services;
    }
}
