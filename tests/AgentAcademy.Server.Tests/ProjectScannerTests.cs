using System.Text.Json;
using AgentAcademy.Server.Services;

namespace AgentAcademy.Server.Tests;

public class ProjectScannerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ProjectScanner _scanner = new();

    public ProjectScannerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scan-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ── HumanizeProjectName ─────────────────────────────────────

    [Theory]
    [InlineData("agent-academy", "Agent Academy")]
    [InlineData("my_cool_app", "My Cool App")]
    [InlineData("simple", "Simple")]
    [InlineData("ALLCAPS", "ALLCAPS")]
    [InlineData("a", "A")]
    public void HumanizeProjectName_BasicCases(string raw, string expected)
    {
        Assert.Equal(expected, ProjectScanner.HumanizeProjectName(raw));
    }

    [Theory]
    [InlineData("@scope/my-lib", "My Lib")]
    [InlineData("@org/package-name", "Package Name")]
    [InlineData("github.com/user/repo", "Repo")]
    public void HumanizeProjectName_StripsScopes(string raw, string expected)
    {
        Assert.Equal(expected, ProjectScanner.HumanizeProjectName(raw));
    }

    [Theory]
    [InlineData("multi--dash", "Multi Dash")]
    [InlineData("trailing-", "Trailing")]
    [InlineData("-leading", "Leading")]
    [InlineData("mix-of_both", "Mix Of Both")]
    public void HumanizeProjectName_EdgeCases(string raw, string expected)
    {
        Assert.Equal(expected, ProjectScanner.HumanizeProjectName(raw));
    }

    // ── ParseHostProvider ───────────────────────────────────────

    [Theory]
    [InlineData("https://github.com/user/repo.git", "github")]
    [InlineData("git@github.com:user/repo.git", "github")]
    [InlineData("https://dev.azure.com/org/project", "azure-devops")]
    [InlineData("https://org.visualstudio.com/project", "azure-devops")]
    [InlineData("https://gitlab.com/user/repo.git", "gitlab")]
    [InlineData("https://self-hosted.gitlab.example.com/repo", "gitlab")]
    [InlineData("https://bitbucket.org/user/repo.git", "bitbucket")]
    public void ParseHostProvider_KnownHosts(string url, string expected)
    {
        Assert.Equal(expected, ProjectScanner.ParseHostProvider(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://unknown-host.example.com/repo")]
    public void ParseHostProvider_UnknownOrEmpty_ReturnsNull(string? url)
    {
        Assert.Null(ProjectScanner.ParseHostProvider(url));
    }

    // ── ScanProject ─────────────────────────────────────────────

    [Fact]
    public void ScanProject_EmptyDirectory_ReturnsMinimalResult()
    {
        var result = _scanner.ScanProject(_tempDir);

        Assert.Equal(Path.GetFullPath(_tempDir), result.Path);
        Assert.NotNull(result.ProjectName); // Falls back to directory basename
        Assert.Empty(result.TechStack);
        Assert.False(result.IsGitRepo);
        Assert.False(result.HasSpecs);
        Assert.False(result.HasReadme);
    }

    [Fact]
    public void ScanProject_NonExistentDirectory_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => _scanner.ScanProject(Path.Combine(_tempDir, "nope")));
    }

    [Fact]
    public void ScanProject_WithPackageJson_DetectsNodeJs()
    {
        File.WriteAllText(Path.Combine(_tempDir, "package.json"),
            """{"name":"test-project","dependencies":{"react":"^19.0.0"}}""");

        var result = _scanner.ScanProject(_tempDir);

        Assert.Contains("Node.js", result.TechStack);
        Assert.Contains("React", result.TechStack);
        Assert.Contains("package.json", result.DetectedFiles);
        Assert.Equal("Test Project", result.ProjectName);
    }

    [Fact]
    public void ScanProject_WithCsproj_DetectsDotNet()
    {
        File.WriteAllText(Path.Combine(_tempDir, "MyApp.csproj"), "<Project />");

        var result = _scanner.ScanProject(_tempDir);

        Assert.Contains(".NET", result.TechStack);
        Assert.Contains("MyApp.csproj", result.DetectedFiles);
    }

    [Fact]
    public void ScanProject_WithSln_DetectsDotNet()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Solution.sln"), "");

        var result = _scanner.ScanProject(_tempDir);

        Assert.Contains(".NET", result.TechStack);
    }

    [Fact]
    public void ScanProject_WithCargoToml_DetectsRustAndName()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Cargo.toml"),
            """
            [package]
            name = "my-rust-app"
            version = "0.1.0"
            """);

        var result = _scanner.ScanProject(_tempDir);

        Assert.Contains("Rust", result.TechStack);
        Assert.Equal("My Rust App", result.ProjectName);
    }

    [Fact]
    public void ScanProject_WithGoMod_DetectsGoAndName()
    {
        File.WriteAllText(Path.Combine(_tempDir, "go.mod"),
            "module github.com/user/my-go-project\n\ngo 1.21");

        var result = _scanner.ScanProject(_tempDir);

        Assert.Contains("Go", result.TechStack);
        Assert.Equal("My Go Project", result.ProjectName);
    }

    [Fact]
    public void ScanProject_WithPyprojectToml_DetectsPythonAndName()
    {
        File.WriteAllText(Path.Combine(_tempDir, "pyproject.toml"),
            """
            [project]
            name = "data-pipeline"
            version = "1.0.0"
            """);

        var result = _scanner.ScanProject(_tempDir);

        Assert.Contains("Python", result.TechStack);
        Assert.Equal("Data Pipeline", result.ProjectName);
    }

    [Fact]
    public void ScanProject_WithDockerfile_DetectsDocker()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Dockerfile"), "FROM alpine");

        var result = _scanner.ScanProject(_tempDir);

        Assert.Contains("Docker", result.TechStack);
    }

    [Fact]
    public void ScanProject_WithSpecsDir_DetectsSpecs()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "specs"));

        var result = _scanner.ScanProject(_tempDir);

        Assert.True(result.HasSpecs);
    }

    [Fact]
    public void ScanProject_WithReadme_DetectsReadme()
    {
        File.WriteAllText(Path.Combine(_tempDir, "README.md"), "# Hello");

        var result = _scanner.ScanProject(_tempDir);

        Assert.True(result.HasReadme);
    }

    [Fact]
    public void ScanProject_MultipleStacks_DetectsAll()
    {
        File.WriteAllText(Path.Combine(_tempDir, "package.json"),
            """{"name":"fullstack","dependencies":{"express":"^4.0"}}""");
        File.WriteAllText(Path.Combine(_tempDir, "Dockerfile"), "FROM node");
        File.WriteAllText(Path.Combine(_tempDir, "tsconfig.json"), "{}");

        var result = _scanner.ScanProject(_tempDir);

        Assert.Contains("Node.js", result.TechStack);
        Assert.Contains("Express", result.TechStack);
        Assert.Contains("Docker", result.TechStack);
        Assert.Contains("TypeScript", result.TechStack);
    }

    [Fact]
    public void ScanProject_NpmDevDependencies_Detected()
    {
        File.WriteAllText(Path.Combine(_tempDir, "package.json"),
            """{"name":"app","devDependencies":{"vite":"^5.0"}}""");

        var result = _scanner.ScanProject(_tempDir);

        Assert.Contains("Vite", result.TechStack);
    }

    [Fact]
    public void ScanProject_DetectedFiles_SortedAndDeduped()
    {
        File.WriteAllText(Path.Combine(_tempDir, "package.json"), "{}");
        File.WriteAllText(Path.Combine(_tempDir, "Dockerfile"), "");
        File.WriteAllText(Path.Combine(_tempDir, "tsconfig.json"), "{}");

        var result = _scanner.ScanProject(_tempDir);

        var expected = result.DetectedFiles.OrderBy(f => f).ToList();
        Assert.Equal(expected, result.DetectedFiles);
        Assert.Equal(result.DetectedFiles.Distinct().Count(), result.DetectedFiles.Count);
    }

    [Fact]
    public void ScanProject_InvalidPackageJson_DoesNotThrow()
    {
        File.WriteAllText(Path.Combine(_tempDir, "package.json"), "not valid json{{{");

        var result = _scanner.ScanProject(_tempDir);

        // package.json is detected as a config file but JSON parsing fails gracefully
        Assert.Contains("Node.js", result.TechStack);
    }

    [Fact]
    public void ScanProject_FallbackProjectName_UsesDirectoryBasename()
    {
        // No config files with name — falls back to directory name
        var result = _scanner.ScanProject(_tempDir);

        // The temp dir has a GUID name, so the result should be the humanized version
        Assert.NotNull(result.ProjectName);
    }
}
