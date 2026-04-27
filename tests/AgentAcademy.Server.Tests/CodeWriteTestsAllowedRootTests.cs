using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Verifies P1.9 blocker E1: the production <c>code-write</c> tool group must
/// permit writes under both <c>src/</c> and <c>tests/</c>. Previously code-write
/// was scoped to <c>src/</c> only, which made it impossible for SoftwareEngineer
/// agents to fulfil sprint briefs that required integration tests in
/// <c>tests/</c> (the §10 acceptance run for P1.9 hit this on Sprint #13).
///
/// Constructs the wrapper with the same allowedRoots that
/// <c>AgentToolFunctions.CreateCodeWriteTools</c> uses in production
/// (<c>["src", "tests"]</c>) so this test pins the intended production config.
/// </summary>
[Collection("WorkspaceRuntime")]
public sealed class CodeWriteTestsAllowedRootTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly CodeWriteToolWrapper _wrapper;

    public CodeWriteTestsAllowedRootTests()
    {
        var services = new ServiceCollection();
        _sp = services.BuildServiceProvider();
        _wrapper = new CodeWriteToolWrapper(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            "test-agent", "TestAgent",
            new AgentGitIdentity("Test Agent", "test@agent.local"),
            roomId: null,
            allowedRoots: new[] { "src", "tests" },
            protectedPaths: CodeWriteToolWrapper.CodeWriteProtectedPaths,
            scopeRoot: null,
            requireWorktree: false);
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    public void Constructor_PinsProductionAllowedRoots()
    {
        Assert.Equal(new[] { "src", "tests" }, _wrapper.AllowedRoots);
    }

    [Fact]
    public async Task WriteFileAsync_TestsPath_NotRejectedByAllowedRoots()
    {
        // The write may still fail downstream (e.g. file system or git side-effects
        // in the unit-test sandbox), but it must NOT be rejected by the
        // allowedRoots scope check, which is the bug under test.
        var result = await _wrapper.WriteFileAsync(
            "tests/AgentAcademy.Server.Tests/_anvil_blocker_e1_probe.cs",
            "// scope probe");
        Assert.DoesNotContain("Writes are restricted to", result);
        Cleanup("tests/AgentAcademy.Server.Tests/_anvil_blocker_e1_probe.cs");
    }

    [Fact]
    public async Task WriteFileAsync_SrcPath_NotRejectedByAllowedRoots()
    {
        // Regression: src/ must keep working after the allowedRoots expansion.
        var unique = $"_anvil_blocker_e1_src_probe_{Guid.NewGuid():N}.cs";
        var path = $"src/AgentAcademy.Server/{unique}";
        var result = await _wrapper.WriteFileAsync(path, "// scope probe");
        Assert.DoesNotContain("Writes are restricted to", result);
        Cleanup(path);
    }

    [Theory]
    [InlineData("specs/foo.md")]
    [InlineData("docs/foo.md")]
    [InlineData("README.md")]
    [InlineData(".github/workflows/ci.yml")]
    public async Task WriteFileAsync_OutOfScopePath_StillRejected(string path)
    {
        var result = await _wrapper.WriteFileAsync(path, "content");
        Assert.Contains("Writes are restricted to", result);
        // Multi-root error message lists both roots.
        Assert.Contains("src/", result);
        Assert.Contains("tests/", result);
    }

    [Fact]
    public async Task WriteFileAsync_TraversalEscape_StillRejected()
    {
        var result = await _wrapper.WriteFileAsync("tests/../etc/passwd", "x");
        Assert.Contains("Writes are restricted to", result);
    }

    /// <summary>
    /// Pins the *production* factory wiring against a real worktree so the
    /// wrapper's worktree gate passes and the allowedRoots scope check is the
    /// behavior actually under test. If <see cref="AgentToolFunctions.CreateCodeWriteTools"/>
    /// regresses (e.g. drops <c>tests</c> from allowedRoots, or stops requiring
    /// a worktree), this test breaks. Closes the reviewer-flagged gap where a
    /// hand-built wrapper test pins constructor args but says nothing about
    /// whether production uses them.
    /// </summary>
    [Fact]
    public async Task ProductionFactory_CreateCodeWriteTools_AcceptsTestsPath_RejectsOutOfScope_RequiresWorktree()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "aa-prod-factory-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);
        try
        {
            var repoRoot = Path.Combine(tempRoot, "repo");
            Directory.CreateDirectory(repoRoot);
            Git(repoRoot, "init", "-b", "develop");
            Git(repoRoot, "config", "user.email", "test@example.com");
            Git(repoRoot, "config", "user.name", "Test");
            Directory.CreateDirectory(Path.Combine(repoRoot, "src"));
            File.WriteAllText(Path.Combine(repoRoot, "src/initial.cs"), "// initial\n");
            File.WriteAllText(Path.Combine(repoRoot, "AgentAcademy.sln"), "// fake sentinel\n");
            Git(repoRoot, "add", "-A");
            Git(repoRoot, "commit", "-m", "initial");

            var worktreePath = Path.Combine(tempRoot, "wt");
            Git(repoRoot, "worktree", "add", "-b", "task/probe", worktreePath);

            using var sp = BuildAgentToolFactoryServices();
            var factory = new AgentToolFunctions(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IAgentCatalog>(),
                NullLogger<AgentToolFunctions>.Instance);

            // Provide the worktree as workspacePath so the worktree gate passes
            // and the allowedRoots scope check is what gets exercised.
            var tools = factory.CreateCodeWriteTools("test-agent", "TestAgent",
                new AgentGitIdentity("Test Agent", "test@agent.local"),
                roomId: null, workspacePath: worktreePath);
            var writeFile = tools.Single(t => t.Name == "write_file");

            var unique = $"_anvil_blocker_e1_factory_{Guid.NewGuid():N}.cs";

            // tests/ must be accepted by the production allowedRoots config.
            // The test directory doesn't exist in the worktree yet — the
            // wrapper creates intermediate dirs.
            var testsResult = (await writeFile.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["path"] = $"tests/{unique}",
                ["content"] = "// scope probe via production factory"
            })))?.ToString() ?? string.Empty;

            // src/ must continue to be accepted (regression check).
            var srcResult = (await writeFile.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["path"] = $"src/{unique}",
                ["content"] = "// scope probe via production factory"
            })))?.ToString() ?? string.Empty;

            // specs/ must be rejected by the production allowedRoots config —
            // production code-write does NOT include specs/.
            var oosResult = (await writeFile.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["path"] = $"specs/{unique}.md",
                ["content"] = "# probe"
            })))?.ToString() ?? string.Empty;

            Assert.DoesNotContain("Writes are restricted to", testsResult);
            Assert.DoesNotContain("Writes are restricted to", srcResult);
            Assert.Contains("Writes are restricted to", oosResult);
            // Multi-root error message must list both roots so the agent has
            // actionable diagnostics.
            Assert.Contains("src/", oosResult);
            Assert.Contains("tests/", oosResult);

            // Now exercise the requireWorktree=true wiring directly: build a
            // second tool set with NO workspacePath. The factory must wire
            // requireWorktree:true so the worktree gate refuses the write
            // (P1.9 blocker D protection). If the factory ever regresses to
            // requireWorktree:false (or omits it via a default), the
            // refusal message disappears and this assertion breaks.
            var noWtTools = factory.CreateCodeWriteTools("test-agent", "TestAgent",
                new AgentGitIdentity("Test Agent", "test@agent.local"),
                roomId: null, workspacePath: null);
            var noWtWrite = noWtTools.Single(t => t.Name == "write_file");
            var noWtResult = (await noWtWrite.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["path"] = $"src/{unique}",
                ["content"] = "// would-be develop write"
            })))?.ToString() ?? string.Empty;
            // Worktree refusal is a hard error mentioning the develop checkout
            // and the CLAIM_TASK requirement. Use both substrings so a partial
            // wording change is still caught.
            Assert.Contains("develop", noWtResult, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CLAIM_TASK", noWtResult);
        }
        finally
        {
            TryDeleteDir(tempRoot);
        }
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("git failed to start");
        p.WaitForExit(15_000);
        if (p.ExitCode != 0)
        {
            var stderr = p.StandardError.ReadToEnd();
            throw new InvalidOperationException($"git {string.Join(' ', args)} exit {p.ExitCode}: {stderr}");
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }

    private static ServiceProvider BuildAgentToolFactoryServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentCatalog>(new AgentCatalogOptions(
            "main", "Main Room",
            new List<AgentDefinition>
            {
                new("test-agent", "TestAgent", "SoftwareEngineer",
                    "Test agent", "prompt", "claude-opus-4.7",
                    new List<string> { "engineering" }, new List<string> { "code-write" }, true)
            }));
        return services.BuildServiceProvider();
    }

    private static void Cleanup(string relPath)
    {
        try
        {
            var projectRoot = AgentToolFunctions.FindProjectRoot();
            var fullPath = Path.Combine(projectRoot, relPath);
            if (File.Exists(fullPath))
                File.Delete(fullPath);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("reset");
            psi.ArgumentList.Add("HEAD");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(relPath);
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(2000);
        }
        catch
        {
            // Best effort. The test asserted the contract; leftover state
            // would be cleaned by the next CI run.
        }
    }
}
