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
}
