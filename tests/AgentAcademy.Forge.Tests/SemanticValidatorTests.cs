using System.Text.Json;
using AgentAcademy.Forge.Llm;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Schemas;
using AgentAcademy.Forge.Validation;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Forge.Tests;

public sealed class SemanticValidatorTests
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

    private static SchemaEntry RequirementsSchema => new SchemaRegistry().GetSchema("requirements/v1");

    [Fact]
    public async Task AllRulesPass_ReturnsEmptyFindings()
    {
        var llm = StubLlmClient.WithFixedResponse("""{"findings": []}""");
        var validator = new SemanticValidator(llm, NullLogger<SemanticValidator>.Instance);
        var envelope = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "Build an MCP server",
          "user_outcomes": [{"id": "U1", "outcome": "Server starts", "priority": "must"}],
          "functional_requirements": [{"id": "FR1", "statement": "Server starts on port 3000", "outcome_ids": ["U1"]}],
          "non_functional_requirements": [],
          "out_of_scope": ["Auth"],
          "open_questions": []
        }
        """);

        var results = await validator.ValidateAsync(envelope, RequirementsSchema, 1);

        Assert.Empty(results);
        Assert.Single(llm.ReceivedRequests);
    }

    [Fact]
    public async Task FailedRule_ReturnsBlockingFinding()
    {
        var llm = StubLlmClient.WithFixedResponse("""
        {
          "findings": [
            {"rule_index": 0, "passed": false, "severity": "error", "reason": "Task summary exceeds 200 chars"}
          ]
        }
        """);
        var validator = new SemanticValidator(llm, NullLogger<SemanticValidator>.Instance);
        var envelope = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "test",
          "user_outcomes": [{"id": "U1", "outcome": "test", "priority": "must"}],
          "functional_requirements": [{"id": "FR1", "statement": "test", "outcome_ids": ["U1"]}],
          "non_functional_requirements": [],
          "out_of_scope": [],
          "open_questions": []
        }
        """);

        var results = await validator.ValidateAsync(envelope, RequirementsSchema, 1);

        Assert.Single(results);
        Assert.Equal("semantic", results[0].Phase);
        Assert.Equal("SEMANTIC_RULE_0", results[0].Code);
        Assert.True(results[0].Blocking);
        Assert.Equal("error", results[0].Severity);
    }

    [Fact]
    public async Task WarningFinding_IsNotBlocking()
    {
        var llm = StubLlmClient.WithFixedResponse("""
        {
          "findings": [
            {"rule_index": 2, "passed": false, "severity": "warning", "reason": "Could be more specific"}
          ]
        }
        """);
        var validator = new SemanticValidator(llm, NullLogger<SemanticValidator>.Instance);
        var envelope = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "test",
          "user_outcomes": [{"id": "U1", "outcome": "test", "priority": "must"}],
          "functional_requirements": [{"id": "FR1", "statement": "test", "outcome_ids": ["U1"]}],
          "non_functional_requirements": [],
          "out_of_scope": [],
          "open_questions": []
        }
        """);

        var results = await validator.ValidateAsync(envelope, RequirementsSchema, 1);

        Assert.Single(results);
        Assert.False(results[0].Blocking);
        Assert.Equal("warning", results[0].Severity);
        Assert.Equal("SEMANTIC_RULE_2", results[0].Code);
    }

    [Fact]
    public async Task PassedFindingsAreFiltered()
    {
        var llm = StubLlmClient.WithFixedResponse("""
        {
          "findings": [
            {"rule_index": 0, "passed": true, "severity": "error", "reason": "OK"},
            {"rule_index": 1, "passed": false, "severity": "error", "reason": "Missing"}
          ]
        }
        """);
        var validator = new SemanticValidator(llm, NullLogger<SemanticValidator>.Instance);
        var envelope = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "test",
          "user_outcomes": [{"id": "U1", "outcome": "test", "priority": "must"}],
          "functional_requirements": [{"id": "FR1", "statement": "test", "outcome_ids": ["U1"]}],
          "non_functional_requirements": [],
          "out_of_scope": [],
          "open_questions": []
        }
        """);

        var results = await validator.ValidateAsync(envelope, RequirementsSchema, 1);

        Assert.Single(results);
        Assert.Equal("SEMANTIC_RULE_1", results[0].Code);
    }

    [Fact]
    public async Task LlmFailure_ReturnsSemanticLlmFailedFinding()
    {
        var llm = StubLlmClient.WithError(LlmErrorKind.Transient, "Rate limited");
        var validator = new SemanticValidator(llm, NullLogger<SemanticValidator>.Instance);
        var envelope = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "test",
          "user_outcomes": [{"id": "U1", "outcome": "test", "priority": "must"}],
          "functional_requirements": [{"id": "FR1", "statement": "test", "outcome_ids": ["U1"]}],
          "non_functional_requirements": [],
          "out_of_scope": [],
          "open_questions": []
        }
        """);

        var results = await validator.ValidateAsync(envelope, RequirementsSchema, 1);

        Assert.Single(results);
        Assert.Equal("SEMANTIC_LLM_FAILED", results[0].Code);
        Assert.True(results[0].Blocking);
    }

    [Fact]
    public async Task LlmReturnsInvalidJson_ReturnsSemanticParseFailed()
    {
        var llm = StubLlmClient.WithFixedResponse("not json at all");
        var validator = new SemanticValidator(llm, NullLogger<SemanticValidator>.Instance);
        var envelope = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "test",
          "user_outcomes": [{"id": "U1", "outcome": "test", "priority": "must"}],
          "functional_requirements": [{"id": "FR1", "statement": "test", "outcome_ids": ["U1"]}],
          "non_functional_requirements": [],
          "out_of_scope": [],
          "open_questions": []
        }
        """);

        var results = await validator.ValidateAsync(envelope, RequirementsSchema, 1);

        Assert.Single(results);
        Assert.Equal("SEMANTIC_PARSE_FAILED", results[0].Code);
        Assert.True(results[0].Blocking);
    }

    [Fact]
    public async Task LlmReturnsMissingFindingsField_ReturnsSemanticParseFailed()
    {
        var llm = StubLlmClient.WithFixedResponse("""{"something_else": true}""");
        var validator = new SemanticValidator(llm, NullLogger<SemanticValidator>.Instance);
        var envelope = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "test",
          "user_outcomes": [{"id": "U1", "outcome": "test", "priority": "must"}],
          "functional_requirements": [{"id": "FR1", "statement": "test", "outcome_ids": ["U1"]}],
          "non_functional_requirements": [],
          "out_of_scope": [],
          "open_questions": []
        }
        """);

        var results = await validator.ValidateAsync(envelope, RequirementsSchema, 1);

        Assert.Single(results);
        Assert.Equal("SEMANTIC_PARSE_FAILED", results[0].Code);
    }

    [Fact]
    public async Task EmptySemanticRules_SkipsValidation()
    {
        var llm = new StubLlmClient();
        var validator = new SemanticValidator(llm, NullLogger<SemanticValidator>.Instance);
        var emptyRulesSchema = new SchemaEntry
        {
            SchemaId = "test/v1",
            ArtifactType = "test",
            SchemaVersion = "1",
            SchemaBodyJson = "{}",
            SemanticRules = ""
        };
        var envelope = MakeEnvelope("test", "1", "{}");

        var results = await validator.ValidateAsync(envelope, emptyRulesSchema, 1);

        Assert.Empty(results);
        Assert.Empty(llm.ReceivedRequests); // No LLM call made
    }

    [Fact]
    public async Task SeverityNormalization_CaseInsensitive()
    {
        var llm = StubLlmClient.WithFixedResponse("""
        {
          "findings": [
            {"rule_index": 0, "passed": false, "severity": "ERROR", "reason": "uppercase"}
          ]
        }
        """);
        var validator = new SemanticValidator(llm, NullLogger<SemanticValidator>.Instance);
        var envelope = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "test",
          "user_outcomes": [{"id": "U1", "outcome": "test", "priority": "must"}],
          "functional_requirements": [{"id": "FR1", "statement": "test", "outcome_ids": ["U1"]}],
          "non_functional_requirements": [],
          "out_of_scope": [],
          "open_questions": []
        }
        """);

        var results = await validator.ValidateAsync(envelope, RequirementsSchema, 1);

        Assert.Single(results);
        Assert.Equal("error", results[0].Severity);
        Assert.True(results[0].Blocking);
    }

    [Fact]
    public async Task UnknownSeverity_TreatedAsBlocking()
    {
        var llm = StubLlmClient.WithFixedResponse("""
        {
          "findings": [
            {"rule_index": 0, "passed": false, "severity": "critical", "reason": "unknown severity"}
          ]
        }
        """);
        var validator = new SemanticValidator(llm, NullLogger<SemanticValidator>.Instance);
        var envelope = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "test",
          "user_outcomes": [{"id": "U1", "outcome": "test", "priority": "must"}],
          "functional_requirements": [{"id": "FR1", "statement": "test", "outcome_ids": ["U1"]}],
          "non_functional_requirements": [],
          "out_of_scope": [],
          "open_questions": []
        }
        """);

        var results = await validator.ValidateAsync(envelope, RequirementsSchema, 1);

        Assert.Single(results);
        Assert.Equal("error", results[0].Severity); // Normalized to "error"
        Assert.True(results[0].Blocking);
    }

    [Fact]
    public async Task PromptContainsArtifactPayload()
    {
        var llm = StubLlmClient.WithFixedResponse("""{"findings": []}""");
        var validator = new SemanticValidator(llm, NullLogger<SemanticValidator>.Instance);
        var envelope = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "Build an MCP server",
          "user_outcomes": [{"id": "U1", "outcome": "Server starts", "priority": "must"}],
          "functional_requirements": [{"id": "FR1", "statement": "test", "outcome_ids": ["U1"]}],
          "non_functional_requirements": [],
          "out_of_scope": [],
          "open_questions": []
        }
        """);

        await validator.ValidateAsync(envelope, RequirementsSchema, 1);

        Assert.Single(llm.ReceivedRequests);
        var req = llm.ReceivedRequests[0];
        Assert.Contains("Build an MCP server", req.UserMessage);
        Assert.Contains("requirements/v1", req.UserMessage);
        Assert.True(req.JsonMode);
    }

    [Fact]
    public async Task CancellationToken_IsPropagated()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var llm = new StubLlmClient((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new LlmResponse { Content = "{}", InputTokens = 0, OutputTokens = 0, Model = "test", LatencyMs = 0 });
        });
        var validator = new SemanticValidator(llm, NullLogger<SemanticValidator>.Instance);
        var envelope = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "test",
          "user_outcomes": [{"id": "U1", "outcome": "test", "priority": "must"}],
          "functional_requirements": [{"id": "FR1", "statement": "test", "outcome_ids": ["U1"]}],
          "non_functional_requirements": [],
          "out_of_scope": [],
          "open_questions": []
        }
        """);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => validator.ValidateAsync(envelope, RequirementsSchema, 1, cts.Token));
    }
}
