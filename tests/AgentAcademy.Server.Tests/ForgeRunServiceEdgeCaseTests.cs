using AgentAcademy.Forge.Artifacts;
using AgentAcademy.Forge.Execution;
using AgentAcademy.Forge.Llm;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Schemas;
using AgentAcademy.Forge.Storage;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Edge-case tests for ForgeRunService durable job store:
/// recovery scenarios, entity mapping, JSON roundtrip, and ordering.
/// Complements the core tests in ForgeRunServiceTests (ForgeControllerTests.cs).
/// </summary>
public sealed class ForgeRunServiceEdgeCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly List<string> _tempDirs = [];

    public ForgeRunServiceEdgeCaseTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
        foreach (var dir in _tempDirs)
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // ── Recovery: execution unavailable ─────────────────────────────────

    [Fact]
    public async Task RecoverJobsAsync_WhenExecutionUnavailable_MarksQueuedAsInterrupted()
    {
        SeedJob("q1", "queued");
        SeedJob("q2", "queued");

        var service = CreateService(executionAvailable: false);
        await service.RecoverJobsAsync();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var jobs = await db.ForgeJobs.Where(e => e.Id == "q1" || e.Id == "q2").ToListAsync();

        Assert.All(jobs, j =>
        {
            Assert.Equal("interrupted", j.Status);
            Assert.Contains("execution is unavailable", j.Error);
            Assert.NotNull(j.CompletedAt);
        });
    }

    // ── Recovery: mixed running + queued with execution unavailable ──────

    [Fact]
    public async Task RecoverJobsAsync_MixedJobsWithNoExecution_AllMarkedInterrupted()
    {
        SeedJob("run1", "running", startedAt: DateTime.UtcNow.AddMinutes(-5));
        SeedJob("q1", "queued");

        var service = CreateService(executionAvailable: false);
        await service.RecoverJobsAsync();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var running = await db.ForgeJobs.FindAsync("run1");
        Assert.Equal("interrupted", running!.Status);
        Assert.Contains("Server restarted", running.Error);

        var queued = await db.ForgeJobs.FindAsync("q1");
        Assert.Equal("interrupted", queued!.Status);
        Assert.Contains("execution is unavailable", queued.Error);
    }

    // ── Recovery: no jobs to recover ────────────────────────────────────

    [Fact]
    public async Task RecoverJobsAsync_WhenNoJobs_CompletesWithoutError()
    {
        var service = CreateService();
        await service.RecoverJobsAsync(); // should not throw
    }

    // ── Recovery: completed/failed jobs are untouched ───────────────────

    [Fact]
    public async Task RecoverJobsAsync_DoesNotTouchCompletedOrFailedJobs()
    {
        SeedJob("done1", "completed", completedAt: DateTime.UtcNow.AddHours(-1));
        SeedJob("fail1", "failed", error: "original error", completedAt: DateTime.UtcNow.AddHours(-1));

        var service = CreateService();
        await service.RecoverJobsAsync();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var completed = await db.ForgeJobs.FindAsync("done1");
        Assert.Equal("completed", completed!.Status);

        var failed = await db.ForgeJobs.FindAsync("fail1");
        Assert.Equal("failed", failed!.Status);
        Assert.Equal("original error", failed.Error);
    }

    // ── Entity mapping: unrecognized status falls back to Failed ────────

    [Fact]
    public async Task GetJobAsync_UnrecognizedStatus_FallsBackToFailed()
    {
        SeedJob("bogus-status", "some_unknown_status");

        var service = CreateService();
        var job = await service.GetJobAsync("bogus-status");

        Assert.NotNull(job);
        Assert.Equal(ForgeJobStatus.Failed, job.Status);
    }

    // ── Entity mapping: malformed JSON throws (no graceful fallback) ────
    // NOTE: EntityToJob uses `?? default` which handles null but not
    // invalid JSON. If corrupt data enters the DB, this will throw.
    // Consider wrapping in try/catch if data integrity isn't guaranteed.

    [Fact]
    public async Task GetJobAsync_MalformedTaskBriefJson_ReturnsFallback()
    {
        SeedJobRaw("bad-brief", "completed", taskBriefJson: "not valid json {{", methodologyJson: MinimalMethodologyJson);

        var service = CreateService();
        var job = await service.GetJobAsync("bad-brief");

        Assert.NotNull(job);
        Assert.Equal("unknown", job.TaskBrief.TaskId);
        Assert.Contains("corrupt", job.TaskBrief.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetJobAsync_MalformedMethodologyJson_ReturnsFallback()
    {
        SeedJobRaw("bad-meth", "completed", taskBriefJson: MinimalTaskBriefJson, methodologyJson: "}{invalid");

        var service = CreateService();
        var job = await service.GetJobAsync("bad-meth");

        Assert.NotNull(job);
        Assert.Equal("unknown", job.Methodology.Id);
        Assert.Empty(job.Methodology.Phases);
    }

    // ── Entity mapping: null JSON fields → graceful default ─────────────

    [Fact]
    public async Task GetJobAsync_NullJsonContent_FallsBackToDefaults()
    {
        SeedJobRaw("null-json", "completed", taskBriefJson: "null", methodologyJson: "null");

        var service = CreateService();
        var job = await service.GetJobAsync("null-json");

        Assert.NotNull(job);
        Assert.Equal("unknown", job.TaskBrief.TaskId);
        Assert.Equal("unknown", job.Methodology.Id);
    }

    // ── ListJobsAsync: ordering ─────────────────────────────────────────

    [Fact]
    public async Task ListJobsAsync_ReturnsMostRecentFirst()
    {
        SeedJob("old", "completed", createdAt: DateTime.UtcNow.AddHours(-3));
        SeedJob("mid", "completed", createdAt: DateTime.UtcNow.AddHours(-2));
        SeedJob("new", "completed", createdAt: DateTime.UtcNow.AddHours(-1));

        var service = CreateService();
        var jobs = await service.ListJobsAsync();

        Assert.Equal(3, jobs.Count);
        Assert.Equal("new", jobs[0].JobId);
        Assert.Equal("mid", jobs[1].JobId);
        Assert.Equal("old", jobs[2].JobId);
    }

    // ── Full lifecycle: persist → "restart" → recover → visible ─────────

    [Fact]
    public async Task FullLifecycle_PersistThenRecoverMaintainsJobData()
    {
        // Start a job with the first service instance
        var service1 = CreateService();
        var job = await service1.StartRunAsync(
            new TaskBrief { TaskId = "lifecycle-t1", Title = "Lifecycle Test", Description = "End-to-end" },
            new MethodologyDefinition
            {
                Id = "lifecycle-meth",
                Phases =
                [
                    new PhaseDefinition
                    {
                        Id = "req", Goal = "g", Inputs = [],
                        OutputSchema = "requirements/v1", Instructions = "i"
                    }
                ]
            });

        Assert.Equal(ForgeJobStatus.Queued, job.Status);

        // Simulate server restart: create a new service instance (new active cache)
        var service2 = CreateService();
        await service2.RecoverJobsAsync();

        // Job should be accessible and re-enqueued
        var recovered = await service2.GetJobAsync(job.JobId);
        Assert.NotNull(recovered);
        Assert.Equal(ForgeJobStatus.Queued, recovered.Status);
        Assert.Equal("lifecycle-t1", recovered.TaskBrief.TaskId);
        Assert.Equal("lifecycle-meth", recovered.Methodology.Id);
    }

    // ── StartRunAsync: queue full → marks failed and throws ────────────

    [Fact]
    public async Task StartRunAsync_QueueFull_JobIsMarkedFailedInDb()
    {
        // Fill the queue (bounded capacity = 100)
        var service = CreateService();
        var tasks = new List<Task>();
        for (var i = 0; i < 100; i++)
        {
            tasks.Add(service.StartRunAsync(
                new TaskBrief { TaskId = $"fill-{i}", Title = "Fill", Description = "Filling queue" },
                MinimalMethodology));
        }
        await Task.WhenAll(tasks);

        // The 101st should fail
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartRunAsync(
                new TaskBrief { TaskId = "overflow", Title = "Overflow", Description = "Should fail" },
                MinimalMethodology));

        Assert.Contains("queue is full", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The overflow job should be persisted as failed
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var overflowEntity = await db.ForgeJobs
            .FirstOrDefaultAsync(e => e.TaskBriefJson.Contains("overflow"));
        Assert.NotNull(overflowEntity);
        Assert.Equal("failed", overflowEntity.Status);
    }

    // ── GetJobAsync: active cache takes precedence over DB ──────────────

    [Fact]
    public async Task GetJobAsync_ActiveCacheTakesPrecedenceOverDb()
    {
        var service = CreateService();
        var job = await service.StartRunAsync(
            new TaskBrief { TaskId = "cached", Title = "Cached", Description = "In cache" },
            MinimalMethodology);

        // Tamper with DB directly (set a different status)
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var entity = await db.ForgeJobs.FindAsync(job.JobId);
            entity!.Status = "completed";
            await db.SaveChangesAsync();
        }

        // GetJobAsync should return from cache (still Queued), not DB (Completed)
        var retrieved = await service.GetJobAsync(job.JobId);
        Assert.NotNull(retrieved);
        Assert.Equal(ForgeJobStatus.Queued, retrieved.Status);
    }

    // ── Entity mapping: all fields roundtrip correctly ──────────────────

    [Fact]
    public async Task EntityToJob_RoundtripsAllFields()
    {
        var now = DateTime.UtcNow;
        SeedJobRaw("roundtrip", "completed",
            runId: "R_01HWXYZ123456789ABCDEFGH",
            error: "some error",
            taskBriefJson: """{"taskId":"rt-task","title":"Roundtrip","description":"Full roundtrip test"}""",
            methodologyJson: """{"id":"rt-meth","phases":[{"id":"req","goal":"g","inputs":[],"output_schema":"requirements/v1","instructions":"i"}]}""",
            createdAt: now.AddHours(-2),
            startedAt: now.AddHours(-1),
            completedAt: now);

        var service = CreateService();
        var job = await service.GetJobAsync("roundtrip");

        Assert.NotNull(job);
        Assert.Equal("roundtrip", job.JobId);
        Assert.Equal("R_01HWXYZ123456789ABCDEFGH", job.RunId);
        Assert.Equal(ForgeJobStatus.Completed, job.Status);
        Assert.Equal("some error", job.Error);
        Assert.Equal("rt-task", job.TaskBrief.TaskId);
        Assert.Equal("Roundtrip", job.TaskBrief.Title);
        Assert.Equal("rt-meth", job.Methodology.Id);
        Assert.Single(job.Methodology.Phases);
    }

    // ── Interrupted status roundtrips ────────────────────────────────────

    [Fact]
    public async Task GetJobAsync_InterruptedStatus_RoundtripsCorrectly()
    {
        SeedJob("int1", "interrupted", error: "Server restarted during execution.");

        var service = CreateService();
        var job = await service.GetJobAsync("int1");

        Assert.NotNull(job);
        Assert.Equal(ForgeJobStatus.Interrupted, job.Status);
        Assert.Equal("Server restarted during execution.", job.Error);
    }

    // ════════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════════

    private static readonly string MinimalTaskBriefJson =
        """{"taskId":"t1","title":"Test","description":"D"}""";

    private static readonly string MinimalMethodologyJson =
        """{"id":"test","phases":[{"id":"req","goal":"g","inputs":[],"output_schema":"requirements/v1","instructions":"i"}]}""";

    private static readonly MethodologyDefinition MinimalMethodology = new()
    {
        Id = "test",
        Phases =
        [
            new PhaseDefinition
            {
                Id = "req", Goal = "g", Inputs = [],
                OutputSchema = "requirements/v1", Instructions = "i"
            }
        ]
    };

    private void SeedJob(
        string id,
        string status,
        string? error = null,
        DateTime? createdAt = null,
        DateTime? startedAt = null,
        DateTime? completedAt = null)
    {
        SeedJobRaw(id, status,
            taskBriefJson: MinimalTaskBriefJson,
            methodologyJson: MinimalMethodologyJson,
            error: error,
            createdAt: createdAt,
            startedAt: startedAt,
            completedAt: completedAt);
    }

    private void SeedJobRaw(
        string id,
        string status,
        string taskBriefJson = "{}",
        string methodologyJson = "{}",
        string? runId = null,
        string? error = null,
        DateTime? createdAt = null,
        DateTime? startedAt = null,
        DateTime? completedAt = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.ForgeJobs.Add(new ForgeJobEntity
        {
            Id = id,
            RunId = runId,
            Status = status,
            Error = error,
            TaskBriefJson = taskBriefJson,
            MethodologyJson = methodologyJson,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            StartedAt = startedAt,
            CompletedAt = completedAt
        });
        db.SaveChanges();
    }

    private ForgeRunService CreateService(bool executionAvailable = true)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"forge-edge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        _tempDirs.Add(tempDir);

        var options = new ForgeOptions
        {
            Enabled = true,
            OpenAiApiKey = executionAvailable ? "test-key" : ""
        };

        var runStore = new DiskRunStore(tempDir, NullLogger<DiskRunStore>.Instance);
        var artifactStore = new DiskArtifactStore(
            Path.Combine(tempDir, "artifacts"),
            NullLogger<DiskArtifactStore>.Instance);
        var schemaRegistry = new SchemaRegistry();
        var llmClient = new StubLlmClient();
        var promptBuilder = new AgentAcademy.Forge.Prompt.PromptBuilder(schemaRegistry);
        var structValidator = new AgentAcademy.Forge.Validation.StructuralValidator(schemaRegistry);
        var semValidator = new AgentAcademy.Forge.Validation.SemanticValidator(
            llmClient, NullLogger<AgentAcademy.Forge.Validation.SemanticValidator>.Instance);
        var crossValidator = new AgentAcademy.Forge.Validation.CrossArtifactValidator();
        var validatorPipeline = new AgentAcademy.Forge.Validation.ValidatorPipeline(
            structValidator, semValidator, crossValidator, schemaRegistry);
        var costCalculator = new AgentAcademy.Forge.Costs.CostCalculator();
        var phaseExecutor = new PhaseExecutor(
            llmClient, promptBuilder, validatorPipeline, artifactStore, runStore,
            costCalculator, TimeProvider.System, NullLogger<PhaseExecutor>.Instance);
        var pipelineRunner = new PipelineRunner(
            phaseExecutor, artifactStore, runStore, schemaRegistry,
            costCalculator, TimeProvider.System,
            NullLogger<PipelineRunner>.Instance);

        return new ForgeRunService(
            pipelineRunner, options,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            new ActivityBroadcaster(),
            NullLogger<ForgeRunService>.Instance);
    }
}
