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

    [Fact]
    public async Task ProcessResponse_DestructiveCommand_WithoutConfirm_ReturnsConfirmationRequired()
    {
        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "FORGET" },
            Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        var result = await _pipeline.ProcessResponseAsync(
            "test-1", "FORGET:\n  key: some-memory", "room-1", agent, scope.ServiceProvider);

        Assert.Single(result.Results);
        Assert.Equal(CommandStatus.Denied, result.Results[0].Status);
        Assert.Equal(CommandErrorCode.ConfirmationRequired, result.Results[0].ErrorCode);
        Assert.NotNull(result.Results[0].Result);
        Assert.Equal(true, result.Results[0].Result!["requiresConfirmation"]);
        Assert.Equal("confirm=true", result.Results[0].Result!["retryWith"]?.ToString());
    }

    [Fact]
    public async Task ProcessResponse_DestructiveCommand_WithConfirmTrue_Executes()
    {
        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "FORGET" },
            Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();

        // First remember something so FORGET has something to delete
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "test-1",
            Key = "test-key",
            Value = "test-value",
            Category = "lesson",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var result = await _pipeline.ProcessResponseAsync(
            "test-1", "FORGET:\n  key: test-key\n  confirm: true", "room-1", agent, scope.ServiceProvider);

        Assert.Single(result.Results);
        Assert.Equal(CommandStatus.Success, result.Results[0].Status);
        Assert.Equal("deleted", result.Results[0].Result?["action"]?.ToString());
    }

    [Fact]
    public async Task ProcessResponse_NonDestructiveCommand_ExecutesWithoutConfirm()
    {
        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "REMEMBER" },
            Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        var result = await _pipeline.ProcessResponseAsync(
            "test-1",
            "REMEMBER:\n  Category: lesson\n  Key: no-confirm\n  Value: works without confirm",
            "room-1", agent, scope.ServiceProvider);

        Assert.Single(result.Results);
        Assert.Equal(CommandStatus.Success, result.Results[0].Status);
    }

    [Fact]
    public async Task ProcessResponse_DestructiveCommand_WithConfirmFalse_StillRequiresConfirmation()
    {
        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "FORGET" },
            Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        var result = await _pipeline.ProcessResponseAsync(
            "test-1", "FORGET:\n  key: test-key\n  confirm: false", "room-1", agent, scope.ServiceProvider);

        Assert.Single(result.Results);
        Assert.Equal(CommandStatus.Denied, result.Results[0].Status);
        Assert.Equal(CommandErrorCode.ConfirmationRequired, result.Results[0].ErrorCode);
    }

    [Fact]
    public async Task ProcessResponse_DestructiveCommand_DoesNotConsumeRateLimit()
    {
        // Create pipeline with a tight rate limiter
        var rateLimiter = new CommandRateLimiter(maxCommands: 1, windowSeconds: 60);
        var pipeline = new CommandPipeline(
            new ICommandHandler[] { new ForgetHandler() },
            NullLogger<CommandPipeline>.Instance,
            rateLimiter);

        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "FORGET" },
            Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();

        // First call: confirmation required (should NOT consume rate limit)
        var result1 = await pipeline.ProcessResponseAsync(
            "test-1", "FORGET:\n  key: k1", "room-1", agent, scope.ServiceProvider);
        Assert.Equal(CommandErrorCode.ConfirmationRequired, result1.Results[0].ErrorCode);

        // Second call: confirmation required again (rate limit should still be available)
        var result2 = await pipeline.ProcessResponseAsync(
            "test-1", "FORGET:\n  key: k2", "room-1", agent, scope.ServiceProvider);
        Assert.Equal(CommandErrorCode.ConfirmationRequired, result2.Results[0].ErrorCode);
    }

    [Fact]
    public async Task ProcessResponse_DestructiveConfirmation_IsAudited()
    {
        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "FORGET" },
            Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        await _pipeline.ProcessResponseAsync(
            "test-1", "FORGET:\n  key: some-memory", "room-1", agent, scope.ServiceProvider);

        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var audits = await db.CommandAudits.Where(a => a.Command == "FORGET").ToListAsync();
        Assert.Single(audits);
        Assert.Equal("Denied", audits[0].Status);
        Assert.Equal(CommandErrorCode.ConfirmationRequired, audits[0].ErrorCode);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("no", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HasConfirmFlag_ParsesCorrectly(string? value, bool expected)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (value != null)
            args["confirm"] = value;

        Assert.Equal(expected, CommandPipeline.HasConfirmFlag(args));
    }

    [Fact]
    public void HasConfirmFlag_MissingKey_ReturnsFalse()
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["key"] = "some-value"
        };

        Assert.False(CommandPipeline.HasConfirmFlag(args));
    }

    // --- Retry tests ---

    /// <summary>
    /// Fake handler whose failure behavior is configurable per test.
    /// </summary>
    private sealed class FakeHandler : ICommandHandler
    {
        private readonly Queue<Func<CommandEnvelope, Task<CommandEnvelope>>> _behaviors = new();

        public string CommandName { get; init; } = "LIST_ROOMS";
        public bool IsRetrySafe { get; init; }
        public int CallCount { get; private set; }

        public FakeHandler Throws(Exception ex)
        {
            _behaviors.Enqueue(_ => throw ex);
            return this;
        }

        public FakeHandler Succeeds()
        {
            _behaviors.Enqueue(cmd => Task.FromResult(cmd with { Status = CommandStatus.Success }));
            return this;
        }

        public FakeHandler ReturnsError(string errorCode, string message)
        {
            _behaviors.Enqueue(cmd => Task.FromResult(cmd with
            {
                Status = CommandStatus.Error,
                ErrorCode = errorCode,
                Error = message
            }));
            return this;
        }

        public Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
        {
            CallCount++;
            if (_behaviors.Count > 0)
                return _behaviors.Dequeue()(command);
            return Task.FromResult(command with { Status = CommandStatus.Success });
        }
    }

    private CommandPipeline PipelineWith(params ICommandHandler[] handlers)
    {
        return new CommandPipeline(handlers, NullLogger<CommandPipeline>.Instance);
    }

    [Fact]
    public async Task Retry_RetrySafeHandler_ThrowsOnceThenSucceeds_ReturnsSuccess()
    {
        var handler = new FakeHandler { CommandName = "LIST_ROOMS", IsRetrySafe = true };
        handler.Throws(new InvalidOperationException("transient")).Succeeds();
        var pipeline = PipelineWith(handler);

        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "LIST_ROOMS" }, Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        var result = await pipeline.ProcessResponseAsync(
            "test-1", "LIST_ROOMS:", "room-1", agent, scope.ServiceProvider);

        Assert.Single(result.Results);
        Assert.Equal(CommandStatus.Success, result.Results[0].Status);
        Assert.Equal(1, result.Results[0].RetryCount);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Retry_RetrySafeHandler_ThrowsAllAttempts_ReturnsFinalError()
    {
        var handler = new FakeHandler { CommandName = "LIST_ROOMS", IsRetrySafe = true };
        handler
            .Throws(new InvalidOperationException("fail 1"))
            .Throws(new InvalidOperationException("fail 2"))
            .Throws(new InvalidOperationException("fail 3"));
        var pipeline = PipelineWith(handler);

        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "LIST_ROOMS" }, Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        var result = await pipeline.ProcessResponseAsync(
            "test-1", "LIST_ROOMS:", "room-1", agent, scope.ServiceProvider);

        Assert.Single(result.Results);
        Assert.Equal(CommandStatus.Error, result.Results[0].Status);
        Assert.Equal(CommandErrorCode.Internal, result.Results[0].ErrorCode);
        Assert.Contains("3 attempt(s)", result.Results[0].Error);
        Assert.Equal(2, result.Results[0].RetryCount);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task Retry_RetrySafeHandler_ReturnsRetryableErrorThenSucceeds()
    {
        var handler = new FakeHandler { CommandName = "LIST_ROOMS", IsRetrySafe = true };
        handler
            .ReturnsError(CommandErrorCode.Timeout, "timed out")
            .Succeeds();
        var pipeline = PipelineWith(handler);

        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "LIST_ROOMS" }, Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        var result = await pipeline.ProcessResponseAsync(
            "test-1", "LIST_ROOMS:", "room-1", agent, scope.ServiceProvider);

        Assert.Single(result.Results);
        Assert.Equal(CommandStatus.Success, result.Results[0].Status);
        Assert.Equal(1, result.Results[0].RetryCount);
    }

    [Fact]
    public async Task Retry_RetrySafeHandler_NonRetryableError_NoRetry()
    {
        var handler = new FakeHandler { CommandName = "LIST_ROOMS", IsRetrySafe = true };
        handler.ReturnsError(CommandErrorCode.Validation, "bad args");
        var pipeline = PipelineWith(handler);

        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "LIST_ROOMS" }, Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        var result = await pipeline.ProcessResponseAsync(
            "test-1", "LIST_ROOMS:", "room-1", agent, scope.ServiceProvider);

        Assert.Single(result.Results);
        Assert.Equal(CommandStatus.Error, result.Results[0].Status);
        Assert.Equal(CommandErrorCode.Validation, result.Results[0].ErrorCode);
        Assert.Equal(0, result.Results[0].RetryCount);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Retry_NonRetrySafeHandler_Throws_NoRetry()
    {
        var handler = new FakeHandler { CommandName = "LIST_ROOMS", IsRetrySafe = false };
        handler.Throws(new InvalidOperationException("transient"));
        var pipeline = PipelineWith(handler);

        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "LIST_ROOMS" }, Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        var result = await pipeline.ProcessResponseAsync(
            "test-1", "LIST_ROOMS:", "room-1", agent, scope.ServiceProvider);

        Assert.Single(result.Results);
        Assert.Equal(CommandStatus.Error, result.Results[0].Status);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Retry_HandlerReturnedRateLimit_NotRetriedByPipeline()
    {
        var handler = new FakeHandler { CommandName = "LIST_ROOMS", IsRetrySafe = true };
        handler.ReturnsError(CommandErrorCode.RateLimit, "quota exceeded");
        var pipeline = PipelineWith(handler);

        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "LIST_ROOMS" }, Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        var result = await pipeline.ProcessResponseAsync(
            "test-1", "LIST_ROOMS:", "room-1", agent, scope.ServiceProvider);

        Assert.Single(result.Results);
        Assert.Equal(CommandStatus.Error, result.Results[0].Status);
        Assert.Equal(CommandErrorCode.RateLimit, result.Results[0].ErrorCode);
        Assert.Equal(0, result.Results[0].RetryCount);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Retry_OnlyFinalResultAudited()
    {
        var handler = new FakeHandler { CommandName = "LIST_ROOMS", IsRetrySafe = true };
        handler
            .Throws(new InvalidOperationException("transient"))
            .Succeeds();
        var pipeline = PipelineWith(handler);

        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "LIST_ROOMS" }, Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        await pipeline.ProcessResponseAsync(
            "test-1", "LIST_ROOMS:", "room-1", agent, scope.ServiceProvider);

        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var audits = await db.CommandAudits.Where(a => a.Command == "LIST_ROOMS").ToListAsync();
        Assert.Single(audits);
        Assert.Equal("Success", audits[0].Status);
    }

    [Fact]
    public void FormatResultsForContext_IncludesRetryCount()
    {
        var envelope = new CommandEnvelope(
            "READ_FILE",
            new Dictionary<string, object?>(),
            CommandStatus.Success,
            new Dictionary<string, object?> { ["content"] = "hello" },
            null, "cmd-retry", DateTime.UtcNow, "test-1")
        { RetryCount = 2 };

        var result = CommandPipeline.FormatResultsForContext(new List<CommandEnvelope> { envelope });

        Assert.Contains("Retries: 2", result);
    }

    [Fact]
    public void FormatResultsForContext_OmitsRetryCountWhenZero()
    {
        var envelope = new CommandEnvelope(
            "LIST_ROOMS",
            new Dictionary<string, object?>(),
            CommandStatus.Success,
            null, null, "cmd-no-retry", DateTime.UtcNow, "test-1");

        var result = CommandPipeline.FormatResultsForContext(new List<CommandEnvelope> { envelope });

        Assert.DoesNotContain("Retries:", result);
    }
}
