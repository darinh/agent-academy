using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Schemas;

namespace AgentAcademy.Forge.Tests;

public sealed class SchemaRegistryTests
{
    private readonly SchemaRegistry _registry = new();

    [Theory]
    [InlineData("requirements/v1", "requirements", "1")]
    [InlineData("contract/v1", "contract", "1")]
    [InlineData("function_design/v1", "function_design", "1")]
    [InlineData("implementation/v1", "implementation", "1")]
    [InlineData("review/v1", "review", "1")]
    [InlineData("source_intent/v1", "source_intent", "1")]
    [InlineData("fidelity/v1", "fidelity", "1")]
    public void GetSchema_ReturnsEntry_ForAllKnownSchemas(string schemaId, string expectedType, string expectedVersion)
    {
        var entry = _registry.GetSchema(schemaId);

        Assert.Equal(schemaId, entry.SchemaId);
        Assert.Equal(expectedType, entry.ArtifactType);
        Assert.Equal(expectedVersion, entry.SchemaVersion);
        Assert.NotEmpty(entry.SchemaBodyJson);
        Assert.NotEmpty(entry.SemanticRules);
    }

    [Fact]
    public void GetSchema_ThrowsForUnknown()
    {
        var ex = Assert.Throws<ArgumentException>(() => _registry.GetSchema("nonexistent/v1"));
        Assert.Contains("nonexistent/v1", ex.Message);
    }

    [Fact]
    public void SchemaIds_ContainsAll7Schemas()
    {
        var ids = _registry.SchemaIds;

        Assert.Equal(7, ids.Count);
        Assert.Contains("requirements/v1", ids);
        Assert.Contains("contract/v1", ids);
        Assert.Contains("function_design/v1", ids);
        Assert.Contains("implementation/v1", ids);
        Assert.Contains("review/v1", ids);
        Assert.Contains("source_intent/v1", ids);
        Assert.Contains("fidelity/v1", ids);
    }

    [Theory]
    [InlineData("requirements/v1")]
    [InlineData("contract/v1")]
    [InlineData("function_design/v1")]
    [InlineData("implementation/v1")]
    [InlineData("review/v1")]
    [InlineData("source_intent/v1")]
    [InlineData("fidelity/v1")]
    public void SchemaBodyJson_IsValidJson(string schemaId)
    {
        var entry = _registry.GetSchema(schemaId);
        var doc = System.Text.Json.JsonDocument.Parse(entry.SchemaBodyJson);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    // --- Schema Evolution: TryGetSchema ---

    [Fact]
    public void TryGetSchema_ReturnsTrue_ForKnownSchema()
    {
        var found = _registry.TryGetSchema("requirements/v1", out var entry);

        Assert.True(found);
        Assert.NotNull(entry);
        Assert.Equal("requirements/v1", entry!.SchemaId);
    }

    [Fact]
    public void TryGetSchema_ReturnsFalse_ForUnknownSchema()
    {
        var found = _registry.TryGetSchema("nonexistent/v99", out var entry);

        Assert.False(found);
        Assert.Null(entry);
    }

    // --- Schema Evolution: GetSchemasByType ---

    [Theory]
    [InlineData("requirements", 1)]
    [InlineData("contract", 1)]
    [InlineData("fidelity", 1)]
    public void GetSchemasByType_ReturnsAllVersionsForType(string artifactType, int expectedCount)
    {
        var schemas = _registry.GetSchemasByType(artifactType);

        Assert.Equal(expectedCount, schemas.Count);
        Assert.All(schemas, s => Assert.Equal(artifactType, s.ArtifactType));
    }

    [Fact]
    public void GetSchemasByType_ReturnsEmpty_ForUnknownType()
    {
        var schemas = _registry.GetSchemasByType("nonexistent");

        Assert.Empty(schemas);
    }

    // --- Schema Evolution: Lifecycle status ---

    [Theory]
    [InlineData("requirements/v1")]
    [InlineData("contract/v1")]
    [InlineData("function_design/v1")]
    [InlineData("implementation/v1")]
    [InlineData("review/v1")]
    public void AllPipelineSchemas_AreActive(string schemaId)
    {
        var entry = _registry.GetSchema(schemaId);
        Assert.Equal(SchemaStatus.Active, entry.Status);
    }

    [Theory]
    [InlineData("source_intent/v1")]
    [InlineData("fidelity/v1")]
    public void InternalSchemas_AreMarkedInternal(string schemaId)
    {
        var entry = _registry.GetSchema(schemaId);
        Assert.True(entry.IsInternal);
    }

    [Theory]
    [InlineData("requirements/v1")]
    [InlineData("contract/v1")]
    [InlineData("function_design/v1")]
    [InlineData("implementation/v1")]
    [InlineData("review/v1")]
    public void PipelineSchemas_AreNotInternal(string schemaId)
    {
        var entry = _registry.GetSchema(schemaId);
        Assert.False(entry.IsInternal);
    }

    // --- Schema Evolution: ValidateMethodology ---

    [Fact]
    public void ValidateMethodology_ReturnsNoDiagnostics_ForValidMethodology()
    {
        var methodology = new MethodologyDefinition
        {
            Id = "test-v1",
            Phases = new[]
            {
                new PhaseDefinition { Id = "req", Goal = "G", Inputs = [], OutputSchema = "requirements/v1", Instructions = "I" },
                new PhaseDefinition { Id = "con", Goal = "G", Inputs = ["req"], OutputSchema = "contract/v1", Instructions = "I" }
            }
        };

        var diagnostics = _registry.ValidateMethodology(methodology);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ValidateMethodology_ReturnsError_ForUnknownSchema()
    {
        var methodology = new MethodologyDefinition
        {
            Id = "test-v1",
            Phases = new[]
            {
                new PhaseDefinition { Id = "req", Goal = "G", Inputs = [], OutputSchema = "nonexistent/v99", Instructions = "I" }
            }
        };

        var diagnostics = _registry.ValidateMethodology(methodology);

        Assert.Single(diagnostics);
        Assert.Equal(SchemaValidationSeverity.Error, diagnostics[0].Severity);
        Assert.Contains("nonexistent/v99", diagnostics[0].Message);
        Assert.Equal("req", diagnostics[0].PhaseId);
    }

    [Fact]
    public void ValidateMethodology_ReturnsError_ForUnknownControlSchema()
    {
        var methodology = new MethodologyDefinition
        {
            Id = "test-v1",
            Phases = new[]
            {
                new PhaseDefinition { Id = "req", Goal = "G", Inputs = [], OutputSchema = "requirements/v1", Instructions = "I" }
            },
            Control = new ControlConfig { TargetSchema = "nonexistent/v99" }
        };

        var diagnostics = _registry.ValidateMethodology(methodology);

        Assert.Single(diagnostics);
        Assert.Equal(SchemaValidationSeverity.Error, diagnostics[0].Severity);
        Assert.Equal("control", diagnostics[0].PhaseId);
    }

    [Fact]
    public void ValidateMethodology_AllowsRetiredSchemas_OnResume()
    {
        // To test this, we'd need a retired schema in the registry.
        // For now, verify that all current schemas produce no diagnostics when isNewRun=false.
        var methodology = new MethodologyDefinition
        {
            Id = "test-v1",
            Phases = new[]
            {
                new PhaseDefinition { Id = "req", Goal = "G", Inputs = [], OutputSchema = "requirements/v1", Instructions = "I" }
            }
        };

        var diagnostics = _registry.ValidateMethodology(methodology, isNewRun: false);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ValidateMethodology_RejectsInternalSchema_InPhase()
    {
        var methodology = new MethodologyDefinition
        {
            Id = "test-v1",
            Phases = new[]
            {
                new PhaseDefinition { Id = "si", Goal = "G", Inputs = [], OutputSchema = "source_intent/v1", Instructions = "I" }
            }
        };

        var diagnostics = _registry.ValidateMethodology(methodology);

        Assert.Single(diagnostics);
        Assert.Equal(SchemaValidationSeverity.Error, diagnostics[0].Severity);
        Assert.Contains("engine-internal", diagnostics[0].Message);
    }

    [Fact]
    public void ValidateMethodology_RejectsInternalSchema_InControl()
    {
        var methodology = new MethodologyDefinition
        {
            Id = "test-v1",
            Phases = new[]
            {
                new PhaseDefinition { Id = "req", Goal = "G", Inputs = [], OutputSchema = "requirements/v1", Instructions = "I" }
            },
            Control = new ControlConfig { TargetSchema = "fidelity/v1" }
        };

        var diagnostics = _registry.ValidateMethodology(methodology);

        Assert.Single(diagnostics);
        Assert.Equal(SchemaValidationSeverity.Error, diagnostics[0].Severity);
        Assert.Contains("engine-internal", diagnostics[0].Message);
        Assert.Equal("control", diagnostics[0].PhaseId);
    }

    // --- Schema Evolution: SchemaStatus defaults ---

    [Fact]
    public void SchemaEntry_DefaultsToActive()
    {
        var entry = new SchemaEntry
        {
            SchemaId = "test/v1",
            ArtifactType = "test",
            SchemaVersion = "1",
            SchemaBodyJson = "{}",
            SemanticRules = "none"
        };

        Assert.Equal(SchemaStatus.Active, entry.Status);
        Assert.False(entry.IsInternal);
    }

    // --- Schema Evolution: edge cases ---

    [Fact]
    public void ValidateMethodology_EmptyPhases_ReturnsNoDiagnostics()
    {
        var methodology = new MethodologyDefinition
        {
            Id = "empty-v1",
            Phases = []
        };

        var diagnostics = _registry.ValidateMethodology(methodology);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ValidateMethodology_MultipleInvalidSchemas_ReturnsAllDiagnostics()
    {
        var methodology = new MethodologyDefinition
        {
            Id = "multi-bad-v1",
            Phases = new[]
            {
                new PhaseDefinition { Id = "p1", Goal = "G", Inputs = [], OutputSchema = "fake/v99", Instructions = "I" },
                new PhaseDefinition { Id = "p2", Goal = "G", Inputs = ["p1"], OutputSchema = "also_fake/v1", Instructions = "I" }
            },
            Control = new ControlConfig { TargetSchema = "control_fake/v1" }
        };

        var diagnostics = _registry.ValidateMethodology(methodology);

        Assert.Equal(3, diagnostics.Count);
        Assert.All(diagnostics, d => Assert.Equal(SchemaValidationSeverity.Error, d.Severity));
        Assert.Contains(diagnostics, d => d.PhaseId == "p1" && d.SchemaId == "fake/v99");
        Assert.Contains(diagnostics, d => d.PhaseId == "p2" && d.SchemaId == "also_fake/v1");
        Assert.Contains(diagnostics, d => d.PhaseId == "control" && d.SchemaId == "control_fake/v1");
    }

    [Fact]
    public void ValidateMethodology_NullControl_SkipsControlValidation()
    {
        var methodology = new MethodologyDefinition
        {
            Id = "no-control-v1",
            Phases = new[]
            {
                new PhaseDefinition { Id = "req", Goal = "G", Inputs = [], OutputSchema = "requirements/v1", Instructions = "I" }
            },
            Control = null
        };

        var diagnostics = _registry.ValidateMethodology(methodology);

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("requirements")]
    [InlineData("contract")]
    [InlineData("function_design")]
    [InlineData("implementation")]
    [InlineData("review")]
    [InlineData("source_intent")]
    [InlineData("fidelity")]
    public void GetSchemasByType_ReturnsOrderedByVersion(string artifactType)
    {
        var schemas = _registry.GetSchemasByType(artifactType);

        Assert.NotEmpty(schemas);
        for (int i = 1; i < schemas.Count; i++)
        {
            var prev = int.Parse(schemas[i - 1].SchemaVersion);
            var curr = int.Parse(schemas[i].SchemaVersion);
            Assert.True(prev <= curr, $"Schema versions not ordered: {prev} > {curr}");
        }
    }
}
