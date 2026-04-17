using System.Text.Json;
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

    [Fact]
    public void HasConfirmFlag_UnsupportedType_ReturnsFalse()
    {
        // Kills the boolean mutation on the `_ => false` default arm of the switch
        // expression — any non-bool, non-string value for "confirm" must be rejected.
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["confirm"] = 1
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

    // --- Mutation-kill tests ---
    // These tests exist specifically to kill mutants that would otherwise survive
    // Stryker.NET analysis. See stryker-config.json for mutation testing setup.

    [Fact]
    public async Task ProcessResponse_CustomRateLimiter_IsUsed_NotReplacedByDefault()
    {
        // Kills L43 null-coalesce mutation: if the custom rate limiter is ignored
        // and a default (maxCommands=30) is constructed, the second call succeeds.
        var rateLimiter = new CommandRateLimiter(maxCommands: 1, windowSeconds: 60);
        var handler = new FakeHandler { CommandName = "LIST_ROOMS", IsRetrySafe = true };
        handler.Succeeds().Succeeds();
        var pipeline = new CommandPipeline(
            new ICommandHandler[] { handler },
            NullLogger<CommandPipeline>.Instance,
            rateLimiter);

        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "LIST_ROOMS" }, Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();

        var first = await pipeline.ProcessResponseAsync(
            "rl-agent", "LIST_ROOMS:", "room-1", agent, scope.ServiceProvider);
        Assert.Equal(CommandStatus.Success, first.Results[0].Status);

        var second = await pipeline.ProcessResponseAsync(
            "rl-agent", "LIST_ROOMS:", "room-1", agent, scope.ServiceProvider);
        Assert.Equal(CommandStatus.Denied, second.Results[0].Status);
        Assert.Equal(CommandErrorCode.RateLimit, second.Results[0].ErrorCode);
    }

    [Fact]
    public async Task ProcessResponse_CorrelationId_UsesCmdPrefixAndNFormatGuid()
    {
        // Kills L94 string mutation on $"cmd-{Guid.NewGuid():N}".
        var handler = new FakeHandler { CommandName = "LIST_ROOMS", IsRetrySafe = true };
        handler.Succeeds();
        var pipeline = PipelineWith(handler);
        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "LIST_ROOMS" }, Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        var result = await pipeline.ProcessResponseAsync(
            "test-1", "LIST_ROOMS:", "room-1", agent, scope.ServiceProvider);

        var correlationId = result.Results[0].CorrelationId;
        Assert.StartsWith("cmd-", correlationId);
        // N-format GUID: 32 hex chars, no hyphens
        Assert.Matches("^cmd-[0-9a-fA-F]{32}$", correlationId);
    }

    [Fact]
    public async Task ProcessResponse_DeniedCommand_IsAudited()
    {
        // Kills L108 statement mutation removing AuditAsync on the denied path.
        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "LIST_ROOMS" },
            Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        await _pipeline.ProcessResponseAsync(
            "test-1", "READ_FILE:\n  path: a.cs", "room-1", agent, scope.ServiceProvider);

        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var audits = await db.CommandAudits.Where(a => a.Command == "READ_FILE").ToListAsync();
        Assert.Single(audits);
        Assert.Equal("Denied", audits[0].Status);
        Assert.Equal(CommandErrorCode.Permission, audits[0].ErrorCode);
        // Kills L314 conditional/equality mutants: Result is null on the denied envelope,
        // so ResultJson must be null (not the string "null").
        Assert.Null(audits[0].ResultJson);
        // Kills L308 Guid format "N" mutation: Id is 32 lowercase hex chars, no hyphens.
        Assert.Matches("^[0-9a-f]{32}$", audits[0].Id);
    }

    [Fact]
    public async Task ProcessResponse_SuccessAudit_HasSerializedResultJson()
    {
        // Kills L314 conditional-false and equality mutants: when envelope.Result is
        // non-null, ResultJson must be the serialized dict (not null, not "null").
        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "REMEMBER" }, Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        await _pipeline.ProcessResponseAsync(
            "test-1",
            "REMEMBER:\n  Category: lesson\n  Key: k\n  Value: v",
            "room-1", agent, scope.ServiceProvider);

        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var audit = await db.CommandAudits.SingleAsync(a => a.Command == "REMEMBER");
        Assert.NotNull(audit.ResultJson);
        Assert.NotEqual("null", audit.ResultJson);
        Assert.StartsWith("{", audit.ResultJson);
    }

    [Fact]
    public async Task ProcessResponse_DestructiveConfirmation_ErrorMessageHasReissueInstruction()
    {
        // Kills L134 string concatenation mutation — the error message must include
        // the literal "Re-issue with confirm=true to proceed." instruction.
        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "FORGET" }, Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        var result = await _pipeline.ProcessResponseAsync(
            "test-1", "FORGET:\n  key: k", "room-1", agent, scope.ServiceProvider);

        Assert.Single(result.Results);
        Assert.NotNull(result.Results[0].Error);
        Assert.Contains("Re-issue with confirm=true to proceed.", result.Results[0].Error);
    }

    [Fact]
    public async Task ProcessResponse_DestructiveConfirmation_ResultHasCommandAndWarningKeys()
    {
        // Kills L138 and L139 string mutations on the "command" and "warning"
        // dictionary keys in the confirmation-required payload.
        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "FORGET" }, Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        var result = await _pipeline.ProcessResponseAsync(
            "test-1", "FORGET:\n  key: k", "room-1", agent, scope.ServiceProvider);

        var payload = result.Results[0].Result;
        Assert.NotNull(payload);
        Assert.True(payload!.ContainsKey("command"));
        Assert.Equal("FORGET", payload["command"]?.ToString());
        Assert.True(payload.ContainsKey("warning"));
        Assert.NotNull(payload["warning"]);
    }

    [Fact]
    public void FormatResultsForContext_SuccessWithNoError_OmitsErrorLine()
    {
        // Kills L215 equality mutation: when r.Error is null, no "Error:" line is emitted.
        var envelope = new CommandEnvelope(
            "LIST_ROOMS",
            new Dictionary<string, object?>(),
            CommandStatus.Success,
            new Dictionary<string, object?> { ["count"] = 0 },
            null, "cmd-ok", DateTime.UtcNow, "test-1");

        var output = CommandPipeline.FormatResultsForContext(new List<CommandEnvelope> { envelope });

        Assert.DoesNotContain("Error:", output);
    }

    [Fact]
    public void FormatResultsForContext_ErrorEnvelope_IncludesErrorMessageText()
    {
        // Kills L216 statement + string mutations — the output must contain the
        // literal "  Error: {error}" line for envelopes with a non-null Error.
        var envelope = new CommandEnvelope(
            "READ_FILE",
            new Dictionary<string, object?>(),
            CommandStatus.Error,
            null,
            "File not found: foo.cs",
            "cmd-err", DateTime.UtcNow, "test-1")
        { ErrorCode = CommandErrorCode.NotFound };

        var output = CommandPipeline.FormatResultsForContext(new List<CommandEnvelope> { envelope });

        Assert.Contains("  Error: File not found: foo.cs", output);
    }

    [Fact]
    public void FormatResultsForContext_NullResult_OmitsResultBlock()
    {
        // Kills L217 equality mutation: when r.Result is null, no JSON block is added.
        // With the mutant flipped, the code would try to serialize null and emit it.
        var envelope = new CommandEnvelope(
            "READ_FILE",
            new Dictionary<string, object?>(),
            CommandStatus.Error,
            null,
            "boom",
            "cmd-nores", DateTime.UtcNow, "test-1");

        var output = CommandPipeline.FormatResultsForContext(new List<CommandEnvelope> { envelope });

        // The error line is present, but there must be no JSON body (no "{" or "null"
        // serialized object block after the error line).
        Assert.DoesNotContain("{", output);
        Assert.DoesNotContain("  null", output);
    }

    [Fact]
    public void FormatResultsForContext_ResultJson_IsIndented()
    {
        // Kills L219 object-initializer and WriteIndented=false mutations — the
        // serialized result must be indented (contains newlines and 2-space indent).
        var envelope = new CommandEnvelope(
            "LIST_ROOMS",
            new Dictionary<string, object?>(),
            CommandStatus.Success,
            new Dictionary<string, object?> { ["alpha"] = "beta" },
            null, "cmd-ind", DateTime.UtcNow, "test-1");

        var output = CommandPipeline.FormatResultsForContext(new List<CommandEnvelope> { envelope });

        // Compact JSON would be {"alpha":"beta"} on one line. Indented JSON contains
        // a newline followed by 2-space indent before the key.
        Assert.Contains("\n  \"alpha\"", output);
    }

    [Fact]
    public void FormatResultsForContext_ResultJson_IncludesPayload()
    {
        // Kills L223 statement and string mutations — the serialized JSON body
        // must actually be appended to the output.
        var envelope = new CommandEnvelope(
            "LIST_ROOMS",
            new Dictionary<string, object?>(),
            CommandStatus.Success,
            new Dictionary<string, object?> { ["marker"] = "uniquevalue42" },
            null, "cmd-body", DateTime.UtcNow, "test-1");

        var output = CommandPipeline.FormatResultsForContext(new List<CommandEnvelope> { envelope });

        Assert.Contains("uniquevalue42", output);
        Assert.Contains("\"marker\"", output);
    }

    [Fact]
    public void FormatResultsForContext_JsonExactlyAtBoundary_IsNotTruncated()
    {
        // Kills L221 equality mutation (> 2000 vs >= 2000): at exactly 2000 chars
        // the JSON must NOT be truncated.
        var opts = new JsonSerializerOptions { WriteIndented = true };
        var value = "a";
        var dict = new Dictionary<string, object?> { ["k"] = value };
        // Grow the string value one char at a time until the serialized form is
        // exactly 2000 chars. Since the padding lives inside a JSON string, each
        // added char increases the serialized length by exactly 1.
        while (JsonSerializer.Serialize(dict, opts).Length < 2000)
        {
            value += "a";
            dict["k"] = value;
        }
        Assert.Equal(2000, JsonSerializer.Serialize(dict, opts).Length);

        var envelope = new CommandEnvelope(
            "LIST_ROOMS",
            new Dictionary<string, object?>(),
            CommandStatus.Success,
            dict,
            null, "cmd-bound", DateTime.UtcNow, "test-1");

        var output = CommandPipeline.FormatResultsForContext(new List<CommandEnvelope> { envelope });

        Assert.DoesNotContain("... (truncated)", output);
    }

    [Fact]
    public void FormatResultsForContext_LongJson_IsTruncated()
    {
        // Complements the boundary test: at > 2000 chars the JSON IS truncated.
        var big = new string('x', 3000);
        var envelope = new CommandEnvelope(
            "LIST_ROOMS",
            new Dictionary<string, object?>(),
            CommandStatus.Success,
            new Dictionary<string, object?> { ["big"] = big },
            null, "cmd-big", DateTime.UtcNow, "test-1");

        var output = CommandPipeline.FormatResultsForContext(new List<CommandEnvelope> { envelope });

        Assert.Contains("... (truncated)", output);
    }

    [Fact]
    public async Task ProcessResponse_NoHandlerRegistered_ReturnsNotFoundAndAudits()
    {
        // Kills L126 (error text), L128 (audit), L129 (add to results), L130 (continue)
        // on the unknown-command branch. With L130 removed the code would null-deref on
        // handler.IsDestructive.
        var pipeline = new CommandPipeline(
            Array.Empty<ICommandHandler>(),
            NullLogger<CommandPipeline>.Instance);

        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "LIST_ROOMS" }, Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        var result = await pipeline.ProcessResponseAsync(
            "test-1", "LIST_ROOMS:", "room-1", agent, scope.ServiceProvider);

        Assert.Single(result.Results);
        Assert.Equal(CommandStatus.Error, result.Results[0].Status);
        Assert.Equal(CommandErrorCode.NotFound, result.Results[0].ErrorCode);
        Assert.Contains("Unknown command: LIST_ROOMS", result.Results[0].Error);

        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var audits = await db.CommandAudits.Where(a => a.Command == "LIST_ROOMS").ToListAsync();
        Assert.Single(audits);
        Assert.Equal(CommandErrorCode.NotFound, audits[0].ErrorCode);
    }

    [Fact]
    public async Task ProcessResponse_RateLimited_ErrorAuditedAndHandlerNotCalled()
    {
        // Kills L165 (rate-limit error message format), L170 (AuditAsync for rate-limited),
        // and L172 (continue — if removed, falls through to execute block and handler runs
        // despite rate limit).
        var handler = new FakeHandler { CommandName = "LIST_ROOMS", IsRetrySafe = true };
        handler.Succeeds().Succeeds();
        var rateLimiter = new CommandRateLimiter(maxCommands: 1, windowSeconds: 60);
        var pipeline = new CommandPipeline(
            new ICommandHandler[] { handler },
            NullLogger<CommandPipeline>.Instance,
            rateLimiter);

        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "LIST_ROOMS" }, Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();

        // Consume the single-command budget.
        var first = await pipeline.ProcessResponseAsync(
            "rl-audited", "LIST_ROOMS:", "room-1", agent, scope.ServiceProvider);
        Assert.Equal(CommandStatus.Success, first.Results[0].Status);
        Assert.Equal(1, handler.CallCount);

        // Second call: rate limited.
        var second = await pipeline.ProcessResponseAsync(
            "rl-audited", "LIST_ROOMS:", "room-1", agent, scope.ServiceProvider);
        Assert.Equal(CommandStatus.Denied, second.Results[0].Status);
        Assert.Equal(CommandErrorCode.RateLimit, second.Results[0].ErrorCode);
        Assert.NotNull(second.Results[0].Error);
        Assert.Contains("Rate limit exceeded", second.Results[0].Error);
        Assert.Contains("Try again in", second.Results[0].Error);

        // Handler must NOT be invoked a second time (kills the `continue` mutation).
        Assert.Equal(1, handler.CallCount);

        // Rate-limit denial is audited.
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var rateLimitedAudits = await db.CommandAudits
            .Where(a => a.ErrorCode == CommandErrorCode.RateLimit)
            .ToListAsync();
        Assert.Single(rateLimitedAudits);
        Assert.Equal("LIST_ROOMS", rateLimitedAudits[0].Command);
    }

    [Fact]
    public void CommandPipelineResult_DefaultProcessingFailed_IsFalse()
    {
        // Kills the boolean mutation on the optional ProcessingFailed record parameter.
        var result = new CommandPipelineResult(new List<CommandEnvelope>(), "remaining");
        Assert.False(result.ProcessingFailed);
    }

    /// <summary>
    /// Wraps a real service provider and makes the *first* resolution of
    /// <see cref="AgentAcademyDbContext"/> throw, simulating a transient DB
    /// failure on the first <c>AuditAsync</c> call. Subsequent resolutions
    /// (the catch-block retry) succeed normally.
    /// </summary>
    private sealed class FirstDbContextResolveFails : IServiceProvider
    {
        private readonly IServiceProvider _inner;
        private int _dbContextResolveCount;

        public FirstDbContextResolveFails(IServiceProvider inner) => _inner = inner;

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(AgentAcademyDbContext))
            {
                _dbContextResolveCount++;
                if (_dbContextResolveCount == 1)
                    throw new InvalidOperationException("transient services failure");
            }
            return _inner.GetService(serviceType);
        }
    }

    [Fact]
    public async Task ProcessResponse_AuditThrowsAfterExecute_CatchBlockReturnsInternalError()
    {
        // Kills the catch-block mutants (block removal, error envelope fields,
        // catch-path AuditAsync, and results.Add). Simulates a transient services
        // failure: the first AuditAsync after a successful execute throws, the
        // catch rebuilds an error envelope and the retry audit call succeeds.
        var handler = new FakeHandler { CommandName = "LIST_ROOMS", IsRetrySafe = true };
        handler.Succeeds();
        var pipeline = PipelineWith(handler);
        var agent = TestAgent(new CommandPermissionSet(
            Allowed: new List<string> { "LIST_ROOMS" }, Denied: new List<string>()));

        using var scope = _serviceProvider.CreateScope();
        var flaky = new FirstDbContextResolveFails(scope.ServiceProvider);

        var result = await pipeline.ProcessResponseAsync(
            "test-1", "LIST_ROOMS:", "room-1", agent, flaky);

        Assert.Single(result.Results);
        Assert.Equal(CommandStatus.Error, result.Results[0].Status);
        Assert.Equal(CommandErrorCode.Internal, result.Results[0].ErrorCode);
        Assert.NotNull(result.Results[0].Error);
        Assert.Contains(
            "Command execution failed: transient services failure",
            result.Results[0].Error);

        // Handler ran exactly once despite the audit failure.
        Assert.Equal(1, handler.CallCount);

        // The catch-path AuditAsync wrote exactly one error audit.
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var errorAudits = await db.CommandAudits
            .Where(a => a.Status == CommandStatus.Error.ToString())
            .ToListAsync();
        Assert.Single(errorAudits);
        Assert.Equal(CommandErrorCode.Internal, errorAudits[0].ErrorCode);
    }
}
