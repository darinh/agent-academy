using System.Text.Json;
using AgentAcademy.Forge;
using AgentAcademy.Forge.Execution;
using AgentAcademy.Forge.Llm;
using AgentAcademy.Forge.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// --- Configuration ---
var methodologyPath = args.Length > 0
    ? args[0]
    : Path.Combine(FindRepoRoot(), "docs", "forge-spike", "methodology.json");

var forgeRunsRoot = args.Length > 1
    ? args[1]
    : Path.Combine(FindRepoRoot(), "forge-runs");

var taskFilter = args.Length > 2
    ? args[2].ToUpperInvariant()
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

