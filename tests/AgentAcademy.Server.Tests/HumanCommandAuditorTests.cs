using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for <see cref="HumanCommandAuditor"/> — the persistence service
/// for human-initiated command audit records. Verifies CRUD operations
/// against an in-memory SQLite database with real DI scoping.
/// </summary>
public sealed class HumanCommandAuditorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly HumanCommandAuditor _auditor;

    public HumanCommandAuditorTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        // EnsureCreated on the first scope so the schema exists for all operations.
        using var initScope = _serviceProvider.CreateScope();
        initScope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>()
            .Database.EnsureCreated();

        _auditor = new HumanCommandAuditor(_serviceProvider.GetRequiredService<IServiceScopeFactory>());
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    // ── CreatePendingAsync ──────────────────────────────────────

    [Fact]
    public async Task CreatePendingAsync_PersistsAuditRow()
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var args = new Dictionary<string, object?> { ["taskId"] = "t-1" };

        await _auditor.CreatePendingAsync("RUN_BUILD", args, correlationId);

        var audit = await GetAuditByCorrelationIdAsync(correlationId);
        Assert.NotNull(audit);
        Assert.Equal("RUN_BUILD", audit.Command);
        Assert.Equal("Pending", audit.Status);
        Assert.Equal("human", audit.AgentId);
        Assert.Equal("human-ui", audit.Source);
        Assert.Contains("t-1", audit.ArgsJson);
    }

    [Fact]
    public async Task CreatePendingAsync_SetsTimestampCloseToNow()
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var before = DateTime.UtcNow.AddSeconds(-2);

        await _auditor.CreatePendingAsync("LIST_TASKS", new Dictionary<string, object?>(), correlationId);

        var audit = await GetAuditByCorrelationIdAsync(correlationId);
        Assert.NotNull(audit);
        Assert.InRange(audit.Timestamp, before, DateTime.UtcNow.AddSeconds(2));
    }

    // ── CreateCompletedAsync ────────────────────────────────────

    [Fact]
    public async Task CreateCompletedAsync_PersistsSuccessEnvelope()
    {
        var envelope = MakeEnvelope(CommandStatus.Success, result: new() { ["output"] = "done" });

        await _auditor.CreateCompletedAsync(envelope);

        var audit = await GetAuditByCorrelationIdAsync(envelope.CorrelationId);
        Assert.NotNull(audit);
        Assert.Equal(envelope.Command, audit.Command);
        Assert.Equal("Success", audit.Status);
        Assert.Contains("done", audit.ResultJson!);
        Assert.Null(audit.ErrorMessage);
    }

    [Fact]
    public async Task CreateCompletedAsync_PersistsErrorEnvelope()
    {
        var envelope = MakeEnvelope(
            CommandStatus.Error,
            error: "Something broke",
            errorCode: CommandErrorCode.Internal);

        await _auditor.CreateCompletedAsync(envelope);

        var audit = await GetAuditByCorrelationIdAsync(envelope.CorrelationId);
        Assert.NotNull(audit);
        Assert.Equal("Error", audit.Status);
        Assert.Equal("Something broke", audit.ErrorMessage);
        Assert.Equal(CommandErrorCode.Internal, audit.ErrorCode);
    }

    [Fact]
    public async Task CreateCompletedAsync_NullResultStoresNullJson()
    {
        var envelope = MakeEnvelope(CommandStatus.Success, result: null);

        await _auditor.CreateCompletedAsync(envelope);

        var audit = await GetAuditByCorrelationIdAsync(envelope.CorrelationId);
        Assert.NotNull(audit);
        Assert.Null(audit.ResultJson);
    }

    // ── CreateConfirmationRequiredAsync ─────────────────────────

    [Fact]
    public async Task CreateConfirmationRequiredAsync_CreatesDeniedRow()
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var args = new Dictionary<string, object?> { ["target"] = "main" };

        await _auditor.CreateConfirmationRequiredAsync("DESTROY_DATA", args, correlationId);

        var audit = await GetAuditByCorrelationIdAsync(correlationId);
        Assert.NotNull(audit);
        Assert.Equal("DESTROY_DATA", audit.Command);
        Assert.Equal("Denied", audit.Status);
        Assert.Equal(CommandErrorCode.ConfirmationRequired, audit.ErrorCode);
        Assert.Contains("Confirmation required", audit.ErrorMessage!);
    }

    // ── UpdateAsync ────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_UpdatesExistingPendingRow()
    {
        var correlationId = Guid.NewGuid().ToString("N");
        await _auditor.CreatePendingAsync("RUN_TESTS", new Dictionary<string, object?>(), correlationId);

        var envelope = MakeEnvelope(
            CommandStatus.Success,
            correlationId: correlationId,
            result: new() { ["passed"] = 42 });

        await _auditor.UpdateAsync(envelope);

        var audit = await GetAuditByCorrelationIdAsync(correlationId);
        Assert.NotNull(audit);
        Assert.Equal("Success", audit.Status);
        Assert.Contains("42", audit.ResultJson!);
    }

    [Fact]
    public async Task UpdateAsync_SetsErrorFieldsOnFailure()
    {
        var correlationId = Guid.NewGuid().ToString("N");
        await _auditor.CreatePendingAsync("RUN_BUILD", new Dictionary<string, object?>(), correlationId);

        var envelope = MakeEnvelope(
            CommandStatus.Error,
            correlationId: correlationId,
            error: "Build failed",
            errorCode: CommandErrorCode.Internal);

        await _auditor.UpdateAsync(envelope);

        var audit = await GetAuditByCorrelationIdAsync(correlationId);
        Assert.NotNull(audit);
        Assert.Equal("Error", audit.Status);
        Assert.Equal("Build failed", audit.ErrorMessage);
        Assert.Equal(CommandErrorCode.Internal, audit.ErrorCode);
    }

    [Fact]
    public async Task UpdateAsync_FallsBackToInsertWhenCorrelationIdMissing()
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var envelope = MakeEnvelope(CommandStatus.Success, correlationId: correlationId);

        await _auditor.UpdateAsync(envelope);

        var audit = await GetAuditByCorrelationIdAsync(correlationId);
        Assert.NotNull(audit);
        Assert.Equal("Success", audit.Status);
        Assert.Equal("human", audit.AgentId);
    }

    [Fact]
    public async Task UpdateAsync_DoesNotAffectOtherCorrelationIds()
    {
        var id1 = Guid.NewGuid().ToString("N");
        var id2 = Guid.NewGuid().ToString("N");
        await _auditor.CreatePendingAsync("CMD_A", new Dictionary<string, object?>(), id1);
        await _auditor.CreatePendingAsync("CMD_B", new Dictionary<string, object?>(), id2);

        var envelope = MakeEnvelope(CommandStatus.Success, correlationId: id1);
        await _auditor.UpdateAsync(envelope);

        var audit2 = await GetAuditByCorrelationIdAsync(id2);
        Assert.NotNull(audit2);
        Assert.Equal("Pending", audit2.Status);
    }

    // ── GetByCorrelationIdAsync ────────────────────────────────

    [Fact]
    public async Task GetByCorrelationIdAsync_ReturnsMatchingRow()
    {
        var correlationId = Guid.NewGuid().ToString("N");
        await _auditor.CreatePendingAsync("LIST_TASKS", new Dictionary<string, object?>(), correlationId);

        var result = await _auditor.GetByCorrelationIdAsync(correlationId);

        Assert.NotNull(result);
        Assert.Equal(correlationId, result.CorrelationId);
    }

    [Fact]
    public async Task GetByCorrelationIdAsync_ReturnsNullForNonExistentId()
    {
        var result = await _auditor.GetByCorrelationIdAsync("does-not-exist");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByCorrelationIdAsync_IgnoresNonHumanRows()
    {
        var correlationId = Guid.NewGuid().ToString("N");

        // Seed a non-human audit row directly
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.CommandAudits.Add(new CommandAuditEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            CorrelationId = correlationId,
            AgentId = "agent-1",
            Source = "agent",
            Command = "RUN_BUILD",
            ArgsJson = "{}",
            Status = "Success",
            Timestamp = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var result = await _auditor.GetByCorrelationIdAsync(correlationId);

        Assert.Null(result);
    }

    // ── Scope isolation ────────────────────────────────────────

    [Fact]
    public async Task MultipleOperations_EachGetIndependentScope()
    {
        // Verify that successive calls don't share EF change tracker state.
        var id1 = Guid.NewGuid().ToString("N");
        var id2 = Guid.NewGuid().ToString("N");

        await _auditor.CreatePendingAsync("CMD_1", new Dictionary<string, object?>(), id1);
        await _auditor.CreatePendingAsync("CMD_2", new Dictionary<string, object?>(), id2);

        var a1 = await _auditor.GetByCorrelationIdAsync(id1);
        var a2 = await _auditor.GetByCorrelationIdAsync(id2);
        Assert.NotNull(a1);
        Assert.NotNull(a2);
        Assert.NotEqual(a1.Id, a2.Id);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static CommandEnvelope MakeEnvelope(
        CommandStatus status,
        string? correlationId = null,
        string command = "TEST_CMD",
        Dictionary<string, object?>? result = null,
        string? error = null,
        string? errorCode = null)
    {
        return new CommandEnvelope(
            Command: command,
            Args: new Dictionary<string, object?> { ["key"] = "value" },
            Status: status,
            Result: result,
            Error: error,
            CorrelationId: correlationId ?? Guid.NewGuid().ToString("N"),
            Timestamp: DateTime.UtcNow,
            ExecutedBy: "human")
        {
            ErrorCode = errorCode
        };
    }

    private async Task<CommandAuditEntity?> GetAuditByCorrelationIdAsync(string correlationId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        return await db.CommandAudits
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.CorrelationId == correlationId);
    }
}
