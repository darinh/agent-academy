namespace AgentAcademy.Forge.Schemas;

/// <summary>
/// source_intent/v1 schema definition. Captures the human's original ask
/// as a structured artifact, separate from the derived requirements artifact.
/// Created once at Run start (immutable), passed to the requirements phase
/// for grounding, and to the fidelity phase for end-to-end validation.
/// </summary>
internal static class SourceIntentV1
{
    public static SchemaEntry Entry { get; } = new()
    {
        SchemaId = "source_intent/v1",
        ArtifactType = "source_intent",
        SchemaVersion = "1",
        SchemaBodyJson = SchemaBody,
        SemanticRules = SemanticRulesText,
        IsInternal = true
    };

    internal const string SchemaBody =
        """
        {
          "type": "object",
          "required": ["task_brief", "acceptance_criteria", "explicit_constraints", "examples", "counter_examples"],
          "properties": {
            "task_brief": {
              "type": "string",
              "description": "Verbatim task description from the human. Do NOT paraphrase, summarize, or edit."
            },
            "acceptance_criteria": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "required": ["id", "criterion", "verifiable"],
                "properties": {
                  "id": { "type": "string", "pattern": "^AC\\d+$" },
                  "criterion": { "type": "string" },
                  "verifiable": { "type": "boolean" }
                },
                "additionalProperties": false
              }
            },
            "explicit_constraints": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["id", "constraint"],
                "properties": {
                  "id": { "type": "string", "pattern": "^EC\\d+$" },
                  "constraint": { "type": "string" }
                },
                "additionalProperties": false
              }
            },
            "examples": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["scenario", "expected_behavior"],
                "properties": {
                  "scenario": { "type": "string" },
                  "expected_behavior": { "type": "string" }
                },
                "additionalProperties": false
              }
            },
            "counter_examples": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["scenario", "unacceptable_behavior"],
                "properties": {
                  "scenario": { "type": "string" },
                  "unacceptable_behavior": { "type": "string" }
                },
                "additionalProperties": false
              }
            },
            "preferred_approach": {
              "type": ["string", "null"],
              "description": "If the human specified a preferred implementation approach, record it. Null otherwise."
            }
          },
          "additionalProperties": false
        }
        """;

    internal const string SemanticRulesText =
        """
        - task_brief MUST be the verbatim task description — not paraphrased, summarized, or reworded.
        - Every acceptance criterion must be binary-testable (an observer can determine true/false).
        - Explicit constraints must be direct quotes or faithful extractions from the task brief, not inferred.
        - Examples and counter-examples should be derived from the task brief when present, not invented.
        - If the task brief contains no examples, the examples array may be empty.
        - If the task brief specifies no constraints beyond the functional ask, explicit_constraints may be empty.
        - preferred_approach is null unless the human explicitly stated a preferred approach.
        """;
}
