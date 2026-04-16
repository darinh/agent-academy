using BenchmarkDotNet.Attributes;
using AgentAcademy.Server.Commands;

namespace AgentAcademy.Server.Benchmarks;

/// <summary>
/// Benchmarks for <see cref="CommandParser.Parse"/> — the regex + string-splitting
/// pipeline that extracts structured commands from agent text responses.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
public class CommandParserBenchmarks
{
    private readonly CommandParser _parser = new();

    private string _simpleCommand = default!;
    private string _multiArgCommand = default!;
    private string _multiCommandResponse = default!;
    private string _noCommandProseOnly = default!;
    private string _mixedProseAndCommands = default!;

    [GlobalSetup]
    public void Setup()
    {
        _simpleCommand = "READ_FILE: path=src/Program.cs";

        _multiArgCommand = """
            SHELL:
              op: git-log
              args: --oneline -20
              reason: checking recent commit history for test coverage gaps
            """;

        _multiCommandResponse = string.Join("\n\n", Enumerable.Range(0, 10).Select(i =>
            $"REMEMBER: category=learning key=pattern-{i} value=This is a learned pattern from iteration {i}"));

        _noCommandProseOnly = string.Join("\n", Enumerable.Range(0, 50).Select(i =>
            $"This is line {i} of a typical agent response that contains no commands at all. " +
            $"It discusses various aspects of the codebase and proposes changes."));

        _mixedProseAndCommands = $"""
            I've analyzed the codebase and found several issues that need attention.
            The main problem is in the authentication module where tokens expire prematurely.

            Let me check the relevant files:

            READ_FILE: path=src/Auth/TokenProvider.cs

            After reviewing, I'll also need to look at the tests:

            SEARCH_CODE: query=TokenProvider pattern=*.cs

            Based on what I've found, here's my assessment of the situation.
            The token refresh logic has a race condition that can cause...

            REMEMBER: category=bug key=token-race value=TokenProvider has refresh race condition

            I recommend the following changes to fix this issue...
            """;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Parse")]
    public object SimpleCommand() => _parser.Parse(_simpleCommand);

    [Benchmark]
    [BenchmarkCategory("Parse")]
    public object MultiArgCommand() => _parser.Parse(_multiArgCommand);

    [Benchmark]
    [BenchmarkCategory("Parse")]
    public object TenCommands() => _parser.Parse(_multiCommandResponse);

    [Benchmark]
    [BenchmarkCategory("Parse")]
    public object ProseOnly() => _parser.Parse(_noCommandProseOnly);

    [Benchmark]
    [BenchmarkCategory("Parse")]
    public object MixedProseAndCommands() => _parser.Parse(_mixedProseAndCommands);
}
