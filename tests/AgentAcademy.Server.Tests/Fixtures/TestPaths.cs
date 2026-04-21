namespace AgentAcademy.Server.Tests.Fixtures;

/// <summary>
/// Resolves project paths from the test assembly location (AppContext.BaseDirectory),
/// making them stable regardless of process-wide Directory.SetCurrentDirectory() calls.
/// </summary>
internal static class TestPaths
{
    private static readonly Lazy<string> _solutionDir = new(FindSolutionDirectory);

    /// <summary>The repo root containing AgentAcademy.sln.</summary>
    public static string SolutionDirectory => _solutionDir.Value;

    /// <summary>The ASP.NET Core server project directory (content root for WebApplicationFactory).</summary>
    public static string ServerContentRoot =>
        Path.Combine(SolutionDirectory, "src", "AgentAcademy.Server");

    private static string FindSolutionDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var slnPath = Path.Combine(dir, "AgentAcademy.sln");
            if (File.Exists(slnPath))
            {
                var serverProj = Path.Combine(dir, "src", "AgentAcademy.Server", "AgentAcademy.Server.csproj");
                if (File.Exists(serverProj))
                    return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException(
            "Could not find AgentAcademy.sln by walking up from " + AppContext.BaseDirectory);
    }
}
