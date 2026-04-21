using AgentAcademy.Server.Data;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles RUN_MIGRATIONS — applies all pending EF Core migrations.
/// Destructive (schema change). Restricted to Human role.
/// Uses a process-wide lock to prevent concurrent migrations.
/// </summary>
public sealed class RunMigrationsHandler : ICommandHandler
{
    public string CommandName => "RUN_MIGRATIONS";
    public bool IsDestructive => true;
    public string DestructiveWarning =>
        "RUN_MIGRATIONS applies pending database schema changes. " +
        "This modifies the database structure and cannot be automatically reversed.";

    private static readonly SemaphoreSlim MigrationLock = new(1, 1);

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        // In-handler role gate — Human only
        if (!string.Equals(context.AgentRole, "Human", StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Permission,
                Error = $"RUN_MIGRATIONS is restricted to Human role. Your role: {context.AgentRole}"
            };
        }

        using var scope = context.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetService<AgentAcademyDbContext>();
        if (dbContext is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = "Database context is not available."
            };
        }

        // Acquire process-wide lock to prevent concurrent migrations
        if (!await MigrationLock.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = "Another migration is already in progress. Please wait and try again."
            };
        }

        try
        {
            // Check for pending migrations inside the lock to avoid TOCTOU race
            var pending = (await dbContext.Database.GetPendingMigrationsAsync()).ToList();
            if (pending.Count == 0)
            {
                return command with
                {
                    Status = CommandStatus.Success,
                    Result = new Dictionary<string, object?>
                    {
                        ["message"] = "No pending migrations.",
                        ["applied"] = new List<string>()
                    }
                };
            }

            await dbContext.Database.MigrateAsync();

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["message"] = $"Applied {pending.Count} migration(s) successfully.",
                    ["applied"] = pending
                }
            };
        }
        catch (Exception ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = $"Migration failed: {ex.Message}"
            };
        }
        finally
        {
            MigrationLock.Release();
        }
    }
}
