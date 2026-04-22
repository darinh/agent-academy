using System.Text.Json;
using AgentAcademy.Forge.Models;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class DiskMethodologyCatalogTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiskMethodologyCatalog _catalog;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public DiskMethodologyCatalogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"catalog-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _catalog = new DiskMethodologyCatalog(_tempDir, NullLogger<DiskMethodologyCatalog>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static MethodologyDefinition MakeMethodology(string id = "test-v1", int phases = 2)
    {
        var phaseList = new List<PhaseDefinition>();
        for (var i = 0; i < phases; i++)
        {
            phaseList.Add(new PhaseDefinition
            {
                Id = $"phase{i}",
                Goal = $"Phase {i} goal",
                Inputs = i > 0 ? [$"phase{i - 1}"] : [],
                OutputSchema = $"type{i}/v1",
                Instructions = $"Phase {i} instructions"
            });
        }

        return new MethodologyDefinition
        {
            Id = id,
            Description = $"Test methodology {id}",
            Phases = phaseList
        };
    }

    // ── Save and Get ────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_ThenGetAsync_RoundTrips()
    {
        var methodology = MakeMethodology("my-method-v1");
        await _catalog.SaveAsync(methodology);

        var loaded = await _catalog.GetAsync("my-method-v1");

        Assert.NotNull(loaded);
        Assert.Equal("my-method-v1", loaded.Id);
        Assert.Equal("Test methodology my-method-v1", loaded.Description);
        Assert.Equal(2, loaded.Phases.Count);
    }

    [Fact]
    public async Task SaveAsync_OverwritesExisting()
    {
        var v1 = MakeMethodology("overwrite-test");
        await _catalog.SaveAsync(v1);

        var v2 = MakeMethodology("overwrite-test", phases: 3);
        await _catalog.SaveAsync(v2);

        var loaded = await _catalog.GetAsync("overwrite-test");
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded.Phases.Count);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _catalog.GetAsync("nonexistent");
        Assert.Null(result);
    }

    // ── List ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_ReturnsAllMethodologies_SortedById()
    {
        await _catalog.SaveAsync(MakeMethodology("beta-v1", phases: 3));
        await _catalog.SaveAsync(MakeMethodology("alpha-v1", phases: 1));

        var list = await _catalog.ListAsync();

        Assert.Equal(2, list.Count);
        Assert.Equal("alpha-v1", list[0].Id);
        Assert.Equal("beta-v1", list[1].Id);
        Assert.Equal(1, list[0].PhaseCount);
        Assert.Equal(3, list[1].PhaseCount);
    }

    [Fact]
    public async Task ListAsync_SkipsMalformedFiles()
    {
        await _catalog.SaveAsync(MakeMethodology("good-v1"));

        // Write a malformed JSON file
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "bad.json"), "not json");

        var list = await _catalog.ListAsync();
        Assert.Single(list);
        Assert.Equal("good-v1", list[0].Id);
    }

    [Fact]
    public async Task ListAsync_ReturnsEmpty_WhenDirectoryEmpty()
    {
        var list = await _catalog.ListAsync();
        Assert.Empty(list);
    }

    // ── Seed ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeedAsync_CreatesFile_WhenNotExists()
    {
        var methodology = MakeMethodology("seed-test");
        await _catalog.SeedAsync(methodology);

        var loaded = await _catalog.GetAsync("seed-test");
        Assert.NotNull(loaded);
    }

    [Fact]
    public async Task SeedAsync_DoesNotOverwrite_WhenExists()
    {
        var original = MakeMethodology("seed-no-overwrite", phases: 2);
        await _catalog.SaveAsync(original);

        var replacement = MakeMethodology("seed-no-overwrite", phases: 5);
        await _catalog.SeedAsync(replacement);

        var loaded = await _catalog.GetAsync("seed-no-overwrite");
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Phases.Count); // Original, not replacement
    }

    // ── Validation ──────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_RejectsEmptyId()
    {
        var methodology = MakeMethodology("") with { Id = "" };
        await Assert.ThrowsAsync<ArgumentException>(() => _catalog.SaveAsync(methodology));
    }

    [Fact]
    public async Task SaveAsync_RejectsIdWithSpecialChars()
    {
        var methodology = MakeMethodology("valid-id") with { Id = "../escape" };
        await Assert.ThrowsAsync<ArgumentException>(() => _catalog.SaveAsync(methodology));
    }

    [Fact]
    public async Task SaveAsync_RejectsNoPhases()
    {
        var methodology = new MethodologyDefinition
        {
            Id = "no-phases",
            Phases = []
        };
        await Assert.ThrowsAsync<ArgumentException>(() => _catalog.SaveAsync(methodology));
    }

    [Fact]
    public async Task SaveAsync_RejectsDuplicatePhaseIds()
    {
        var methodology = new MethodologyDefinition
        {
            Id = "dup-phases",
            Phases =
            [
                new PhaseDefinition { Id = "a", Goal = "g", Inputs = [], OutputSchema = "t/v1", Instructions = "i" },
                new PhaseDefinition { Id = "a", Goal = "g", Inputs = [], OutputSchema = "t/v1", Instructions = "i" }
            ]
        };
        await Assert.ThrowsAsync<ArgumentException>(() => _catalog.SaveAsync(methodology));
    }

    [Fact]
    public async Task SaveAsync_RejectsUnknownInputReference()
    {
        var methodology = new MethodologyDefinition
        {
            Id = "bad-input",
            Phases =
            [
                new PhaseDefinition { Id = "a", Goal = "g", Inputs = ["nonexistent"], OutputSchema = "t/v1", Instructions = "i" }
            ]
        };
        await Assert.ThrowsAsync<ArgumentException>(() => _catalog.SaveAsync(methodology));
    }

    [Fact]
    public async Task SaveAsync_RejectsCyclicDependencies()
    {
        var methodology = new MethodologyDefinition
        {
            Id = "cyclic",
            Phases =
            [
                new PhaseDefinition { Id = "a", Goal = "g", Inputs = ["b"], OutputSchema = "t/v1", Instructions = "i" },
                new PhaseDefinition { Id = "b", Goal = "g", Inputs = ["a"], OutputSchema = "t/v1", Instructions = "i" }
            ]
        };
        await Assert.ThrowsAsync<ArgumentException>(() => _catalog.SaveAsync(methodology));
    }

    [Fact]
    public async Task SaveAsync_RejectsInvalidBudget()
    {
        var methodology = MakeMethodology("bad-budget") with { Budget = -5m };
        await Assert.ThrowsAsync<ArgumentException>(() => _catalog.SaveAsync(methodology));
    }

    [Fact]
    public async Task SaveAsync_RejectsFidelityWithBadTarget()
    {
        var methodology = MakeMethodology("bad-fidelity") with
        {
            Fidelity = new FidelityConfig { TargetPhase = "nonexistent" }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => _catalog.SaveAsync(methodology));
    }

    [Fact]
    public async Task SaveAsync_AcceptsValidFidelity()
    {
        var methodology = MakeMethodology("good-fidelity") with
        {
            Fidelity = new FidelityConfig { TargetPhase = "phase1" }
        };
        var id = await _catalog.SaveAsync(methodology);
        Assert.Equal("good-fidelity", id);
    }

    // ── Path safety ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_RejectsPathTraversal()
    {
        var result = await _catalog.GetAsync("../../../etc/passwd");
        Assert.Null(result); // Rejected by regex, returns null
    }

    // ── Summary fields ──────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_SummaryIncludesModelDefaults()
    {
        var methodology = MakeMethodology("with-models") with
        {
            ModelDefaults = new ModelDefaults { Generation = "gpt-4o", Judge = "gpt-4o-mini" },
            Budget = 10m,
            Control = new ControlConfig { TargetSchema = "implementation/v1" },
            Fidelity = new FidelityConfig { TargetPhase = "phase1" }
        };
        await _catalog.SaveAsync(methodology);

        var list = await _catalog.ListAsync();
        var summary = Assert.Single(list);
        Assert.Equal("gpt-4o", summary.GenerationModel);
        Assert.Equal("gpt-4o-mini", summary.JudgeModel);
        Assert.True(summary.HasBudget);
        Assert.True(summary.HasControl);
        Assert.True(summary.HasFidelity);
    }
}
