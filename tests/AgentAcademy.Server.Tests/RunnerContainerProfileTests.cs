using AgentAcademy.Server.Tests.Fixtures;

namespace AgentAcademy.Server.Tests;

public sealed class RunnerContainerProfileTests
{
    [Fact]
    public void Dockerfile_DefinesRunnerStageForContainerizedAgentExecution()
    {
        var dockerfilePath = Path.Combine(TestPaths.SolutionDirectory, "Dockerfile");
        var content = File.ReadAllText(dockerfilePath);

        Assert.Contains("FROM mcr.microsoft.com/dotnet/sdk:8.0 AS runner", content);
        Assert.Contains("apt-get install -y --no-install-recommends curl git", content);
        Assert.Contains("WORKDIR /workspace", content);
        Assert.Contains("git config --system --add safe.directory /workspace", content);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"/app/AgentAcademy.Server.dll\"]", content);
    }

    [Fact]
    public void DockerCompose_DefinesRunnerServiceAndProfile()
    {
        var composePath = Path.Combine(TestPaths.SolutionDirectory, "docker-compose.yml");
        var content = File.ReadAllText(composePath);

        Assert.Contains("runner:", content);
        Assert.Contains("target: runner", content);
        Assert.Contains("${AA_RUNNER_PORT:-8081}:8080", content);
        Assert.Contains("${AA_WORKSPACE:-.}:/workspace", content);
        Assert.Contains("profiles:", content);
        Assert.Contains("- runner", content);
    }
}
