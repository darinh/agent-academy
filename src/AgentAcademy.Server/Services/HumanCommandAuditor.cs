using System.Text.Json;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Persistence service for human-initiated command audit records.
/// Creates its own <see cref="IServiceScope"/> per operation so it remains safe
/// to call from <c>Task.Run</c> (which outlives the originating HTTP request scope).
/// </summary>
public sealed class HumanCommandAuditor
{
    private const string HumanAgentId = "human";
    private const string HumanUiSource = "human-ui";

    private readonly IServiceScopeFactory _scopeFactory;

    public HumanCommandAuditor(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Creates a "Pending" audit row for an async command that returns 202 Accepted immediately.
    /// </summary>
    public async Task CreatePendingAsync(string commandName, Dictionary<string, object?> args, string correlationId)
    {
        await WithDbContextAsync(db =>
        {
            db.CommandAudits.Add(new CommandAuditEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                CorrelationId = correlationId,
                AgentId = HumanAgentId,
                Source = HumanUiSource,
                Command = commandName,
                ArgsJson = JsonSerializer.Serialize(args),
                Status = "Pending",
                Timestamp = DateTime.UtcNow
            });
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Creates a completed audit row for a synchronous command that returns inline.
    /// </summary>
    public async Task CreateCompletedAsync(CommandEnvelope envelope)
    {
        await WithDbContextAsync(db =>
        {
            db.CommandAudits.Add(CreateAuditEntity(envelope));
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Creates a "Denied" / confirmation-required audit row for destructive commands.
    /// </summary>
    public async Task CreateConfirmationRequiredAsync(
        string commandName,
        Dictionary<string, object?> args,
        string correlationId)
    {
        await WithDbContextAsync(db =>
        {
            db.CommandAudits.Add(new CommandAuditEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                CorrelationId = correlationId,
                AgentId = HumanAgentId,
                Source = HumanUiSource,
                Command = commandName,
                ArgsJson = JsonSerializer.Serialize(args),
                Status = nameof(CommandStatus.Denied),
                ErrorCode = CommandErrorCode.ConfirmationRequired,
                ErrorMessage = "Confirmation required for destructive command",
                Timestamp = DateTime.UtcNow
            });
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Updates an existing "Pending" audit row with the final result.
    /// Falls back to creating a new row if the original is missing.
    /// </summary>
    public async Task UpdateAsync(CommandEnvelope envelope)
    {
        await WithDbContextAsync(async db =>
        {
            var audit = await db.CommandAudits.FirstOrDefaultAsync(a =>
                a.CorrelationId == envelope.CorrelationId &&
                a.AgentId == HumanAgentId &&
                a.Source == HumanUiSource);

            if (audit is null)
            {
                db.CommandAudits.Add(CreateAuditEntity(envelope));
                return;
            }

            audit.Status = envelope.Status.ToString();
            audit.ResultJson = envelope.Result is null ? null : JsonSerializer.Serialize(envelope.Result);
            audit.ErrorMessage = envelope.Error;
            audit.ErrorCode = envelope.ErrorCode;
            audit.Timestamp = envelope.Timestamp;
        });
    }

    /// <summary>
    /// Retrieves a human-UI audit row by correlation ID for polling status.
    /// </summary>
    public async Task<CommandAuditEntity?> GetByCorrelationIdAsync(string correlationId)
    {
        return await WithDbContextAsync(db => db.CommandAudits
            .AsNoTracking()
            .FirstOrDefaultAsync(a =>
                a.CorrelationId == correlationId &&
                a.AgentId == HumanAgentId &&
                a.Source == HumanUiSource));
    }

    // ── Private helpers ─────────────────────────────────────────

    private static CommandAuditEntity CreateAuditEntity(CommandEnvelope envelope)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            CorrelationId = envelope.CorrelationId,
            AgentId = HumanAgentId,
            Source = HumanUiSource,
            Command = envelope.Command,
            ArgsJson = JsonSerializer.Serialize(envelope.Args),
            Status = envelope.Status.ToString(),
            ResultJson = envelope.Result is null ? null : JsonSerializer.Serialize(envelope.Result),
            ErrorMessage = envelope.Error,
            ErrorCode = envelope.ErrorCode,
            Timestamp = envelope.Timestamp
        };

    private async Task<T> WithDbContextAsync<T>(Func<AgentAcademyDbContext, Task<T>> work)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        return await work(db);
    }

    private async Task WithDbContextAsync(Func<AgentAcademyDbContext, Task> work)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await work(db);
        await db.SaveChangesAsync();
    }
}
