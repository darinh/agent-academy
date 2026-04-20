namespace AgentAcademy.Forge.Schemas;

/// <summary>
/// contract/v1 schema definition. Frozen for spike.
/// </summary>
internal static class ContractV1
{
    public static SchemaEntry Entry { get; } = new()
    {
        SchemaId = "contract/v1",
        ArtifactType = "contract",
        SchemaVersion = "1",
        SchemaBodyJson = SchemaBody,
        SemanticRules = SemanticRulesText
    };

    internal const string SchemaBody =
        """
        {
          "type": "object",
          "required": ["interfaces", "data_shapes", "invariants", "examples"],
          "properties": {
            "interfaces": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "required": ["name", "kind", "signature", "description", "preconditions", "postconditions", "errors", "satisfies_fr_ids"],
                "properties": {
                  "name": { "type": "string" },
                  "kind": { "type": "string", "enum": ["function", "class", "module", "http_endpoint", "cli", "mcp_tool", "file"] },
                  "signature": { "type": "string" },
                  "description": { "type": "string" },
                  "preconditions": { "type": "array", "items": { "type": "string" } },
                  "postconditions": { "type": "array", "items": { "type": "string" } },
                  "errors": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "required": ["condition", "behavior"],
                      "properties": {
                        "condition": { "type": "string" },
                        "behavior": { "type": "string" }
                      },
                      "additionalProperties": false
                    }
                  },
                  "satisfies_fr_ids": {
                    "type": "array",
                    "items": { "type": "string" },
                    "minItems": 1
                  }
                },
                "additionalProperties": false
              }
            },
            "data_shapes": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["name", "fields"],
                "properties": {
                  "name": { "type": "string" },
                  "fields": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "required": ["name", "type", "required"],
                      "properties": {
                        "name": { "type": "string" },
                        "type": { "type": "string" },
                        "required": { "type": "boolean" },
                        "notes": { "type": "string" }
                      },
                      "additionalProperties": false
                    }
                  }
                },
                "additionalProperties": false
              }
            },
            "invariants": {
              "type": "array",
              "items": { "type": "string" }
            },
            "examples": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["scenario", "input", "output", "fr_id"],
                "properties": {
                  "scenario": { "type": "string" },
                  "input": {},
                  "output": {},
                  "fr_id": { "type": "string" }
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
        - Every must-priority and should-priority FR from the input requirements is satisfied by at least one interface.
        - Every satisfies_fr_ids reference resolves to an FR id in the input requirements artifact.
        - Signatures are concrete (no ???, no TODO, no placeholders).
        - Examples agree with the corresponding interface signatures and postconditions.
        - Every must-priority FR has at least one example in examples[].
        """;
}
