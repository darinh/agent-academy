using System.Text.Json;
using AgentAcademy.Forge.Artifacts;
using AgentAcademy.Forge.Execution;
using AgentAcademy.Forge.Llm;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Schemas;
using AgentAcademy.Forge.Storage;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class ForgeControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly ForgeRunService _runService;
    private readonly IRunStore _runStore;
    private readonly IArtifactStore _artifactStore;
    private readonly SchemaRegistry _schemaRegistry;
    private readonly IMethodologyCatalog _methodologyCatalog;
    private readonly ForgeOptions _options;
    private readonly ForgeController _controller;

    public ForgeControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"forge-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _options = new ForgeOptions { Enabled = true, OpenAiApiKey = "test-key" };

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Database.EnsureCreated();
        }

        _runStore = new DiskRunStore(_tempDir, NullLogger<DiskRunStore>.Instance);
        _artifactStore = new DiskArtifactStore(
            Path.Combine(_tempDir, "artifacts"),
            NullLogger<DiskArtifactStore>.Instance);
        _schemaRegistry = new SchemaRegistry();
        _methodologyCatalog = new DiskMethodologyCatalog(
            Path.Combine(_tempDir, "methodologies"),
            NullLogger<DiskMethodologyCatalog>.Instance);

        var pipelineRunner = CreatePipelineRunner();
        _runService = new ForgeRunService(
            pipelineRunner, _options, _serviceProvider.GetRequiredService<IServiceScopeFactory>(), new ActivityBroadcaster(), NullLogger<ForgeRunService>.Instance);

        _controller = new ForgeController(
            _runService, _runStore, _artifactStore, _schemaRegistry, _methodologyCatalog, _options);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private PipelineRunner CreatePipelineRunner()
    {
        var llmClient = new StubLlmClient();
        var schemaRegistry = new SchemaRegistry();
        var promptBuilder = new AgentAcademy.Forge.Prompt.PromptBuilder(schemaRegistry);
        var structValidator = new AgentAcademy.Forge.Validation.StructuralValidator(schemaRegistry);
        var semValidator = new AgentAcademy.Forge.Validation.SemanticValidator(
            llmClient, NullLogger<AgentAcademy.Forge.Validation.SemanticValidator>.Instance);
        var crossValidator = new AgentAcademy.Forge.Validation.CrossArtifactValidator();
        var validatorPipeline = new AgentAcademy.Forge.Validation.ValidatorPipeline(
            structValidator, semValidator, crossValidator, schemaRegistry);
        var costCalculator = new AgentAcademy.Forge.Costs.CostCalculator();
        var phaseExecutor = new PhaseExecutor(
            llmClient, promptBuilder, validatorPipeline, _artifactStore, _runStore,
            costCalculator, TimeProvider.System, NullLogger<PhaseExecutor>.Instance);

        return new PipelineRunner(
            phaseExecutor, _artifactStore, _runStore, schemaRegistry,
            costCalculator, TimeProvider.System,
            NullLogger<PipelineRunner>.Instance);
    }

    private static MethodologyDefinition SimpleMethodology => new()
    {
        Id = "test-simple-v1",
        MaxAttemptsDefault = 1,
        Phases =
        [
            new PhaseDefinition
            {
                Id = "requirements",
                Goal = "Extract requirements",
                Inputs = [],
                OutputSchema = "requirements/v1",
                Instructions = "Produce requirements."
            }
        ]
    };

    // ── Status endpoint ─────────────────────────────────────────────────

    [Fact]
    public async Task GetStatus_ReturnsForgeState()
    {
        var result = await _controller.GetStatus();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"Enabled\":true", json);
        Assert.Contains("\"ExecutionAvailable\":true", json);
    }

    [Fact]
    public async Task GetStatus_WhenNoApiKey_ShowsExecutionUnavailable()
    {
        var noKeyOptions = new ForgeOptions { Enabled = true, OpenAiApiKey = "" };
        var controller = new ForgeController(
            _runService, _runStore, _artifactStore, _schemaRegistry, _methodologyCatalog, noKeyOptions);

        var result = await controller.GetStatus();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"ExecutionAvailable\":false", json);
    }

    // ── Schema endpoint ─────────────────────────────────────────────────

    [Fact]
    public void ListSchemas_ReturnsRegisteredSchemas()
    {
        var result = _controller.ListSchemas();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        // SchemaRegistry has 5 frozen schemas + internal schemas
        Assert.Contains("requirements/v1", json);
        Assert.Contains("contract/v1", json);
    }

    // ── Job endpoints ───────────────────────────────────────────────────

    [Fact]
    public async Task StartRun_Returns202WithJobId()
    {
        var request = new StartForgeRunRequest
        {
            Title = "Test task",
            Description = "Build something",
            Methodology = SimpleMethodology
        };

        var result = await _controller.StartRun(request);

        var accepted = Assert.IsType<AcceptedAtActionResult>(result);
        Assert.Equal(StatusCodes.Status202Accepted, accepted.StatusCode);
        var json = JsonSerializer.Serialize(accepted.Value);
        Assert.Contains("\"JobId\"", json);
        Assert.Contains("\"Status\"", json);
    }

    [Fact]
    public async Task StartRun_WhenNoApiKey_Returns503()
    {
        var noKeyOptions = new ForgeOptions { Enabled = true, OpenAiApiKey = "" };
        var controller = new ForgeController(
            _runService, _runStore, _artifactStore, _schemaRegistry, _methodologyCatalog, noKeyOptions);

        var request = new StartForgeRunRequest
        {
            Title = "Test task",
            Description = "Build something",
            Methodology = SimpleMethodology
        };

        var result = await controller.StartRun(request);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
    }

    [Fact]
    public async Task GetJob_ReturnsJobDetails()
    {
        var request = new StartForgeRunRequest
        {
            Title = "Test task",
            Description = "Build something",
            Methodology = SimpleMethodology
        };

        var startResult = await _controller.StartRun(request) as AcceptedAtActionResult;
        var jobId = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(startResult!.Value)).GetProperty("JobId").GetString()!;

        var result = await _controller.GetJob(jobId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains(jobId, json);
    }

    [Fact]
    public async Task GetJob_WhenNotFound_Returns404()
    {
        var result = await _controller.GetJob("nonexistent");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task ListJobs_ReturnsEmptyListInitially()
    {
        var result = await _controller.ListJobs();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Equal("[]", json);
    }

    // ── Run endpoints (read-only) ───────────────────────────────────────

    [Fact]
    public async Task ListRuns_ReturnsEmptyInitially()
    {
        var result = await _controller.ListRuns(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Equal("[]", json);
    }

    [Fact]
    public async Task GetRun_InvalidRunId_Returns400()
    {
        var result = await _controller.GetRun("invalid-id", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetRun_ValidButMissing_Returns404()
    {
        var runId = "R_" + Ulid.NewUlid().ToString();
        var result = await _controller.GetRun(runId, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetRunPhases_InvalidRunId_Returns400()
    {
        var result = await _controller.GetRunPhases("../../etc/passwd", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Artifact endpoints ──────────────────────────────────────────────

    [Fact]
    public async Task GetArtifact_InvalidHash_Returns400()
    {
        var result = await _controller.GetArtifact("not-a-hash", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetArtifact_ValidButMissing_Returns404()
    {
        var hash = new string('a', 64);
        var result = await _controller.GetArtifact(hash, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetArtifact_AcceptsSha256Prefix()
    {
        var hash = "sha256:" + new string('b', 64);
        var result = await _controller.GetArtifact(hash, CancellationToken.None);

        // Should normalize and look up — returns 404 (not 400) since hash format is valid
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── Resume endpoint ─────────────────────────────────────────────────

    [Fact]
    public void ResumeRun_InvalidRunId_Returns400()
    {
        var result = _controller.ResumeRun("bad-id");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void ResumeRun_ValidRunId_Returns501()
    {
        var runId = "R_" + Ulid.NewUlid().ToString();
        var result = _controller.ResumeRun(runId);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status501NotImplemented, problem.StatusCode);
    }

    [Fact]
    public void ResumeRun_WhenNoApiKey_Returns503()
    {
        var noKeyOptions = new ForgeOptions { Enabled = true, OpenAiApiKey = "" };
        var controller = new ForgeController(
            _runService, _runStore, _artifactStore, _schemaRegistry, _methodologyCatalog, noKeyOptions);

        var runId = "R_" + Ulid.NewUlid().ToString();
        var result = controller.ResumeRun(runId);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
    }
}

public sealed class ForgeRunServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public ForgeRunServiceTests()
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
    }

    // ── Run ID Validation ───────────────────────────────────────────────

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("invalid", false)]
    [InlineData("../../etc/passwd", false)]
    [InlineData("R_short", false)]
    public void IsValidRunId_RejectsInvalid(string? runId, bool expected)
    {
        Assert.Equal(expected, ForgeRunService.IsValidRunId(runId));
    }

    [Fact]
    public void IsValidRunId_AcceptsValidUlid()
    {
        var runId = "R_" + Ulid.NewUlid().ToString();
        Assert.True(ForgeRunService.IsValidRunId(runId));
    }

    // ── Artifact Hash Normalization ─────────────────────────────────────

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("not-hex", null)]
    [InlineData("abc", null)]
    [InlineData("../../traversal", null)]
    public void NormalizeArtifactHash_RejectsInvalid(string? hash, string? expected)
    {
        Assert.Equal(expected, ForgeRunService.NormalizeArtifactHash(hash));
    }

    [Fact]
    public void NormalizeArtifactHash_AcceptsRawHex()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, ForgeRunService.NormalizeArtifactHash(hash));
    }

    [Fact]
    public void NormalizeArtifactHash_StripsSha256Prefix()
    {
        var rawHash = new string('b', 64);
        var prefixed = "sha256:" + rawHash;
        Assert.Equal(rawHash, ForgeRunService.NormalizeArtifactHash(prefixed));
    }

    // ── Job management ──────────────────────────────────────────────────

    [Fact]
    public async Task GetJobAsync_ReturnsNullForUnknownJob()
    {
        var service = CreateService();
        Assert.Null(await service.GetJobAsync("nonexistent"));
    }

    [Fact]
    public async Task ListJobsAsync_EmptyInitially()
    {
        var service = CreateService();
        Assert.Empty(await service.ListJobsAsync());
    }

    [Fact]
    public async Task StartRunAsync_WhenNoApiKey_Throws()
    {
        var service = CreateService(executionAvailable: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartRunAsync(
                new TaskBrief { TaskId = "t1", Title = "Test", Description = "Test" },
                new MethodologyDefinition
                {
                    Id = "test", Phases = [new PhaseDefinition
                    {
                        Id = "req", Goal = "g", Inputs = [],
                        OutputSchema = "requirements/v1", Instructions = "i"
                    }]
                }));
    }

    [Fact]
    public async Task StartRunAsync_EnqueuesAndPersistsJob()
    {
        var service = CreateService();

        var job = await service.StartRunAsync(
            new TaskBrief { TaskId = "t1", Title = "Test", Description = "Test" },
            new MethodologyDefinition
            {
                Id = "test", Phases = [new PhaseDefinition
                {
                    Id = "req", Goal = "g", Inputs = [],
                    OutputSchema = "requirements/v1", Instructions = "i"
                }]
            });

        Assert.NotNull(job.JobId);
        Assert.Equal(ForgeJobStatus.Queued, job.Status);
        Assert.Equal("t1", job.TaskBrief.TaskId);
        Assert.NotNull(await service.GetJobAsync(job.JobId));
        Assert.Single(await service.ListJobsAsync());

        // Verify persisted to DB
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var entity = await db.ForgeJobs.FindAsync(job.JobId);
        Assert.NotNull(entity);
        Assert.Equal("queued", entity.Status);
        Assert.Contains("t1", entity.TaskBriefJson);
    }

    [Fact]
    public async Task RecoverJobsAsync_MarksRunningAsInterrupted()
    {
        // Seed a "running" job directly in DB (simulating a crash)
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.ForgeJobs.Add(new Data.Entities.ForgeJobEntity
            {
                Id = "crashed-job",
                Status = "running",
                TaskBriefJson = """{"taskId":"t1","title":"T","description":"D"}""",
                MethodologyJson = """{"id":"test","phases":[{"id":"r","goal":"g","inputs":[],"output_schema":"requirements/v1","instructions":"i"}]}""",
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                StartedAt = DateTime.UtcNow.AddMinutes(-4)
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService();
        await service.RecoverJobsAsync();

        // Verify the job was marked interrupted
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var entity = await db.ForgeJobs.FindAsync("crashed-job");
            Assert.NotNull(entity);
            Assert.Equal("interrupted", entity.Status);
            Assert.Contains("Server restarted", entity.Error);
            Assert.NotNull(entity.CompletedAt);
        }
    }

    [Fact]
    public async Task RecoverJobsAsync_ReenqueuesQueuedJobs()
    {
        // Seed a "queued" job directly in DB
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.ForgeJobs.Add(new Data.Entities.ForgeJobEntity
            {
                Id = "queued-job",
                Status = "queued",
                TaskBriefJson = """{"taskId":"t2","title":"T","description":"D"}""",
                MethodologyJson = """{"id":"test","phases":[{"id":"r","goal":"g","inputs":[],"output_schema":"requirements/v1","instructions":"i"}]}""",
                CreatedAt = DateTime.UtcNow.AddMinutes(-3)
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService();
        await service.RecoverJobsAsync();

        // Verify the job is available in the active cache
        var job = await service.GetJobAsync("queued-job");
        Assert.NotNull(job);
        Assert.Equal(ForgeJobStatus.Queued, job.Status);
        Assert.Equal("t2", job.TaskBrief.TaskId);
    }

    [Fact]
    public async Task GetJobAsync_FallsBackToDbForCompletedJobs()
    {
        // Seed a completed job directly in DB (not in active cache)
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.ForgeJobs.Add(new Data.Entities.ForgeJobEntity
            {
                Id = "old-job",
                RunId = "R_01HWXYZ123456789ABCDEFGH",
                Status = "completed",
                TaskBriefJson = """{"taskId":"t3","title":"Old Task","description":"Done"}""",
                MethodologyJson = """{"id":"test","phases":[{"id":"r","goal":"g","inputs":[],"output_schema":"requirements/v1","instructions":"i"}]}""",
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                CompletedAt = DateTime.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService();
        var job = await service.GetJobAsync("old-job");

        Assert.NotNull(job);
        Assert.Equal(ForgeJobStatus.Completed, job.Status);
        Assert.Equal("t3", job.TaskBrief.TaskId);
        Assert.Equal("R_01HWXYZ123456789ABCDEFGH", job.RunId);
    }

    private ForgeRunService CreateService(bool executionAvailable = true)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"forge-svc-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

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

        return new ForgeRunService(pipelineRunner, options, _serviceProvider.GetRequiredService<IServiceScopeFactory>(), new ActivityBroadcaster(), NullLogger<ForgeRunService>.Instance);
    }
}
