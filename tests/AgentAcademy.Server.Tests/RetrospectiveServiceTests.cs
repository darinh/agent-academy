using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

public class RetrospectiveServiceTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly SqliteConnection _connection;
    private readonly IAgentExecutor _executor;
    private readonly AgentCatalogOptions _catalog;
    private readonly RetrospectiveService _service;

    public RetrospectiveServiceTests()
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
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: ["code", "shell"], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["REMEMBER", "RECALL", "LIST_*"], [])),
                new AgentDefinition(
                    Id: "reviewer-1", Name: "Socrates", Role: "Reviewer",
                    Summary: "Reviewer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: false,
                    Permissions: new CommandPermissionSet(["APPROVE_TASK"], []))
            ]
        );

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton(_catalog);
        services.AddSingleton<IAgentCatalog>(_catalog);
        services.AddSingleton(_executor);
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<MessageBroadcaster>();
        services.AddScoped<ActivityPublisher>();
        services.AddScoped<TaskQueryService>();
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<TaskDependencyService>();
        services.AddScoped<MessageService>();
        services.AddSingleton<CommandRateLimiter>();
        services.AddSingleton<CommandPipeline>();
        services.AddSingleton<CommandParser>();
        // Register the REMEMBER handler so commands are processed
        services.AddSingleton<ICommandHandler, Server.Commands.Handlers.RememberHandler>();
        services.AddSingleton<CommandAuthorizer>();
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        _service = new RetrospectiveService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _catalog,
            _executor,
            _serviceProvider.GetRequiredService<CommandPipeline>(),
            NullLogger<RetrospectiveService>.Instance);

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

    // ── Helper ──────────────────────────────────────────────────

    private async Task<string> CreateTestTask(
        string assignedAgentId = "engineer-1",
        string assignedAgentName = "Hephaestus",
        int reviewRounds = 1,
        TaskStatus status = TaskStatus.Completed)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var roomId = $"room-{Guid.NewGuid():N}";
        db.Rooms.Add(new RoomEntity
        {
            Id = roomId,
            Name = "Test Room",
            Status = nameof(RoomStatus.Active),
            CurrentPhase = nameof(CollaborationPhase.Planning),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var taskId = Guid.NewGuid().ToString("N");
        db.Tasks.Add(new TaskEntity
        {
            Id = taskId,
            Title = "Add user authentication",
            Description = "Implement JWT-based auth with bcrypt password hashing",
            SuccessCriteria = "Login, logout, and token refresh work end-to-end",
            Status = nameof(status),
            Type = nameof(TaskType.Feature),
            AssignedAgentId = assignedAgentId,
            AssignedAgentName = assignedAgentName,
            ReviewRounds = reviewRounds,
            CommitCount = 3,
            RoomId = roomId,
            CreatedAt = DateTime.UtcNow.AddHours(-2),
            StartedAt = DateTime.UtcNow.AddHours(-1.5),
            CompletedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            BranchName = "task/add-user-auth-abc123"
        });

        await db.SaveChangesAsync();
        return taskId;
    }

    private async Task AddReviewMessage(string taskId, string content)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var task = await db.Tasks.FindAsync(taskId);

        db.Messages.Add(new MessageEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            RoomId = task!.RoomId!,
            SenderId = "reviewer-1",
            SenderName = "Socrates",
            SenderKind = nameof(MessageSenderKind.Agent),
            Kind = nameof(MessageKind.Review),
            Content = content,
            SentAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task AddTaskComment(string taskId, string commentType, string content)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        db.TaskComments.Add(new TaskCommentEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            TaskId = taskId,
            AgentId = "reviewer-1",
            AgentName = "Socrates",
            CommentType = commentType,
            Content = content,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    // ── Tests ───────────────────────────────────────────────────

    [Fact]
    public async Task RunRetrospective_NullAgentId_SkipsSilently()
    {
        await _service.RunRetrospectiveAsync("some-task", null);
        // No exception, no executor calls
        await _executor.DidNotReceive().RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunRetrospective_UnknownAgent_SkipsSilently()
    {
        var taskId = await CreateTestTask(assignedAgentId: "nonexistent-agent");
        await _service.RunRetrospectiveAsync(taskId, "nonexistent-agent");

        await _executor.DidNotReceive().RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunRetrospective_TaskNotFound_SkipsSilently()
    {
        await _service.RunRetrospectiveAsync("nonexistent-task", "engineer-1");

        await _executor.DidNotReceive().RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunRetrospective_ExecutesWithRestrictedPermissions()
    {
        var taskId = await CreateTestTask();
        AgentDefinition? capturedAgent = null;

        _executor.RunAsync(
            Arg.Do<AgentDefinition>(a => capturedAgent = a),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("Retrospective summary here.");

        await _service.RunRetrospectiveAsync(taskId, "engineer-1");

        Assert.NotNull(capturedAgent);
        // Permissions should be restricted to REMEMBER only
        Assert.Single(capturedAgent!.Permissions!.Allowed);
        Assert.Equal("REMEMBER", capturedAgent.Permissions.Allowed[0]);
        Assert.Empty(capturedAgent.Permissions.Denied);
        // Tools should be disabled
        Assert.Empty(capturedAgent.EnabledTools);
    }

    [Fact]
    public async Task RunRetrospective_UsesSyntheticRoomId()
    {
        var taskId = await CreateTestTask();
        string? capturedRoomId = null;

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(),
            Arg.Do<string?>(r => capturedRoomId = r),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("Summary.");

        await _service.RunRetrospectiveAsync(taskId, "engineer-1");

        Assert.NotNull(capturedRoomId);
        Assert.StartsWith("retrospective:", capturedRoomId);
        Assert.Contains(taskId, capturedRoomId);
    }

    [Fact]
    public async Task RunRetrospective_SavesRetrospectiveComment()
    {
        var taskId = await CreateTestTask();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("This went well. JWT auth was straightforward.");

        await _service.RunRetrospectiveAsync(taskId, "engineer-1");

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var comment = await db.TaskComments
            .FirstOrDefaultAsync(c => c.TaskId == taskId && c.CommentType == nameof(TaskCommentType.Retrospective));

        Assert.NotNull(comment);
        Assert.Equal("engineer-1", comment.AgentId);
        Assert.Equal("Hephaestus", comment.AgentName);
        Assert.Contains("JWT auth was straightforward", comment.Content);
    }

    [Fact]
    public async Task RunRetrospective_ProcessesRememberCommands()
    {
        var taskId = await CreateTestTask();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("""
                REMEMBER:
                  category: lesson
                  key: jwt-refresh-pattern
                  value: Always validate the refresh token against the revocation list before issuing a new access token.

                This task taught me that JWT refresh token handling requires careful revocation checks.
                """);

        await _service.RunRetrospectiveAsync(taskId, "engineer-1");

        // Verify the memory was stored
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var memory = await db.AgentMemories
            .FirstOrDefaultAsync(m => m.AgentId == "engineer-1" && m.Key == "jwt-refresh-pattern");

        Assert.NotNull(memory);
        Assert.Equal("lesson", memory.Category);
        Assert.Contains("revocation list", memory.Value);
    }

    [Fact]
    public async Task RunRetrospective_StripsCommandsFromComment()
    {
        var taskId = await CreateTestTask();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("""
                REMEMBER:
                  category: gotcha
                  key: sqlite-fts5-triggers
                  value: SQLite FTS5 triggers must be manually created in test setup.

                The main challenge was getting FTS5 triggers working in test environments.
                """);

        await _service.RunRetrospectiveAsync(taskId, "engineer-1");

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var comment = await db.TaskComments
            .FirstOrDefaultAsync(c => c.TaskId == taskId && c.CommentType == nameof(TaskCommentType.Retrospective));

        Assert.NotNull(comment);
        // Comment should not contain the REMEMBER command text
        Assert.DoesNotContain("REMEMBER:", comment.Content);
        Assert.Contains("FTS5 triggers", comment.Content);
    }

    [Fact]
    public async Task RunRetrospective_Idempotent_SkipsIfAlreadyExists()
    {
        var taskId = await CreateTestTask();

        // Pre-seed a retrospective comment
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.TaskComments.Add(new TaskCommentEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                TaskId = taskId,
                AgentId = "engineer-1",
                AgentName = "Hephaestus",
                CommentType = nameof(TaskCommentType.Retrospective),
                Content = "Existing retrospective",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await _service.RunRetrospectiveAsync(taskId, "engineer-1");

        // Should NOT have called the executor (idempotency guard)
        await _executor.DidNotReceive().RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunRetrospective_EmptyResponse_NoComment()
    {
        var taskId = await CreateTestTask();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("");

        await _service.RunRetrospectiveAsync(taskId, "engineer-1");

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var count = await db.TaskComments.CountAsync(c => c.TaskId == taskId);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task RunRetrospective_EmptyResponse_StillInvalidatesSession()
    {
        var taskId = await CreateTestTask();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("");

        await _service.RunRetrospectiveAsync(taskId, "engineer-1");

        // Session should be invalidated even on empty response
        await _executor.Received(1).InvalidateSessionAsync("engineer-1", $"retrospective:{taskId}");
    }

    [Fact]
    public async Task RunRetrospective_ExecutorFailure_StillInvalidatesSession()
    {
        var taskId = await CreateTestTask();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<string>(x => throw new InvalidOperationException("LLM connection failed"));

        await _service.RunRetrospectiveAsync(taskId, "engineer-1");

        // Session should be invalidated even on failure
        await _executor.Received(1).InvalidateSessionAsync("engineer-1", $"retrospective:{taskId}");
    }

    [Fact]
    public async Task RunRetrospective_PublishesActivityEvent()
    {
        var taskId = await CreateTestTask();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("Retrospective completed.");

        await _service.RunRetrospectiveAsync(taskId, "engineer-1");

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var activityEvent = await db.ActivityEvents
            .FirstOrDefaultAsync(e => e.Type == nameof(ActivityEventType.TaskRetrospectiveCompleted)
                && e.TaskId == taskId);

        Assert.NotNull(activityEvent);
        Assert.Equal("engineer-1", activityEvent.ActorId);
        Assert.Contains("retrospective", activityEvent.Message);
    }

    [Fact]
    public async Task RunRetrospective_ExecutorFailure_LogsButDoesNotThrow()
    {
        var taskId = await CreateTestTask();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<string>(x => throw new InvalidOperationException("LLM connection failed"));

        // Should not throw
        await _service.RunRetrospectiveAsync(taskId, "engineer-1");
    }

    [Fact]
    public async Task RunRetrospective_InvalidatesSessionAfter()
    {
        var taskId = await CreateTestTask();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("Summary.");

        await _service.RunRetrospectiveAsync(taskId, "engineer-1");

        await _executor.Received(1).InvalidateSessionAsync("engineer-1", $"retrospective:{taskId}");
    }

    // ── Edge-case tests (test backfill) ────────────────────────

    [Fact]
    public async Task RunRetrospective_WhitespaceAgentId_SkipsSilently()
    {
        await _service.RunRetrospectiveAsync("some-task", "   ");
        await _executor.DidNotReceive().RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunRetrospective_OnlyRememberCommands_NoCommentSaved()
    {
        var taskId = await CreateTestTask();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("""
                REMEMBER:
                  category: lesson
                  key: edge-case-only-commands
                  value: Testing that only-command responses produce no comment.
                """);

        await _service.RunRetrospectiveAsync(taskId, "engineer-1");

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        // Memory should be stored
        var memory = await db.AgentMemories
            .FirstOrDefaultAsync(m => m.Key == "edge-case-only-commands");
        Assert.NotNull(memory);

        // But no comment should be saved (only whitespace remains after command stripping)
        var commentCount = await db.TaskComments
            .CountAsync(c => c.TaskId == taskId && c.CommentType == nameof(TaskCommentType.Retrospective));
        Assert.Equal(0, commentCount);
    }

    [Fact]
    public async Task RunRetrospective_OnlyRememberCommands_StillInvalidatesSession()
    {
        var taskId = await CreateTestTask();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("""
                REMEMBER:
                  category: lesson
                  key: session-invalidation-test
                  value: Testing session invalidation on command-only response.
                """);

        await _service.RunRetrospectiveAsync(taskId, "engineer-1");

        await _executor.Received(1).InvalidateSessionAsync("engineer-1", $"retrospective:{taskId}");
    }

    [Fact]
    public async Task RunRetrospective_MultipleRememberCommands_AllProcessed()
    {
        var taskId = await CreateTestTask();

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("""
                REMEMBER:
                  category: lesson
                  key: multi-remember-first
                  value: First learning from retrospective.

                REMEMBER:
                  category: gotcha
                  key: multi-remember-second
                  value: Second learning — a gotcha discovered.

                The retrospective revealed two key insights.
                """);

        await _service.RunRetrospectiveAsync(taskId, "engineer-1");

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var memories = await db.AgentMemories
            .Where(m => m.AgentId == "engineer-1" && m.Key.StartsWith("multi-remember"))
            .ToListAsync();

        Assert.Equal(2, memories.Count);
    }

    [Fact]
    public async Task RunRetrospective_NonRetroComments_DoNotBlockNew()
    {
        var taskId = await CreateTestTask();

        // Pre-seed a non-retrospective comment (Finding type)
        await AddTaskComment(taskId, "Finding", "Some finding comment");

        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("Retrospective summary here.");

        await _service.RunRetrospectiveAsync(taskId, "engineer-1");

        // Should have called executor (Finding comment doesn't block retrospective)
        await _executor.Received(1).RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var retroComment = await db.TaskComments
            .FirstOrDefaultAsync(c => c.TaskId == taskId && c.CommentType == nameof(TaskCommentType.Retrospective));
        Assert.NotNull(retroComment);
    }

    // ── Context Gathering Tests ─────────────────────────────────

    [Fact]
    public async Task GatherContext_IncludesReviewMessages()
    {
        var taskId = await CreateTestTask(reviewRounds: 2);
        await AddReviewMessage(taskId, "Missing error handling in auth middleware");

        var context = await _service.GatherRetrospectiveContextAsync(taskId);

        Assert.NotNull(context);
        Assert.Single(context.ReviewMessages);
        Assert.Contains("error handling", context.ReviewMessages[0].Content);
    }

    [Fact]
    public async Task GatherContext_IncludesTaskComments()
    {
        var taskId = await CreateTestTask();
        await AddTaskComment(taskId, "Finding", "Race condition in token refresh");

        var context = await _service.GatherRetrospectiveContextAsync(taskId);

        Assert.NotNull(context);
        Assert.Single(context.Comments);
        Assert.Equal("Finding", context.Comments[0].Type);
    }

    [Fact]
    public async Task GatherContext_CalculatesCycleTime()
    {
        var taskId = await CreateTestTask();

        var context = await _service.GatherRetrospectiveContextAsync(taskId);

        Assert.NotNull(context);
        Assert.NotNull(context.CycleTime);
        Assert.True(context.CycleTime.Value.TotalMinutes > 0);
    }

    [Fact]
    public async Task GatherContext_TaskNotFound_ReturnsNull()
    {
        var context = await _service.GatherRetrospectiveContextAsync("nonexistent");
        Assert.Null(context);
    }

    [Fact]
    public async Task GatherContext_NullRoomId_NoReviewMessages()
    {
        // Create task with no RoomId
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Tasks.Add(new TaskEntity
            {
                Id = "task-no-room",
                Title = "No room task",
                Description = "Test task without room",
                Status = nameof(TaskStatus.Completed),
                Type = nameof(TaskType.Feature),
                AssignedAgentId = "engineer-1",
                RoomId = null,
                CreatedAt = DateTime.UtcNow.AddHours(-2),
                StartedAt = DateTime.UtcNow.AddHours(-1),
                CompletedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var context = await _service.GatherRetrospectiveContextAsync("task-no-room");

        Assert.NotNull(context);
        Assert.Empty(context.ReviewMessages);
    }

    [Fact]
    public async Task GatherContext_ManyReviewMessages_CapsAt20()
    {
        var taskId = await CreateTestTask();

        // Seed 25 review messages
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var task = await db.Tasks.FindAsync(taskId);

            for (int i = 0; i < 25; i++)
            {
                db.Messages.Add(new MessageEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    RoomId = task!.RoomId!,
                    SenderId = "reviewer-1",
                    SenderName = "Socrates",
                    SenderKind = nameof(MessageSenderKind.Agent),
                    Kind = nameof(MessageKind.Review),
                    Content = $"Review message {i:D2}",
                    SentAt = DateTime.UtcNow.AddMinutes(-25 + i)
                });
            }
            await db.SaveChangesAsync();
        }

        var context = await _service.GatherRetrospectiveContextAsync(taskId);

        Assert.NotNull(context);
        Assert.Equal(20, context.ReviewMessages.Count);
        // Should be in chronological order (earliest first after reverse)
        Assert.Contains("05", context.ReviewMessages[0].Content);
    }

    [Fact]
    public async Task GatherContext_NoCompletedAt_NullCycleTime()
    {
        // Create task with StartedAt but no CompletedAt
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Tasks.Add(new TaskEntity
            {
                Id = "task-no-completed",
                Title = "Still in progress",
                Description = "Not completed yet",
                Status = nameof(TaskStatus.Active),
                Type = nameof(TaskType.Feature),
                AssignedAgentId = "engineer-1",
                CreatedAt = DateTime.UtcNow.AddHours(-2),
                StartedAt = DateTime.UtcNow.AddHours(-1),
                CompletedAt = null,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var context = await _service.GatherRetrospectiveContextAsync("task-no-completed");

        Assert.NotNull(context);
        Assert.Null(context.CycleTime);
    }

    [Fact]
    public async Task GatherContext_NoStartedAt_NullCycleTime()
    {
        // Create task with CompletedAt but no StartedAt
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Tasks.Add(new TaskEntity
            {
                Id = "task-no-started",
                Title = "Never started",
                Description = "Completed but no start time",
                Status = nameof(TaskStatus.Completed),
                Type = nameof(TaskType.Feature),
                AssignedAgentId = "engineer-1",
                CreatedAt = DateTime.UtcNow.AddHours(-2),
                StartedAt = null,
                CompletedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var context = await _service.GatherRetrospectiveContextAsync("task-no-started");

        Assert.NotNull(context);
        Assert.Null(context.CycleTime);
    }

    // ── Prompt Tests ────────────────────────────────────────────

    [Fact]
    public async Task Prompt_IncludesTaskContext()
    {
        var taskId = await CreateTestTask();
        var context = await _service.GatherRetrospectiveContextAsync(taskId);

        var agent = _catalog.Agents[0];
        var prompt = PromptBuilder.BuildRetrospectivePrompt(agent, context!);

        Assert.Contains("Add user authentication", prompt);
        Assert.Contains("JWT-based auth", prompt);
        Assert.Contains("Hephaestus", prompt);
    }

    [Fact]
    public async Task Prompt_IncludesMetrics()
    {
        var taskId = await CreateTestTask(reviewRounds: 3);
        var context = await _service.GatherRetrospectiveContextAsync(taskId);

        var agent = _catalog.Agents[0];
        var prompt = PromptBuilder.BuildRetrospectivePrompt(agent, context!);

        Assert.Contains("Review rounds: 3", prompt);
        Assert.Contains("Commit count: 3", prompt);
        Assert.Contains("Cycle time:", prompt);
    }

    [Fact]
    public async Task Prompt_IncludesReviewFeedback()
    {
        var taskId = await CreateTestTask();
        await AddReviewMessage(taskId, "Missing error handling");
        var context = await _service.GatherRetrospectiveContextAsync(taskId);

        var agent = _catalog.Agents[0];
        var prompt = PromptBuilder.BuildRetrospectivePrompt(agent, context!);

        Assert.Contains("REVIEW FEEDBACK", prompt);
        Assert.Contains("Missing error handling", prompt);
    }

    [Fact]
    public async Task Prompt_HighlightsMultipleReviewRounds()
    {
        var taskId = await CreateTestTask(reviewRounds: 3);
        var context = await _service.GatherRetrospectiveContextAsync(taskId);

        var agent = _catalog.Agents[0];
        var prompt = PromptBuilder.BuildRetrospectivePrompt(agent, context!);

        Assert.Contains("3 review rounds", prompt);
        Assert.Contains("high-value learning", prompt);
    }

    [Fact]
    public void Prompt_IncludesRememberFormat()
    {
        var context = new RetrospectiveContext(
            TaskId: "test-1", Title: "Test", Description: "Desc",
            SuccessCriteria: null, TaskType: "Feature", Status: "Completed",
            ReviewRounds: 1, CommitCount: 2, CycleTime: TimeSpan.FromHours(1),
            CreatedAt: DateTime.UtcNow, CompletedAt: DateTime.UtcNow,
            Comments: [], ReviewMessages: []);

        var agent = _catalog.Agents[0];
        var prompt = PromptBuilder.BuildRetrospectivePrompt(agent, context);

        Assert.Contains("REMEMBER:", prompt);
        Assert.Contains("category: lesson", prompt);
        Assert.Contains("key: descriptive-kebab-case-key", prompt);
        Assert.Contains("value:", prompt);
    }
}
