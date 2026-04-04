using AgentAcademy.Server.Services;

namespace AgentAcademy.Server.Tests;

public class SpecManagerTests : IDisposable
{
    private readonly string _tempDir;

    public SpecManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"spec-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── LoadSpecContextAsync ──────────────────────────────────────────

    [Fact]
    public async Task LoadSpecContextAsync_ReturnsNull_WhenSpecsDirDoesNotExist()
    {
        var manager = new SpecManager(Path.Combine(_tempDir, "nonexistent"));
        Assert.Null(await manager.LoadSpecContextAsync());
    }

    [Fact]
    public async Task LoadSpecContextAsync_ReturnsNull_WhenSpecsDirIsEmpty()
    {
        Directory.CreateDirectory(_tempDir);
        var manager = new SpecManager(_tempDir);
        Assert.Null(await manager.LoadSpecContextAsync());
    }

    [Fact]
    public async Task LoadSpecContextAsync_ReturnsNull_WhenSubdirsHaveNoSpecFile()
    {
        var subdir = Path.Combine(_tempDir, "001-example");
        Directory.CreateDirectory(subdir);
        // No spec.md inside
        File.WriteAllText(Path.Combine(subdir, "notes.txt"), "not a spec");

        var manager = new SpecManager(_tempDir);
        Assert.Null(await manager.LoadSpecContextAsync());
    }

    [Fact]
    public async Task LoadSpecContextAsync_ReturnsSingleSection()
    {
        CreateSpec("000-overview", "# System Overview\n\n## Purpose\nHigh-level architecture.\n\n## Current Behavior\nStuff.");

        var manager = new SpecManager(_tempDir);
        var result = await manager.LoadSpecContextAsync();

        Assert.NotNull(result);
        Assert.Contains("specs/000-overview/spec.md", result);
        Assert.Contains("System Overview", result);
        Assert.Contains("High-level architecture.", result);
    }

    [Fact]
    public async Task LoadSpecContextAsync_ReturnsMultipleSections_InOrder()
    {
        CreateSpec("002-second", "# Second Section\n\n## Purpose\nSecond purpose.\n\n## Other\nX.");
        CreateSpec("001-first", "# First Section\n\n## Purpose\nFirst purpose.\n\n## Other\nY.");

        var manager = new SpecManager(_tempDir);
        var result = await manager.LoadSpecContextAsync();

        Assert.NotNull(result);
        var lines = result!.Split('\n');
        Assert.Equal(2, lines.Length);
        // Should be sorted by directory name
        Assert.Contains("001-first", lines[0]);
        Assert.Contains("002-second", lines[1]);
    }

    [Fact]
    public async Task LoadSpecContextAsync_UsesDirectoryName_WhenNoHeading()
    {
        CreateSpec("003-no-heading", "Some content without a heading.\n\n## Purpose\nA purpose.");

        var manager = new SpecManager(_tempDir);
        var result = await manager.LoadSpecContextAsync();

        Assert.NotNull(result);
        Assert.Contains("003-no-heading", result);
        Assert.Contains("A purpose.", result);
    }

    [Fact]
    public async Task LoadSpecContextAsync_OmitsPurpose_WhenNoPurposeSection()
    {
        CreateSpec("004-minimal", "# Minimal Spec\n\nJust some text without a purpose section.");

        var manager = new SpecManager(_tempDir);
        var result = await manager.LoadSpecContextAsync();

        Assert.NotNull(result);
        Assert.Contains("Minimal Spec", result);
        // No " — " separator since there's no purpose
        Assert.DoesNotContain(" — ", result);
    }

    [Fact]
    public async Task LoadSpecContextAsync_ExtractsPurpose_WhenPurposeIsLastSectionNoTrailingNewline()
    {
        // Regression: Purpose as last section with no trailing newline must still extract
        CreateSpec("005-edge", "# Edge Case\n\n## Purpose\nFinal purpose text");

        var manager = new SpecManager(_tempDir);
        var result = await manager.LoadSpecContextAsync();

        Assert.NotNull(result);
        Assert.Contains("Final purpose text", result);
    }

    // ── GetSpecSectionsAsync ─────────────────────────────────────────

    [Fact]
    public async Task GetSpecSectionsAsync_ReturnsEmpty_WhenNoSpecsDir()
    {
        var manager = new SpecManager(Path.Combine(_tempDir, "nonexistent"));
        Assert.Empty(await manager.GetSpecSectionsAsync());
    }

    [Fact]
    public async Task GetSpecSectionsAsync_ReturnsMetadata()
    {
        CreateSpec("000-overview", "# System Overview\n\n## Purpose\nHigh-level architecture.\n\n## Current Behavior\nStuff.");
        CreateSpec("001-domain", "# Domain Model\n\n## Purpose\nAll domain types.\n\n## Other\nY.");

        var manager = new SpecManager(_tempDir);
        var sections = await manager.GetSpecSectionsAsync();

        Assert.Equal(2, sections.Count);

        Assert.Equal("000-overview", sections[0].Id);
        Assert.Equal("System Overview", sections[0].Heading);
        Assert.Equal("High-level architecture.", sections[0].Summary);
        Assert.Equal("specs/000-overview/spec.md", sections[0].FilePath);

        Assert.Equal("001-domain", sections[1].Id);
        Assert.Equal("Domain Model", sections[1].Heading);
        Assert.Equal("All domain types.", sections[1].Summary);
    }

    // ── GetSpecContentAsync ──────────────────────────────────────────

    [Fact]
    public async Task GetSpecContentAsync_ReturnsContent_ForValidSection()
    {
        var content = "# Test Spec\n\n## Purpose\nA test.\n\n## Current Behavior\nWorks.";
        CreateSpec("005-test", content);

        var manager = new SpecManager(_tempDir);
        var result = await manager.GetSpecContentAsync("005-test");

        Assert.Equal(content, result);
    }

    [Fact]
    public async Task GetSpecContentAsync_ReturnsNull_ForNonexistentSection()
    {
        Directory.CreateDirectory(_tempDir);
        var manager = new SpecManager(_tempDir);
        Assert.Null(await manager.GetSpecContentAsync("999-nonexistent"));
    }

    [Fact]
    public async Task GetSpecContentAsync_ReturnsNull_ForNullOrEmpty()
    {
        var manager = new SpecManager(_tempDir);
        Assert.Null(await manager.GetSpecContentAsync(null!));
        Assert.Null(await manager.GetSpecContentAsync(""));
        Assert.Null(await manager.GetSpecContentAsync("  "));
    }

    [Fact]
    public async Task GetSpecContentAsync_BlocksPathTraversal()
    {
        CreateSpec("000-legit", "# Legit");

        var manager = new SpecManager(_tempDir);
        Assert.Null(await manager.GetSpecContentAsync("../../../etc"));
        Assert.Null(await manager.GetSpecContentAsync(".."));
    }

    // ── Helper ──────────────────────────────────────────────────

    private void CreateSpec(string dirName, string content)
    {
        var dir = Path.Combine(_tempDir, dirName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "spec.md"), content);
    }
}
