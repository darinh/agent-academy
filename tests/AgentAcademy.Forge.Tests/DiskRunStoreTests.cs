using System.Text.Json;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Forge.Tests;

public sealed class DiskRunStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiskRunStore _store;

    public DiskRunStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"forge-run-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new DiskRunStore(_tempDir, NullLogger<DiskRunStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static RunTrace CreateRunTrace(string runId, string outcome = "Pending") => new()
    {
        RunId = runId,
        TaskId = "T1-test",
        MethodologyVersion = "1",
        StartedAt = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc),
        Outcome = outcome,
        PipelineTokens = new TokenCount { In = 0, Out = 0 },
        ControlTokens = new TokenCount { In = 0, Out = 0 },
        FinalArtifactHashes = new Dictionary<string, string>()
    };

    private static TaskBrief CreateTaskBrief() => new()
    {
        TaskId = "T1-test",
        Title = "Test Task",
        Description = "A test task for unit testing."
    };

    private static MethodologyDefinition CreateMethodology() => new()
    {
        Id = "test-v1",
        MaxAttemptsDefault = 3,
        Phases = new[]
        {
            new PhaseDefinition
            {
                Id = "requirements",
                Goal = "Test goal",
                Inputs = Array.Empty<string>(),
                OutputSchema = "requirements/v1",
                Instructions = "Test instructions"
            }
        }
    };

    private static PhaseRunTrace CreatePhaseRunTrace(string phaseId = "requirements") => new()
    {
        PhaseId = phaseId,
        ArtifactType = "requirements",
        StateTransitions = new[]
        {
            new StateTransition { From = null, To = "Pending", At = DateTime.UtcNow },
            new StateTransition { From = "Pending", To = "Running", At = DateTime.UtcNow }
        },
        Attempts = Array.Empty<AttemptTrace>(),
        InputArtifactHashes = Array.Empty<string>(),
        OutputArtifactHashes = Array.Empty<string>()
    };

    // --- InitializeRunAsync tests ---

    [Fact]
    public async Task InitializeRunAsync_CreatesRunDirectory()
    {
        var runId = "R_TEST001";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId), CreateTaskBrief(), CreateMethodology());

        Assert.True(Directory.Exists(Path.Combine(_tempDir, "runs", runId)));
    }

    [Fact]
    public async Task InitializeRunAsync_WritesRunJson()
    {
        var runId = "R_TEST002";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId), CreateTaskBrief(), CreateMethodology());

        Assert.True(File.Exists(Path.Combine(_tempDir, "runs", runId, "run.json")));
    }

    [Fact]
    public async Task InitializeRunAsync_WritesTaskJson()
    {
        var runId = "R_TEST003";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId), CreateTaskBrief(), CreateMethodology());

        Assert.True(File.Exists(Path.Combine(_tempDir, "runs", runId, "task.json")));
    }

    [Fact]
    public async Task InitializeRunAsync_WritesMethodologyJson()
    {
        var runId = "R_TEST004";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId), CreateTaskBrief(), CreateMethodology());

        Assert.True(File.Exists(Path.Combine(_tempDir, "runs", runId, "methodology.json")));
    }

    [Fact]
    public async Task InitializeRunAsync_CreatesPhasesDirectory()
    {
        var runId = "R_TEST005";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId), CreateTaskBrief(), CreateMethodology());

        Assert.True(Directory.Exists(Path.Combine(_tempDir, "runs", runId, "phases")));
    }

    // --- ReadRunAsync tests ---

    [Fact]
    public async Task ReadRunAsync_RoundTrips()
    {
        var runId = "R_TEST010";
        var run = CreateRunTrace(runId);
        await _store.InitializeRunAsync(runId, run, CreateTaskBrief(), CreateMethodology());

        var read = await _store.ReadRunAsync(runId);

        Assert.NotNull(read);
        Assert.Equal(runId, read.RunId);
        Assert.Equal("T1-test", read.TaskId);
        Assert.Equal("Pending", read.Outcome);
    }

    [Fact]
    public async Task ReadRunAsync_NonExistent_ReturnsNull()
    {
        var result = await _store.ReadRunAsync("R_DOESNOTEXIST");
        Assert.Null(result);
    }

    // --- WriteRunSnapshotAsync tests ---

    [Fact]
    public async Task WriteRunSnapshotAsync_UpdatesRunJson()
    {
        var runId = "R_TEST020";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId, "Pending"), CreateTaskBrief(), CreateMethodology());

        var updated = CreateRunTrace(runId, "Running");
        await _store.WriteRunSnapshotAsync(runId, updated);

        var read = await _store.ReadRunAsync(runId);
        Assert.Equal("Running", read!.Outcome);
    }

    // --- PhaseRunScratch tests ---

    [Fact]
    public async Task WritePhaseRunScratchAsync_CreatesPhaseDirectory()
    {
        var runId = "R_TEST030";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId), CreateTaskBrief(), CreateMethodology());

        await _store.WritePhaseRunScratchAsync(runId, 0, "requirements", CreatePhaseRunTrace());

        Assert.True(Directory.Exists(Path.Combine(_tempDir, "runs", runId, "phases", "01-requirements")));
    }

    [Fact]
    public async Task WritePhaseRunScratchAsync_CreatesPhaseRunJson()
    {
        var runId = "R_TEST031";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId), CreateTaskBrief(), CreateMethodology());

        await _store.WritePhaseRunScratchAsync(runId, 0, "requirements", CreatePhaseRunTrace());

        Assert.True(File.Exists(Path.Combine(_tempDir, "runs", runId, "phases", "01-requirements", "phase-run.json")));
    }

    [Fact]
    public async Task ReadPhaseRunScratchAsync_RoundTrips()
    {
        var runId = "R_TEST032";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId), CreateTaskBrief(), CreateMethodology());

        var phaseRun = CreatePhaseRunTrace();
        await _store.WritePhaseRunScratchAsync(runId, 0, "requirements", phaseRun);

        var read = await _store.ReadPhaseRunScratchAsync(runId, 0, "requirements");
        Assert.NotNull(read);
        Assert.Equal("requirements", read.PhaseId);
    }

    [Fact]
    public async Task WritePhaseRunScratchAsync_UnderscorePhaseId_UsesHyphensInDir()
    {
        var runId = "R_TEST033";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId), CreateTaskBrief(), CreateMethodology());

        await _store.WritePhaseRunScratchAsync(runId, 2, "function_design", CreatePhaseRunTrace("function_design"));

        Assert.True(Directory.Exists(Path.Combine(_tempDir, "runs", runId, "phases", "03-function-design")));
    }

    // --- PhaseRunsRollup tests ---

    [Fact]
    public async Task WritePhaseRunsRollupAsync_CreatesPhaseRunsJson()
    {
        var runId = "R_TEST040";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId), CreateTaskBrief(), CreateMethodology());

        await _store.WritePhaseRunsRollupAsync(runId, new[] { CreatePhaseRunTrace() });

        Assert.True(File.Exists(Path.Combine(_tempDir, "runs", runId, "phase-runs.json")));
    }

    [Fact]
    public async Task ReadPhaseRunsRollupAsync_RoundTrips()
    {
        var runId = "R_TEST041";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId), CreateTaskBrief(), CreateMethodology());

        var phaseRuns = new[] { CreatePhaseRunTrace() };
        await _store.WritePhaseRunsRollupAsync(runId, phaseRuns);

        var read = await _store.ReadPhaseRunsRollupAsync(runId);
        Assert.NotNull(read);
        Assert.Single(read);
        Assert.Equal("requirements", read[0].PhaseId);
    }

    [Fact]
    public async Task ReadPhaseRunsRollupAsync_NonExistent_ReturnsNull()
    {
        var runId = "R_TEST042";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId), CreateTaskBrief(), CreateMethodology());

        var result = await _store.ReadPhaseRunsRollupAsync(runId);
        Assert.Null(result);
    }

    // --- AttemptFiles tests ---

    [Fact]
    public async Task WriteAttemptFilesAsync_CreatesAttemptDirectory()
    {
        var runId = "R_TEST050";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId), CreateTaskBrief(), CreateMethodology());

        var files = new AttemptFiles { PromptText = "test prompt" };
        await _store.WriteAttemptFilesAsync(runId, 0, "requirements", 1, files);

        Assert.True(Directory.Exists(
            Path.Combine(_tempDir, "runs", runId, "phases", "01-requirements", "attempts", "01")));
    }

    [Fact]
    public async Task WriteAttemptFilesAsync_WritesAllFiles()
    {
        var runId = "R_TEST051";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId), CreateTaskBrief(), CreateMethodology());

        var files = new AttemptFiles
        {
            PromptText = "the prompt",
            ResponseRaw = "the response",
            ResponseParsedJson = """{"parsed": true}""",
            ValidatorReportJson = """{"valid": true}""",
            MetaJson = """{"model": "test"}"""
        };
        await _store.WriteAttemptFilesAsync(runId, 0, "requirements", 1, files);

        var attemptDir = Path.Combine(_tempDir, "runs", runId, "phases", "01-requirements", "attempts", "01");
        Assert.True(File.Exists(Path.Combine(attemptDir, "prompt.txt")));
        Assert.True(File.Exists(Path.Combine(attemptDir, "response.raw.txt")));
        Assert.True(File.Exists(Path.Combine(attemptDir, "response.parsed.json")));
        Assert.True(File.Exists(Path.Combine(attemptDir, "validator-report.json")));
        Assert.True(File.Exists(Path.Combine(attemptDir, "meta.json")));
    }

    [Fact]
    public async Task WriteAttemptFilesAsync_PartialFiles_WritesOnlyProvided()
    {
        var runId = "R_TEST052";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId), CreateTaskBrief(), CreateMethodology());

        var files = new AttemptFiles { PromptText = "only prompt" };
        await _store.WriteAttemptFilesAsync(runId, 0, "requirements", 1, files);

        var attemptDir = Path.Combine(_tempDir, "runs", runId, "phases", "01-requirements", "attempts", "01");
        Assert.True(File.Exists(Path.Combine(attemptDir, "prompt.txt")));
        Assert.False(File.Exists(Path.Combine(attemptDir, "response.raw.txt")));
    }

    [Fact]
    public async Task WriteAttemptFilesAsync_ContentPreserved()
    {
        var runId = "R_TEST053";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId), CreateTaskBrief(), CreateMethodology());

        var files = new AttemptFiles { PromptText = "hello world\nline 2" };
        await _store.WriteAttemptFilesAsync(runId, 0, "requirements", 1, files);

        var content = await File.ReadAllTextAsync(
            Path.Combine(_tempDir, "runs", runId, "phases", "01-requirements", "attempts", "01", "prompt.txt"));
        Assert.Equal("hello world\nline 2", content);
    }

    // --- ReviewSummary tests ---

    [Fact]
    public async Task WriteReviewSummaryAsync_CreatesFile()
    {
        var runId = "R_TEST060";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId), CreateTaskBrief(), CreateMethodology());

        var summary = new ReviewSummaryTrace
        {
            RunId = runId,
            TaskId = "T1-test",
            PipelineOutcome = "Succeeded"
        };
        await _store.WriteReviewSummaryAsync(runId, summary);

        Assert.True(File.Exists(Path.Combine(_tempDir, "runs", runId, "review-summary.json")));
    }

    // --- TraceEvent tests ---

    [Fact]
    public async Task AppendTraceEventAsync_CreatesNdjsonLog()
    {
        var runId = "R_TEST070";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId), CreateTaskBrief(), CreateMethodology());

        await _store.AppendTraceEventAsync(runId, new { Type = "run_started", RunId = runId });
        await _store.AppendTraceEventAsync(runId, new { Type = "phase_started", PhaseId = "requirements" });

        var content = await File.ReadAllTextAsync(Path.Combine(_tempDir, "runs", runId, "trace.log"));
        var lines = content.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);

        // Each line is valid JSON
        foreach (var line in lines)
        {
            var doc = JsonDocument.Parse(line);
            Assert.NotNull(doc);
        }
    }

    // --- ListRuns tests ---

    [Fact]
    public async Task ListRunsAsync_Empty_ReturnsEmpty()
    {
        var runs = await _store.ListRunsAsync();
        Assert.Empty(runs);
    }

    [Fact]
    public async Task ListRunsAsync_ReturnsAllRuns_Sorted()
    {
        await _store.InitializeRunAsync("R_AAA", CreateRunTrace("R_AAA"), CreateTaskBrief(), CreateMethodology());
        await _store.InitializeRunAsync("R_CCC", CreateRunTrace("R_CCC"), CreateTaskBrief(), CreateMethodology());
        await _store.InitializeRunAsync("R_BBB", CreateRunTrace("R_BBB"), CreateTaskBrief(), CreateMethodology());

        var runs = await _store.ListRunsAsync();

        Assert.Equal(3, runs.Count);
        Assert.Equal("R_AAA", runs[0]);
        Assert.Equal("R_BBB", runs[1]);
        Assert.Equal("R_CCC", runs[2]);
    }

    // --- RunExists tests ---

    [Fact]
    public async Task RunExistsAsync_TrueWhenCreated()
    {
        var runId = "R_TEST080";
        await _store.InitializeRunAsync(runId, CreateRunTrace(runId), CreateTaskBrief(), CreateMethodology());
        Assert.True(await _store.RunExistsAsync(runId));
    }

    [Fact]
    public async Task RunExistsAsync_FalseWhenNotCreated()
    {
        Assert.False(await _store.RunExistsAsync("R_NOPE"));
    }

    // --- GetRunDirectory ---

    [Fact]
    public void GetRunDirectory_ReturnsCorrectPath()
    {
        var dir = _store.GetRunDirectory("R_TESTDIR");
        Assert.Equal(Path.Combine(_tempDir, "runs", "R_TESTDIR"), dir);
    }
}
