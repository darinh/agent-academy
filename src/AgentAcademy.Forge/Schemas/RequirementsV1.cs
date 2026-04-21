namespace AgentAcademy.Forge.Schemas;

/// <summary>
/// requirements/v1 schema definition. Frozen for spike.
/// </summary>
internal static class RequirementsV1
{
    public static SchemaEntry Entry { get; } = new()
    {
        SchemaId = "requirements/v1",
        ArtifactType = "requirements",
        SchemaVersion = "1",
        SchemaBodyJson = SchemaBody,
        SemanticRules = SemanticRulesText
    };

    internal const string SchemaBody =
        """
        {
          "type": "object",
          "required": ["task_summary", "user_outcomes", "functional_requirements", "non_functional_requirements", "out_of_scope", "open_questions"],
          "properties": {
            "task_summary": {
              "type": "string",
              "maxLength": 200
            },
            "user_outcomes": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "required": ["id", "outcome", "priority"],
                "properties": {
                  "id": { "type": "string", "pattern": "^U\\d+$" },
                  "outcome": { "type": "string" },
                  "priority": { "type": "string", "enum": ["must", "should", "could"] }
                },
                "additionalProperties": false
              }
            },
            "functional_requirements": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "required": ["id", "statement", "outcome_ids"],
                "properties": {
                  "id": { "type": "string", "pattern": "^FR\\d+$" },
                  "statement": { "type": "string" },
                  "outcome_ids": {
                    "type": "array",
                    "items": { "type": "string" },
                    "minItems": 1
                  }
                },
                "additionalProperties": false
              }
            },
            "non_functional_requirements": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["id", "category", "statement"],
                "properties": {
                  "id": { "type": "string", "pattern": "^NFR\\d+$" },
                  "category": { "type": "string", "enum": ["performance", "security", "reliability", "usability", "other"] },
                  "statement": { "type": "string" }
                },
                "additionalProperties": false
              }
            },
            "out_of_scope": {
              "type": "array",
              "minItems": 1,
              "items": { "type": "string" }
            },
            "open_questions": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["id", "question", "assumed_answer"],
                "properties": {
                  "id": { "type": "string", "pattern": "^Q\\d+$" },
                  "question": { "type": "string" },
                  "assumed_answer": { "type": "string" }
                },
                "additionalProperties": false
              }
            }
          },
          "additionalProperties": false
        }
        """;

    internal const string SemanticRulesText =
        """
        - Each functional requirement must be testable (binary true/false determinable from observation).
        - No functional requirement is a solution-in-disguise (e.g., "use Redis" is a solution, not a requirement).
        - out_of_scope contains at least one entry demonstrating scope discipline.
        - Every outcome_ids reference in functional_requirements resolves to a user_outcomes id.
        - IDs are unique within their respective lists.
        """;
}
