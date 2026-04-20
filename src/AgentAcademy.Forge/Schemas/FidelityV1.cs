namespace AgentAcademy.Forge.Schemas;

/// <summary>
/// fidelity/v1 schema definition. Produced by the terminal fidelity phase,
/// which compares the final pipeline output against the source intent
/// with zero access to intermediate artifacts.
/// </summary>
internal static class FidelityV1
{
    public static SchemaEntry Entry { get; } = new()
    {
        SchemaId = "fidelity/v1",
        ArtifactType = "fidelity",
        SchemaVersion = "1",
        SchemaBodyJson = SchemaBody,
        SemanticRules = SemanticRulesText
    };

    internal const string SchemaBody =
        """
        {
          "type": "object",
          "required": ["overall_match", "acceptance_criteria_results", "drift_detected"],
          "properties": {
            "overall_match": {
              "type": "string",
              "enum": ["PASS", "FAIL", "PARTIAL"]
            },
            "acceptance_criteria_results": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "required": ["criterion_id", "satisfied", "evidence"],
                "properties": {
                  "criterion_id": { "type": "string", "pattern": "^AC\\d+$" },
                  "satisfied": { "type": "boolean" },
                  "evidence": { "type": "string" }
                },
                "additionalProperties": false
              }
            },
            "drift_detected": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["code", "source_intent_ref", "evidence_locator", "explanation"],
                "properties": {
                  "code": {
                    "type": "string",
                    "enum": ["OMITTED_CONSTRAINT", "INVENTED_REQUIREMENT", "SCOPE_BROADENED", "SCOPE_NARROWED", "CONSTRAINT_WEAKENED"]
                  },
                  "source_intent_ref": {
                    "type": "string",
                    "description": "JSON pointer into the source-intent payload (e.g., /explicit_constraints/0)"
                  },
                  "evidence_locator": {
                    "type": "string",
                    "description": "JSON pointer or description locating the evidence in the final output artifact"
                  },
                  "explanation": {
                    "type": "string",
                    "description": "Human-readable explanation of how the drift occurred"
                  }
                },
                "additionalProperties": false
              }
            },
            "summary": {
              "type": "string",
              "description": "Brief overall assessment of fidelity between source intent and final output"
            }
          },
          "additionalProperties": false
        }
        """;

    internal const string SemanticRulesText =
        """
        - overall_match is PASS only if ALL acceptance criteria are satisfied AND zero blocking drift codes are detected.
        - overall_match is FAIL if ANY blocking drift code (OMITTED_CONSTRAINT, CONSTRAINT_WEAKENED) is detected.
        - overall_match is PARTIAL if all blocking criteria pass but advisory drift codes (INVENTED_REQUIREMENT, SCOPE_BROADENED, SCOPE_NARROWED) are present.
        - Every acceptance_criteria_results entry must reference a criterion_id from the source-intent artifact.
        - The drift_detected array uses a CLOSED taxonomy of exactly 5 codes. No free-text codes.
        - Drift codes must have evidence: source_intent_ref points to the original intent, evidence_locator points to where the drift manifests.
        - Do NOT invent drift where none exists. An empty drift_detected array with overall_match PASS is valid.
        - OMITTED_CONSTRAINT and CONSTRAINT_WEAKENED are blocking — they indicate the output may be functionally incorrect.
        - INVENTED_REQUIREMENT, SCOPE_BROADENED, SCOPE_NARROWED are advisory — they indicate divergence but not necessarily incorrectness.
        """;
}
