using AgentAcademy.Server.Data;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles SHOW_MIGRATION_STATUS — lists applied and pending EF Core
/// migrations for the application database.
/// </summary>
public sealed class ShowMigrationStatusHandler : ICommandHandler
{
    public string CommandName => "SHOW_MIGRATION_STATUS";
    public bool IsRetrySafe => true;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
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

        var applied = (await dbContext.Database.GetAppliedMigrationsAsync()).ToList();
        var pending = (await dbContext.Database.GetPendingMigrationsAsync()).ToList();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["appliedCount"] = applied.Count,
                ["pendingCount"] = pending.Count,
                ["applied"] = applied,
                ["pending"] = pending,
                ["isUpToDate"] = pending.Count == 0
            }
        };
    }
}
