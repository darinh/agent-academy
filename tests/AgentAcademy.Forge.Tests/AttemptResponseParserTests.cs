using System.Text.Json;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Validation;

namespace AgentAcademy.Forge.Tests;

public sealed class AttemptResponseParserTests
{
    private static readonly PhaseDefinition RequirementsPhase = new()
    {
        Id = "requirements",
        Goal = "test",
        Inputs = [],
        OutputSchema = "requirements/v1",
        Instructions = "test"
    };

    [Fact]
    public void Parse_ValidBodyObject_ReturnsEnvelope()
    {
        var json = """{"body": {"task_summary": "test", "user_outcomes": []}}""";
        var result = AttemptResponseParser.Parse(json, RequirementsPhase, 1);

        Assert.True(result.Success);
        Assert.NotNull(result.Envelope);
        Assert.Equal("requirements", result.Envelope!.ArtifactType);
        Assert.Equal("1", result.Envelope.SchemaVersion);
        Assert.Equal("requirements", result.Envelope.ProducedByPhase);
        Assert.Equal(JsonValueKind.Object, result.Envelope.Payload.ValueKind);
    }

    [Fact]
    public void Parse_InvalidJson_FailsWithJsonParseError()
    {
        var result = AttemptResponseParser.Parse("not json at all", RequirementsPhase, 1);

        Assert.False(result.Success);
        Assert.Null(result.Envelope);
        Assert.Single(result.Failures);
        Assert.Equal("JSON_PARSE_FAILED", result.Failures[0].Code);
        Assert.True(result.Failures[0].Blocking);
        Assert.Equal(1, result.Failures[0].AttemptNumber);
    }

    [Fact]
    public void Parse_MissingBodyField_FailsWithBodyMissing()
    {
        var json = """{"result": {"x": 1}}""";
        var result = AttemptResponseParser.Parse(json, RequirementsPhase, 1);

        Assert.False(result.Success);
        Assert.Single(result.Failures);
        Assert.Equal("BODY_FIELD_MISSING", result.Failures[0].Code);
        Assert.Contains("result", result.Failures[0].Evidence!);
    }

    [Fact]
    public void Parse_BodyNotObject_FailsWithBodyNotObject()
    {
        var json = """{"body": "not an object"}""";
        var result = AttemptResponseParser.Parse(json, RequirementsPhase, 1);

        Assert.False(result.Success);
        Assert.Single(result.Failures);
        Assert.Equal("BODY_NOT_OBJECT", result.Failures[0].Code);
    }

    [Fact]
    public void Parse_RootNotObject_FailsWithRootNotObject()
    {
        var result = AttemptResponseParser.Parse("[1,2,3]", RequirementsPhase, 1);

        Assert.False(result.Success);
        Assert.Equal("ROOT_NOT_OBJECT", result.Failures[0].Code);
    }

    [Fact]
    public void Parse_EmptyBody_ReturnsEmptyPayload()
    {
        var json = """{"body": {}}""";
        var result = AttemptResponseParser.Parse(json, RequirementsPhase, 1);

        Assert.True(result.Success);
        Assert.Equal(JsonValueKind.Object, result.Envelope!.Payload.ValueKind);
    }

    [Fact]
    public void Parse_PreservesPayloadStructure()
    {
        var json = """
        {
          "body": {
            "task_summary": "Build an MCP server",
            "user_outcomes": [
              {"id": "U1", "outcome": "Server starts", "priority": "must"}
            ],
            "functional_requirements": [
              {"id": "FR1", "statement": "starts", "outcome_ids": ["U1"]}
            ],
            "non_functional_requirements": [],
            "out_of_scope": ["auth"],
            "open_questions": []
          }
        }
        """;

        var result = AttemptResponseParser.Parse(json, RequirementsPhase, 1);

        Assert.True(result.Success);
        var payload = result.Envelope!.Payload;
        Assert.Equal("Build an MCP server", payload.GetProperty("task_summary").GetString());
        Assert.Equal(1, payload.GetProperty("user_outcomes").GetArrayLength());
    }

    [Fact]
    public void Parse_DifferentPhase_SetsCorrectArtifactType()
    {
        var implPhase = new PhaseDefinition
        {
            Id = "implement",
            Goal = "Produce code",
            Inputs = ["contract", "function_design"],
            OutputSchema = "implementation/v1",
            Instructions = "Write code"
        };

        var json = """{"body": {"files": [], "build_command": "npm build", "notes": ""}}""";
        var result = AttemptResponseParser.Parse(json, implPhase, 2);

        Assert.True(result.Success);
        Assert.Equal("implementation", result.Envelope!.ArtifactType);
        Assert.Equal("1", result.Envelope.SchemaVersion);
        Assert.Equal("implement", result.Envelope.ProducedByPhase);
    }

    [Fact]
    public void Parse_AttemptNumber_PropagatedToFailures()
    {
        var result = AttemptResponseParser.Parse("bad json", RequirementsPhase, 3);

        Assert.Equal(3, result.Failures[0].AttemptNumber);
    }
}
