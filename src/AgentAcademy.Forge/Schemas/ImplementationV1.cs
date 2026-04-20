namespace AgentAcademy.Forge.Schemas;

/// <summary>
/// implementation/v1 schema definition. Frozen for spike.
/// </summary>
internal static class ImplementationV1
{
    public static SchemaEntry Entry { get; } = new()
    {
        SchemaId = "implementation/v1",
        ArtifactType = "implementation",
        SchemaVersion = "1",
        SchemaBodyJson = SchemaBody,
        SemanticRules = SemanticRulesText
    };

    internal const string SchemaBody =
        """
        {
          "type": "object",
          "required": ["files", "build_command", "notes"],
          "properties": {
            "files": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "required": ["path", "language", "content", "implements_component_ids"],
                "properties": {
                  "path": { "type": "string" },
                  "language": { "type": "string", "enum": ["typescript", "python", "csharp", "go", "rust", "markdown", "json", "yaml", "other"] },
                  "content": { "type": "string" },
                  "implements_component_ids": {
                    "type": "array",
                    "items": { "type": "string" }
                  }
                },
                "additionalProperties": false
              }
            },
            "build_command": { "type": "string" },
            "test_command": { "type": ["string", "null"] },
            "notes": { "type": "string" }
          },
          "additionalProperties": false
        }
        """;

    internal const string SemanticRulesText =
        """
        - At least one file is present.
        - File paths are relative, contain no ".." segments, and are not absolute.
        - Languages are from the declared enum.
        - implements_component_ids references resolve to component IDs from the input function_design.
        - Each file's content is parseable as the declared language (best-effort syntactic check).
        - No obvious placeholders (TODO, // implement me, pass # implement) in file contents.
        - Union of implements_component_ids across all files covers all component IDs in the input function_design.
        """;
}
