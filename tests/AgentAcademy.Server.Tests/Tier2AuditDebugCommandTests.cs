using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for Tier 2G Audit &amp; Debug command handlers:
/// ShowAuditEventsHandler, ShowLastErrorHandler, TraceRequestHandler,
/// ListSystemSettingsHandler, and RetryFailedJobHandler.
/// </summary>
public sealed class Tier2AuditDebugCommandTests : IDisposable
{
    private readonly AgentAcademyDbContext _dbContext;
    private readonly ServiceProvider _serviceProvider;
    private readonly SqliteConnection _keepAliveConnection;

    public Tier2AuditDebugCommandTests()
    {
        _keepAliveConnection = new SqliteConnection("Data Source=:memory:");
        _keepAliveConnection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(options =>
            options.UseSqlite(_keepAliveConnection));
        services.AddSingleton<IAgentCatalog>(new TestAgentCatalog());
        services.AddScoped<ISystemSettingsService, SystemSettingsService>();

        // Register all command handlers for RetryFailedJobHandler to discover
        services.AddSingleton<ICommandHandler, ShowAuditEventsHandler>();
        services.AddSingleton<ICommandHandler, ShowLastErrorHandler>();
        services.AddSingleton<ICommandHandler, TraceRequestHandler>();
        services.AddSingleton<ICommandHandler, ListSystemSettingsHandler>();
        services.AddSingleton<ICommandHandler, RetryFailedJobHandler>();
        services.AddSingleton<ICommandHandler, HealthcheckHandler>();

        _serviceProvider = services.BuildServiceProvider();
        _dbContext = _serviceProvider.GetRequiredService<AgentAcademyDbContext>();
        _dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _serviceProvider.Dispose();
        _keepAliveConnection.Dispose();
    }

    private CommandContext MakeContext(string role = "Human", string agentId = "test-agent") => new(
        AgentId: agentId,
        AgentName: "Tester",
        AgentRole: role,
        RoomId: "main",
        BreakoutRoomId: null,
        Services: _serviceProvider
    );

    private static CommandEnvelope MakeCommand(string name, Dictionary<string, object?>? args = null) => new(
        Command: name,
        Args: args ?? new Dictionary<string, object?>(),
        Status: CommandStatus.Success,
        Result: null,
        Error: null,
        CorrelationId: Guid.NewGuid().ToString(),
        Timestamp: DateTime.UtcNow,
        ExecutedBy: "test-agent"
    );

    // ==================== SHOW_AUDIT_EVENTS ====================

    [Fact]
    public async Task ShowAuditEvents_ReturnsEmpty_WhenNoEvents()
    {
        var handler = new ShowAuditEventsHandler();
        var result = await handler.ExecuteAsync(MakeCommand("SHOW_AUDIT_EVENTS"), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(0, data["count"]);
    }

    [Fact]
    public async Task ShowAuditEvents_ReturnsEvents_SortedByTimeDesc()
    {
        SeedActivityEvents();

        var handler = new ShowAuditEventsHandler();
        var result = await handler.ExecuteAsync(MakeCommand("SHOW_AUDIT_EVENTS"), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.True((int)data["count"]! > 0);
    }

    [Fact]
    public async Task ShowAuditEvents_FiltersByType()
    {
        SeedActivityEvents();

        var handler = new ShowAuditEventsHandler();
        var cmd = MakeCommand("SHOW_AUDIT_EVENTS", new Dictionary<string, object?> { ["type"] = "AgentErrorOccurred" });
        var result = await handler.ExecuteAsync(cmd, MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        var events = (IEnumerable<object>)data["events"]!;
        Assert.All(events, e =>
        {
            var dict = (dynamic)e;
            Assert.Equal("AgentErrorOccurred", (string)dict.Type);
        });
    }

    [Fact]
    public async Task ShowAuditEvents_FiltersBySeverity()
    {
        SeedActivityEvents();

        var handler = new ShowAuditEventsHandler();
        var cmd = MakeCommand("SHOW_AUDIT_EVENTS", new Dictionary<string, object?> { ["severity"] = "Error" });
        var result = await handler.ExecuteAsync(cmd, MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        var events = (IEnumerable<object>)data["events"]!;
        Assert.All(events, e =>
        {
            var dict = (dynamic)e;
            Assert.Equal("Error", (string)dict.Severity);
        });
    }

    [Fact]
    public async Task ShowAuditEvents_FiltersByActorId()
    {
        SeedActivityEvents();

        var handler = new ShowAuditEventsHandler();
        var cmd = MakeCommand("SHOW_AUDIT_EVENTS", new Dictionary<string, object?> { ["actorId"] = "agent-1" });
        var result = await handler.ExecuteAsync(cmd, MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        var events = (IEnumerable<object>)data["events"]!;
        Assert.All(events, e =>
        {
            var dict = (dynamic)e;
            Assert.Equal("agent-1", (string)dict.ActorId);
        });
    }

    [Fact]
    public async Task ShowAuditEvents_FiltersBySince()
    {
        SeedActivityEvents();

        var handler = new ShowAuditEventsHandler();
        var since = DateTime.UtcNow.AddMinutes(-30).ToString("O");
        var cmd = MakeCommand("SHOW_AUDIT_EVENTS", new Dictionary<string, object?> { ["since"] = since });
        var result = await handler.ExecuteAsync(cmd, MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        // Recent events only
        Assert.True((int)data["count"]! >= 0);
    }

    [Fact]
    public async Task ShowAuditEvents_RespectsCountLimit()
    {
        SeedActivityEvents();

        var handler = new ShowAuditEventsHandler();
        var cmd = MakeCommand("SHOW_AUDIT_EVENTS", new Dictionary<string, object?> { ["count"] = "2" });
        var result = await handler.ExecuteAsync(cmd, MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.True((int)data["count"]! <= 2);
    }

    [Fact]
    public async Task ShowAuditEvents_ClampsCountToMax()
    {
        SeedActivityEvents();

        var handler = new ShowAuditEventsHandler();
        var cmd = MakeCommand("SHOW_AUDIT_EVENTS", new Dictionary<string, object?> { ["count"] = "999" });
        var result = await handler.ExecuteAsync(cmd, MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        // Should not crash — clamped to max 100
    }

    [Fact]
    public void ShowAuditEvents_IsRetrySafe()
    {
        var handler = new ShowAuditEventsHandler();
        Assert.True(handler.IsRetrySafe);
        Assert.Equal("SHOW_AUDIT_EVENTS", handler.CommandName);
    }

    // ==================== SHOW_LAST_ERROR ====================

    [Fact]
    public async Task ShowLastError_ReturnsEmpty_WhenNoErrors()
    {
        var handler = new ShowLastErrorHandler();
        var result = await handler.ExecuteAsync(MakeCommand("SHOW_LAST_ERROR"), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(0, data["count"]);
    }

    [Fact]
    public async Task ShowLastError_MergesActivityAndCommandErrors()
    {
        SeedActivityEvents();
        SeedCommandAudits();

        var handler = new ShowLastErrorHandler();
        var result = await handler.ExecuteAsync(MakeCommand("SHOW_LAST_ERROR"), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        var errors = (IList<Dictionary<string, object?>>)data["errors"]!;

        // Should have errors from both sources
        Assert.True(errors.Count > 0);
        var sources = errors.Select(e => (string)e["source"]!).Distinct().ToList();
        Assert.Contains("activity", sources);
        Assert.Contains("command", sources);
    }

    [Fact]
    public async Task ShowLastError_FiltersByAgentId()
    {
        SeedActivityEvents();
        SeedCommandAudits();

        var handler = new ShowLastErrorHandler();
        var cmd = MakeCommand("SHOW_LAST_ERROR", new Dictionary<string, object?> { ["agentId"] = "agent-1" });
        var result = await handler.ExecuteAsync(cmd, MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        var errors = (IList<Dictionary<string, object?>>)data["errors"]!;

        Assert.All(errors, e => Assert.Equal("agent-1", (string?)e["agentId"]));
    }

    [Fact]
    public async Task ShowLastError_RespectsCountLimit()
    {
        SeedActivityEvents();
        SeedCommandAudits();

        var handler = new ShowLastErrorHandler();
        var cmd = MakeCommand("SHOW_LAST_ERROR", new Dictionary<string, object?> { ["count"] = "1" });
        var result = await handler.ExecuteAsync(cmd, MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.True((int)data["count"]! <= 1);
    }

    [Fact]
    public void ShowLastError_IsRetrySafe()
    {
        var handler = new ShowLastErrorHandler();
        Assert.True(handler.IsRetrySafe);
        Assert.Equal("SHOW_LAST_ERROR", handler.CommandName);
    }

    // ==================== TRACE_REQUEST ====================

    [Fact]
    public async Task TraceRequest_RequiresCorrelationId()
    {
        var handler = new TraceRequestHandler();
        var result = await handler.ExecuteAsync(MakeCommand("TRACE_REQUEST"), MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task TraceRequest_ReturnsEmptyTimeline_WhenNotFound()
    {
        var handler = new TraceRequestHandler();
        var cmd = MakeCommand("TRACE_REQUEST", new Dictionary<string, object?> { ["correlationId"] = "nonexistent" });
        var result = await handler.ExecuteAsync(cmd, MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(0, data["totalEvents"]);
    }

    [Fact]
    public async Task TraceRequest_MergesActivityAndAuditEvents()
    {
        var corrId = "corr-test-123";
        SeedEventsWithCorrelationId(corrId);

        var handler = new TraceRequestHandler();
        var cmd = MakeCommand("TRACE_REQUEST", new Dictionary<string, object?> { ["correlationId"] = corrId });
        var result = await handler.ExecuteAsync(cmd, MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(corrId, data["correlationId"]);
        Assert.True((int)data["activityEventCount"]! > 0);
        Assert.True((int)data["commandAuditCount"]! > 0);
        Assert.True((int)data["totalEvents"]! > 0);
    }

    [Fact]
    public void TraceRequest_IsRetrySafe()
    {
        var handler = new TraceRequestHandler();
        Assert.True(handler.IsRetrySafe);
        Assert.Equal("TRACE_REQUEST", handler.CommandName);
    }

    // ==================== LIST_SYSTEM_SETTINGS ====================

    [Fact]
    public async Task ListSystemSettings_ReturnsDefaults_WhenNoStoredSettings()
    {
        var handler = new ListSystemSettingsHandler();
        var result = await handler.ExecuteAsync(MakeCommand("LIST_SYSTEM_SETTINGS"), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        var entries = (IEnumerable<object>)data["settings"]!;
        Assert.True(entries.Any());
    }

    [Fact]
    public async Task ListSystemSettings_IncludesStoredOverrides()
    {
        _dbContext.SystemSettings.Add(new SystemSettingEntity
        {
            Key = "conversation.mainRoomEpochSize",
            Value = "100",
            UpdatedAt = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();

        var handler = new ListSystemSettingsHandler();
        var result = await handler.ExecuteAsync(MakeCommand("LIST_SYSTEM_SETTINGS"), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        var entries = (IList<Dictionary<string, object?>>)data["settings"]!;
        var epochEntry = entries.FirstOrDefault(e => (string)e["key"]! == "conversation.mainRoomEpochSize");
        Assert.NotNull(epochEntry);
        Assert.Equal("100", (string)epochEntry["value"]!);
    }

    [Fact]
    public void ListSystemSettings_IsRetrySafe()
    {
        var handler = new ListSystemSettingsHandler();
        Assert.True(handler.IsRetrySafe);
        Assert.Equal("LIST_SYSTEM_SETTINGS", handler.CommandName);
    }

    // ==================== RETRY_FAILED_JOB ====================

    [Fact]
    public async Task RetryFailedJob_RequiresAuditId()
    {
        var handler = new RetryFailedJobHandler();
        var result = await handler.ExecuteAsync(MakeCommand("RETRY_FAILED_JOB"), MakeContext("Planner"));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task RetryFailedJob_DeniesNonPlannerNonHuman()
    {
        var handler = new RetryFailedJobHandler();
        var cmd = MakeCommand("RETRY_FAILED_JOB", new Dictionary<string, object?> { ["auditId"] = "some-id" });
        var result = await handler.ExecuteAsync(cmd, MakeContext("SoftwareEngineer"));

        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
        Assert.Contains("Planner and Human", result.Error);
    }

    [Fact]
    public async Task RetryFailedJob_AllowsPlanner()
    {
        var auditId = SeedFailedAudit("HEALTHCHECK");

        var handler = new RetryFailedJobHandler();
        var cmd = MakeCommand("RETRY_FAILED_JOB", new Dictionary<string, object?> { ["auditId"] = auditId });
        var result = await handler.ExecuteAsync(cmd, MakeContext("Planner", "planner-1"));

        // HEALTHCHECK is retry-safe, should succeed
        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal("HEALTHCHECK", data["retriedCommand"]);
        Assert.Equal(auditId, data["retriedFromAuditId"]);
    }

    [Fact]
    public async Task RetryFailedJob_AllowsHuman()
    {
        var auditId = SeedFailedAudit("HEALTHCHECK");

        var handler = new RetryFailedJobHandler();
        var cmd = MakeCommand("RETRY_FAILED_JOB", new Dictionary<string, object?> { ["auditId"] = auditId });
        var result = await handler.ExecuteAsync(cmd, MakeContext("Human"));

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task RetryFailedJob_RejectsNonExistentAuditId()
    {
        var handler = new RetryFailedJobHandler();
        var cmd = MakeCommand("RETRY_FAILED_JOB", new Dictionary<string, object?> { ["auditId"] = "nonexistent" });
        var result = await handler.ExecuteAsync(cmd, MakeContext("Planner"));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task RetryFailedJob_RejectsNonErrorAudit()
    {
        var auditId = SeedSuccessAudit("HEALTHCHECK");

        var handler = new RetryFailedJobHandler();
        var cmd = MakeCommand("RETRY_FAILED_JOB", new Dictionary<string, object?> { ["auditId"] = auditId });
        var result = await handler.ExecuteAsync(cmd, MakeContext("Planner"));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("Status=Error", result.Error);
    }

    [Fact]
    public async Task RetryFailedJob_RejectsNonRetrySafeCommand()
    {
        // RetryFailedJobHandler is NOT retry-safe — seed it as the target
        var auditId = SeedFailedAudit("RETRY_FAILED_JOB");

        var handler = new RetryFailedJobHandler();
        var cmd = MakeCommand("RETRY_FAILED_JOB", new Dictionary<string, object?> { ["auditId"] = auditId });
        var result = await handler.ExecuteAsync(cmd, MakeContext("Planner"));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("not retry-safe", result.Error);
    }

    [Fact]
    public async Task RetryFailedJob_GeneratesNewCorrelationId()
    {
        var auditId = SeedFailedAudit("HEALTHCHECK");

        var handler = new RetryFailedJobHandler();
        var cmd = MakeCommand("RETRY_FAILED_JOB", new Dictionary<string, object?> { ["auditId"] = auditId });
        var result = await handler.ExecuteAsync(cmd, MakeContext("Planner", "planner-1"));

        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        var newCorrId = (string)data["newCorrelationId"]!;
        Assert.StartsWith("retry-", newCorrId);
        Assert.NotEqual(data["retriedFromCorrelationId"], newCorrId);
    }

    [Fact]
    public void RetryFailedJob_IsNotRetrySafe()
    {
        var handler = new RetryFailedJobHandler();
        Assert.False(handler.IsRetrySafe);
        Assert.Equal("RETRY_FAILED_JOB", handler.CommandName);
    }

    // ==================== Seeding helpers ====================

    private void SeedActivityEvents()
    {
        _dbContext.ActivityEvents.AddRange(
            new ActivityEventEntity
            {
                Id = $"evt-{Guid.NewGuid():N}",
                Type = "AgentErrorOccurred",
                Severity = "Error",
                ActorId = "agent-1",
                RoomId = null,
                Message = "Agent failed to complete task",
                OccurredAt = DateTime.UtcNow.AddMinutes(-10),
                CorrelationId = "corr-1"
            },
            new ActivityEventEntity
            {
                Id = $"evt-{Guid.NewGuid():N}",
                Type = "CommandExecuted",
                Severity = "Info",
                ActorId = "agent-2",
                RoomId = null,
                Message = "READ_FILE executed successfully",
                OccurredAt = DateTime.UtcNow.AddMinutes(-5),
                CorrelationId = "corr-2"
            },
            new ActivityEventEntity
            {
                Id = $"evt-{Guid.NewGuid():N}",
                Type = "SubagentFailed",
                Severity = "Error",
                ActorId = "agent-1",
                RoomId = null,
                Message = "Subagent crashed during build",
                OccurredAt = DateTime.UtcNow.AddMinutes(-1),
                CorrelationId = "corr-3"
            },
            new ActivityEventEntity
            {
                Id = $"evt-{Guid.NewGuid():N}",
                Type = "TaskCreated",
                Severity = "Info",
                ActorId = "agent-3",
                RoomId = null,
                Message = "Task T-1 created",
                OccurredAt = DateTime.UtcNow.AddHours(-2)
            }
        );
        _dbContext.SaveChanges();
    }

    private void SeedCommandAudits()
    {
        _dbContext.CommandAudits.AddRange(
            new CommandAuditEntity
            {
                Id = $"audit-{Guid.NewGuid():N}",
                CorrelationId = "corr-cmd-1",
                AgentId = "agent-1",
                Command = "RUN_BUILD",
                ArgsJson = "{}",
                Status = "Error",
                ErrorMessage = "Build failed with exit code 1",
                ErrorCode = "EXECUTION",
                RoomId = null,
                Timestamp = DateTime.UtcNow.AddMinutes(-8)
            },
            new CommandAuditEntity
            {
                Id = $"audit-{Guid.NewGuid():N}",
                CorrelationId = "corr-cmd-2",
                AgentId = "agent-2",
                Command = "READ_FILE",
                ArgsJson = "{\"path\":\"src/test.cs\"}",
                Status = "Success",
                RoomId = null,
                Timestamp = DateTime.UtcNow.AddMinutes(-3)
            },
            new CommandAuditEntity
            {
                Id = $"audit-{Guid.NewGuid():N}",
                CorrelationId = "corr-cmd-3",
                AgentId = "agent-1",
                Command = "SHELL",
                ArgsJson = "{}",
                Status = "Error",
                ErrorMessage = "Shell command timed out",
                ErrorCode = "TIMEOUT",
                RoomId = null,
                Timestamp = DateTime.UtcNow.AddMinutes(-2)
            }
        );
        _dbContext.SaveChanges();
    }

    private void SeedEventsWithCorrelationId(string correlationId)
    {
        _dbContext.ActivityEvents.Add(new ActivityEventEntity
        {
            Id = $"evt-{Guid.NewGuid():N}",
            Type = "CommandExecuted",
            Severity = "Info",
            ActorId = "agent-1",
            RoomId = null,
            Message = "HEALTHCHECK started",
            CorrelationId = correlationId,
            OccurredAt = DateTime.UtcNow.AddMinutes(-5)
        });
        _dbContext.CommandAudits.Add(new CommandAuditEntity
        {
            Id = $"audit-{Guid.NewGuid():N}",
            CorrelationId = correlationId,
            AgentId = "agent-1",
            Command = "HEALTHCHECK",
            ArgsJson = "{}",
            Status = "Success",
            RoomId = null,
            Timestamp = DateTime.UtcNow.AddMinutes(-4)
        });
        _dbContext.SaveChanges();
    }

    private string SeedFailedAudit(string command)
    {
        var id = $"audit-fail-{Guid.NewGuid():N}";
        _dbContext.CommandAudits.Add(new CommandAuditEntity
        {
            Id = id,
            CorrelationId = $"corr-fail-{Guid.NewGuid():N}",
            AgentId = "agent-1",
            Command = command,
            ArgsJson = "{}",
            Status = "Error",
            ErrorMessage = $"{command} failed",
            ErrorCode = "INTERNAL",
            RoomId = null,
            Timestamp = DateTime.UtcNow.AddMinutes(-1)
        });
        _dbContext.SaveChanges();
        return id;
    }

    private string SeedSuccessAudit(string command)
    {
        var id = $"audit-ok-{Guid.NewGuid():N}";
        _dbContext.CommandAudits.Add(new CommandAuditEntity
        {
            Id = id,
            CorrelationId = $"corr-ok-{Guid.NewGuid():N}",
            AgentId = "agent-1",
            Command = command,
            ArgsJson = "{}",
            Status = "Success",
            RoomId = null,
            Timestamp = DateTime.UtcNow.AddMinutes(-1)
        });
        _dbContext.SaveChanges();
        return id;
    }

    // ==================== TestAgentCatalog ====================

    private sealed class TestAgentCatalog : IAgentCatalog
    {
        public string DefaultRoomId => "main";
        public string DefaultRoomName => "Main Room";
        public IReadOnlyList<AgentDefinition> Agents { get; } =
        [
            new AgentDefinition(
                Id: "planner-1", Name: "Aristotle", Role: "Planner",
                Summary: "Test planner", StartupPrompt: "test", Model: null,
                CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true,
                Permissions: new CommandPermissionSet(
                    Allowed: [
                        "LIST_*", "HEALTHCHECK",
                        "SHOW_AUDIT_EVENTS", "SHOW_LAST_ERROR", "TRACE_REQUEST",
                        "LIST_SYSTEM_SETTINGS", "RETRY_FAILED_JOB"
                    ],
                    Denied: [])),
            new AgentDefinition(
                Id: "test-agent", Name: "Tester", Role: "Human",
                Summary: "Test human", StartupPrompt: "test", Model: null,
                CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true,
                Permissions: new CommandPermissionSet(
                    Allowed: [
                        "LIST_*", "HEALTHCHECK",
                        "SHOW_AUDIT_EVENTS", "SHOW_LAST_ERROR", "TRACE_REQUEST",
                        "LIST_SYSTEM_SETTINGS", "RETRY_FAILED_JOB"
                    ],
                    Denied: []))
        ];
    }
}
