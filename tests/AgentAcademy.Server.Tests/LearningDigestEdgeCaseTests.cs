using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
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
using NSubstitute.ExceptionExtensions;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Edge-case tests for LearningDigestService and GenerateDigestHandler.
/// Covers concurrency, error paths, boundary conditions, and argument parsing
/// not exercised by the main test suites.
/// </summary>
public sealed class LearningDigestEdgeCaseTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly SqliteConnection _connection;
    private readonly IAgentExecutor _executor;
    private readonly AgentCatalogOptions _catalog;
    private readonly LearningDigestService _service;

    public LearningDigestEdgeCaseTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _executor = Substitute.For<IAgentExecutor>();
        _executor.IsFullyOperational.Returns(true);

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "planner-1", Name: "Aristotle", Role: "Planner",
                    Summary: "Planner", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: ["code", "shell"], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["REMEMBER", "LIST_*", "GENERATE_DIGEST"], [])),
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
        services.AddSingleton<ICommandHandler, RememberHandler>();
        services.AddSingleton<CommandAuthorizer>();
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        _service = new LearningDigestService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _catalog,
            _executor,
            _serviceProvider.GetRequiredService<CommandPipeline>(),
            NullLogger<LearningDigestService>.Instance);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

        // FTS5 infrastructure for REMEMBER command
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

        SeedRoom(db);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private static void SeedRoom(AgentAcademyDbContext db)
    {
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

    private async Task<string> SeedRetrospective(
        string agentName = "Hephaestus",
        string content = "Learned something",
        DateTime? createdAt = null)
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
            CreatedAt = createdAt ?? DateTime.UtcNow
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

    // ── LearningDigestService: Concurrency ──────────────────────

    [Fact]
    public async Task ConcurrentDigest_SecondCallReturnsNull_WhileFirstIsRunning()
    {
        await SeedRetrospective();

        // Deterministic signal: RunAsync signals when it's been called
        var runAsyncReached = new TaskCompletionSource();
        var tcs = new TaskCompletionSource<string>();
        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(callInfo =>
            {
                runAsyncReached.TrySetResult();
                return tcs.Task;
            });

        var firstCall = _service.TryGenerateDigestAsync(force: true);

        // Wait until first call has acquired the lock and reached RunAsync
        await runAsyncReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Second call should return null immediately (lock held)
        var secondResult = await _service.TryGenerateDigestAsync(force: true);
        Assert.Null(secondResult);

        // Release the first call
        tcs.SetResult("Digest summary.");
        var firstResult = await firstCall;
        Assert.NotNull(firstResult);
    }

    [Fact]
    public async Task ConcurrentDigest_InvalidateSessionCalledEvenOnSecondNull()
    {
        await SeedRetrospective();

        var runAsyncReached = new TaskCompletionSource();
        var tcs = new TaskCompletionSource<string>();
        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(callInfo =>
            {
                runAsyncReached.TrySetResult();
                return tcs.Task;
            });

        var firstCall = _service.TryGenerateDigestAsync(force: true);
        await runAsyncReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Second concurrent call returns null — no InvalidateSession expected for it
        await _service.TryGenerateDigestAsync(force: true);

        tcs.SetResult("Done.");
        await firstCall;

        // Only one InvalidateSession call (from the first digest, not the second)
        await _executor.Received(1).InvalidateSessionAsync(
            "planner-1", Arg.Is<string>(s => s.StartsWith("digest:")));
    }

    // ── LearningDigestService: Response parsing ─────────────────

    [Fact]
    public async Task WhitespaceOnlyResponse_MarksCompletedWithEmptySummary()
    {
        await SeedRetrospective();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("   \n\t  \n  ");

        var digestId = await _service.TryGenerateDigestAsync(force: true);
        Assert.NotNull(digestId);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var digest = await db.LearningDigests.FindAsync(digestId!.Value);

        Assert.NotNull(digest);
        Assert.Equal("Completed", digest.Status);
        Assert.Equal(string.Empty, digest.Summary);
        Assert.Equal(0, digest.MemoriesCreated);
    }

    [Fact]
    public async Task MultipleRememberCommands_AllCountedCorrectly()
    {
        await SeedRetrospective(content: "Retro 1");
        await SeedRetrospective(content: "Retro 2");
        await SeedRetrospective(content: "Retro 3");

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("""
                REMEMBER:
                  category: shared
                  key: pattern-error-handling
                  value: All agents should validate error response shapes.

                REMEMBER:
                  category: shared
                  key: pattern-testing
                  value: Write tests before implementing features.

                REMEMBER:
                  category: shared
                  key: pattern-code-review
                  value: Review code changes in pull requests.

                Overall the team is improving its practices.
                """);

        var digestId = await _service.TryGenerateDigestAsync(force: true);
        Assert.NotNull(digestId);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var digest = await db.LearningDigests.FindAsync(digestId!.Value);

        Assert.Equal(3, digest!.MemoriesCreated);

        var memories = await db.AgentMemories
            .Where(m => m.Key.StartsWith("pattern-"))
            .ToListAsync();
        Assert.Equal(3, memories.Count);
    }

    [Fact]
    public async Task EnforceSharedCategory_MultipleMixedCategories_AllCorrected()
    {
        await SeedRetrospective();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("""
                REMEMBER:
                  category: lesson
                  key: mixed-cat-lesson
                  value: Should become shared.

                REMEMBER:
                  category: pattern
                  key: mixed-cat-pattern
                  value: This too should become shared.

                REMEMBER:
                  category: shared
                  key: mixed-cat-already-shared
                  value: Already correct.

                Summary.
                """);

        await _service.TryGenerateDigestAsync(force: true);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var memories = await db.AgentMemories
            .Where(m => m.Key.StartsWith("mixed-cat-"))
            .ToListAsync();

        Assert.Equal(3, memories.Count);
        Assert.All(memories, m => Assert.Equal("shared", m.Category));
    }

    [Fact]
    public async Task EnforceSharedCategory_AlreadyShared_NoUnnecessaryUpdate()
    {
        await SeedRetrospective();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("""
                REMEMBER:
                  category: shared
                  key: already-shared-key
                  value: Correct from the start.

                Done.
                """);

        var digestId = await _service.TryGenerateDigestAsync(force: true);
        Assert.NotNull(digestId);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var memory = await db.AgentMemories.FirstOrDefaultAsync(m => m.Key == "already-shared-key");
        Assert.NotNull(memory);
        Assert.Equal("shared", memory.Category);
    }

    // ── LearningDigestService: Error paths ──────────────────────

    [Fact]
    public async Task InvalidateSessionFailure_DoesNotPropagateException()
    {
        await SeedRetrospective();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Summary.");

        _executor.InvalidateSessionAsync(Arg.Any<string>(), Arg.Any<string>())
            .Throws(new InvalidOperationException("Session invalidation failed"));

        // Should NOT throw despite InvalidateSessionAsync failing
        var digestId = await _service.TryGenerateDigestAsync(force: true);
        Assert.NotNull(digestId);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var digest = await db.LearningDigests.FindAsync(digestId!.Value);
        Assert.Equal("Completed", digest!.Status);
    }

    [Fact]
    public async Task ExecutorFailure_DigestMarkedFailed_SourcesDeleted()
    {
        var commentId = await SeedRetrospective();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Throws(new TimeoutException("LLM timed out"));

        var result = await _service.TryGenerateDigestAsync(force: true);
        Assert.Null(result);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        // Digest exists with Failed status
        var digest = await db.LearningDigests.FirstOrDefaultAsync();
        Assert.NotNull(digest);
        Assert.Equal("Failed", digest.Status);

        // Sources were cleaned up
        var sources = await db.LearningDigestSources.ToListAsync();
        Assert.Empty(sources);

        // Retrospective is available for retry
        var undigested = await _service.GetUndigestedRetrospectivesAsync();
        Assert.Single(undigested);
        Assert.Equal(commentId, undigested[0].CommentId);
    }

    // ── LearningDigestService: GetUndigestedRetrospectivesAsync ─

    [Fact]
    public async Task GetUndigested_OrderedByCreatedAtAscending()
    {
        var now = DateTime.UtcNow;
        var id3 = await SeedRetrospective(content: "Third", createdAt: now.AddMinutes(3));
        var id1 = await SeedRetrospective(content: "First", createdAt: now.AddMinutes(1));
        var id2 = await SeedRetrospective(content: "Second", createdAt: now.AddMinutes(2));

        var undigested = await _service.GetUndigestedRetrospectivesAsync();

        Assert.Equal(3, undigested.Count);
        Assert.Equal(id1, undigested[0].CommentId);
        Assert.Equal(id2, undigested[1].CommentId);
        Assert.Equal(id3, undigested[2].CommentId);
    }

    [Fact]
    public async Task GetUndigested_IncludesFromFailedDigests()
    {
        var commentId = await SeedRetrospective(content: "Should be retried");

        // Simulate a failed digest that claimed this retrospective
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var digest = new LearningDigestEntity
            {
                CreatedAt = DateTime.UtcNow,
                Summary = string.Empty,
                Status = "Failed",
                MemoriesCreated = 0,
                RetrospectivesProcessed = 1,
                Sources = [new LearningDigestSourceEntity { RetrospectiveCommentId = commentId }]
            };
            db.LearningDigests.Add(digest);
            await db.SaveChangesAsync();
        }

        // Failed digest sources should NOT exclude the retrospective
        var undigested = await _service.GetUndigestedRetrospectivesAsync();
        Assert.Single(undigested);
        Assert.Equal(commentId, undigested[0].CommentId);
    }

    [Fact]
    public async Task GetUndigested_ExcludesFromCompletedDigests()
    {
        var commentId = await SeedRetrospective(content: "Already digested");

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var digest = new LearningDigestEntity
            {
                CreatedAt = DateTime.UtcNow,
                Summary = "Done",
                Status = "Completed",
                MemoriesCreated = 1,
                RetrospectivesProcessed = 1,
                Sources = [new LearningDigestSourceEntity { RetrospectiveCommentId = commentId }]
            };
            db.LearningDigests.Add(digest);
            await db.SaveChangesAsync();
        }

        var undigested = await _service.GetUndigestedRetrospectivesAsync();
        Assert.Empty(undigested);
    }

    [Fact]
    public async Task GetUndigested_MixedDigestStatuses_OnlyCompletedExcluded()
    {
        var completedId = await SeedRetrospective(content: "Completed digest retro");
        var failedId = await SeedRetrospective(content: "Failed digest retro");
        var freeId = await SeedRetrospective(content: "Free retro");

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

            db.LearningDigests.Add(new LearningDigestEntity
            {
                CreatedAt = DateTime.UtcNow,
                Summary = "Done",
                Status = "Completed",
                MemoriesCreated = 1,
                RetrospectivesProcessed = 1,
                Sources = [new LearningDigestSourceEntity { RetrospectiveCommentId = completedId }]
            });

            db.LearningDigests.Add(new LearningDigestEntity
            {
                CreatedAt = DateTime.UtcNow,
                Summary = string.Empty,
                Status = "Failed",
                MemoriesCreated = 0,
                RetrospectivesProcessed = 1,
                Sources = [new LearningDigestSourceEntity { RetrospectiveCommentId = failedId }]
            });

            await db.SaveChangesAsync();
        }

        var undigested = await _service.GetUndigestedRetrospectivesAsync();
        Assert.Equal(2, undigested.Count);
        Assert.Contains(undigested, r => r.CommentId == failedId);
        Assert.Contains(undigested, r => r.CommentId == freeId);
        Assert.DoesNotContain(undigested, r => r.CommentId == completedId);
    }

    // ── LearningDigestService: Sequential digests ───────────────

    [Fact]
    public async Task SequentialDigests_SecondDigest_OnlyProcessesNewRetrospectives()
    {
        // Seed retrospectives for first digest
        await SeedRetrospective(content: "First batch retro 1");
        await SeedRetrospective(content: "First batch retro 2");

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("First digest summary.");

        var firstDigestId = await _service.TryGenerateDigestAsync(force: true);
        Assert.NotNull(firstDigestId);

        // Add new retrospective after first digest
        await SeedRetrospective(content: "Second batch retro");

        string? capturedPrompt = null;
        _executor.RunAsync(
            Arg.Any<AgentDefinition>(),
            Arg.Do<string>(p => capturedPrompt = p),
            Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Second digest summary.");

        var secondDigestId = await _service.TryGenerateDigestAsync(force: true);
        Assert.NotNull(secondDigestId);
        Assert.NotEqual(firstDigestId, secondDigestId);

        // Second digest should only include the new retro
        Assert.NotNull(capturedPrompt);
        Assert.Contains("Second batch retro", capturedPrompt);
        Assert.DoesNotContain("First batch retro", capturedPrompt);

        // Verify digest entity
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var second = await db.LearningDigests
            .Include(d => d.Sources)
            .FirstAsync(d => d.Id == secondDigestId!.Value);
        Assert.Single(second.Sources);
        Assert.Equal(1, second.RetrospectivesProcessed);
    }

    // ── LearningDigestService: Threshold boundary ───────────────

    [Fact]
    public async Task ThresholdBoundary_ExactlyAtThreshold_Generates()
    {
        await SetDigestThreshold(3);
        for (int i = 0; i < 3; i++)
            await SeedRetrospective(content: $"Retro {i}");

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("At threshold.");

        var result = await _service.TryGenerateDigestAsync();
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ThresholdBoundary_OneBelow_Skips()
    {
        await SetDigestThreshold(3);
        for (int i = 0; i < 2; i++)
            await SeedRetrospective(content: $"Retro {i}");

        var result = await _service.TryGenerateDigestAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task DefaultThreshold_IsUsedWhenNoSettingExists()
    {
        // Default threshold is 5 — seed exactly 5
        for (int i = 0; i < 5; i++)
            await SeedRetrospective(content: $"Retro {i}");

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Default threshold met.");

        var result = await _service.TryGenerateDigestAsync();
        Assert.NotNull(result);
    }

    [Fact]
    public async Task DefaultThreshold_FourRetrospectives_Skips()
    {
        // Default threshold is 5 — 4 should not trigger
        for (int i = 0; i < 4; i++)
            await SeedRetrospective(content: $"Retro {i}");

        var result = await _service.TryGenerateDigestAsync();
        Assert.Null(result);
    }

    // ── LearningDigestService: Prompt content ───────────────────

    [Fact]
    public async Task PromptIncludesAllRetrospectiveAgentNames()
    {
        await SeedRetrospective(agentName: "Hephaestus", content: "Retro from engineer");
        await SeedRetrospective(agentName: "Athena", content: "Retro from reviewer");

        string? capturedPrompt = null;
        _executor.RunAsync(
            Arg.Any<AgentDefinition>(),
            Arg.Do<string>(p => capturedPrompt = p),
            Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Summary.");

        await _service.TryGenerateDigestAsync(force: true);

        Assert.NotNull(capturedPrompt);
        Assert.Contains("Hephaestus", capturedPrompt);
        Assert.Contains("Athena", capturedPrompt);
    }

    [Fact]
    public async Task PromptIncludesTaskTitles()
    {
        await SeedRetrospective(agentName: "Hephaestus", content: "Learned from auth task");

        string? capturedPrompt = null;
        _executor.RunAsync(
            Arg.Any<AgentDefinition>(),
            Arg.Do<string>(p => capturedPrompt = p),
            Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Summary.");

        await _service.TryGenerateDigestAsync(force: true);

        Assert.NotNull(capturedPrompt);
        Assert.Contains("Task for Hephaestus", capturedPrompt);
    }

    // ── LearningDigestService: Digest room ID format ────────────

    [Fact]
    public async Task DigestRoomId_ContainsDigestId()
    {
        await SeedRetrospective();
        string? capturedRoomId = null;

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(),
            Arg.Do<string?>(r => capturedRoomId = r),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Summary.");

        var digestId = await _service.TryGenerateDigestAsync(force: true);

        Assert.NotNull(capturedRoomId);
        Assert.Equal($"digest:{digestId}", capturedRoomId);
    }

    // ── GenerateDigestHandler: Argument parsing ─────────────────

    [Fact]
    public async Task Handler_ForceArgAsInteger_DefaultsToFalse()
    {
        var handler = new GenerateDigestHandler();
        var context = MakeHandlerContext();
        var args = new Dictionary<string, object?> { ["force"] = 42 };
        var result = await handler.ExecuteAsync(MakeEnvelope(args), context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(false, dict["generated"]);
        Assert.Contains("threshold", dict["message"]!.ToString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handler_ForceArgAsNull_DefaultsToFalse()
    {
        var handler = new GenerateDigestHandler();
        var context = MakeHandlerContext();
        var args = new Dictionary<string, object?> { ["force"] = null };
        var result = await handler.ExecuteAsync(MakeEnvelope(args), context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(false, dict["generated"]);
    }

    [Fact]
    public async Task Handler_ForceArgMixedCase_ParsedCorrectly()
    {
        var handler = new GenerateDigestHandler();
        var context = MakeHandlerContext();

        foreach (var trueVariant in new[] { "TRUE", "True", "tRuE" })
        {
            var args = new Dictionary<string, object?> { ["force"] = trueVariant };
            var result = await handler.ExecuteAsync(MakeEnvelope(args), context);

            Assert.Equal(CommandStatus.Success, result.Status);
            // Force=true but no retrospectives → generated=false, but no threshold message
            var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
            Assert.Equal(false, dict["generated"]);
        }
    }

    [Fact]
    public async Task Handler_ExtraArgsIgnored()
    {
        var handler = new GenerateDigestHandler();
        var context = MakeHandlerContext();
        var args = new Dictionary<string, object?>
        {
            ["force"] = false,
            ["unknown_arg"] = "whatever",
            ["count"] = 99
        };

        var result = await handler.ExecuteAsync(MakeEnvelope(args), context);
        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task Handler_ResultMessageDiffers_ForceVsThreshold()
    {
        var handler = new GenerateDigestHandler();
        var context = MakeHandlerContext();

        // Without force: mentions threshold
        var noForce = await handler.ExecuteAsync(MakeEnvelope(), context);
        var noForceDict = Assert.IsType<Dictionary<string, object?>>(noForce.Result);
        Assert.Contains("threshold", noForceDict["message"]!.ToString()!, StringComparison.OrdinalIgnoreCase);

        // With force: mentions "skipped" or "in progress" (not threshold)
        var withForce = await handler.ExecuteAsync(
            MakeEnvelope(new Dictionary<string, object?> { ["force"] = true }), context);
        var forceDict = Assert.IsType<Dictionary<string, object?>>(withForce.Result);
        var forceMsg = forceDict["message"]!.ToString()!;
        Assert.DoesNotContain("threshold", forceMsg, StringComparison.OrdinalIgnoreCase);
    }

    // ── Handler helpers ─────────────────────────────────────────

    private CommandContext MakeHandlerContext()
    {
        var scope = _serviceProvider.CreateScope();
        return new CommandContext(
            AgentId: "planner-1",
            AgentName: "Aristotle",
            AgentRole: "Planner",
            RoomId: "main",
            BreakoutRoomId: null,
            Services: new DigestServiceProvider(scope.ServiceProvider, _service));
    }

    private static CommandEnvelope MakeEnvelope(Dictionary<string, object?>? args = null)
        => new(
            Command: "GENERATE_DIGEST",
            Args: args ?? new Dictionary<string, object?>(),
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: $"cmd-{Guid.NewGuid():N}",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: "planner-1");

    private sealed class DigestServiceProvider : IServiceProvider
    {
        private readonly IServiceProvider _inner;
        private readonly LearningDigestService _digestService;

        public DigestServiceProvider(IServiceProvider inner, LearningDigestService digestService)
        {
            _inner = inner;
            _digestService = digestService;
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ILearningDigestService) || serviceType == typeof(LearningDigestService))
                return _digestService;
            return _inner.GetService(serviceType);
        }
    }
}
