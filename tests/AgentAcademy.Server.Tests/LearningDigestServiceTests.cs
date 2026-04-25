using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

public class LearningDigestServiceTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly SqliteConnection _connection;
    private readonly IAgentExecutor _executor;
    private readonly AgentCatalogOptions _catalog;
    private readonly LearningDigestService _service;

    public LearningDigestServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _executor = Substitute.For<IAgentExecutor>();
        _executor.IsFullyOperational.Returns(true);

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Collaboration Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "planner-1", Name: "Aristotle", Role: "Planner",
                    Summary: "Planner", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: ["code", "shell"], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["REMEMBER", "LIST_*"], [])),
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: ["code", "shell"], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["REMEMBER"], []))
            ]
        );

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton(_catalog);
        services.AddSingleton<IAgentCatalog>(_catalog);
        services.AddSingleton(_executor);
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<IActivityBroadcaster>(sp => sp.GetRequiredService<ActivityBroadcaster>());
        services.AddSingleton<MessageBroadcaster>();
        services.AddSingleton<IMessageBroadcaster>(sp => sp.GetRequiredService<MessageBroadcaster>());
        services.AddScoped<ActivityPublisher>();
        services.AddScoped<IActivityPublisher>(sp => sp.GetRequiredService<ActivityPublisher>());
        services.AddScoped<TaskQueryService>();
        services.AddScoped<ITaskQueryService>(sp => sp.GetRequiredService<TaskQueryService>());
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<ITaskLifecycleService>(sp => sp.GetRequiredService<TaskLifecycleService>());
        services.AddScoped<TaskDependencyService>();
        services.AddScoped<ITaskDependencyService>(sp => sp.GetRequiredService<TaskDependencyService>());
        services.AddScoped<MessageService>();
        services.AddScoped<IMessageService>(sp => sp.GetRequiredService<MessageService>());
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<ISystemSettingsService>(sp => sp.GetRequiredService<SystemSettingsService>());
        services.AddSingleton<CommandRateLimiter>();
        services.AddSingleton<CommandPipeline>();
        services.AddSingleton<CommandParser>();
        services.AddSingleton<ICommandHandler, Server.Commands.Handlers.RememberHandler>();
        services.AddSingleton<CommandAuthorizer>();
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        _service = new LearningDigestService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _catalog,
            _executor, new TestDoubles.NoOpWatchdogAgentRunner(_executor),
            _serviceProvider.GetRequiredService<CommandPipeline>(),
            NullLogger<LearningDigestService>.Instance);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

        // Set up FTS5 for memory tests
        db.Database.ExecuteSqlRaw("""
            CREATE VIRTUAL TABLE IF NOT EXISTS agent_memories_fts
            USING fts5(key, value, content='agent_memories', content_rowid='rowid');
        """);
        db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS agent_memories_ai AFTER INSERT ON agent_memories BEGIN
                INSERT INTO agent_memories_fts(rowid, key, value)
                VALUES (new.rowid, new.Key, new.Value);
            END;
        """);
        db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS agent_memories_ad AFTER DELETE ON agent_memories BEGIN
                INSERT INTO agent_memories_fts(agent_memories_fts, rowid, key, value)
                VALUES ('delete', old.rowid, old.Key, old.Value);
            END;
        """);
        db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS agent_memories_au AFTER UPDATE ON agent_memories BEGIN
                INSERT INTO agent_memories_fts(agent_memories_fts, rowid, key, value)
                VALUES ('delete', old.rowid, old.Key, old.Value);
                INSERT INTO agent_memories_fts(rowid, key, value)
                VALUES (new.rowid, new.Key, new.Value);
            END;
        """);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private async Task<string> SeedRetrospective(string agentName = "Hephaestus", string content = "Learned something")
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var taskId = Guid.NewGuid().ToString("N");
        db.Tasks.Add(new TaskEntity
        {
            Id = taskId,
            Title = $"Task for {agentName}",
            Description = "Test task",
            Status = nameof(TaskStatus.Completed),
            Type = nameof(TaskType.Feature),
            AssignedAgentId = "engineer-1",
            CreatedAt = DateTime.UtcNow.AddHours(-2),
            CompletedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var commentId = Guid.NewGuid().ToString("N");
        db.TaskComments.Add(new TaskCommentEntity
        {
            Id = commentId,
            TaskId = taskId,
            AgentId = "engineer-1",
            AgentName = agentName,
            CommentType = nameof(TaskCommentType.Retrospective),
            Content = content,
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
        return commentId;
    }

    private async Task SetDigestThreshold(int threshold)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var setting = await db.SystemSettings.FindAsync(SystemSettingsService.DigestThresholdKey);
        if (setting is not null)
        {
            setting.Value = threshold.ToString();
        }
        else
        {
            db.SystemSettings.Add(new SystemSettingEntity
            {
                Key = SystemSettingsService.DigestThresholdKey,
                Value = threshold.ToString()
            });
        }
        await db.SaveChangesAsync();
    }

    // ── Tests ───────────────────────────────────────────────────

    [Fact]
    public async Task TryGenerate_NoRetrospectives_ReturnsNull()
    {
        var result = await _service.TryGenerateDigestAsync(force: true);
        Assert.Null(result);
    }

    [Fact]
    public async Task TryGenerate_BelowThreshold_Skips()
    {
        await SetDigestThreshold(5);
        await SeedRetrospective();
        await SeedRetrospective();

        var result = await _service.TryGenerateDigestAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task TryGenerate_ForceBypassesThreshold()
    {
        await SeedRetrospective();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Cross-cutting insight discovered.");

        var result = await _service.TryGenerateDigestAsync(force: true);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task TryGenerate_MeetsThreshold_Generates()
    {
        await SetDigestThreshold(3);
        for (int i = 0; i < 3; i++)
            await SeedRetrospective(content: $"Retrospective #{i}");

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Cross-cutting patterns identified.");

        var result = await _service.TryGenerateDigestAsync();
        Assert.NotNull(result);
    }

    [Fact]
    public async Task TryGenerate_UsesRestrictedPermissions()
    {
        await SeedRetrospective();
        AgentDefinition? capturedAgent = null;

        _executor.RunAsync(
            Arg.Do<AgentDefinition>(a => capturedAgent = a),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Digest summary.");

        await _service.TryGenerateDigestAsync(force: true);

        Assert.NotNull(capturedAgent);
        Assert.Single(capturedAgent!.Permissions!.Allowed);
        Assert.Equal("REMEMBER", capturedAgent.Permissions.Allowed[0]);
        Assert.Empty(capturedAgent.EnabledTools);
    }

    [Fact]
    public async Task TryGenerate_UsesSyntheticDigestRoomId()
    {
        await SeedRetrospective();
        string? capturedRoomId = null;

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(),
            Arg.Do<string?>(r => capturedRoomId = r),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Digest summary.");

        await _service.TryGenerateDigestAsync(force: true);

        Assert.NotNull(capturedRoomId);
        Assert.StartsWith("digest:", capturedRoomId);
    }

    [Fact]
    public async Task TryGenerate_SavesSummaryToDigest()
    {
        await SeedRetrospective();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Team consistently struggles with error handling.");

        var digestId = await _service.TryGenerateDigestAsync(force: true);
        Assert.NotNull(digestId);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var digest = await db.LearningDigests.FindAsync(digestId!.Value);

        Assert.NotNull(digest);
        Assert.Equal("Completed", digest.Status);
        Assert.Contains("error handling", digest.Summary);
        Assert.Equal(1, digest.RetrospectivesProcessed);
    }

    [Fact]
    public async Task TryGenerate_ProcessesRememberCommands()
    {
        await SeedRetrospective();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("""
                REMEMBER:
                  category: shared
                  key: cross-cutting-error-handling
                  value: Always validate error response shapes match ProblemDetails format.

                Team needs better error handling consistency.
                """);

        var digestId = await _service.TryGenerateDigestAsync(force: true);
        Assert.NotNull(digestId);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var memory = await db.AgentMemories
            .FirstOrDefaultAsync(m => m.Key == "cross-cutting-error-handling");
        Assert.NotNull(memory);
        Assert.Contains("ProblemDetails", memory.Value);

        var digest = await db.LearningDigests.FindAsync(digestId!.Value);
        Assert.Equal(1, digest!.MemoriesCreated);
    }

    [Fact]
    public async Task TryGenerate_EmptyResponse_MarksCompleted()
    {
        await SeedRetrospective();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("");

        var digestId = await _service.TryGenerateDigestAsync(force: true);
        Assert.NotNull(digestId);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var digest = await db.LearningDigests.FindAsync(digestId!.Value);

        Assert.NotNull(digest);
        Assert.Equal("Completed", digest.Status);
    }

    [Fact]
    public async Task TryGenerate_ExecutorFailure_ReleasesRetrospectives()
    {
        var commentId = await SeedRetrospective();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns<string>(x => throw new InvalidOperationException("LLM connection failed"));

        var result = await _service.TryGenerateDigestAsync(force: true);
        Assert.Null(result); // outer catch returns null

        // Retrospective should be available for retry
        var undigested = await _service.GetUndigestedRetrospectivesAsync();
        Assert.Single(undigested);
        Assert.Equal(commentId, undigested[0].CommentId);

        // Digest should be marked Failed with no sources
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var digest = await db.LearningDigests.FirstOrDefaultAsync();
        Assert.NotNull(digest);
        Assert.Equal("Failed", digest.Status);
    }

    [Fact]
    public async Task TryGenerate_PlannerNotFound_ReturnsNull()
    {
        // Use a catalog without the planner
        var catalogNoPlanner = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["REMEMBER"], []))
            ]
        );

        var service = new LearningDigestService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            catalogNoPlanner,
            _executor, new TestDoubles.NoOpWatchdogAgentRunner(_executor),
            _serviceProvider.GetRequiredService<CommandPipeline>(),
            NullLogger<LearningDigestService>.Instance);

        await SeedRetrospective();
        var result = await service.TryGenerateDigestAsync(force: true);
        Assert.Null(result);
    }

    [Fact]
    public async Task TryGenerate_ClaimsRetrospectives_ExcludesFromNext()
    {
        await SeedRetrospective(content: "First retro");

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Digest complete.");

        await _service.TryGenerateDigestAsync(force: true);

        // After successful digest, the retrospective should not be available
        var undigested = await _service.GetUndigestedRetrospectivesAsync();
        Assert.Empty(undigested);
    }

    [Fact]
    public async Task TryGenerate_InvalidatesSessionAfter()
    {
        await SeedRetrospective();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Summary.");

        await _service.TryGenerateDigestAsync(force: true);

        await _executor.Received(1).InvalidateSessionAsync(
            "planner-1", Arg.Is<string>(s => s.StartsWith("digest:")));
    }

    [Fact]
    public async Task TryGenerate_PublishesActivityEvent()
    {
        await SeedRetrospective();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Summary.");

        await _service.TryGenerateDigestAsync(force: true);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var evt = await db.ActivityEvents
            .FirstOrDefaultAsync(e => e.Type == nameof(ActivityEventType.LearningDigestCompleted));

        Assert.NotNull(evt);
        Assert.Equal("planner-1", evt.ActorId);
        Assert.Contains("digest", evt.Message);
    }

    [Fact]
    public async Task TryGenerate_EnforcesSharedCategory()
    {
        await SeedRetrospective();

        // Planner returns a REMEMBER with category: lesson (not shared)
        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("""
                REMEMBER:
                  category: lesson
                  key: enforce-shared-test
                  value: This should be forced to shared category.

                Summary text.
                """);

        await _service.TryGenerateDigestAsync(force: true);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var memory = await db.AgentMemories
            .FirstOrDefaultAsync(m => m.Key == "enforce-shared-test");

        Assert.NotNull(memory);
        Assert.Equal("shared", memory.Category);
    }

    [Fact]
    public async Task GetUndigested_ReturnsTaskTitle()
    {
        await SeedRetrospective(agentName: "Hephaestus", content: "Test content");

        var undigested = await _service.GetUndigestedRetrospectivesAsync();

        Assert.Single(undigested);
        Assert.Contains("Hephaestus", undigested[0].TaskTitle);
    }

    [Fact]
    public async Task GetUndigested_ExcludesNonRetroComments()
    {
        // Seed a non-retrospective comment
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var taskId = Guid.NewGuid().ToString("N");
            db.Tasks.Add(new TaskEntity
            {
                Id = taskId,
                Title = "Test",
                Status = nameof(TaskStatus.Completed),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            db.TaskComments.Add(new TaskCommentEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                TaskId = taskId,
                AgentId = "engineer-1",
                AgentName = "Hephaestus",
                CommentType = "Finding",
                Content = "This is a finding, not a retrospective",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var undigested = await _service.GetUndigestedRetrospectivesAsync();
        Assert.Empty(undigested);
    }

    [Fact]
    public async Task TryGenerate_MultipleRetrospectives_AllClaimed()
    {
        for (int i = 0; i < 5; i++)
            await SeedRetrospective(content: $"Retro {i}");

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Digest of 5 retrospectives.");

        var digestId = await _service.TryGenerateDigestAsync(force: true);
        Assert.NotNull(digestId);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var digest = await db.LearningDigests
            .Include(d => d.Sources)
            .FirstAsync(d => d.Id == digestId!.Value);

        Assert.Equal(5, digest.Sources.Count);
        Assert.Equal(5, digest.RetrospectivesProcessed);

        // All should be claimed
        var undigested = await _service.GetUndigestedRetrospectivesAsync();
        Assert.Empty(undigested);
    }

    [Fact]
    public async Task TryGenerate_PromptIncludesRetrospectiveContent()
    {
        await SeedRetrospective(content: "JWT refresh tokens need revocation checks");
        string? capturedPrompt = null;

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(),
            Arg.Do<string>(p => capturedPrompt = p),
            Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Summary.");

        await _service.TryGenerateDigestAsync(force: true);

        Assert.NotNull(capturedPrompt);
        Assert.Contains("JWT refresh tokens", capturedPrompt);
        Assert.Contains("LEARNING DIGEST", capturedPrompt);
        Assert.Contains("REMEMBER", capturedPrompt);
        Assert.Contains("category: shared", capturedPrompt);
    }
}
