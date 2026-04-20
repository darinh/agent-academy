using System.Text.Json;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Schemas;
using Json.Schema;
using ForgeSchemaRegistry = AgentAcademy.Forge.Schemas.SchemaRegistry;

namespace AgentAcademy.Forge.Validation;

/// <summary>
/// Structural validator: JSON Schema validation + in-artifact reference checks.
/// Pure code, no LLM. Cheap and deterministic.
/// </summary>
public sealed class StructuralValidator
{
    private readonly ForgeSchemaRegistry _schemas;

    public StructuralValidator(ForgeSchemaRegistry schemas)
    {
        _schemas = schemas;
    }

    /// <summary>
    /// Validate an artifact envelope against its schema and in-artifact constraints.
    /// Returns empty list on success, or a list of blocking/non-blocking findings.
    /// </summary>
    public IReadOnlyList<ValidatorResultTrace> Validate(ArtifactEnvelope envelope, int attemptNumber)
    {
        var results = new List<ValidatorResultTrace>();
        var entry = _schemas.GetSchema($"{envelope.ArtifactType}/v{envelope.SchemaVersion}");

        // Phase 1: JSON Schema validation
        var schemaResults = ValidateJsonSchema(envelope.Payload, entry, attemptNumber);
        results.AddRange(schemaResults);

        // If schema validation failed with blocking errors, skip in-artifact checks
        if (results.Any(r => r.Blocking))
            return results;

        // Phase 2: In-artifact reference checks (per schema type)
        var refResults = ValidateInArtifactReferences(envelope.Payload, entry, attemptNumber);
        results.AddRange(refResults);

        return results;
    }

    private static IReadOnlyList<ValidatorResultTrace> ValidateJsonSchema(
        JsonElement payload, SchemaEntry entry, int attemptNumber)
    {
        var results = new List<ValidatorResultTrace>();

        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromText(entry.SchemaBodyJson);
        }
        catch (Exception ex)
        {
            results.Add(new ValidatorResultTrace
            {
                Phase = "structural",
                Code = "SCHEMA_LOAD_FAILED",
                Severity = "error",
                Blocking = true,
                AttemptNumber = attemptNumber,
                BlockingReason = $"Internal error: could not load schema for {entry.SchemaId}: {ex.Message}"
            });
            return results;
        }

        var evalOptions = new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        };

        var evalResult = schema.Evaluate(payload, evalOptions);

        if (!evalResult.IsValid)
        {
            // Collect all schema validation errors
            if (evalResult.Details is { Count: > 0 })
            {
                foreach (var detail in evalResult.Details.Where(d => !d.IsValid && d.Errors is { Count: > 0 }))
                {
                    var path = detail.InstanceLocation?.ToString() ?? "/";
                    foreach (var error in detail.Errors!)
                    {
                        results.Add(new ValidatorResultTrace
                        {
                            Phase = "structural",
                            Code = MapSchemaErrorToCode(error.Key),
                            Severity = "error",
                            Blocking = true,
                            AttemptNumber = attemptNumber,
                            Path = path,
                            Evidence = error.Value,
                            BlockingReason = $"Schema validation failed at {path}: {error.Value}"
                        });
                    }
                }
            }

            // If no details but still invalid, add a generic error
            if (results.Count == 0)
            {
                results.Add(new ValidatorResultTrace
                {
                    Phase = "structural",
                    Code = "SCHEMA_VALIDATION_FAILED",
                    Severity = "error",
                    Blocking = true,
                    AttemptNumber = attemptNumber,
                    BlockingReason = $"Payload does not match schema {entry.SchemaId}."
                });
            }
        }

        return results;
    }

    private static IReadOnlyList<ValidatorResultTrace> ValidateInArtifactReferences(
        JsonElement payload, SchemaEntry entry, int attemptNumber)
    {
        // Dispatch by full schema ID for version-aware in-artifact checks.
        // When a v2 schema is added, add a new case here (e.g. "requirements/v2").
        return entry.SchemaId switch
        {
            "requirements/v1" => ValidateRequirementsRefs(payload, attemptNumber),
            "contract/v1" => ValidateContractRefs(payload, attemptNumber),
            "function_design/v1" => ValidateFunctionDesignRefs(payload, attemptNumber),
            "implementation/v1" => ValidateImplementationRefs(payload, attemptNumber),
            "review/v1" => ValidateReviewRefs(payload, attemptNumber),
            _ => []
        };
    }

    // --- Per-schema in-artifact reference checks ---

    private static IReadOnlyList<ValidatorResultTrace> ValidateRequirementsRefs(
        JsonElement payload, int attemptNumber)
    {
        var results = new List<ValidatorResultTrace>();

        var outcomeIds = CollectIds(payload, "user_outcomes", "id");
        var frIds = CollectIds(payload, "functional_requirements", "id");

        // Check ID uniqueness
        results.AddRange(CheckIdUniqueness(outcomeIds, "user_outcomes", attemptNumber));
        results.AddRange(CheckIdUniqueness(frIds, "functional_requirements", attemptNumber));
        results.AddRange(CheckIdUniqueness(
            CollectIds(payload, "non_functional_requirements", "id"),
            "non_functional_requirements", attemptNumber));
        results.AddRange(CheckIdUniqueness(
            CollectIds(payload, "open_questions", "id"),
            "open_questions", attemptNumber));

        // Check outcome_ids references in functional_requirements
        if (payload.TryGetProperty("functional_requirements", out var frs))
        {
            foreach (var fr in frs.EnumerateArray())
            {
                var frId = fr.GetProperty("id").GetString()!;
                if (fr.TryGetProperty("outcome_ids", out var outcomeRefs))
                {
                    foreach (var refEl in outcomeRefs.EnumerateArray())
                    {
                        var refId = refEl.GetString()!;
                        if (!outcomeIds.Ids.Contains(refId))
                        {
                            results.Add(new ValidatorResultTrace
                            {
                                Phase = "structural",
                                Code = "DANGLING_REFERENCE",
                                Severity = "error",
                                Blocking = true,
                                AttemptNumber = attemptNumber,
                                Path = $"/functional_requirements/{frId}/outcome_ids",
                                Evidence = $"'{refId}' not found in user_outcomes",
                                BlockingReason = $"FR '{frId}' references outcome '{refId}' which does not exist in user_outcomes."
                            });
                        }
                    }
                }
            }
        }

        return results;
    }

    private static IReadOnlyList<ValidatorResultTrace> ValidateContractRefs(
        JsonElement payload, int attemptNumber)
    {
        var results = new List<ValidatorResultTrace>();

        // Interface name uniqueness
        var ifaceNames = CollectStringValues(payload, "interfaces", "name");
        results.AddRange(CheckStringUniqueness(ifaceNames, "interfaces/name", attemptNumber));

        return results;
    }

    private static IReadOnlyList<ValidatorResultTrace> ValidateFunctionDesignRefs(
        JsonElement payload, int attemptNumber)
    {
        var results = new List<ValidatorResultTrace>();

        var componentIds = CollectIds(payload, "components", "id");
        results.AddRange(CheckIdUniqueness(componentIds, "components", attemptNumber));

        // Check depends_on references
        if (payload.TryGetProperty("components", out var components))
        {
            foreach (var comp in components.EnumerateArray())
            {
                var compId = comp.GetProperty("id").GetString()!;
                if (comp.TryGetProperty("depends_on", out var deps))
                {
                    foreach (var dep in deps.EnumerateArray())
                    {
                        var depId = dep.GetString()!;
                        if (!componentIds.Ids.Contains(depId))
                        {
                            results.Add(new ValidatorResultTrace
                            {
                                Phase = "structural",
                                Code = "DANGLING_REFERENCE",
                                Severity = "error",
                                Blocking = true,
                                AttemptNumber = attemptNumber,
                                Path = $"/components/{compId}/depends_on",
                                Evidence = $"'{depId}' not found in components",
                                BlockingReason = $"Component '{compId}' depends on '{depId}' which is not a known component."
                            });
                        }
                    }
                }
            }

            // Check for dependency cycles (DAG check)
            results.AddRange(CheckDependencyCycles(components, attemptNumber));
        }

        // Check data_flow endpoint references
        if (payload.TryGetProperty("data_flow", out var dataFlow))
        {
            var idx = 0;
            foreach (var flow in dataFlow.EnumerateArray())
            {
                var from = flow.GetProperty("from").GetString()!;
                var to = flow.GetProperty("to").GetString()!;

                if (!componentIds.Ids.Contains(from))
                {
                    results.Add(new ValidatorResultTrace
                    {
                        Phase = "structural",
                        Code = "DANGLING_REFERENCE",
                        Severity = "error",
                        Blocking = true,
                        AttemptNumber = attemptNumber,
                        Path = $"/data_flow[{idx}]/from",
                        Evidence = $"'{from}' not found in components",
                        BlockingReason = $"data_flow[{idx}].from '{from}' is not a known component."
                    });
                }

                if (!componentIds.Ids.Contains(to))
                {
                    results.Add(new ValidatorResultTrace
                    {
                        Phase = "structural",
                        Code = "DANGLING_REFERENCE",
                        Severity = "error",
                        Blocking = true,
                        AttemptNumber = attemptNumber,
                        Path = $"/data_flow[{idx}]/to",
                        Evidence = $"'{to}' not found in components",
                        BlockingReason = $"data_flow[{idx}].to '{to}' is not a known component."
                    });
                }

                idx++;
            }
        }

        // Check error_handling component_id references
        if (payload.TryGetProperty("error_handling", out var errorHandling))
        {
            var idx = 0;
            foreach (var eh in errorHandling.EnumerateArray())
            {
                var compRef = eh.GetProperty("component_id").GetString()!;
                if (!componentIds.Ids.Contains(compRef))
                {
                    results.Add(new ValidatorResultTrace
                    {
                        Phase = "structural",
                        Code = "DANGLING_REFERENCE",
                        Severity = "error",
                        Blocking = true,
                        AttemptNumber = attemptNumber,
                        Path = $"/error_handling[{idx}]/component_id",
                        Evidence = $"'{compRef}' not found in components",
                        BlockingReason = $"error_handling[{idx}].component_id '{compRef}' is not a known component."
                    });
                }
                idx++;
            }
        }

        return results;
    }

    private static IReadOnlyList<ValidatorResultTrace> ValidateImplementationRefs(
        JsonElement payload, int attemptNumber)
    {
        var results = new List<ValidatorResultTrace>();

        // Check file paths: relative, no "..", not absolute
        if (payload.TryGetProperty("files", out var files))
        {
            var idx = 0;
            foreach (var file in files.EnumerateArray())
            {
                var path = file.GetProperty("path").GetString()!;

                if (System.IO.Path.IsPathRooted(path))
                {
                    results.Add(new ValidatorResultTrace
                    {
                        Phase = "structural",
                        Code = "ABSOLUTE_PATH",
                        Severity = "error",
                        Blocking = true,
                        AttemptNumber = attemptNumber,
                        Path = $"/files[{idx}]/path",
                        Evidence = path,
                        BlockingReason = $"File path must be relative, not absolute: '{path}'"
                    });
                }

                if (path.Split('/', '\\').Any(segment => segment == ".."))
                {
                    results.Add(new ValidatorResultTrace
                    {
                        Phase = "structural",
                        Code = "PATH_TRAVERSAL",
                        Severity = "error",
                        Blocking = true,
                        AttemptNumber = attemptNumber,
                        Path = $"/files[{idx}]/path",
                        Evidence = path,
                        BlockingReason = $"File path must not contain '..': '{path}'"
                    });
                }

                idx++;
            }
        }

        return results;
    }

    private static IReadOnlyList<ValidatorResultTrace> ValidateReviewRefs(
        JsonElement payload, int attemptNumber)
    {
        var results = new List<ValidatorResultTrace>();

        // Check verdict consistency with defects
        var verdict = payload.GetProperty("verdict").GetString()!;
        if (payload.TryGetProperty("defects", out var defects))
        {
            var hasCritical = defects.EnumerateArray()
                .Any(d => d.GetProperty("severity").GetString() == "critical");

            if (hasCritical && verdict == "pass")
            {
                results.Add(new ValidatorResultTrace
                {
                    Phase = "structural",
                    Code = "VERDICT_DEFECT_MISMATCH",
                    Severity = "error",
                    Blocking = true,
                    AttemptNumber = attemptNumber,
                    Path = "/verdict",
                    Evidence = $"verdict=pass but critical defects exist",
                    BlockingReason = "Verdict is 'pass' but there are critical defects. Verdict must not be 'pass' when critical defects exist."
                });
            }
        }

        // Check ID uniqueness
        results.AddRange(CheckIdUniqueness(
            CollectIds(payload, "checks", "id"), "checks", attemptNumber));
        results.AddRange(CheckIdUniqueness(
            CollectIds(payload, "defects", "id"), "defects", attemptNumber));

        return results;
    }

    // --- Helpers ---

    private static IdSet CollectIds(JsonElement root, string arrayProp, string idProp)
    {
        var ids = new List<string>();
        if (root.TryGetProperty(arrayProp, out var array))
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.TryGetProperty(idProp, out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    ids.Add(idEl.GetString()!);
            }
        }
        return new IdSet(ids, new HashSet<string>(ids));
    }

    private static StringSet CollectStringValues(JsonElement root, string arrayProp, string valueProp)
    {
        var values = new List<string>();
        if (root.TryGetProperty(arrayProp, out var array))
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.TryGetProperty(valueProp, out var valEl) && valEl.ValueKind == JsonValueKind.String)
                    values.Add(valEl.GetString()!);
            }
        }
        return new StringSet(values);
    }

    private static IReadOnlyList<ValidatorResultTrace> CheckIdUniqueness(
        IdSet idSet, string listName, int attemptNumber)
    {
        var results = new List<ValidatorResultTrace>();
        var seen = new HashSet<string>();
        foreach (var id in idSet.All)
        {
            if (!seen.Add(id))
            {
                results.Add(new ValidatorResultTrace
                {
                    Phase = "structural",
                    Code = "DUPLICATE_ID",
                    Severity = "error",
                    Blocking = true,
                    AttemptNumber = attemptNumber,
                    Path = $"/{listName}",
                    Evidence = $"Duplicate id: '{id}'",
                    BlockingReason = $"Duplicate ID '{id}' in {listName}. All IDs must be unique within their list."
                });
            }
        }
        return results;
    }

    private static IReadOnlyList<ValidatorResultTrace> CheckStringUniqueness(
        StringSet stringSet, string path, int attemptNumber)
    {
        var results = new List<ValidatorResultTrace>();
        var seen = new HashSet<string>();
        foreach (var val in stringSet.All)
        {
            if (!seen.Add(val))
            {
                results.Add(new ValidatorResultTrace
                {
                    Phase = "structural",
                    Code = "DUPLICATE_VALUE",
                    Severity = "error",
                    Blocking = true,
                    AttemptNumber = attemptNumber,
                    Path = $"/{path}",
                    Evidence = $"Duplicate: '{val}'",
                    BlockingReason = $"Duplicate value '{val}' at {path}. Values must be unique."
                });
            }
        }
        return results;
    }

    private static IReadOnlyList<ValidatorResultTrace> CheckDependencyCycles(
        JsonElement components, int attemptNumber)
    {
        // Build adjacency list
        var graph = new Dictionary<string, List<string>>();
        foreach (var comp in components.EnumerateArray())
        {
            var id = comp.GetProperty("id").GetString()!;
            var deps = new List<string>();
            if (comp.TryGetProperty("depends_on", out var depsArr))
            {
                foreach (var dep in depsArr.EnumerateArray())
                    deps.Add(dep.GetString()!);
            }
            graph[id] = deps;
        }

        // DFS cycle detection
        var visited = new HashSet<string>();
        var onStack = new HashSet<string>();
        var results = new List<ValidatorResultTrace>();

        foreach (var node in graph.Keys)
        {
            if (HasCycle(node, graph, visited, onStack))
            {
                results.Add(new ValidatorResultTrace
                {
                    Phase = "structural",
                    Code = "DEPENDENCY_CYCLE",
                    Severity = "error",
                    Blocking = true,
                    AttemptNumber = attemptNumber,
                    Path = "/components",
                    Evidence = $"Cycle detected involving component '{node}'",
                    BlockingReason = "Components must form a DAG (no dependency cycles)."
                });
                break; // One cycle error is enough
            }
        }

        return results;
    }

    private static bool HasCycle(
        string node,
        Dictionary<string, List<string>> graph,
        HashSet<string> visited,
        HashSet<string> onStack)
    {
        if (onStack.Contains(node)) return true;
        if (visited.Contains(node)) return false;

        visited.Add(node);
        onStack.Add(node);

        if (graph.TryGetValue(node, out var deps))
        {
            foreach (var dep in deps)
            {
                if (graph.ContainsKey(dep) && HasCycle(dep, graph, visited, onStack))
                    return true;
            }
        }

        onStack.Remove(node);
        return false;
    }

    private sealed record IdSet(IReadOnlyList<string> All, HashSet<string> Ids);
    private sealed record StringSet(IReadOnlyList<string> All);

    private static string MapSchemaErrorToCode(string schemaKeyword) => schemaKeyword switch
    {
        "type" => "TYPE_MISMATCH",
        "required" => "MISSING_REQUIRED_FIELD",
        "enum" => "INVALID_ENUM_VALUE",
        "minItems" => "ARRAY_TOO_SHORT",
        "maxLength" => "STRING_TOO_LONG",
        "pattern" => "PATTERN_MISMATCH",
        "additionalProperties" => "ADDITIONAL_PROPERTIES",
        _ => $"SCHEMA_{schemaKeyword.ToUpperInvariant()}"
    };
}
