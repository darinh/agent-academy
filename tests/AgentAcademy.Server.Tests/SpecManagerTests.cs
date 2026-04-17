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

    private void CreateVersionFile(string content)
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "spec-version.json"), content);
    }

    // ── GetSpecVersionAsync ─────────────────────────────────────

    [Fact]
    public async Task GetSpecVersionAsync_ReturnsNull_WhenSpecsDirDoesNotExist()
    {
        var manager = new SpecManager(Path.Combine(_tempDir, "nonexistent"));
        Assert.Null(await manager.GetSpecVersionAsync());
    }

    [Fact]
    public async Task GetSpecVersionAsync_ReturnsFallbackVersion_WhenNoVersionFile()
    {
        CreateSpec("000-overview", "# Overview\n\n## Purpose\nTest.");
        var manager = new SpecManager(_tempDir);

        var version = await manager.GetSpecVersionAsync();

        Assert.NotNull(version);
        Assert.Equal("0.0.0", version!.Version);
        Assert.Equal("unknown", version.LastUpdated);
        Assert.Equal(1, version.SectionCount);
        Assert.NotEmpty(version.ContentHash);
    }

    [Fact]
    public async Task GetSpecVersionAsync_ReadsDeclaredVersion()
    {
        CreateSpec("000-overview", "# Overview\n\n## Purpose\nTest.");
        CreateVersionFile("""{"version": "2.1.0", "lastUpdated": "2026-04-12"}""");

        var manager = new SpecManager(_tempDir);
        var version = await manager.GetSpecVersionAsync();

        Assert.NotNull(version);
        Assert.Equal("2.1.0", version!.Version);
        Assert.Equal("2026-04-12", version.LastUpdated);
        Assert.Equal(1, version.SectionCount);
    }

    [Fact]
    public async Task GetSpecVersionAsync_FallsBackOnMalformedJson()
    {
        CreateSpec("000-overview", "# Overview\n\n## Purpose\nTest.");
        CreateVersionFile("not json at all {{{");

        var manager = new SpecManager(_tempDir);
        var version = await manager.GetSpecVersionAsync();

        Assert.NotNull(version);
        Assert.Equal("0.0.0", version!.Version);
        Assert.Equal("unknown", version.LastUpdated);
    }

    [Fact]
    public async Task GetSpecVersionAsync_CountsMultipleSections()
    {
        CreateSpec("000-overview", "# Overview\n\n## Purpose\nTest 1.");
        CreateSpec("001-domain", "# Domain\n\n## Purpose\nTest 2.");
        CreateSpec("002-workflow", "# Workflow\n\n## Purpose\nTest 3.");
        CreateVersionFile("""{"version": "1.0.0", "lastUpdated": "2026-01-01"}""");

        var manager = new SpecManager(_tempDir);
        var version = await manager.GetSpecVersionAsync();

        Assert.Equal(3, version!.SectionCount);
    }

    // ── ComputeContentHashAsync ─────────────────────────────────

    [Fact]
    public async Task ComputeContentHashAsync_ReturnsEmpty_WhenNoDirExists()
    {
        var manager = new SpecManager(Path.Combine(_tempDir, "nonexistent"));
        Assert.Equal("", await manager.ComputeContentHashAsync());
    }

    [Fact]
    public async Task ComputeContentHashAsync_ReturnsDeterministicHash()
    {
        CreateSpec("000-overview", "# Overview\n\n## Purpose\nTest.");
        var manager = new SpecManager(_tempDir);

        var hash1 = await manager.ComputeContentHashAsync();
        var hash2 = await manager.ComputeContentHashAsync();

        Assert.NotEmpty(hash1);
        Assert.Equal(hash1, hash2);
        Assert.Equal(12, hash1.Length);
    }

    [Fact]
    public async Task ComputeContentHashAsync_ChangesWhenContentChanges()
    {
        CreateSpec("000-overview", "# Overview\n\n## Purpose\nOriginal.");
        var manager = new SpecManager(_tempDir);

        var hash1 = await manager.ComputeContentHashAsync();

        // Wait a moment to ensure filesystem timestamp changes
        await Task.Delay(50);

        // Modify the spec file
        File.WriteAllText(Path.Combine(_tempDir, "000-overview", "spec.md"),
            "# Overview\n\n## Purpose\nModified content.");

        var hash2 = await manager.ComputeContentHashAsync();

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public async Task ComputeContentHashAsync_IgnoresNonSpecFiles()
    {
        CreateSpec("000-overview", "# Overview\n\n## Purpose\nTest.");
        var manager = new SpecManager(_tempDir);

        var hash1 = await manager.ComputeContentHashAsync();

        // Add a non-spec file to the specs directory
        File.WriteAllText(Path.Combine(_tempDir, "README.md"), "# Readme");

        var hash2 = await manager.ComputeContentHashAsync();

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public async Task ComputeContentHashAsync_NormalizesLineEndings()
    {
        var contentLf = "# Overview\n\n## Purpose\nTest.\n";
        var contentCrlf = "# Overview\r\n\r\n## Purpose\r\nTest.\r\n";

        CreateSpec("000-overview", contentLf);
        var manager1 = new SpecManager(_tempDir);
        var hash1 = await manager1.ComputeContentHashAsync();

        // Recreate with CRLF
        File.WriteAllText(Path.Combine(_tempDir, "000-overview", "spec.md"), contentCrlf);
        var manager2 = new SpecManager(_tempDir);
        var hash2 = await manager2.ComputeContentHashAsync();

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public async Task GetSpecVersionAsync_HandlesPartialVersionFile()
    {
        CreateSpec("000-overview", "# Overview\n\n## Purpose\nTest.");
        CreateVersionFile("""{"version": "1.5.0"}""");

        var manager = new SpecManager(_tempDir);
        var version = await manager.GetSpecVersionAsync();

        Assert.NotNull(version);
        Assert.Equal("1.5.0", version!.Version);
        Assert.Equal("unknown", version.LastUpdated);
    }

    [Fact]
    public async Task ComputeContentHashAsync_DetectsEditToOlderFile_WhenNewestMtimeUnchanged()
    {
        // Regression: previous cache invalidation used (newestMtime, count), so edits to an
        // older file left the cache key stable and served stale content. See issue #63.
        CreateSpec("000-overview", "# Overview\n\n## Purpose\nOriginal.");
        CreateSpec("014-recent", "# Recent\n\n## Purpose\nNewer file.");

        // Force deterministic mtimes: 000-overview older, 014-recent newest.
        var older = Path.Combine(_tempDir, "000-overview", "spec.md");
        var newest = Path.Combine(_tempDir, "014-recent", "spec.md");
        var olderMtime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newestMtime = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(older, olderMtime);
        File.SetLastWriteTimeUtc(newest, newestMtime);

        var manager = new SpecManager(_tempDir);
        var hash1 = await manager.ComputeContentHashAsync();

        // Edit the older file. Bump its mtime but keep it still older than the newest file.
        File.WriteAllText(older, "# Overview\n\n## Purpose\nEdited in place.");
        File.SetLastWriteTimeUtc(older, olderMtime.AddSeconds(1));
        // Keep the newest file untouched.
        File.SetLastWriteTimeUtc(newest, newestMtime);

        var hash2 = await manager.ComputeContentHashAsync();

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public async Task ComputeContentHashAsync_InvalidatesCacheOnFileDeletion()
    {
        CreateSpec("000-overview", "# Overview\n\n## Purpose\nTest 1.");
        CreateSpec("001-domain", "# Domain\n\n## Purpose\nTest 2.");
        var manager = new SpecManager(_tempDir);

        var hash1 = await manager.ComputeContentHashAsync();

        // Delete a spec section (not the newest)
        Directory.Delete(Path.Combine(_tempDir, "000-overview"), recursive: true);

        var hash2 = await manager.ComputeContentHashAsync();

        Assert.NotEqual(hash1, hash2);
    }

    // ── SearchSpecsAsync ───────────────────────────────────────

    [Fact]
    public async Task SearchSpecs_ReturnsEmpty_WhenQueryIsEmpty()
    {
        CreateSpec("000-overview", "# System Overview\n\n## Purpose\nHigh-level architecture.");
        var manager = new SpecManager(_tempDir);

        Assert.Empty(await manager.SearchSpecsAsync(""));
        Assert.Empty(await manager.SearchSpecsAsync("   "));
        Assert.Empty(await manager.SearchSpecsAsync(null!));
    }

    [Fact]
    public async Task SearchSpecs_ReturnsEmpty_WhenNoDirExists()
    {
        var manager = new SpecManager(Path.Combine(_tempDir, "nonexistent"));
        Assert.Empty(await manager.SearchSpecsAsync("test query"));
    }

    [Fact]
    public async Task SearchSpecs_FindsSectionByHeadingKeyword()
    {
        CreateSpec("000-overview", "# System Overview\n\n## Purpose\nHigh-level architecture.");
        CreateSpec("001-domain", "# Domain Model\n\n## Purpose\nAll domain types.");
        var manager = new SpecManager(_tempDir);

        var results = await manager.SearchSpecsAsync("domain");

        Assert.Single(results);
        Assert.Equal("001-domain", results[0].Id);
        Assert.Contains("domain", results[0].MatchedTerms);
    }

    [Fact]
    public async Task SearchSpecs_RanksHeadingMatchesHigherThanBody()
    {
        // "agent" in heading should rank higher than "agent" only in body
        CreateSpec("000-agents", "# Agent System\n\n## Purpose\nManages agents.\n\nAgents execute tasks.");
        CreateSpec("001-tasks", "# Task Management\n\n## Purpose\nManages tasks.\n\nTasks are assigned to agents.");
        var manager = new SpecManager(_tempDir);

        var results = await manager.SearchSpecsAsync("agent");

        Assert.True(results.Count >= 2);
        Assert.Equal("000-agents", results[0].Id);
    }

    [Fact]
    public async Task SearchSpecs_RespectsMaxResults()
    {
        for (var i = 0; i < 10; i++)
            CreateSpec($"{i:D3}-section", $"# Section {i}\n\n## Purpose\nTest purpose {i}.\n\nCommon keyword here.");

        var manager = new SpecManager(_tempDir);
        var results = await manager.SearchSpecsAsync("keyword", maxResults: 3);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task SearchSpecs_MultiTermQuery_BoostsSectionsMatchingMoreTerms()
    {
        CreateSpec("000-both", "# Agent Orchestrator\n\n## Purpose\nOrchestrates agent tasks.");
        CreateSpec("001-agent-only", "# Agent System\n\n## Purpose\nAgent management.");
        CreateSpec("002-task-only", "# Task Queue\n\n## Purpose\nTask processing.");
        var manager = new SpecManager(_tempDir);

        var results = await manager.SearchSpecsAsync("agent task");

        Assert.True(results.Count >= 2);
        // Section matching both terms should rank highest
        Assert.Equal("000-both", results[0].Id);
    }

    [Fact]
    public async Task SearchSpecs_FiltersStopWords()
    {
        CreateSpec("000-overview", "# The System\n\n## Purpose\nAn overview.");
        var manager = new SpecManager(_tempDir);

        // "the" and "is" are stop words; "system" should match
        var results = await manager.SearchSpecsAsync("the system is");

        Assert.Single(results);
        Assert.Equal("000-overview", results[0].Id);
        Assert.Contains("system", results[0].MatchedTerms);
    }

    [Fact]
    public async Task SearchSpecs_ReturnsEmpty_WhenNoMatches()
    {
        CreateSpec("000-overview", "# System Overview\n\n## Purpose\nArchitecture.");
        var manager = new SpecManager(_tempDir);

        var results = await manager.SearchSpecsAsync("xyznonexistentterm");
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchSpecs_CaseInsensitive()
    {
        CreateSpec("000-auth", "# Authentication\n\n## Purpose\nUser authentication.");
        var manager = new SpecManager(_tempDir);

        var lower = await manager.SearchSpecsAsync("authentication");
        var upper = await manager.SearchSpecsAsync("AUTHENTICATION");

        Assert.Single(lower);
        Assert.Single(upper);
        Assert.Equal(lower[0].Score, upper[0].Score);
    }

    // ── LoadSpecContextWithRelevanceAsync ───────────────────────

    [Fact]
    public async Task LoadWithRelevance_ReturnsNull_WhenNoDirExists()
    {
        var manager = new SpecManager(Path.Combine(_tempDir, "nonexistent"));
        Assert.Null(await manager.LoadSpecContextWithRelevanceAsync("query"));
    }

    [Fact]
    public async Task LoadWithRelevance_MarksLinkedSectionsWithStar()
    {
        CreateSpec("000-overview", "# System Overview\n\n## Purpose\nArchitecture.");
        CreateSpec("001-domain", "# Domain Model\n\n## Purpose\nTypes.");
        var manager = new SpecManager(_tempDir);

        var result = await manager.LoadSpecContextWithRelevanceAsync(null, ["001-domain"]);

        Assert.NotNull(result);
        Assert.Contains("[★]", result);
        Assert.Contains("001-domain", result);
    }

    [Fact]
    public async Task LoadWithRelevance_MarksSearchMatchesWithDiamond()
    {
        CreateSpec("000-overview", "# System Overview\n\n## Purpose\nArchitecture.");
        CreateSpec("001-domain", "# Domain Model\n\n## Purpose\nAll entity types and relationships.");
        var manager = new SpecManager(_tempDir);

        var result = await manager.LoadSpecContextWithRelevanceAsync("domain entity");

        Assert.NotNull(result);
        Assert.Contains("[◆]", result);
        Assert.Contains("001-domain", result);
    }

    [Fact]
    public async Task LoadWithRelevance_PutsRelevantSectionsFirst()
    {
        CreateSpec("000-overview", "# System Overview\n\n## Purpose\nArchitecture.");
        CreateSpec("001-domain", "# Domain Model\n\n## Purpose\nEntity types.");
        CreateSpec("002-auth", "# Authentication\n\n## Purpose\nSecurity and auth flows.");
        var manager = new SpecManager(_tempDir);

        var result = await manager.LoadSpecContextWithRelevanceAsync("authentication security");

        Assert.NotNull(result);
        var lines = result!.Split('\n');
        // "Relevant sections" header should come first
        Assert.Contains("Relevant sections", lines[0]);
        // Auth section should appear before other sections
        var authIndex = Array.FindIndex(lines, l => l.Contains("002-auth"));
        var overviewIndex = Array.FindIndex(lines, l => l.Contains("000-overview"));
        Assert.True(authIndex < overviewIndex, "Relevant section should appear before non-matching sections");
    }

    [Fact]
    public async Task LoadWithRelevance_IncludesAllSections_EvenNonMatching()
    {
        CreateSpec("000-overview", "# System Overview\n\n## Purpose\nArchitecture.");
        CreateSpec("001-domain", "# Domain Model\n\n## Purpose\nEntity types.");
        var manager = new SpecManager(_tempDir);

        var result = await manager.LoadSpecContextWithRelevanceAsync("domain");

        Assert.NotNull(result);
        Assert.Contains("000-overview", result);
        Assert.Contains("001-domain", result);
    }

    [Fact]
    public async Task LoadWithRelevance_FallsBackToFlat_WhenNoQueryAndNoLinks()
    {
        CreateSpec("000-overview", "# System Overview\n\n## Purpose\nArchitecture.");
        var manager = new SpecManager(_tempDir);

        var result = await manager.LoadSpecContextWithRelevanceAsync(null, null);

        Assert.NotNull(result);
        Assert.Contains("000-overview", result);
        // No "Relevant sections" header when nothing is relevant
        Assert.DoesNotContain("Relevant sections", result);
    }

    // ── TokenizeQuery (internal) ───────────────────────────────

    [Fact]
    public void TokenizeQuery_SplitsOnWhitespace()
    {
        var tokens = SpecManager.TokenizeQuery("agent task system");
        Assert.Equal(3, tokens.Count);
        Assert.Contains("agent", tokens);
        Assert.Contains("task", tokens);
        Assert.Contains("system", tokens);
    }

    [Fact]
    public void TokenizeQuery_RemovesStopWords()
    {
        var tokens = SpecManager.TokenizeQuery("the agent is working on a task");
        Assert.DoesNotContain("the", tokens);
        Assert.DoesNotContain("is", tokens);
        Assert.Contains("agent", tokens);
        Assert.Contains("working", tokens);
        Assert.Contains("task", tokens);
    }

    [Fact]
    public void TokenizeQuery_RemovesShortTerms()
    {
        var tokens = SpecManager.TokenizeQuery("a DB in CI");
        // "a" is a stopword, "DB" and "in" and "CI" are 2 chars — all filtered
        Assert.Empty(tokens);
    }

    [Fact]
    public void TokenizeQuery_LowercasesAndDeduplicates()
    {
        var tokens = SpecManager.TokenizeQuery("Agent AGENT agent");
        Assert.Single(tokens);
        Assert.Equal("agent", tokens[0]);
    }

    // ── CountOccurrences (internal) ────────────────────────────

    [Fact]
    public void CountOccurrences_FindsMultipleMatches()
    {
        Assert.Equal(3, SpecManager.CountOccurrences("agent agent agent", "agent"));
    }

    [Fact]
    public void CountOccurrences_CaseInsensitive()
    {
        Assert.Equal(2, SpecManager.CountOccurrences("Agent AGENT", "agent"));
    }

    [Fact]
    public void CountOccurrences_ReturnsZero_WhenNoMatch()
    {
        Assert.Equal(0, SpecManager.CountOccurrences("hello world", "xyz"));
    }

    [Fact]
    public void CountOccurrences_HandlesEmptyInputs()
    {
        Assert.Equal(0, SpecManager.CountOccurrences("", "test"));
        Assert.Equal(0, SpecManager.CountOccurrences("test", ""));
        Assert.Equal(0, SpecManager.CountOccurrences(null!, "test"));
    }
}
