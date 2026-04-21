namespace AgentAcademy.Forge.Schemas;

/// <summary>
/// function_design/v1 schema definition. Frozen for spike.
/// </summary>
internal static class FunctionDesignV1
{
    public static SchemaEntry Entry { get; } = new()
    {
        SchemaId = "function_design/v1",
        ArtifactType = "function_design",
        SchemaVersion = "1",
        SchemaBodyJson = SchemaBody,
        SemanticRules = SemanticRulesText
    };

    internal const string SchemaBody =
        """
        {
          "type": "object",
          "required": ["components", "data_flow", "error_handling", "deferred_decisions"],
          "properties": {
            "components": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "required": ["id", "name", "responsibility", "depends_on", "implements"],
                "properties": {
                  "id": { "type": "string", "pattern": "^C\\d+$" },
                  "name": { "type": "string" },
                  "responsibility": { "type": "string" },
                  "depends_on": { "type": "array", "items": { "type": "string" } },
                  "implements": { "type": "array", "items": { "type": "string" } }
                },
                "additionalProperties": false
              }
            },
            "data_flow": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["from", "to", "carries", "trigger"],
                "properties": {
                  "from": { "type": "string" },
                  "to": { "type": "string" },
                  "carries": { "type": "string" },
                  "trigger": { "type": "string" }
                },
                "additionalProperties": false
              }
            },
            "error_handling": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["component_id", "failure", "response"],
                "properties": {
                  "component_id": { "type": "string" },
                  "failure": { "type": "string" },
                  "response": { "type": "string" }
                },
                "additionalProperties": false
              }
            },
            "deferred_decisions": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["id", "decision", "rationale_for_deferring"],
                "properties": {
                  "id": { "type": "string", "pattern": "^D\\d+$" },
                  "decision": { "type": "string" },
                  "rationale_for_deferring": { "type": "string" }
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
        - Component IDs are unique.
        - Every depends_on reference resolves to another component ID.
        - Every implements reference resolves to an interface name from the input contract.
        - data_flow endpoints (from/to) resolve to component IDs.
        - No dependency cycles exist (components form a DAG).
        - Every contract interface is implemented by at least one component.
        - Every component has a single, narrow responsibility (no "manages everything" descriptions).
        - error_handling covers every interface error condition from the contract.
        """;
}
