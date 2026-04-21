using BenchmarkDotNet.Running;

namespace AgentAcademy.Server.Benchmarks;

public static class BenchmarkRunner
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(BenchmarkRunner).Assembly).Run(args);
}
