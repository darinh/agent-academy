using AgentAcademy.Forge.Models;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for Forge agent command handlers: RunForgeHandler, ForgeStatusHandler, ListForgeRunsHandler.
/// Uses a stub IForgeJobService for isolation.
/// </summary>
public sealed class ForgeCommandHandlerTests : IDisposable
{
    private readonly string _tempDir;

    public ForgeCommandHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"forge-cmd-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ==================== Test Infrastructure ====================

    private sealed class StubForgeJobService : IForgeJobService
    {
        private readonly List<ForgeJob> _jobs = [];
        public bool ThrowQueueFull { get; set; }

        public Task<ForgeJob> StartRunAsync(TaskBrief task, MethodologyDefinition methodology)
        {
            if (ThrowQueueFull)
                throw new InvalidOperationException("Run queue is full. Try again later.");

            var job = new ForgeJob
            {
                JobId = Guid.NewGuid().ToString("N")[..12],
                TaskBrief = task,
                Methodology = methodology,
                Status = ForgeJobStatus.Queued,
                CreatedAt = DateTime.UtcNow
            };
            _jobs.Add(job);
            return Task.FromResult(job);
        }

        public ForgeJob? GetJob(string jobId) => _jobs.FirstOrDefault(j => j.JobId == jobId);

        public IReadOnlyList<ForgeJob> ListJobs() => _jobs.OrderByDescending(j => j.CreatedAt).ToList();

        public ForgeJob AddJob(ForgeJobStatus status, string? error = null, string? runId = null)
        {
            var job = new ForgeJob
            {
                JobId = Guid.NewGuid().ToString("N")[..12],
                TaskBrief = new TaskBrief { TaskId = "t1", Title = "Test Task", Description = "A test" },
                Methodology = MakeMinimalMethodology(),
                Status = status,
                Error = error,
                RunId = runId,
                CreatedAt = DateTime.UtcNow
            };
            _jobs.Add(job);
            return job;
        }
    }

    private static MethodologyDefinition MakeMinimalMethodology() => new()
    {
        Id = "test-v1",
        Phases = new[]
        {
            new PhaseDefinition
            {
                Id = "requirements",
                Goal = "Extract requirements",
                Inputs = Array.Empty<string>(),
                OutputSchema = "requirements/v1",
                Instructions = "Generate requirements"
            }
        }
    };

    private string WriteMethodologyFile(string? content = null)
    {
        content ??= """
        {
            "id": "test-v1",
            "phases": [
                {
                    "id": "requirements",
                    "goal": "Extract requirements",
                    "inputs": [],
                    "output_schema": "requirements/v1",
                    "instructions": "Generate requirements"
                }
            ]
        }
        """;
        var path = Path.Combine(_tempDir, "methodology.json");
        File.WriteAllText(path, content);
        return path;
    }

    private (CommandEnvelope command, CommandContext context) MakeCommand(
        string commandName,
        Dictionary<string, object?> args,
        ForgeOptions? options = null,
        StubForgeJobService? jobService = null)
    {
        options ??= new ForgeOptions { Enabled = true, OpenAiApiKey = "test-key" };
        jobService ??= new StubForgeJobService();

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton<IForgeJobService>(jobService);
        var sp = services.BuildServiceProvider();

        var cmd = new CommandEnvelope(
            Command: commandName,
            Args: args,
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: $"cmd-{Guid.NewGuid():N}",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: "engineer-1"
        );
        var ctx = new CommandContext(
            AgentId: "engineer-1",
            AgentName: "Hephaestus",
            AgentRole: "SoftwareEngineer",
            RoomId: "main",
            BreakoutRoomId: null,
            Services: sp,
            WorkingDirectory: _tempDir
        );
        return (cmd, ctx);
    }

    // ==================== RUN_FORGE: Handler Properties ====================

    [Fact]
    public void RunForge_CommandName()
    {
        var handler = new RunForgeHandler();
        Assert.Equal("RUN_FORGE", handler.CommandName);
    }

    [Fact]
    public void RunForge_IsNotRetrySafe()
    {
        ICommandHandler handler = new RunForgeHandler();
        Assert.False(handler.IsRetrySafe);
    }

    [Fact]
    public void RunForge_IsNotDestructive()
    {
        ICommandHandler handler = new RunForgeHandler();
        Assert.False(handler.IsDestructive);
    }

    // ==================== RUN_FORGE: Validation ====================

    [Fact]
    public async Task RunForge_ForgeDisabled_ReturnsError()
    {
        var options = new ForgeOptions { Enabled = false };
        var (cmd, ctx) = MakeCommand("RUN_FORGE", new Dictionary<string, object?>
        {
            ["title"] = "Test",
            ["description"] = "Test desc",
            ["methodologyPath"] = "methodology.json"
        }, options);

        var handler = new RunForgeHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Execution, result.ErrorCode);
        Assert.Contains("disabled", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunForge_NoApiKey_ReturnsError()
    {
        var options = new ForgeOptions { Enabled = true, OpenAiApiKey = null };
        var (cmd, ctx) = MakeCommand("RUN_FORGE", new Dictionary<string, object?>
        {
            ["title"] = "Test",
            ["description"] = "Test desc",
            ["methodologyPath"] = "methodology.json"
        }, options);

        var handler = new RunForgeHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("unavailable", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunForge_MissingTitle_ReturnsValidationError()
    {
        var (cmd, ctx) = MakeCommand("RUN_FORGE", new Dictionary<string, object?>
        {
            ["description"] = "Test desc",
            ["methodologyPath"] = "methodology.json"
        });

        var handler = new RunForgeHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("title", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunForge_MissingDescription_ReturnsValidationError()
    {
        var (cmd, ctx) = MakeCommand("RUN_FORGE", new Dictionary<string, object?>
        {
            ["title"] = "Test",
            ["methodologyPath"] = "methodology.json"
        });

        var handler = new RunForgeHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("description", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunForge_MissingMethodologyPath_ReturnsValidationError()
    {
        var (cmd, ctx) = MakeCommand("RUN_FORGE", new Dictionary<string, object?>
        {
            ["title"] = "Test",
            ["description"] = "Test desc"
        });

        var handler = new RunForgeHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("methodologyPath", result.Error!);
    }

    [Fact]
    public async Task RunForge_PathTraversal_ReturnsDenied()
    {
        var (cmd, ctx) = MakeCommand("RUN_FORGE", new Dictionary<string, object?>
        {
            ["title"] = "Test",
            ["description"] = "Test desc",
            ["methodologyPath"] = "../../../etc/passwd"
        });

        var handler = new RunForgeHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
        Assert.Contains("outside", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunForge_FileNotFound_ReturnsNotFound()
    {
        var (cmd, ctx) = MakeCommand("RUN_FORGE", new Dictionary<string, object?>
        {
            ["title"] = "Test",
            ["description"] = "Test desc",
            ["methodologyPath"] = "nonexistent.json"
        });

        var handler = new RunForgeHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task RunForge_InvalidJson_ReturnsValidationError()
    {
        var path = WriteMethodologyFile("{ not valid json }}}");
        var (cmd, ctx) = MakeCommand("RUN_FORGE", new Dictionary<string, object?>
        {
            ["title"] = "Test",
            ["description"] = "Test desc",
            ["methodologyPath"] = "methodology.json"
        });

        var handler = new RunForgeHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("Invalid methodology", result.Error!);
    }

    [Fact]
    public async Task RunForge_MethodologyWithNoPhases_ReturnsValidationError()
    {
        WriteMethodologyFile("""{ "id": "test", "phases": [] }""");
        var (cmd, ctx) = MakeCommand("RUN_FORGE", new Dictionary<string, object?>
        {
            ["title"] = "Test",
            ["description"] = "Test desc",
            ["methodologyPath"] = "methodology.json"
        });

        var handler = new RunForgeHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("phase", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunForge_NullJsonContent_ReturnsValidationError()
    {
        WriteMethodologyFile("null");
        var (cmd, ctx) = MakeCommand("RUN_FORGE", new Dictionary<string, object?>
        {
            ["title"] = "Test",
            ["description"] = "Test desc",
            ["methodologyPath"] = "methodology.json"
        });

        var handler = new RunForgeHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("null", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ==================== RUN_FORGE: Success ====================

    [Fact]
    public async Task RunForge_ValidRequest_ReturnsJobId()
    {
        WriteMethodologyFile();
        var stub = new StubForgeJobService();
        var (cmd, ctx) = MakeCommand("RUN_FORGE", new Dictionary<string, object?>
        {
            ["title"] = "Build feature",
            ["description"] = "Build the login feature",
            ["methodologyPath"] = "methodology.json"
        }, jobService: stub);

        var handler = new RunForgeHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.NotNull(dict["jobId"]);
        Assert.Equal("queued", dict["status"]);
        Assert.NotNull(dict["taskId"]);
        Assert.Contains("FORGE_STATUS", dict["message"]!.ToString()!);
    }

    [Fact]
    public async Task RunForge_WithCustomTaskId_UsesProvidedId()
    {
        WriteMethodologyFile();
        var stub = new StubForgeJobService();
        var (cmd, ctx) = MakeCommand("RUN_FORGE", new Dictionary<string, object?>
        {
            ["title"] = "Test",
            ["description"] = "Test desc",
            ["methodologyPath"] = "methodology.json",
            ["taskId"] = "my-task-42"
        }, jobService: stub);

        var handler = new RunForgeHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("my-task-42", dict["taskId"]);
    }

    [Fact]
    public async Task RunForge_QueueFull_ReturnsError()
    {
        WriteMethodologyFile();
        var stub = new StubForgeJobService { ThrowQueueFull = true };
        var (cmd, ctx) = MakeCommand("RUN_FORGE", new Dictionary<string, object?>
        {
            ["title"] = "Test",
            ["description"] = "Test desc",
            ["methodologyPath"] = "methodology.json"
        }, jobService: stub);

        var handler = new RunForgeHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("queue is full", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ==================== FORGE_STATUS: Handler Properties ====================

    [Fact]
    public void ForgeStatus_CommandName()
    {
        var handler = new ForgeStatusHandler();
        Assert.Equal("FORGE_STATUS", handler.CommandName);
    }

    [Fact]
    public void ForgeStatus_IsRetrySafe()
    {
        var handler = new ForgeStatusHandler();
        Assert.True(handler.IsRetrySafe);
    }

    // ==================== FORGE_STATUS: Disabled ====================

    [Fact]
    public async Task ForgeStatus_Disabled_ReturnsError()
    {
        var options = new ForgeOptions { Enabled = false };
        var (cmd, ctx) = MakeCommand("FORGE_STATUS", new(), options);

        var handler = new ForgeStatusHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("disabled", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ==================== FORGE_STATUS: Engine Summary ====================

    [Fact]
    public async Task ForgeStatus_NoJobId_ReturnsEngineSummary()
    {
        var stub = new StubForgeJobService();
        stub.AddJob(ForgeJobStatus.Completed);
        stub.AddJob(ForgeJobStatus.Running);
        stub.AddJob(ForgeJobStatus.Failed, error: "oops");

        var (cmd, ctx) = MakeCommand("FORGE_STATUS", new(), jobService: stub);

        var handler = new ForgeStatusHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(true, dict["enabled"]);
        Assert.Equal(true, dict["executionAvailable"]);
        Assert.Equal(3, dict["totalJobs"]);
        Assert.Equal(1, dict["activeJobs"]);
        Assert.Equal(1, dict["completedJobs"]);
        Assert.Equal(1, dict["failedJobs"]);
    }

    // ==================== FORGE_STATUS: Specific Job ====================

    [Fact]
    public async Task ForgeStatus_WithJobId_ReturnsJobDetails()
    {
        var stub = new StubForgeJobService();
        var job = stub.AddJob(ForgeJobStatus.Completed, runId: "R_ABC123");

        var (cmd, ctx) = MakeCommand("FORGE_STATUS", new Dictionary<string, object?>
        {
            ["jobId"] = job.JobId
        }, jobService: stub);

        var handler = new ForgeStatusHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(job.JobId, dict["jobId"]);
        Assert.Equal("R_ABC123", dict["runId"]);
        Assert.Equal("completed", dict["status"]);
        Assert.Equal("Test Task", dict["taskTitle"]);
    }

    [Fact]
    public async Task ForgeStatus_UnknownJobId_ReturnsNotFound()
    {
        var stub = new StubForgeJobService();
        var (cmd, ctx) = MakeCommand("FORGE_STATUS", new Dictionary<string, object?>
        {
            ["jobId"] = "nonexistent"
        }, jobService: stub);

        var handler = new ForgeStatusHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    // ==================== LIST_FORGE_RUNS: Handler Properties ====================

    [Fact]
    public void ListForgeRuns_CommandName()
    {
        var handler = new ListForgeRunsHandler();
        Assert.Equal("LIST_FORGE_RUNS", handler.CommandName);
    }

    [Fact]
    public void ListForgeRuns_IsRetrySafe()
    {
        var handler = new ListForgeRunsHandler();
        Assert.True(handler.IsRetrySafe);
    }

    // ==================== LIST_FORGE_RUNS: Disabled ====================

    [Fact]
    public async Task ListForgeRuns_Disabled_ReturnsError()
    {
        var options = new ForgeOptions { Enabled = false };
        var (cmd, ctx) = MakeCommand("LIST_FORGE_RUNS", new(), options);

        var handler = new ListForgeRunsHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("disabled", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ==================== LIST_FORGE_RUNS: Listing ====================

    [Fact]
    public async Task ListForgeRuns_NoFilter_ReturnsAllJobs()
    {
        var stub = new StubForgeJobService();
        stub.AddJob(ForgeJobStatus.Completed);
        stub.AddJob(ForgeJobStatus.Running);
        stub.AddJob(ForgeJobStatus.Failed);

        var (cmd, ctx) = MakeCommand("LIST_FORGE_RUNS", new(), jobService: stub);

        var handler = new ListForgeRunsHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(3, (int)dict["count"]!);
        var jobs = (List<Dictionary<string, object?>>)dict["jobs"]!;
        Assert.Equal(3, jobs.Count);
    }

    [Fact]
    public async Task ListForgeRuns_FilterByStatus_ReturnsFiltered()
    {
        var stub = new StubForgeJobService();
        stub.AddJob(ForgeJobStatus.Completed);
        stub.AddJob(ForgeJobStatus.Running);
        stub.AddJob(ForgeJobStatus.Completed);

        var (cmd, ctx) = MakeCommand("LIST_FORGE_RUNS", new Dictionary<string, object?>
        {
            ["status"] = "completed"
        }, jobService: stub);

        var handler = new ListForgeRunsHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(2, (int)dict["count"]!);
    }

    [Fact]
    public async Task ListForgeRuns_InvalidStatus_ReturnsValidationError()
    {
        var (cmd, ctx) = MakeCommand("LIST_FORGE_RUNS", new Dictionary<string, object?>
        {
            ["status"] = "invalid"
        });

        var handler = new ListForgeRunsHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("invalid", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListForgeRuns_EmptyList_ReturnsEmptyArray()
    {
        var stub = new StubForgeJobService();
        var (cmd, ctx) = MakeCommand("LIST_FORGE_RUNS", new(), jobService: stub);

        var handler = new ListForgeRunsHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(0, (int)dict["count"]!);
    }

    [Fact]
    public async Task ListForgeRuns_FilterCaseInsensitive()
    {
        var stub = new StubForgeJobService();
        stub.AddJob(ForgeJobStatus.Running);

        var (cmd, ctx) = MakeCommand("LIST_FORGE_RUNS", new Dictionary<string, object?>
        {
            ["status"] = "RUNNING"
        }, jobService: stub);

        var handler = new ListForgeRunsHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(1, (int)dict["count"]!);
    }

    // ==================== KnownCommands Registration ====================

    [Theory]
    [InlineData("RUN_FORGE")]
    [InlineData("FORGE_STATUS")]
    [InlineData("LIST_FORGE_RUNS")]
    public void KnownCommands_IncludesForgeCommands(string commandName)
    {
        Assert.Contains(commandName, CommandParser.KnownCommands);
    }
}
