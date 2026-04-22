using System.Text.Json;
using AgentAcademy.Forge.Llm;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Schemas;
using AgentAcademy.Forge.Validation;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Forge.Tests;

public sealed class ValidatorPipelineTests
{
    private static ArtifactEnvelope MakeEnvelope(string artifactType, string schemaVersion, string payloadJson)
    {
        var doc = JsonDocument.Parse(payloadJson);
        return new ArtifactEnvelope
        {
            ArtifactType = artifactType,
            SchemaVersion = schemaVersion,
            ProducedByPhase = artifactType,
            Payload = doc.RootElement.Clone()
        };
    }

    private static ValidatorPipeline MakePipeline(StubLlmClient? llm = null)
    {
        var schemas = new SchemaRegistry();
        var structural = new StructuralValidator(schemas);
        var semantic = new SemanticValidator(
            llm ?? StubLlmClient.WithFixedResponse("""{"findings": []}"""),
            NullLogger<SemanticValidator>.Instance);
        var crossArtifact = new CrossArtifactValidator();
        return new ValidatorPipeline(structural, semantic, crossArtifact, schemas);
    }

    private static readonly string ValidRequirementsPayload = """
    {
      "task_summary": "Build an MCP server",
      "user_outcomes": [{"id": "U1", "outcome": "Server starts", "priority": "must"}],
      "functional_requirements": [{"id": "FR1", "statement": "Server starts on port 3000", "outcome_ids": ["U1"]}],
      "non_functional_requirements": [],
      "out_of_scope": ["Auth"],
      "open_questions": []
    }
    """;

    [Fact]
    public async Task AllTiersPass_ReturnsPassedResult()
    {
        var pipeline = MakePipeline();
        var envelope = MakeEnvelope("requirements", "1", ValidRequirementsPayload);

        var result = await pipeline.ValidateAsync(
            envelope, new Dictionary<string, ArtifactEnvelope>(), 1);

        Assert.True(result.Passed);
        Assert.Null(result.StoppedAtTier);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task StructuralFailure_ShortCircuits_SkipsSemantic()
    {
        var llm = new StubLlmClient();
        var pipeline = MakePipeline(llm);
        var envelope = MakeEnvelope("requirements", "1", """{"task_summary": "test"}""");

        var result = await pipeline.ValidateAsync(
            envelope, new Dictionary<string, ArtifactEnvelope>(), 1);

        Assert.False(result.Passed);
        Assert.Equal(ValidatorPhase.Structural, result.StoppedAtTier);
        Assert.NotEmpty(result.Findings);
        Assert.All(result.Findings, f => Assert.Equal("structural", f.Phase));
        Assert.Empty(llm.ReceivedRequests); // Semantic never called
    }

    [Fact]
    public async Task SemanticFailure_ShortCircuits_SkipsCrossArtifact()
    {
        var llm = StubLlmClient.WithFixedResponse("""
        {
          "findings": [
            {"rule_index": 0, "passed": false, "severity": "error", "reason": "Semantic rule violated"}
          ]
        }
        """);
        var pipeline = MakePipeline(llm);
        var envelope = MakeEnvelope("requirements", "1", ValidRequirementsPayload);

        var result = await pipeline.ValidateAsync(
            envelope, new Dictionary<string, ArtifactEnvelope>(), 1);

        Assert.False(result.Passed);
        Assert.Equal(ValidatorPhase.Semantic, result.StoppedAtTier);
        Assert.Contains(result.Findings, f => f.Phase == "semantic");
    }

    [Fact]
    public async Task CrossArtifactFailure_ReportedCorrectly()
    {
        var llm = StubLlmClient.WithFixedResponse("""{"findings": []}""");
        var pipeline = MakePipeline(llm);

        // Contract that references a non-existent FR
        var req = MakeEnvelope("requirements", "1", ValidRequirementsPayload);
        var contract = MakeEnvelope("contract", "1", """
        {
          "interfaces": [
            {"name": "startServer", "kind": "function", "signature": "() => void",
             "description": "starts", "preconditions": [], "postconditions": [],
             "errors": [], "satisfies_fr_ids": ["FR1", "FR_MISSING"]}
          ],
          "data_shapes": [],
          "invariants": [],
          "examples": []
        }
        """);

        var inputs = new Dictionary<string, ArtifactEnvelope> { ["requirements"] = req };
        var result = await pipeline.ValidateAsync(contract, inputs, 1);

        Assert.False(result.Passed);
        Assert.Equal(ValidatorPhase.CrossArtifact, result.StoppedAtTier);
        Assert.Contains(result.Findings, f => f.Phase == "cross-artifact");
    }

    [Fact]
    public async Task NonBlockingWarnings_DoNotShortCircuit()
    {
        var llm = StubLlmClient.WithFixedResponse("""
        {
          "findings": [
            {"rule_index": 0, "passed": false, "severity": "warning", "reason": "Minor concern"}
          ]
        }
        """);
        var pipeline = MakePipeline(llm);
        var envelope = MakeEnvelope("requirements", "1", ValidRequirementsPayload);

        var result = await pipeline.ValidateAsync(
            envelope, new Dictionary<string, ArtifactEnvelope>(), 1);

        Assert.True(result.Passed);
        Assert.Null(result.StoppedAtTier);
        Assert.Single(result.Findings); // Warning is included but didn't block
        Assert.False(result.Findings[0].Blocking);
    }

    [Fact]
    public async Task InputArtifacts_ThreadedToSemanticValidator()
    {
        var llm = StubLlmClient.WithFixedResponse("""{"findings": []}""");
        var pipeline = MakePipeline(llm);

        var req = MakeEnvelope("requirements", "1", ValidRequirementsPayload);
        var contract = MakeEnvelope("contract", "1", """
        {
          "interfaces": [
            {"name": "startServer", "kind": "function", "signature": "() => void",
             "description": "starts", "preconditions": [], "postconditions": [],
             "errors": [], "satisfies_fr_ids": ["FR1"]}
          ],
          "data_shapes": [],
          "invariants": [],
          "examples": []
        }
        """);

        var inputs = new Dictionary<string, ArtifactEnvelope> { ["requirements"] = req };
        var result = await pipeline.ValidateAsync(contract, inputs, 1);

        Assert.True(result.Passed);
        Assert.Single(llm.ReceivedRequests);
        var prompt = llm.ReceivedRequests[0].UserMessage;
        Assert.Contains("INPUT ARTIFACTS", prompt);
        Assert.Contains("requirements", prompt);
    }

    [Fact]
    public async Task AttemptNumber_PropagatedToAllFindings()
    {
        var llm = StubLlmClient.WithFixedResponse("""
        {
          "findings": [
            {"rule_index": 0, "passed": false, "severity": "error", "reason": "Failed"}
          ]
        }
        """);
        var pipeline = MakePipeline(llm);
        var envelope = MakeEnvelope("requirements", "1", ValidRequirementsPayload);

        var result = await pipeline.ValidateAsync(
            envelope, new Dictionary<string, ArtifactEnvelope>(), 3);

        Assert.All(result.Findings, f => Assert.Equal(3, f.AttemptNumber));
    }
}
