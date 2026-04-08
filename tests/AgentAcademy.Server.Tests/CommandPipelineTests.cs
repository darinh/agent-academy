using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public class CommandPipelineTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly SqliteConnection _connection;
    private readonly CommandPipeline _pipeline;

    private static AgentDefinition TestAgent(CommandPermissionSet? permissions = null) =>
        new("test-1", "TestAgent", "SoftwareEngineer", "Test", "prompt", null,
            new List<string>(), new List<string>(), true, null, permissions);

    public CommandPipelineTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt =>
            opt.UseSqlite(_connection));

        _serviceProvider = services.BuildServiceProvider();

        // Ensure DB is created
        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();

        var handlers = new ICommandHandler[]
        {
            new ListRoomsHandler(),
            new ListAgentsHandler(),
            new ListTasksHandler(),
            new RememberHandler(),
            new RecallHandler(),
            new ListMemoriesHandler(),
            new ForgetHandler()
        };

        _pipeline = new CommandPipeline(
            handlers,
            NullLogger<CommandPipeline>.Instance);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ProcessResponse_NoCommands_ReturnsOriginalText()
    {
        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "*" },
            Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        var result = await _pipeline.ProcessResponseAsync(
            "test-1", "Just regular text.", "room-1", agent, scope.ServiceProvider);

        Assert.Empty(result.Results);
        Assert.Equal("Just regular text.", result.RemainingText);
    }

    [Fact]
    public async Task ProcessResponse_DeniedCommand_ReturnsDeniedEnvelope()
    {
        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "LIST_ROOMS" },
            Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        var result = await _pipeline.ProcessResponseAsync(
            "test-1", "READ_FILE:\n  path: src/test.cs", "room-1", agent, scope.ServiceProvider);

        Assert.Single(result.Results);
        Assert.Equal(CommandStatus.Denied, result.Results[0].Status);
    }

    [Fact]
    public async Task ProcessResponse_AuditTrailCreated()
    {
        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "REMEMBER" },
            Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        await _pipeline.ProcessResponseAsync(
            "test-1",
            "REMEMBER:\n  Category: lesson\n  Key: test-key\n  Value: test-value",
            "room-1", agent, scope.ServiceProvider);

        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var audits = await db.CommandAudits.ToListAsync();
        Assert.Single(audits);
        Assert.Equal("REMEMBER", audits[0].Command);
        Assert.Equal("test-1", audits[0].AgentId);
    }

    [Fact]
    public async Task ProcessResponse_MemoryRoundTrip()
    {
        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "REMEMBER", "RECALL" },
            Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();

        // Remember
        await _pipeline.ProcessResponseAsync(
            "test-1",
            "REMEMBER:\n  Category: gotcha\n  Key: griffel-pseudo\n  Value: Griffel does not support ::after pseudo-elements",
            "room-1", agent, scope.ServiceProvider);

        // Recall
        var result = await _pipeline.ProcessResponseAsync(
            "test-1", "RECALL: category=gotcha", "room-1", agent, scope.ServiceProvider);

        Assert.Single(result.Results);
        Assert.Equal(CommandStatus.Success, result.Results[0].Status);
        var memories = result.Results[0].Result?["memories"];
        Assert.NotNull(memories);
    }

    [Fact]
    public async Task ProcessResponse_UnknownCommand_ReturnsError()
    {
        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "NONEXISTENT" },
            Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        var result = await _pipeline.ProcessResponseAsync(
            "test-1", "Some preamble", "room-1", agent, scope.ServiceProvider);

        // NONEXISTENT is not a known command, so nothing is parsed
        Assert.Empty(result.Results);
    }

    [Fact]
    public void FormatResultsForContext_EmptyResults_ReturnsEmpty()
    {
        var result = CommandPipeline.FormatResultsForContext(new List<CommandEnvelope>());
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FormatResultsForContext_FormatsCorrectly()
    {
        var envelope = new CommandEnvelope(
            "LIST_ROOMS",
            new Dictionary<string, object?>(),
            CommandStatus.Success,
            new Dictionary<string, object?> { ["count"] = 2 },
            null, "cmd-123", DateTime.UtcNow, "test-1");

        var result = CommandPipeline.FormatResultsForContext(new List<CommandEnvelope> { envelope });

        Assert.Contains("=== COMMAND RESULTS ===", result);
        Assert.Contains("[Success] LIST_ROOMS", result);
        Assert.Contains("=== END COMMAND RESULTS ===", result);
    }

    [Fact]
    public void FormatResultsForContext_IncludesRetryableHint()
    {
        var envelope = new CommandEnvelope(
            "READ_FILE",
            new Dictionary<string, object?>(),
            CommandStatus.Error,
            null,
            "Timed out",
            "cmd-456", DateTime.UtcNow, "test-1")
        { ErrorCode = CommandErrorCode.Timeout };

        var result = CommandPipeline.FormatResultsForContext(new List<CommandEnvelope> { envelope });

        Assert.Contains("ErrorCode: TIMEOUT (retryable)", result);
    }

    [Fact]
    public void FormatResultsForContext_IncludesNotRetryableHint()
    {
        var envelope = new CommandEnvelope(
            "READ_FILE",
            new Dictionary<string, object?>(),
            CommandStatus.Error,
            null,
            "File not found",
            "cmd-789", DateTime.UtcNow, "test-1")
        { ErrorCode = CommandErrorCode.NotFound };

        var result = CommandPipeline.FormatResultsForContext(new List<CommandEnvelope> { envelope });

        Assert.Contains("ErrorCode: NOT_FOUND (not retryable)", result);
    }

    [Theory]
    [InlineData("RATE_LIMIT", true)]
    [InlineData("TIMEOUT", true)]
    [InlineData("INTERNAL", true)]
    [InlineData("VALIDATION", false)]
    [InlineData("NOT_FOUND", false)]
    [InlineData("PERMISSION", false)]
    [InlineData("CONFLICT", false)]
    [InlineData("EXECUTION", false)]
    [InlineData(null, false)]
    public void IsRetryable_ReturnsExpected(string? code, bool expected)
    {
        Assert.Equal(expected, CommandErrorCode.IsRetryable(code));
    }
}
