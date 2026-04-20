namespace AgentAcademy.Forge.Schemas;

/// <summary>
/// review/v1 schema definition. Frozen for spike.
/// </summary>
internal static class ReviewV1
{
    public static SchemaEntry Entry { get; } = new()
    {
        SchemaId = "review/v1",
        ArtifactType = "review",
        SchemaVersion = "1",
        SchemaBodyJson = SchemaBody,
        SemanticRules = SemanticRulesText
    };

    internal const string SchemaBody =
        """
        {
          "type": "object",
          "required": ["verdict", "summary", "checks", "defects", "improvements_for_next_iteration"],
          "properties": {
            "verdict": { "type": "string", "enum": ["pass", "fail", "needs_revision"] },
            "summary": { "type": "string", "maxLength": 500 },
            "checks": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["id", "kind", "target_id", "result", "evidence"],
                "properties": {
                  "id": { "type": "string", "pattern": "^CHK\\d+$" },
                  "kind": { "type": "string", "enum": ["fr_satisfied", "contract_satisfied", "design_satisfied", "invariant_held", "example_matches"] },
                  "target_id": { "type": "string" },
                  "result": { "type": "string", "enum": ["pass", "fail", "inconclusive"] },
                  "evidence": { "type": "string" }
                },
                "additionalProperties": false
              }
            },
            "defects": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["id", "severity", "description", "location"],
                "properties": {
                  "id": { "type": "string", "pattern": "^D\\d+$" },
                  "severity": { "type": "string", "enum": ["critical", "major", "minor"] },
                  "description": { "type": "string" },
                  "location": { "type": "string" }
                },
                "additionalProperties": false
              }
            },
            "improvements_for_next_iteration": {
              "type": "array",
              "items": { "type": "string" }
            }
          },
          "additionalProperties": false
        }
        """;

    internal const string SemanticRulesText =
        """
        - Verdict is consistent with defects: any critical defect means verdict cannot be "pass".
        - For each must-priority FR, at least one check exists and references concrete evidence.
        - Check IDs are unique.
        - Defect IDs are unique.
        - Reviewer's verdict is justifiable from the listed checks.
        - Every target_id of the appropriate kind resolves to an entity in the corresponding upstream artifact.
        """;
}
