namespace AgentAcademy.Forge.Schemas;

/// <summary>
/// Central registry of artifact schemas, semantic rules, and per-schema
/// structural checkers. Frozen for the spike — all schemas are baked in.
/// </summary>
public sealed class SchemaRegistry
{
    private readonly Dictionary<string, SchemaEntry> _schemas = new(StringComparer.Ordinal);

    public SchemaRegistry()
    {
        Register(RequirementsV1.Entry);
        Register(ContractV1.Entry);
        Register(FunctionDesignV1.Entry);
        Register(ImplementationV1.Entry);
        Register(ReviewV1.Entry);
        Register(SourceIntentV1.Entry);
        Register(FidelityV1.Entry);
    }

    private void Register(SchemaEntry entry)
    {
        _schemas[entry.SchemaId] = entry;
    }

    /// <summary>
    /// Get a schema entry by its schema ID (e.g. "requirements/v1").
    /// Throws if not found.
    /// </summary>
    public SchemaEntry GetSchema(string schemaId)
    {
        if (!_schemas.TryGetValue(schemaId, out var entry))
            throw new ArgumentException($"Unknown schema: {schemaId}. Known schemas: {string.Join(", ", _schemas.Keys)}");
        return entry;
    }

    /// <summary>
    /// Get all registered schema IDs.
    /// </summary>
    public IReadOnlyCollection<string> SchemaIds => _schemas.Keys;
}

/// <summary>
/// A registered schema with its JSON Schema body, semantic rules, and
/// optional in-artifact structural checker.
/// </summary>
public sealed record SchemaEntry
{
    /// <summary>Schema identifier (e.g. "requirements/v1").</summary>
    public required string SchemaId { get; init; }

    /// <summary>Artifact type (e.g. "requirements").</summary>
    public required string ArtifactType { get; init; }

    /// <summary>Schema version (e.g. "1").</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>JSON Schema body as a string (for structural validation and prompt rendering).</summary>
    public required string SchemaBodyJson { get; init; }

    /// <summary>Semantic rules text (for prompt rendering and semantic validator rubric).</summary>
    public required string SemanticRules { get; init; }
}
