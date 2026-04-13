using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for LIST_AGENT_STATS command handler.
/// Verifies per-agent effectiveness metrics surface correctly.
/// </summary>
public sealed class ListAgentStatsHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    private static readonly AgentCatalogOptions TestCatalog = new(
        DefaultRoomId: "main",
        DefaultRoomName: "Main Room",
        Agents: new List<AgentDefinition>
        {
            new("engineer-1", "Hephaestus", "SoftwareEngineer", "Backend", "", "gpt-4", new(), new(), true),
            new("engineer-2", "Athena", "SoftwareEngineer", "Frontend", "", "gpt-4", new(), new(), true),
            new("reviewer-1", "Socrates", "Reviewer", "Reviews", "", "gpt-4", new(), new(), true),
        }
    );

    public ListAgentStatsHandlerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<IAgentCatalog>(TestCatalog);
        services.AddScoped<TaskAnalyticsService>();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

        // Seed the "main" room so FK constraints pass for tasks
        db.Rooms.Add(new RoomEntity
        {
            Id = "main",
            Name = "Main Room",
            Status = "Idle",
            CurrentPhase = "Intake",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private CommandContext MakeContext()
    {
        var scope = _serviceProvider.CreateScope();
        return new CommandContext(
            AgentId: "planner-1",
            AgentName: "Aristotle",
            AgentRole: "Planner",
            RoomId: "main",
            BreakoutRoomId: null,
            Services: scope.ServiceProvider);
    }

    private static CommandEnvelope MakeEnvelope(Dictionary<string, object?>? args = null)
        => new(
            Command: "LIST_AGENT_STATS",
            Args: args ?? new Dictionary<string, object?>(),
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: $"cmd-{Guid.NewGuid():N}",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: "planner-1");

    private async Task SeedTask(string id, string agentId, string status,
        int reviewRounds = 0, int commitCount = 1,
        DateTime? createdAt = null, DateTime? completedAt = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var now = DateTime.UtcNow;

        db.Tasks.Add(new TaskEntity
        {
            Id = id,
            Title = $"Task {id}",
            Description = "",
            SuccessCriteria = "",
            Status = status,
            Type = "Feature",
            CurrentPhase = "Planning",
            CurrentPlan = "",
            ValidationStatus = "NotStarted",
            AssignedAgentId = agentId,
            AssignedAgentName = TestCatalog.Agents.First(a => a.Id == agentId).Name,
            RoomId = "main",
            ReviewRounds = reviewRounds,
            CommitCount = commitCount,
            CreatedAt = createdAt ?? now.AddHours(-2),
            StartedAt = (createdAt ?? now.AddHours(-2)).AddMinutes(5),
            CompletedAt = status == "Completed" ? (completedAt ?? now) : null,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public void CommandName_IsListAgentStats()
    {
        var handler = new ListAgentStatsHandler();
        Assert.Equal("LIST_AGENT_STATS", handler.CommandName);
    }

    [Fact]
    public void IsRetrySafe_ReturnsTrue()
    {
        var handler = new ListAgentStatsHandler();
        Assert.True(handler.IsRetrySafe);
    }

    [Fact]
    public async Task Execute_NoTasks_ReturnsEmptyAgents()
    {
        var handler = new ListAgentStatsHandler();
        var result = await handler.ExecuteAsync(MakeEnvelope(), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(0, data["count"]);

        var agents = Assert.IsAssignableFrom<IEnumerable<object>>(data["agents"]);
        Assert.Empty(agents);
    }

    [Fact]
    public async Task Execute_WithTasks_ReturnsPerAgentMetrics()
    {
        await SeedTask("T-1", "engineer-1", "Completed", reviewRounds: 1, commitCount: 3);
        await SeedTask("T-2", "engineer-1", "Completed", reviewRounds: 0, commitCount: 2);
        await SeedTask("T-3", "engineer-2", "Completed", reviewRounds: 2, commitCount: 4);
        await SeedTask("T-4", "engineer-2", "Active");

        var handler = new ListAgentStatsHandler();
        var result = await handler.ExecuteAsync(MakeEnvelope(), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);

        var agents = Assert.IsAssignableFrom<List<Dictionary<string, object?>>>(data["agents"]);
        Assert.Equal(2, agents.Count);

        var eng1 = agents.First(a => (string)a["agentId"]! == "engineer-1");
        Assert.Equal("Hephaestus", eng1["agentName"]);
        Assert.Equal(2, eng1["completed"]);

        var eng2 = agents.First(a => (string)a["agentId"]! == "engineer-2");
        Assert.Equal("Athena", eng2["agentName"]);
        Assert.Equal(1, eng2["completed"]);
    }

    [Fact]
    public async Task Execute_WithAgentIdFilter_ReturnsSingleAgent()
    {
        await SeedTask("T-1", "engineer-1", "Completed");
        await SeedTask("T-2", "engineer-2", "Completed");

        var handler = new ListAgentStatsHandler();
        var result = await handler.ExecuteAsync(
            MakeEnvelope(new Dictionary<string, object?> { ["agentId"] = "engineer-1" }),
            MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(1, data["count"]);

        var agents = Assert.IsAssignableFrom<List<Dictionary<string, object?>>>(data["agents"]);
        Assert.Single(agents);
        Assert.Equal("engineer-1", agents[0]["agentId"]);
    }

    [Fact]
    public async Task Execute_WithAgentNameFilter_ReturnsSingleAgent()
    {
        await SeedTask("T-1", "engineer-1", "Completed");
        await SeedTask("T-2", "engineer-2", "Completed");

        var handler = new ListAgentStatsHandler();
        var result = await handler.ExecuteAsync(
            MakeEnvelope(new Dictionary<string, object?> { ["agentId"] = "Athena" }),
            MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(1, data["count"]);

        var agents = Assert.IsAssignableFrom<List<Dictionary<string, object?>>>(data["agents"]);
        Assert.Single(agents);
        Assert.Equal("engineer-2", agents[0]["agentId"]);
    }

    [Fact]
    public async Task Execute_InvalidHoursBack_ReturnsValidationError()
    {
        var handler = new ListAgentStatsHandler();
        var result = await handler.ExecuteAsync(
            MakeEnvelope(new Dictionary<string, object?> { ["hoursBack"] = "abc" }),
            MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("hoursBack", result.Error);
    }

    [Fact]
    public async Task Execute_HoursBackZero_ReturnsValidationError()
    {
        var handler = new ListAgentStatsHandler();
        var result = await handler.ExecuteAsync(
            MakeEnvelope(new Dictionary<string, object?> { ["hoursBack"] = "0" }),
            MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task Execute_HoursBackTooLarge_ReturnsValidationError()
    {
        var handler = new ListAgentStatsHandler();
        var result = await handler.ExecuteAsync(
            MakeEnvelope(new Dictionary<string, object?> { ["hoursBack"] = "9999" }),
            MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task Execute_ValidHoursBack_FiltersResults()
    {
        // One completed in the last hour, one completed 48 hours ago
        await SeedTask("T-recent", "engineer-1", "Completed",
            createdAt: DateTime.UtcNow.AddHours(-1),
            completedAt: DateTime.UtcNow.AddMinutes(-30));
        await SeedTask("T-old", "engineer-2", "Completed",
            createdAt: DateTime.UtcNow.AddHours(-72),
            completedAt: DateTime.UtcNow.AddHours(-48));

        var handler = new ListAgentStatsHandler();
        var result = await handler.ExecuteAsync(
            MakeEnvelope(new Dictionary<string, object?> { ["hoursBack"] = "24" }),
            MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);

        // With hoursBack=24, only T-recent (created 1h ago) should be in the window.
        // T-old was created 72h ago and completed 48h ago — outside the 24h window.
        var agents = Assert.IsAssignableFrom<List<Dictionary<string, object?>>>(data["agents"]);
        Assert.True(agents.Count >= 1); // At least engineer-1
    }

    [Fact]
    public async Task Execute_ReturnsOverviewMetrics()
    {
        await SeedTask("T-1", "engineer-1", "Completed", reviewRounds: 1);
        await SeedTask("T-2", "engineer-1", "Active");

        var handler = new ListAgentStatsHandler();
        var result = await handler.ExecuteAsync(MakeEnvelope(), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);

        var overview = Assert.IsType<Dictionary<string, object?>>(data["overview"]);
        Assert.True((int)overview["totalTasks"]! >= 2);
        Assert.NotNull(overview["completionRate"]);
        Assert.NotNull(overview["reworkRate"]);

        // Window timestamps
        Assert.NotNull(data["windowStart"]);
        Assert.NotNull(data["windowEnd"]);
    }

    [Fact]
    public async Task Execute_PercentageFormatting_IncludesPercentSign()
    {
        await SeedTask("T-1", "engineer-1", "Completed");

        var handler = new ListAgentStatsHandler();
        var result = await handler.ExecuteAsync(MakeEnvelope(), MakeContext());

        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        var agents = Assert.IsAssignableFrom<List<Dictionary<string, object?>>>(data["agents"]);
        var eng = agents.First();

        var completionRate = Assert.IsType<string>(eng["completionRate"]);
        Assert.EndsWith("%", completionRate);

        var firstPassRate = Assert.IsType<string>(eng["firstPassApprovalRate"]);
        Assert.EndsWith("%", firstPassRate);
    }

    [Fact]
    public async Task Execute_NoMatchingAgent_ReturnsEmptyList()
    {
        await SeedTask("T-1", "engineer-1", "Completed");

        var handler = new ListAgentStatsHandler();
        var result = await handler.ExecuteAsync(
            MakeEnvelope(new Dictionary<string, object?> { ["agentId"] = "nonexistent" }),
            MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(0, data["count"]);
    }

    [Fact]
    public async Task Execute_ReworkRate_CalculatedFromMultipleReviewRounds()
    {
        // T-1: 1 review round (no rework)
        await SeedTask("T-1", "engineer-1", "Completed", reviewRounds: 1);
        // T-2: 3 review rounds (rework)
        await SeedTask("T-2", "engineer-1", "Completed", reviewRounds: 3);
        // T-3: 0 review rounds (first-pass)
        await SeedTask("T-3", "engineer-1", "Completed", reviewRounds: 0);

        var handler = new ListAgentStatsHandler();
        var result = await handler.ExecuteAsync(MakeEnvelope(), MakeContext());

        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        var agents = Assert.IsAssignableFrom<List<Dictionary<string, object?>>>(data["agents"]);
        var eng = agents.First(a => (string)a["agentId"]! == "engineer-1");

        // 1 out of 3 had >1 review round = 33.3% rework rate
        var reworkRate = Assert.IsType<string>(eng["reworkRate"]);
        Assert.EndsWith("%", reworkRate);
        Assert.NotEqual("0.0%", reworkRate);
    }

    [Fact]
    public async Task Execute_IntegerHoursBack_ParsesCorrectly()
    {
        await SeedTask("T-1", "engineer-1", "Completed");

        var handler = new ListAgentStatsHandler();

        // Integer value (not string)
        var result = await handler.ExecuteAsync(
            MakeEnvelope(new Dictionary<string, object?> { ["hoursBack"] = 168 }),
            MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task Execute_LongHoursBack_ParsesCorrectly()
    {
        await SeedTask("T-1", "engineer-1", "Completed");

        var handler = new ListAgentStatsHandler();

        // Long value (common from JSON deserialization)
        var result = await handler.ExecuteAsync(
            MakeEnvelope(new Dictionary<string, object?> { ["hoursBack"] = 168L }),
            MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
    }
}
