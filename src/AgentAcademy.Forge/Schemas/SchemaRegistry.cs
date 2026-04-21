using AgentAcademy.Forge.Models;

namespace AgentAcademy.Forge.Schemas;

/// <summary>
/// Central registry of artifact schemas, semantic rules, and per-schema
/// structural checkers. Supports multiple schema versions per artifact type.
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
    /// Try to get a schema entry. Returns false if the schema ID is not registered.
    /// </summary>
    public bool TryGetSchema(string schemaId, out SchemaEntry? entry)
    {
        var found = _schemas.TryGetValue(schemaId, out var e);
        entry = e;
        return found;
    }

    /// <summary>
    /// Get all registered schema versions for an artifact type (e.g. "requirements").
    /// Returns entries ordered by schema version ascending.
    /// </summary>
    public IReadOnlyList<SchemaEntry> GetSchemasByType(string artifactType)
    {
        return _schemas.Values
            .Where(e => string.Equals(e.ArtifactType, artifactType, StringComparison.Ordinal))
            .OrderBy(e => int.TryParse(e.SchemaVersion, out var v) ? v : int.MaxValue)
            .ToList();
    }

    /// <summary>
    /// Get all registered schema IDs.
    /// </summary>
    public IReadOnlyCollection<string> SchemaIds => _schemas.Keys;

    /// <summary>
    /// Validate that all schema references in a methodology are resolvable and
    /// respect lifecycle rules. Returns a list of diagnostics.
    /// </summary>
    /// <param name="methodology">The methodology to validate.</param>
    /// <param name="isNewRun">
    /// True when creating a new run (Retired schemas rejected).
    /// False when resuming a historical run (all statuses accepted).
    /// </param>
    public IReadOnlyList<SchemaValidationDiagnostic> ValidateMethodology(
        MethodologyDefinition methodology, bool isNewRun = true)
    {
        var diagnostics = new List<SchemaValidationDiagnostic>();

        foreach (var phase in methodology.Phases)
        {
            if (!_schemas.TryGetValue(phase.OutputSchema, out var entry))
            {
                diagnostics.Add(new SchemaValidationDiagnostic(
                    phase.OutputSchema, phase.Id, SchemaValidationSeverity.Error,
                    $"Schema '{phase.OutputSchema}' referenced by phase '{phase.Id}' is not registered."));
                continue;
            }

            if (entry.IsInternal)
            {
                diagnostics.Add(new SchemaValidationDiagnostic(
                    phase.OutputSchema, phase.Id, SchemaValidationSeverity.Error,
                    $"Schema '{phase.OutputSchema}' is engine-internal and cannot be used in methodology phases."));
                continue;
            }

            if (entry.Status == SchemaStatus.Retired && isNewRun)
            {
                diagnostics.Add(new SchemaValidationDiagnostic(
                    phase.OutputSchema, phase.Id, SchemaValidationSeverity.Error,
                    $"Schema '{phase.OutputSchema}' is Retired and cannot be used in new pipeline runs."));
            }
            else if (entry.Status == SchemaStatus.Deprecated && isNewRun)
            {
                diagnostics.Add(new SchemaValidationDiagnostic(
                    phase.OutputSchema, phase.Id, SchemaValidationSeverity.Warning,
                    $"Schema '{phase.OutputSchema}' is Deprecated. Consider migrating phase '{phase.Id}' to a newer version."));
            }
        }

        // Validate control target schema if present
        if (methodology.Control is not null)
        {
            if (!_schemas.TryGetValue(methodology.Control.TargetSchema, out var controlEntry))
            {
                diagnostics.Add(new SchemaValidationDiagnostic(
                    methodology.Control.TargetSchema, "control", SchemaValidationSeverity.Error,
                    $"Control target schema '{methodology.Control.TargetSchema}' is not registered."));
            }
            else if (controlEntry.IsInternal)
            {
                diagnostics.Add(new SchemaValidationDiagnostic(
                    methodology.Control.TargetSchema, "control", SchemaValidationSeverity.Error,
                    $"Control target schema '{methodology.Control.TargetSchema}' is engine-internal and cannot be used as a control target."));
            }
            else if (controlEntry.Status == SchemaStatus.Retired && isNewRun)
            {
                diagnostics.Add(new SchemaValidationDiagnostic(
                    methodology.Control.TargetSchema, "control", SchemaValidationSeverity.Error,
                    $"Control target schema '{methodology.Control.TargetSchema}' is Retired."));
            }
        }

        return diagnostics;
    }
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

    /// <summary>
    /// Lifecycle status. Defaults to Active for backward compatibility.
    /// See <see cref="Models.SchemaStatus"/> for transition rules.
    /// </summary>
    public SchemaStatus Status { get; init; } = SchemaStatus.Active;

    /// <summary>
    /// Whether this schema is engine-internal (e.g. source_intent, fidelity).
    /// Internal schemas are versioned with the engine, not per-methodology.
    /// </summary>
    public bool IsInternal { get; init; }
}

/// <summary>Severity of a schema validation diagnostic.</summary>
public enum SchemaValidationSeverity
{
    Warning,
    Error
}

/// <summary>
/// Diagnostic produced when validating methodology schema references.
/// </summary>
public sealed record SchemaValidationDiagnostic(
    string SchemaId,
    string PhaseId,
    SchemaValidationSeverity Severity,
    string Message);
