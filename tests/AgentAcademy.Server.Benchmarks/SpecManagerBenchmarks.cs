using BenchmarkDotNet.Attributes;
using AgentAcademy.Server.Services;

namespace AgentAcademy.Server.Benchmarks;

/// <summary>
/// Benchmarks for <see cref="SpecManager"/> — file I/O-heavy spec search and
/// context loading used during every breakout room prompt construction.
/// Uses a temporary spec directory with realistic content to isolate disk I/O costs.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
public class SpecManagerBenchmarks
{
    private string _specsDir = default!;
    private SpecManager _manager = default!;

    [Params(5, 20)]
    public int SectionCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _specsDir = Path.Combine(Path.GetTempPath(), $"bench-specs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_specsDir);

        for (var i = 0; i < SectionCount; i++)
        {
            var dirName = $"{i:D3}-section-{i}";
            var sectionDir = Path.Combine(_specsDir, dirName);
            Directory.CreateDirectory(sectionDir);

            var content = GenerateSpecContent(i);
            File.WriteAllText(Path.Combine(sectionDir, "spec.md"), content);
        }

        // Write spec-version.json
        File.WriteAllText(Path.Combine(_specsDir, "spec-version.json"),
            """{"version": "1.0.0", "lastUpdated": "2026-04-16"}""");

        _manager = new SpecManager(_specsDir);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_specsDir))
            Directory.Delete(_specsDir, true);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Load")]
    public async Task<string?> LoadSpecContext() =>
        await _manager.LoadSpecContextAsync();

    [Benchmark]
    [BenchmarkCategory("Load")]
    public async Task<List<SpecSection>> GetSections() =>
        await _manager.GetSpecSectionsAsync();

    [Benchmark]
    [BenchmarkCategory("Search")]
    public async Task<List<SpecSearchResult>> SearchSingleTerm() =>
        await _manager.SearchSpecsAsync("authentication");

    [Benchmark]
    [BenchmarkCategory("Search")]
    public async Task<List<SpecSearchResult>> SearchMultiTerm() =>
        await _manager.SearchSpecsAsync("agent orchestrator command pipeline");

    [Benchmark]
    [BenchmarkCategory("Relevance")]
    public async Task<string?> RelevanceWithQuery() =>
        await _manager.LoadSpecContextWithRelevanceAsync(
            "task dependency cycle detection",
            ["005-section-5", "010-section-10"]);

    [Benchmark]
    [BenchmarkCategory("Relevance")]
    public async Task<string?> RelevanceLinkedOnly() =>
        await _manager.LoadSpecContextWithRelevanceAsync(
            null, ["003-section-3", "007-section-7", "015-section-15"]);

    [Benchmark]
    [BenchmarkCategory("Version")]
    public async Task<SpecVersionInfo?> GetVersion() =>
        await _manager.GetSpecVersionAsync();

    [Benchmark]
    [BenchmarkCategory("Version")]
    public async Task<string> ContentHashWarmCache() =>
        await _manager.ComputeContentHashAsync();

    [Benchmark]
    [BenchmarkCategory("Version")]
    public async Task<string> ContentHashColdCache() =>
        await new SpecManager(_specsDir).ComputeContentHashAsync();

    [Benchmark]
    [BenchmarkCategory("Tokenize")]
    public List<string> TokenizeSimple() =>
        SpecManager.TokenizeQuery("agent orchestrator breakout room");

    [Benchmark]
    [BenchmarkCategory("Tokenize")]
    public List<string> TokenizeWithStopWords() =>
        SpecManager.TokenizeQuery("the agent is in a breakout room and should be working on the task");

    [Benchmark]
    [BenchmarkCategory("Count")]
    public int CountOccurrences() =>
        SpecManager.CountOccurrences(
            "The agent orchestrator manages the lifecycle of agent conversations. " +
            "Each agent participates in orchestrated rounds where the orchestrator " +
            "collects responses and dispatches them to the appropriate handlers. " +
            "The orchestrator ensures agents take turns and coordinates breakout rooms.",
            "orchestrator");

    private static string GenerateSpecContent(int index)
    {
        var topics = new[] { "authentication", "agent", "orchestrator", "command", "pipeline",
            "task", "dependency", "breakout", "room", "notification", "security", "database",
            "migration", "sprint", "review", "memory", "prompt", "sanitizer", "workspace", "artifact" };

        var topic = topics[index % topics.Length];
        var lines = new List<string>
        {
            $"# Section {index}: {char.ToUpper(topic[0])}{topic[1..]} System",
            "",
            "## Purpose",
            $"This section documents the {topic} subsystem of Agent Academy. " +
            $"It covers the design, implementation, and operational behavior of the {topic} module.",
            "",
            "## Current Behavior",
            ""
        };

        // Generate realistic spec content (~200 lines)
        for (var p = 0; p < 10; p++)
        {
            lines.Add($"### {topic} Feature {p}");
            lines.Add("");
            for (var l = 0; l < 15; l++)
            {
                lines.Add($"The {topic} system handles {topics[(index + l) % topics.Length]} " +
                    $"operations through a well-defined interface. Implementation detail {l} " +
                    $"ensures correctness and thread safety in concurrent scenarios.");
            }
            lines.Add("");
        }

        lines.Add("## Known Gaps");
        lines.Add($"- Performance benchmarks not yet established for {topic} operations.");
        lines.Add("");
        lines.Add("## Revision History");
        lines.Add("| Date | Change |");
        lines.Add("|------|--------|");
        lines.Add($"| 2026-04-16 | Initial {topic} spec |");

        return string.Join("\n", lines);
    }
}
