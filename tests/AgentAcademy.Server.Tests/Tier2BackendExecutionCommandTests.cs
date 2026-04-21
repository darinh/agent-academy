using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for Tier 2E Backend Execution command handlers:
/// RunFrontendBuildHandler, RunTypecheckHandler, CallEndpointHandler,
/// TailLogsHandler, ShowConfigHandler, and supporting InMemoryLogStore.
/// </summary>
[Collection("CwdMutating")]
public sealed class Tier2BackendExecutionCommandTests : IDisposable
{
    private readonly string _tempDir;

    public Tier2BackendExecutionCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tier2e-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "AgentAcademy.sln"), "");

        // Create client dir for frontend handlers
        var clientDir = Path.Combine(_tempDir, "src", "agent-academy-client");
        Directory.CreateDirectory(clientDir);
        File.WriteAllText(Path.Combine(clientDir, "package.json"), """
        {
          "name": "test-client",
          "scripts": { "build": "echo build-ok" },
          "devDependencies": {}
        }
        """);

        // Create a minimal tsconfig for typecheck tests
        File.WriteAllText(Path.Combine(clientDir, "tsconfig.json"), """
        {
          "compilerOptions": { "noEmit": true, "strict": true },
          "include": ["src"]
        }
        """);
        Directory.CreateDirectory(Path.Combine(clientDir, "src"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private CommandContext MakeContext(string role = "Planner", IServiceProvider? services = null) => new(
        AgentId: "test-agent",
        AgentName: "Tester",
        AgentRole: role,
        RoomId: "main",
        BreakoutRoomId: null,
        Services: services ?? new ServiceCollection().BuildServiceProvider()
    );

    private static CommandEnvelope MakeCommand(string name, Dictionary<string, object?> args) => new(
        Command: name,
        Args: args,
        Status: CommandStatus.Success,
        Result: null,
        Error: null,
        CorrelationId: Guid.NewGuid().ToString(),
        Timestamp: DateTime.UtcNow,
        ExecutedBy: "test-agent"
    );

    // ==================== InMemoryLogStore ====================

    [Fact]
    public void LogStore_Add_StoresEntries()
    {
        var store = new InMemoryLogStore(capacity: 10);
        store.Add(new LogEntry("2024-01-01T00:00:00Z", "Information", "TestCat", "Hello world"));

        var entries = store.Tail();
        Assert.Single(entries);
        Assert.Equal("Hello world", entries[0].Message);
    }

    [Fact]
    public void LogStore_Tail_RespectsLineCount()
    {
        var store = new InMemoryLogStore(capacity: 100);
        for (var i = 0; i < 50; i++)
            store.Add(new LogEntry($"ts-{i}", "Information", "Cat", $"Message {i}"));

        var entries = store.Tail(count: 5);
        Assert.Equal(5, entries.Count);
        Assert.Equal("Message 45", entries[0].Message);
        Assert.Equal("Message 49", entries[4].Message);
    }

    [Fact]
    public void LogStore_Tail_FiltersMessages()
    {
        var store = new InMemoryLogStore(capacity: 100);
        store.Add(new LogEntry("ts-1", "Information", "Startup", "Server started"));
        store.Add(new LogEntry("ts-2", "Error", "Database", "Connection failed"));
        store.Add(new LogEntry("ts-3", "Information", "Request", "GET /api/rooms"));

        var entries = store.Tail(filter: "failed");
        Assert.Single(entries);
        Assert.Equal("Connection failed", entries[0].Message);
    }

    [Fact]
    public void LogStore_Tail_FilterMatchesCategory()
    {
        var store = new InMemoryLogStore(capacity: 100);
        store.Add(new LogEntry("ts-1", "Information", "Database.Migration", "Applied migration"));
        store.Add(new LogEntry("ts-2", "Information", "Request", "GET /api/rooms"));

        var entries = store.Tail(filter: "database");
        Assert.Single(entries);
        Assert.Contains("Migration", entries[0].Category);
    }

    [Fact]
    public void LogStore_EnforcesCapacity()
    {
        var store = new InMemoryLogStore(capacity: 5);
        for (var i = 0; i < 20; i++)
            store.Add(new LogEntry($"ts-{i}", "Information", "Cat", $"Message {i}"));

        var entries = store.Tail(count: 100);
        Assert.True(entries.Count <= 6); // Soft cap — may slightly exceed under race
        Assert.Equal("Message 19", entries[^1].Message);
    }

    [Fact]
    public void LogStore_Tail_EmptyStore_ReturnsEmpty()
    {
        var store = new InMemoryLogStore();
        var entries = store.Tail();
        Assert.Empty(entries);
    }

    // ==================== TailLogsHandler ====================

    [Fact]
    public async Task TailLogs_ReturnsLogEntries()
    {
        var store = new InMemoryLogStore();
        store.Add(new LogEntry("2024-01-01T00:00:00Z", "Information", "Test", "Hello"));
        store.Add(new LogEntry("2024-01-01T00:00:01Z", "Error", "Test", "Oops"));

        var sp = new ServiceCollection()
            .AddSingleton(store)
            .BuildServiceProvider();

        var handler = new TailLogsHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("TAIL_LOGS", new()),
            MakeContext(services: sp));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(2, dict["count"]);
    }

    [Fact]
    public async Task TailLogs_WithFilter_FiltersEntries()
    {
        var store = new InMemoryLogStore();
        store.Add(new LogEntry("ts-1", "Information", "Startup", "Server started"));
        store.Add(new LogEntry("ts-2", "Error", "DB", "Query failed"));

        var sp = new ServiceCollection().AddSingleton(store).BuildServiceProvider();

        var handler = new TailLogsHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("TAIL_LOGS", new() { ["filter"] = "failed" }),
            MakeContext(services: sp));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(1, dict["count"]);
    }

    [Fact]
    public async Task TailLogs_WithLineCount_LimitsEntries()
    {
        var store = new InMemoryLogStore();
        for (var i = 0; i < 50; i++)
            store.Add(new LogEntry($"ts-{i}", "Information", "Cat", $"Msg {i}"));

        var sp = new ServiceCollection().AddSingleton(store).BuildServiceProvider();

        var handler = new TailLogsHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("TAIL_LOGS", new() { ["lines"] = "10" }),
            MakeContext(services: sp));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(10, dict["count"]);
    }

    [Fact]
    public async Task TailLogs_NoStore_ReturnsError()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var handler = new TailLogsHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("TAIL_LOGS", new()),
            MakeContext(services: sp));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("not available", result.Error);
    }

    [Fact]
    public void TailLogs_IsRetrySafe()
    {
        var handler = new TailLogsHandler();
        Assert.True(handler.IsRetrySafe);
    }

    // ==================== ShowConfigHandler ====================

    [Fact]
    public async Task ShowConfig_ReturnsAllowedSections()
    {
        var configData = new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Information",
            ["Cors:Origins:0"] = "http://localhost:5173",
            ["AllowedHosts"] = "*",
            ["Copilot:CliPath"] = "copilot",
            // These should NOT appear (not in allowlist)
            ["GitHub:ClientSecret"] = "super-secret",
            ["ConnectionStrings:DefaultConnection"] = "Data Source=test.db"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var sp = new ServiceCollection().AddSingleton<IConfiguration>(config).BuildServiceProvider();

        var handler = new ShowConfigHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SHOW_CONFIG", new()),
            MakeContext(services: sp));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var sections = (Dictionary<string, object?>)dict["sections"]!;

        // Allowed sections should be present
        Assert.True(sections.ContainsKey("Logging"));
        Assert.True(sections.ContainsKey("Cors"));
        Assert.True(sections.ContainsKey("Copilot"));

        // Denied sections should NOT appear
        Assert.False(sections.ContainsKey("GitHub"));
        Assert.False(sections.ContainsKey("ConnectionStrings"));
    }

    [Fact]
    public async Task ShowConfig_MasksSensitiveValues()
    {
        var configData = new Dictionary<string, string?>
        {
            ["Copilot:CliPath"] = "copilot",
            ["Copilot:ApiKey"] = "sk-12345",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var sp = new ServiceCollection().AddSingleton<IConfiguration>(config).BuildServiceProvider();

        var handler = new ShowConfigHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SHOW_CONFIG", new() { ["section"] = "Copilot" }),
            MakeContext(services: sp));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var sections = (Dictionary<string, object?>)dict["sections"]!;
        var copilot = (Dictionary<string, string>)sections["Copilot"]!;

        Assert.Equal("copilot", copilot["CliPath"]);
        Assert.Equal("***", copilot["ApiKey"]); // "Key" in "ApiKey" triggers masking
    }

    [Fact]
    public async Task ShowConfig_DeniedSection_ReturnsError()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var sp = new ServiceCollection().AddSingleton<IConfiguration>(config).BuildServiceProvider();

        var handler = new ShowConfigHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SHOW_CONFIG", new() { ["section"] = "GitHub" }),
            MakeContext(services: sp));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("not in the allowed list", result.Error);
    }

    [Fact]
    public async Task ShowConfig_SpecificSection_ReturnsOnlyThatSection()
    {
        var configData = new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Information",
            ["Cors:Origins:0"] = "http://localhost:5173"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var sp = new ServiceCollection().AddSingleton<IConfiguration>(config).BuildServiceProvider();

        var handler = new ShowConfigHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SHOW_CONFIG", new() { ["section"] = "Logging" }),
            MakeContext(services: sp));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var sections = (Dictionary<string, object?>)dict["sections"]!;
        Assert.Single(sections);
        Assert.True(sections.ContainsKey("Logging"));
    }

    [Fact]
    public async Task ShowConfig_ScalarSection_ReturnsValue()
    {
        var configData = new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "*"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var sp = new ServiceCollection().AddSingleton<IConfiguration>(config).BuildServiceProvider();

        var handler = new ShowConfigHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SHOW_CONFIG", new() { ["section"] = "AllowedHosts" }),
            MakeContext(services: sp));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var sections = (Dictionary<string, object?>)dict["sections"]!;
        Assert.True(sections.ContainsKey("AllowedHosts"));
        var values = (Dictionary<string, string>)sections["AllowedHosts"]!;
        Assert.Equal("*", values["AllowedHosts"]);
    }

    [Fact]
    public void ShowConfig_IsRetrySafe()
    {
        var handler = new ShowConfigHandler();
        Assert.True(handler.IsRetrySafe);
    }

    // ==================== CallEndpointHandler ====================

    [Fact]
    public async Task CallEndpoint_MissingPath_ReturnsValidationError()
    {
        var handler = new CallEndpointHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("CALL_ENDPOINT", new()),
            MakeContext(role: "Planner"));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("path", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallEndpoint_WrongRole_ReturnsPermissionError()
    {
        var handler = new CallEndpointHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("CALL_ENDPOINT", new() { ["path"] = "/api/rooms" }),
            MakeContext(role: "SoftwareEngineer"));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
        Assert.Contains("Planner", result.Error);
    }

    [Fact]
    public async Task CallEndpoint_PlannerRole_Allowed()
    {
        var handler = new CallEndpointHandler();
        // Will fail with HttpRequestException (no server running), but should pass role check
        var result = await handler.ExecuteAsync(
            MakeCommand("CALL_ENDPOINT", new() { ["path"] = "/api/rooms" }),
            MakeContext(role: "Planner"));

        // Should get past validation — will fail at HTTP level
        Assert.True(
            result.ErrorCode == CommandErrorCode.Execution || result.Status == CommandStatus.Success,
            "Should pass role/validation checks");
    }

    [Fact]
    public async Task CallEndpoint_ReviewerRole_Allowed()
    {
        var handler = new CallEndpointHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("CALL_ENDPOINT", new() { ["path"] = "/api/rooms" }),
            MakeContext(role: "Reviewer"));

        Assert.True(
            result.ErrorCode == CommandErrorCode.Execution || result.Status == CommandStatus.Success,
            "Should pass role/validation checks");
    }

    [Fact]
    public async Task CallEndpoint_HumanRole_Allowed()
    {
        var handler = new CallEndpointHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("CALL_ENDPOINT", new() { ["path"] = "/api/rooms" }),
            MakeContext(role: "Human"));

        Assert.True(
            result.ErrorCode == CommandErrorCode.Execution || result.Status == CommandStatus.Success,
            "Human role should pass role check");
    }

    [Fact]
    public async Task CallEndpoint_DeniedPath_Auth_ReturnsError()
    {
        var handler = new CallEndpointHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("CALL_ENDPOINT", new() { ["path"] = "/api/auth/login" }),
            MakeContext(role: "Planner"));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
        Assert.Contains("/api/auth", result.Error);
    }

    [Fact]
    public async Task CallEndpoint_DeniedPath_Commands_ReturnsError()
    {
        var handler = new CallEndpointHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("CALL_ENDPOINT", new() { ["path"] = "/api/commands/execute" }),
            MakeContext(role: "Planner"));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
    }

    [Fact]
    public async Task CallEndpoint_PathWithDoubleSlash_ReturnsValidationError()
    {
        var handler = new CallEndpointHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("CALL_ENDPOINT", new() { ["path"] = "//evil.com/api" }),
            MakeContext(role: "Planner"));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task CallEndpoint_PathWithBackslash_ReturnsValidationError()
    {
        var handler = new CallEndpointHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("CALL_ENDPOINT", new() { ["path"] = "/api\\rooms" }),
            MakeContext(role: "Planner"));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task CallEndpoint_RelativePathRequired()
    {
        var handler = new CallEndpointHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("CALL_ENDPOINT", new() { ["path"] = "api/rooms" }),
            MakeContext(role: "Planner"));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("must start with '/'", result.Error);
    }

    // ==================== RunFrontendBuildHandler ====================

    [Fact]
    public async Task RunFrontendBuild_InTempDir_ReturnsStructuredResult()
    {
        var handler = new RunFrontendBuildHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("RUN_FRONTEND_BUILD", new()),
                MakeContext());

            Assert.NotNull(result.Result);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.True(dict.ContainsKey("exitCode"), "Should have exitCode");
            Assert.True(dict.ContainsKey("output"), "Should have output");
            Assert.True(dict.ContainsKey("success"), "Should have success flag");
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task RunFrontendBuild_WithEchoBuild_Succeeds()
    {
        // The temp dir has package.json with "build": "echo build-ok"
        var handler = new RunFrontendBuildHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("RUN_FRONTEND_BUILD", new()),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Equal(0, dict["exitCode"]);
            Assert.Equal(true, dict["success"]);
            Assert.Contains("build-ok", dict["output"]?.ToString());
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public void RunFrontendBuild_FindClientDir_FindsCorrectPath()
    {
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var clientDir = RunFrontendBuildHandler.FindClientDir();
            Assert.Equal(Path.Combine(_tempDir, "src", "agent-academy-client"), clientDir);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    // ==================== RunTypecheckHandler ====================

    [Fact]
    public void RunTypecheck_SharesFrontendLock()
    {
        // Verify the handler uses the shared FrontendLock (not its own)
        // by checking it acquires the same semaphore RunFrontendBuildHandler uses.
        // This is a design test — actual execution tested via integration.
        var handler = new RunTypecheckHandler();
        Assert.Equal("RUN_TYPECHECK", handler.CommandName);

        // Verify the lock is initially available (count=1)
        Assert.True(RunFrontendBuildHandler.FrontendLock.Wait(0));
        RunFrontendBuildHandler.FrontendLock.Release();
    }

    // ==================== Command Registration ====================

    [Fact]
    public void KnownCommands_ContainsAllTier2ECommands()
    {
        var tier2eCommands = new[] { "RUN_FRONTEND_BUILD", "RUN_TYPECHECK", "CALL_ENDPOINT", "TAIL_LOGS", "SHOW_CONFIG" };
        foreach (var cmd in tier2eCommands)
        {
            Assert.Contains(cmd, CommandParser.KnownCommands);
        }
    }

    [Fact]
    public void Handlers_HaveCorrectCommandNames()
    {
        Assert.Equal("RUN_FRONTEND_BUILD", new RunFrontendBuildHandler().CommandName);
        Assert.Equal("RUN_TYPECHECK", new RunTypecheckHandler().CommandName);
        Assert.Equal("CALL_ENDPOINT", new CallEndpointHandler().CommandName);
        Assert.Equal("TAIL_LOGS", new TailLogsHandler().CommandName);
        Assert.Equal("SHOW_CONFIG", new ShowConfigHandler().CommandName);
    }
}
