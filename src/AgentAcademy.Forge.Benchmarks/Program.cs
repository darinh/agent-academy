using System.Text.Json;
using AgentAcademy.Forge;
using AgentAcademy.Forge.Execution;
using AgentAcademy.Forge.Llm;
using AgentAcademy.Forge.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// --- Flag parsing ---
var seededDefectMode = args.Any(a => a.Equals("--seeded-defects", StringComparison.OrdinalIgnoreCase));
var helpMode = args.Any(a => a is "--help" or "-h");
var customTitle = GetFlagValue(args, "--title");
var customDescription = GetFlagValue(args, "--description");
var flagsWithValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--title", "--description" };
var positionalArgs = GetPositionalArgs(args, flagsWithValues);

if (helpMode)
{
    Console.WriteLine("Usage: forge-benchmarks [methodology.json] [output-dir] [task-filter]");
    Console.WriteLine();
    Console.WriteLine("Arguments:");
    Console.WriteLine("  methodology.json  Path to methodology JSON (default: docs/forge-spike/methodology.json)");
    Console.WriteLine("  output-dir        Root directory for run artifacts (default: ./forge-runs)");
    Console.WriteLine("  task-filter       T1, T2, T3 to run a single benchmark task");
    Console.WriteLine();
    Console.WriteLine("Custom task (standalone mode):");
    Console.WriteLine("  --title \"My Task\"        Task title (required for custom tasks)");
    Console.WriteLine("  --description \"Details\"  Task description");
    Console.WriteLine();
    Console.WriteLine("Other flags:");
    Console.WriteLine("  --seeded-defects  Run seeded-defect benchmark suite");
    Console.WriteLine("  --help, -h        Show this help");
    Console.WriteLine();
    Console.WriteLine("Environment:");
    Console.WriteLine("  OPENAI_API_KEY    Required. Set to your OpenAI API key.");
    return 0;
}

// --- Configuration ---
var methodologyPath = positionalArgs.Length > 0
    ? positionalArgs[0]
    : Path.Combine(FindRepoRoot(), "docs", "forge-spike", "methodology.json");

var forgeRunsRoot = positionalArgs.Length > 1
    ? positionalArgs[1]
    : Path.Combine(FindRepoRoot(), "forge-runs");

var taskFilter = positionalArgs.Length > 2
    ? positionalArgs[2].ToUpperInvariant()
    : null; // Run all tasks if not specified

// --- Load methodology ---
if (!File.Exists(methodologyPath))
{
    Console.Error.WriteLine($"Methodology file not found: {methodologyPath}");
    return 1;
}

var methodologyJson = await File.ReadAllTextAsync(methodologyPath);
var methodology = JsonSerializer.Deserialize<MethodologyDefinition>(methodologyJson)
    ?? throw new InvalidOperationException("Failed to deserialize methodology");

Console.WriteLine($"Loaded methodology: {methodology.Id} ({methodology.Phases.Count} phases)");
Console.WriteLine($"Forge runs directory: {forgeRunsRoot}");
Console.WriteLine();

// --- Build DI container ---
var services = new ServiceCollection();
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});
services.AddForgeEngine(forgeRunsRoot);
services.AddSingleton(TimeProvider.System);

// Register LLM client — OpenAI if API key available, otherwise error
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
{
    services.AddSingleton<ILlmClient>(new OpenAiLlmClient());
    Console.WriteLine("LLM: OpenAI (live API)");
}
else
{
    Console.Error.WriteLine("ERROR: OPENAI_API_KEY environment variable is not set.");
    Console.Error.WriteLine("Set it to run benchmarks: export OPENAI_API_KEY=sk-...");
    return 1;
}

var provider = services.BuildServiceProvider();
var runner = provider.GetRequiredService<PipelineRunner>();

Console.WriteLine();

// --- Seeded-defect mode ---
if (seededDefectMode)
{
    Console.WriteLine("═══ SEEDED-DEFECT BENCHMARKS ═══");
    Console.WriteLine($"Running {SeededDefectCatalog.All.Count} seeded defect cases against live LLM");
    Console.WriteLine();

    var seededMethodology = new MethodologyDefinition
    {
        Id = "seeded-defect-v1",
        Phases =
        [
            new PhaseDefinition
            {
                Id = "implementation",
                Goal = "Implement the solution",
                Inputs = [],
                OutputSchema = "implementation/v1",
                Instructions = "Build it."
            }
        ],
        Fidelity = new FidelityConfig { TargetPhase = "implementation" }
    };

    var seededRunner = provider.GetRequiredService<SeededDefectRunner>();
    var report = await seededRunner.RunAsync(SeededDefectCatalog.All, seededMethodology);

    Console.WriteLine();
    Console.WriteLine("═══ SEEDED-DEFECT RESULTS ═══");
    Console.WriteLine();

    foreach (var result in report.Results)
    {
        var icon = result.Inconclusive ? "⚠" : result.MatchCorrect && result.DriftCodesDetected ? "✅" : "❌";
        Console.WriteLine($"  {icon} {result.DefectId}: expected={result.ExpectedMatch}, actual={result.ActualMatch ?? "N/A"}, " +
                          $"match={result.MatchCorrect}, codes={result.DriftCodesDetected}");
        if (result.ActualDriftCodes.Count > 0)
            Console.WriteLine($"       detected: [{string.Join(", ", result.ActualDriftCodes)}]");
    }

    Console.WriteLine();
    Console.WriteLine("═══ DETECTION RATES ═══");
    Console.WriteLine($"  Blocking:  {report.BlockingDetectionRate:P0} (threshold: 80%) {(report.MeetsBlockingThreshold ? "✅" : "❌")}");
    Console.WriteLine($"  Advisory:  {report.AdvisoryDetectionRate:P0} (threshold: 60%) {(report.MeetsAdvisoryThreshold ? "✅" : "❌")}");
    Console.WriteLine($"  Match accuracy: {report.OverallMatchAccuracy:P0}");
    Console.WriteLine($"  False positive: {report.FalsePositiveRate:P0}");
    Console.WriteLine($"  Inconclusive:   {report.InconclusiveCount}");
    Console.WriteLine();

    if (report.PerCodeRecall.Count > 0)
    {
        Console.WriteLine("  Per-code recall:");
        foreach (var (code, recall) in report.PerCodeRecall.OrderBy(kv => kv.Key))
        {
            Console.WriteLine($"    {code}: {recall:P0}");
        }
    }

    return report.MeetsBlockingThreshold && report.MeetsAdvisoryThreshold ? 0 : 1;
}

// --- Custom task mode (--title flag) ---
if (customTitle is not null)
{
    var customTask = new TaskBrief
    {
        TaskId = $"custom-{DateTime.UtcNow:yyyyMMddHHmmss}",
        Title = customTitle,
        Description = customDescription ?? customTitle
    };

    Console.WriteLine($"═══ Custom Task: {customTask.Title} ═══");
    Console.WriteLine($"  Task ID: {customTask.TaskId}");
    Console.WriteLine();

    try
    {
        var trace = await runner.ExecuteAsync(customTask, methodology);

        Console.WriteLine();
        Console.WriteLine($"  Run ID:  {trace.RunId}");
        Console.WriteLine($"  Outcome: {trace.Outcome}");
        Console.WriteLine($"  Tokens:  {trace.PipelineTokens.In} in / {trace.PipelineTokens.Out} out");
        Console.WriteLine($"  Cost:    ${trace.PipelineCost:F4}");
        Console.WriteLine($"  Phases:  {trace.FinalArtifactHashes.Count}/{methodology.Phases.Count} produced artifacts");

        if (trace.FinalArtifactHashes.Count > 0)
        {
            Console.WriteLine("  Artifacts:");
            foreach (var (phaseId, hash) in trace.FinalArtifactHashes)
            {
                Console.WriteLine($"    {phaseId}: {hash[..Math.Min(20, hash.Length)]}...");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  Run directory: {Path.Combine(forgeRunsRoot, trace.RunId)}");
        return trace.Outcome == "succeeded" ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  FATAL: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
}

// --- Select tasks ---
var allTasks = new[] { BenchmarkTasks.T1, BenchmarkTasks.T2, BenchmarkTasks.T3 };
var tasksToRun = taskFilter is not null
    ? allTasks.Where(t => t.TaskId.Equals(taskFilter, StringComparison.OrdinalIgnoreCase)).ToArray()
    : allTasks;

if (tasksToRun.Length == 0)
{
    Console.Error.WriteLine($"No task matching '{taskFilter}'. Available: T1, T2, T3");
    return 1;
}

// --- Run benchmarks ---
var results = new List<(string TaskId, RunTrace Trace)>();

foreach (var task in tasksToRun)
{
    Console.WriteLine($"═══ Running {task.TaskId}: {task.Title} ═══");
    Console.WriteLine();

    try
    {
        var trace = await runner.ExecuteAsync(task, methodology);
        results.Add((task.TaskId, trace));

        Console.WriteLine();
        Console.WriteLine($"  Run ID:  {trace.RunId}");
        Console.WriteLine($"  Outcome: {trace.Outcome}");
        Console.WriteLine($"  Tokens:  {trace.PipelineTokens.In} in / {trace.PipelineTokens.Out} out");
        Console.WriteLine($"  Phases:  {trace.FinalArtifactHashes.Count}/{methodology.Phases.Count} produced artifacts");

        if (trace.FinalArtifactHashes.Count > 0)
        {
            Console.WriteLine("  Artifacts:");
            foreach (var (phaseId, hash) in trace.FinalArtifactHashes)
            {
                Console.WriteLine($"    {phaseId}: {hash[..20]}...");
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  FATAL: {ex.GetType().Name}: {ex.Message}");
        results.Add((task.TaskId, new RunTrace
        {
            RunId = "ERROR",
            TaskId = task.TaskId,
            MethodologyVersion = methodology.Id,
            StartedAt = DateTime.UtcNow,
            Outcome = "error",
            PipelineTokens = new TokenCount(),
            ControlTokens = new TokenCount(),
            FinalArtifactHashes = new Dictionary<string, string>()
        }));
    }

    Console.WriteLine();
}

// --- Summary ---
Console.WriteLine("═══ SUMMARY ═══");
foreach (var (taskId, trace) in results)
{
    var status = trace.Outcome switch
    {
        "succeeded" => "✅ PASS",
        "failed" => "❌ FAIL",
        "aborted" => "⏹ ABORT",
        _ => $"⚠ {trace.Outcome.ToUpperInvariant()}"
    };
    Console.WriteLine($"  {taskId}: {status} (tokens: {trace.PipelineTokens.In + trace.PipelineTokens.Out})");
}

return results.All(r => r.Trace.Outcome == "succeeded") ? 0 : 1;

// --- Helpers ---
static string FindRepoRoot()
{
    var dir = Directory.GetCurrentDirectory();
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, "AgentAcademy.sln")))
            return dir;
        dir = Path.GetDirectoryName(dir);
    }
    return Directory.GetCurrentDirectory();
}

static string? GetFlagValue(string[] args, string flag)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

static string[] GetPositionalArgs(string[] args, HashSet<string> flagsWithValues)
{
    var result = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--") || args[i].StartsWith("-"))
        {
            if (flagsWithValues.Contains(args[i]) && i + 1 < args.Length)
                i++; // Skip the value too
            continue;
        }
        result.Add(args[i]);
    }
    return result.ToArray();
}

