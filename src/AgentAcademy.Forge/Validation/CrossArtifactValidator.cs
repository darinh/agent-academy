using System.Text.Json;
using AgentAcademy.Forge.Models;

namespace AgentAcademy.Forge.Validation;

/// <summary>
/// Cross-artifact reference validator. Checks that references between
/// the current artifact and input artifacts actually resolve.
/// Pure code — no LLM. Runs after structural and semantic validators.
/// </summary>
public sealed class CrossArtifactValidator
{
    /// <summary>
    /// Validate cross-artifact references between the current artifact and its inputs.
    /// </summary>
    /// <param name="envelope">The artifact being validated.</param>
    /// <param name="inputArtifacts">
    /// Accepted input artifacts keyed by producing phase ID. In the spike methodology,
    /// phase IDs match artifact types (e.g. phase "requirements" produces artifact type "requirements").
    /// </param>
    /// <param name="attemptNumber">Current attempt number for trace records.</param>
    public IReadOnlyList<ValidatorResultTrace> Validate(
        ArtifactEnvelope envelope,
        IReadOnlyDictionary<string, ArtifactEnvelope> inputArtifacts,
        int attemptNumber)
    {
        // Dispatch by full schema ID for version-aware cross-artifact checks.
        // When a v2 schema is added, add a new case here (e.g. "contract/v2").
        var schemaId = $"{envelope.ArtifactType}/v{envelope.SchemaVersion}";
        return schemaId switch
        {
            "requirements/v1" => [], // No cross-artifact refs — first phase
            "contract/v1" => ValidateContractCrossRefs(envelope.Payload, inputArtifacts, attemptNumber),
            "function_design/v1" => ValidateFunctionDesignCrossRefs(envelope.Payload, inputArtifacts, attemptNumber),
            "implementation/v1" => ValidateImplementationCrossRefs(envelope.Payload, inputArtifacts, attemptNumber),
            "review/v1" => ValidateReviewCrossRefs(envelope.Payload, inputArtifacts, attemptNumber),
            _ => []
        };
    }

    /// <summary>
    /// Contract → Requirements: satisfies_fr_ids and examples[].fr_id must resolve.
    /// </summary>
    private static IReadOnlyList<ValidatorResultTrace> ValidateContractCrossRefs(
        JsonElement payload,
        IReadOnlyDictionary<string, ArtifactEnvelope> inputs,
        int attemptNumber)
    {
        var results = new List<ValidatorResultTrace>();

        if (!TryGetInputPayload(inputs, "requirements", out var reqPayload))
        {
            results.Add(MissingInput("contract", "requirements", attemptNumber));
            return results;
        }

        var frIds = CollectIds(reqPayload, "functional_requirements", "id");

        // Check satisfies_fr_ids in interfaces
        if (payload.TryGetProperty("interfaces", out var interfaces))
        {
            foreach (var iface in interfaces.EnumerateArray())
            {
                var ifaceName = SafeGetString(iface, "name") ?? "<unknown>";

                if (iface.TryGetProperty("satisfies_fr_ids", out var frRefs))
                {
                    foreach (var frRef in frRefs.EnumerateArray())
                    {
                        var refId = frRef.GetString();
                        if (refId is not null && !frIds.Contains(refId))
                        {
                            results.Add(DanglingRef(
                                $"/interfaces/{ifaceName}/satisfies_fr_ids",
                                refId, "requirements.functional_requirements",
                                attemptNumber));
                        }
                    }
                }
            }
        }

        // Check examples[].fr_id
        if (payload.TryGetProperty("examples", out var examples))
        {
            for (var i = 0; i < examples.GetArrayLength(); i++)
            {
                var example = examples[i];
                var frId = SafeGetString(example, "fr_id");
                if (frId is not null && !frIds.Contains(frId))
                {
                    results.Add(DanglingRef(
                        $"/examples/{i}/fr_id",
                        frId, "requirements.functional_requirements",
                        attemptNumber));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// FunctionDesign → Contract: implements[] must resolve to contract interface names.
    /// </summary>
    private static IReadOnlyList<ValidatorResultTrace> ValidateFunctionDesignCrossRefs(
        JsonElement payload,
        IReadOnlyDictionary<string, ArtifactEnvelope> inputs,
        int attemptNumber)
    {
        var results = new List<ValidatorResultTrace>();

        if (!TryGetInputPayload(inputs, "contract", out var contractPayload))
        {
            results.Add(MissingInput("function_design", "contract", attemptNumber));
            return results;
        }

        var ifaceNames = CollectStringValues(contractPayload, "interfaces", "name");

        // Check components[].implements → contract interface names
        if (payload.TryGetProperty("components", out var components))
        {
            foreach (var comp in components.EnumerateArray())
            {
                var compId = SafeGetString(comp, "id") ?? "<unknown>";

                if (comp.TryGetProperty("implements", out var impls))
                {
                    foreach (var impl in impls.EnumerateArray())
                    {
                        var implName = impl.GetString();
                        if (implName is not null && !ifaceNames.Contains(implName))
                        {
                            results.Add(DanglingRef(
                                $"/components/{compId}/implements",
                                implName, "contract.interfaces",
                                attemptNumber));
                        }
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Implementation → FunctionDesign: implements_component_ids must resolve.
    /// </summary>
    private static IReadOnlyList<ValidatorResultTrace> ValidateImplementationCrossRefs(
        JsonElement payload,
        IReadOnlyDictionary<string, ArtifactEnvelope> inputs,
        int attemptNumber)
    {
        var results = new List<ValidatorResultTrace>();

        if (!TryGetInputPayload(inputs, "function_design", out var fdPayload))
        {
            results.Add(MissingInput("implementation", "function_design", attemptNumber));
            return results;
        }

        var componentIds = CollectIds(fdPayload, "components", "id");

        // Check files[].implements_component_ids → function_design component IDs
        if (payload.TryGetProperty("files", out var files))
        {
            for (var i = 0; i < files.GetArrayLength(); i++)
            {
                var file = files[i];
                var filePath = SafeGetString(file, "path") ?? $"<file[{i}]>";

                if (file.TryGetProperty("implements_component_ids", out var compRefs))
                {
                    foreach (var compRef in compRefs.EnumerateArray())
                    {
                        var refId = compRef.GetString();
                        if (refId is not null && !componentIds.Contains(refId))
                        {
                            results.Add(DanglingRef(
                                $"/files/{filePath}/implements_component_ids",
                                refId, "function_design.components",
                                attemptNumber));
                        }
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Review → upstream artifacts: checks[].target_id must resolve by check kind.
    /// </summary>
    private static IReadOnlyList<ValidatorResultTrace> ValidateReviewCrossRefs(
        JsonElement payload,
        IReadOnlyDictionary<string, ArtifactEnvelope> inputs,
        int attemptNumber)
    {
        var results = new List<ValidatorResultTrace>();

        // Collect available ID sets from inputs
        var frIds = TryGetInputPayload(inputs, "requirements", out var reqPayload)
            ? CollectIds(reqPayload, "functional_requirements", "id")
            : null;

        var ifaceNames = TryGetInputPayload(inputs, "contract", out var contractPayload)
            ? CollectStringValues(contractPayload, "interfaces", "name")
            : null;

        var componentIds = TryGetInputPayload(inputs, "function_design", out var fdPayload)
            ? CollectIds(fdPayload, "components", "id")
            : null;

        if (payload.TryGetProperty("checks", out var checks))
        {
            foreach (var check in checks.EnumerateArray())
            {
                var checkId = SafeGetString(check, "id") ?? "<unknown>";
                var kind = SafeGetString(check, "kind");
                var targetId = SafeGetString(check, "target_id");

                if (kind is null || targetId is null) continue;

                HashSet<string>? validIds = kind switch
                {
                    "fr_satisfied" => frIds,
                    "contract_satisfied" => ifaceNames,
                    "design_satisfied" => componentIds,
                    _ => null // invariant_held and example_matches use string-based targets
                };

                if (validIds is not null && !validIds.Contains(targetId))
                {
                    results.Add(DanglingRef(
                        $"/checks/{checkId}/target_id",
                        targetId, $"upstream ({kind})",
                        attemptNumber));
                }
            }
        }

        return results;
    }

    // --- Helpers ---

    private static bool TryGetInputPayload(
        IReadOnlyDictionary<string, ArtifactEnvelope> inputs,
        string phaseId,
        out JsonElement payload)
    {
        if (inputs.TryGetValue(phaseId, out var envelope))
        {
            payload = envelope.Payload;
            return payload.ValueKind == JsonValueKind.Object;
        }

        payload = default;
        return false;
    }

    private static HashSet<string> CollectIds(JsonElement payload, string arrayProp, string idProp)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (payload.TryGetProperty(arrayProp, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var id = SafeGetString(item, idProp);
                if (id is not null) ids.Add(id);
            }
        }
        return ids;
    }

    private static HashSet<string> CollectStringValues(JsonElement payload, string arrayProp, string valueProp)
    {
        return CollectIds(payload, arrayProp, valueProp); // Same logic, different semantic name
    }

    private static string? SafeGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static ValidatorResultTrace DanglingRef(string path, string refId, string target, int attemptNumber)
    {
        return new ValidatorResultTrace
        {
            Phase = "cross-artifact",
            Code = "CROSS_ARTIFACT_DANGLING_REF",
            Severity = "error",
            Blocking = true,
            AttemptNumber = attemptNumber,
            Path = path,
            Evidence = $"'{refId}' not found in {target}",
            BlockingReason = $"Reference '{refId}' does not resolve to any entity in {target}."
        };
    }

    private static ValidatorResultTrace MissingInput(string artifactType, string requiredPhase, int attemptNumber)
    {
        return new ValidatorResultTrace
        {
            Phase = "cross-artifact",
            Code = "CROSS_ARTIFACT_INPUT_MISSING",
            Severity = "error",
            Blocking = true,
            AttemptNumber = attemptNumber,
            Evidence = $"{artifactType} requires input from '{requiredPhase}' phase",
            BlockingReason = $"Required input artifact from phase '{requiredPhase}' is not available."
        };
    }
}
