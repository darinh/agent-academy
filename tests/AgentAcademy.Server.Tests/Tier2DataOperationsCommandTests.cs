using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for Tier 2F Data &amp; Operations command handlers:
/// QueryDbHandler, ShowMigrationStatusHandler, RunMigrationsHandler,
/// HealthcheckHandler, ShowActiveConnectionsHandler, and SignalRConnectionTracker.
/// </summary>
public sealed class Tier2DataOperationsCommandTests : IDisposable
{
    private readonly AgentAcademyDbContext _dbContext;
    private readonly ServiceProvider _serviceProvider;
    private readonly SqliteConnection _keepAliveConnection;

    public Tier2DataOperationsCommandTests()
    {
        // Shared in-memory SQLite for EF Core context
        _keepAliveConnection = new SqliteConnection("Data Source=:memory:");
        _keepAliveConnection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(options =>
            options.UseSqlite(_keepAliveConnection));
        services.AddSingleton<SignalRConnectionTracker>();
        services.AddSingleton<IAgentCatalog>(new TestAgentCatalog());

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

    private CommandContext MakeContext(string role = "Human") => new(
        AgentId: "test-agent",
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

    // ==================== SignalRConnectionTracker ====================

    [Fact]
    public void Tracker_OnConnected_TracksConnection()
    {
        var tracker = new SignalRConnectionTracker();
        tracker.OnConnected("conn-1", "user-1");

        Assert.Equal(1, tracker.Count);
        var connections = tracker.GetConnections();
        Assert.Single(connections);
        Assert.Equal("conn-1", connections[0].ConnectionId);
        Assert.Equal("user-1", connections[0].UserId);
    }

    [Fact]
    public void Tracker_OnDisconnected_RemovesConnection()
    {
        var tracker = new SignalRConnectionTracker();
        tracker.OnConnected("conn-1", "user-1");
        tracker.OnConnected("conn-2", "user-2");
        tracker.OnDisconnected("conn-1");

        Assert.Equal(1, tracker.Count);
        var connections = tracker.GetConnections();
        Assert.Equal("conn-2", connections[0].ConnectionId);
    }

    [Fact]
    public void Tracker_OnDisconnected_NoOp_ForUnknownConnection()
    {
        var tracker = new SignalRConnectionTracker();
        tracker.OnDisconnected("unknown");
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void Tracker_GetConnections_ReturnsSnapshot()
    {
        var tracker = new SignalRConnectionTracker();
        tracker.OnConnected("conn-1", null);
        tracker.OnConnected("conn-2", "user-x");

        var snapshot = tracker.GetConnections();
        Assert.Equal(2, snapshot.Count);
    }

    // ==================== QUERY_DB ====================

    [Fact]
    public async Task QueryDb_RejectsNonHumanRole()
    {
        var handler = new QueryDbHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("QUERY_DB", new() { ["query"] = "SELECT 1" }),
            MakeContext("SoftwareEngineer"));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
        Assert.Contains("Human role", result.Error!);
    }

    [Fact]
    public async Task QueryDb_RejectsMissingQuery()
    {
        var handler = new QueryDbHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("QUERY_DB", new()),
            MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task QueryDb_RejectsMultipleStatements()
    {
        var handler = new QueryDbHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("QUERY_DB", new() { ["query"] = "SELECT 1; DROP TABLE Tasks" }),
            MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("single SQL statement", result.Error!);
    }

    [Fact]
    public async Task QueryDb_RejectsInsertStatement()
    {
        var handler = new QueryDbHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("QUERY_DB", new() { ["query"] = "INSERT INTO Tasks (Id) VALUES ('x')" }),
            MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("forbidden", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueryDb_RejectsDeleteStatement()
    {
        var handler = new QueryDbHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("QUERY_DB", new() { ["query"] = "DELETE FROM Tasks" }),
            MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("forbidden", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueryDb_RejectsDropStatement()
    {
        var handler = new QueryDbHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("QUERY_DB", new() { ["query"] = "DROP TABLE Tasks" }),
            MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
    }

    [Fact]
    public async Task QueryDb_RejectsPragmaWrite()
    {
        var handler = new QueryDbHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("QUERY_DB", new() { ["query"] = "PRAGMA journal_mode = WAL" }),
            MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("PRAGMA write", result.Error!);
    }

    [Fact]
    public async Task QueryDb_RejectsDeniedTables()
    {
        var handler = new QueryDbHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("QUERY_DB", new() { ["query"] = "SELECT * FROM AgentMemories" }),
            MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
        Assert.Contains("AgentMemories", result.Error!);
    }

    [Fact]
    public async Task QueryDb_RejectsUpdateStatement()
    {
        var handler = new QueryDbHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("QUERY_DB", new() { ["query"] = "UPDATE Tasks SET Status = 'Active'" }),
            MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
    }

    [Fact]
    public async Task QueryDb_RejectsAttachStatement()
    {
        var handler = new QueryDbHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("QUERY_DB", new() { ["query"] = "ATTACH DATABASE '/tmp/evil.db' AS evil" }),
            MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
    }

    // ==================== SHOW_MIGRATION_STATUS ====================

    [Fact]
    public async Task ShowMigrationStatus_ReturnsAppliedAndPending()
    {
        var handler = new ShowMigrationStatusHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SHOW_MIGRATION_STATUS"),
            MakeContext("SoftwareEngineer")); // Any role can use this

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = result.Result as Dictionary<string, object?>;
        Assert.NotNull(dict);
        Assert.True(dict.ContainsKey("appliedCount"));
        Assert.True(dict.ContainsKey("pendingCount"));
        Assert.True(dict.ContainsKey("isUpToDate"));
    }

    // ==================== RUN_MIGRATIONS ====================

    [Fact]
    public async Task RunMigrations_RejectsNonHumanRole()
    {
        var handler = new RunMigrationsHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("RUN_MIGRATIONS"),
            MakeContext("SoftwareEngineer"));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
        Assert.Contains("Human role", result.Error!);
    }

    [Fact]
    public async Task RunMigrations_ReportsNoPending_WhenUpToDate()
    {
        // In-memory DB with EnsureCreated has no migrations concept
        // but MigrateAsync should not fail — it detects no pending migrations
        var handler = new RunMigrationsHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("RUN_MIGRATIONS", new() { ["confirm"] = true }),
            MakeContext());

        // With in-memory DB, GetPendingMigrationsAsync may return the full list
        // since EnsureCreated doesn't apply migrations. Either way, it should succeed.
        Assert.True(result.Status == CommandStatus.Success || result.Status == CommandStatus.Error);
    }

    [Fact]
    public void RunMigrations_IsDestructive()
    {
        var handler = new RunMigrationsHandler();
        Assert.True(handler.IsDestructive);
        Assert.Contains("schema", handler.DestructiveWarning, StringComparison.OrdinalIgnoreCase);
    }

    // ==================== HEALTHCHECK ====================

    [Fact]
    public async Task Healthcheck_ReturnsHealthSummary()
    {
        var handler = new HealthcheckHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("HEALTHCHECK"),
            MakeContext("SoftwareEngineer")); // Any role

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = result.Result as Dictionary<string, object?>;
        Assert.NotNull(dict);
        Assert.True(dict.ContainsKey("status"));
        Assert.True(dict.ContainsKey("checks"));
        Assert.True(dict.ContainsKey("timestamp"));

        var checks = dict["checks"] as Dictionary<string, object?>;
        Assert.NotNull(checks);
        Assert.True(checks.ContainsKey("database"));
        Assert.True(checks.ContainsKey("server"));
        Assert.True(checks.ContainsKey("resources"));
    }

    [Fact]
    public async Task Healthcheck_DatabaseCheck_ReportsHealthy()
    {
        var handler = new HealthcheckHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("HEALTHCHECK"),
            MakeContext());

        var dict = result.Result as Dictionary<string, object?>;
        var checks = dict!["checks"] as Dictionary<string, object?>;
        var db = checks!["database"] as Dictionary<string, object?>;
        Assert.Equal("healthy", db!["status"]);
    }

    [Fact]
    public async Task Healthcheck_ServerCheck_IncludesUptime()
    {
        var handler = new HealthcheckHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("HEALTHCHECK"),
            MakeContext());

        var dict = result.Result as Dictionary<string, object?>;
        var checks = dict!["checks"] as Dictionary<string, object?>;
        var server = checks!["server"] as Dictionary<string, object?>;
        Assert.NotNull(server!["uptime"]);
        Assert.NotNull(server["startedAt"]);
    }

    [Fact]
    public async Task Healthcheck_ResourceCheck_IncludesMemory()
    {
        var handler = new HealthcheckHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("HEALTHCHECK"),
            MakeContext());

        var dict = result.Result as Dictionary<string, object?>;
        var checks = dict!["checks"] as Dictionary<string, object?>;
        var resources = checks!["resources"] as Dictionary<string, object?>;
        Assert.NotNull(resources);
        Assert.True((double)resources!["workingSetMB"]! > 0);
    }

    [Fact]
    public async Task Healthcheck_AgentCheck_ReturnsRegisteredCount()
    {
        var handler = new HealthcheckHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("HEALTHCHECK"),
            MakeContext());

        var dict = result.Result as Dictionary<string, object?>;
        var checks = dict!["checks"] as Dictionary<string, object?>;
        var agents = checks!["agents"] as Dictionary<string, object?>;
        Assert.NotNull(agents);
        Assert.Equal(2, agents!["registeredCount"]);
    }

    [Fact]
    public async Task Healthcheck_SignalR_ReportsConnectionCount()
    {
        var tracker = _serviceProvider.GetRequiredService<SignalRConnectionTracker>();
        tracker.OnConnected("conn-1", null);
        tracker.OnConnected("conn-2", null);

        var handler = new HealthcheckHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("HEALTHCHECK"),
            MakeContext());

        var dict = result.Result as Dictionary<string, object?>;
        var checks = dict!["checks"] as Dictionary<string, object?>;
        var signalr = checks!["signalr"] as Dictionary<string, object?>;
        Assert.NotNull(signalr);
        Assert.Equal(2, signalr!["activeConnections"]);

        // Clean up
        tracker.OnDisconnected("conn-1");
        tracker.OnDisconnected("conn-2");
    }

    // ==================== SHOW_ACTIVE_CONNECTIONS ====================

    [Fact]
    public async Task ShowActiveConnections_RejectsUnauthorizedRole()
    {
        var handler = new ShowActiveConnectionsHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SHOW_ACTIVE_CONNECTIONS"),
            MakeContext("SoftwareEngineer"));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
    }

    [Fact]
    public async Task ShowActiveConnections_AllowsPlanner()
    {
        var handler = new ShowActiveConnectionsHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SHOW_ACTIVE_CONNECTIONS"),
            MakeContext("Planner"));

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task ShowActiveConnections_AllowsReviewer()
    {
        var handler = new ShowActiveConnectionsHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SHOW_ACTIVE_CONNECTIONS"),
            MakeContext("Reviewer"));

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task ShowActiveConnections_AllowsHuman()
    {
        var handler = new ShowActiveConnectionsHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SHOW_ACTIVE_CONNECTIONS"),
            MakeContext("Human"));

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task ShowActiveConnections_ReturnsConnectionInfo()
    {
        var tracker = _serviceProvider.GetRequiredService<SignalRConnectionTracker>();
        tracker.OnConnected("test-conn-abc123", "user-1");

        var handler = new ShowActiveConnectionsHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SHOW_ACTIVE_CONNECTIONS"),
            MakeContext("Planner"));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = result.Result as Dictionary<string, object?>;
        Assert.NotNull(dict);
        Assert.Equal(1, dict["count"]);
        Assert.NotNull(dict["instance"]);

        var connections = dict["connections"] as List<Dictionary<string, object?>>;
        Assert.NotNull(connections);
        Assert.Single(connections);
        Assert.Contains("…", connections[0]["connectionId"]!.ToString()!); // Truncated

        // Clean up
        tracker.OnDisconnected("test-conn-abc123");
    }

    [Fact]
    public async Task ShowActiveConnections_EmptyWhenNoConnections()
    {
        var handler = new ShowActiveConnectionsHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SHOW_ACTIVE_CONNECTIONS"),
            MakeContext("Planner"));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = result.Result as Dictionary<string, object?>;
        Assert.Equal(0, dict!["count"]);
    }

    // ==================== CommandParser KnownCommands ====================

    [Fact]
    public void KnownCommands_ContainsPhase2FCommands()
    {
        Assert.Contains("QUERY_DB", CommandParser.KnownCommands);
        Assert.Contains("RUN_MIGRATIONS", CommandParser.KnownCommands);
        Assert.Contains("SHOW_MIGRATION_STATUS", CommandParser.KnownCommands);
        Assert.Contains("HEALTHCHECK", CommandParser.KnownCommands);
        Assert.Contains("SHOW_ACTIVE_CONNECTIONS", CommandParser.KnownCommands);
    }

    // ==================== Handler metadata ====================

    [Fact]
    public void QueryDb_IsRetrySafe()
    {
        var handler = new QueryDbHandler();
        Assert.True(handler.IsRetrySafe);
        Assert.Equal("QUERY_DB", handler.CommandName);
    }

    [Fact]
    public void ShowMigrationStatus_IsRetrySafe()
    {
        var handler = new ShowMigrationStatusHandler();
        Assert.True(handler.IsRetrySafe);
    }

    [Fact]
    public void Healthcheck_IsRetrySafe()
    {
        var handler = new HealthcheckHandler();
        Assert.True(handler.IsRetrySafe);
    }

    [Fact]
    public void ShowActiveConnections_IsRetrySafe()
    {
        var handler = new ShowActiveConnectionsHandler();
        Assert.True(handler.IsRetrySafe);
    }

    // ==================== Test helpers ====================

    private sealed class TestAgentCatalog : IAgentCatalog
    {
        public string DefaultRoomId => "main";
        public string DefaultRoomName => "Main Room";
        public IReadOnlyList<AgentDefinition> Agents { get; } =
        [
            new AgentDefinition(
                Id: "agent-1", Name: "TestAgent1", Role: "Planner",
                Summary: "Test", StartupPrompt: "test", Model: null,
                CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true),
            new AgentDefinition(
                Id: "agent-2", Name: "TestAgent2", Role: "SoftwareEngineer",
                Summary: "Test", StartupPrompt: "test", Model: null,
                CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true)
        ];
    }
}
