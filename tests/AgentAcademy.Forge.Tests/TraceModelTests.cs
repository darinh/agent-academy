using System.Text.Json;
using AgentAcademy.Forge.Models;

namespace AgentAcademy.Forge.Tests;

public sealed class TraceModelTests
{
    [Fact]
    public void RunTrace_Serialization_MatchesContract()
    {
        var run = new RunTrace
        {
            RunId = "R_01HX123",
            TaskId = "T1-mcp-server",
            MethodologyVersion = "1",
            StartedAt = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc),
            EndedAt = new DateTime(2026, 4, 20, 11, 0, 0, DateTimeKind.Utc),
            Outcome = "Succeeded",
            PipelineTokens = new TokenCount { In = 1000, Out = 2000 },
            ControlTokens = new TokenCount { In = 500, Out = 1000 },
            CostRatio = 2.5,
            FinalArtifactHashes = new Dictionary<string, string>
            {
                ["requirements"] = "sha256:abc",
                ["contract"] = "sha256:def"
            }
        };

        var json = JsonSerializer.Serialize(run);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("R_01HX123", root.GetProperty("runId").GetString());
        Assert.Equal("T1-mcp-server", root.GetProperty("taskId").GetString());
        Assert.Equal(1000, root.GetProperty("pipelineTokens").GetProperty("in").GetInt32());
        Assert.Equal("sha256:abc", root.GetProperty("finalArtifactHashes").GetProperty("requirements").GetString());
    }

    [Fact]
    public void RunTrace_FinalArtifactHashes_OmitsMissingPhases()
    {
        var run = new RunTrace
        {
            RunId = "R_01HX456",
            TaskId = "T1",
            MethodologyVersion = "1",
            StartedAt = DateTime.UtcNow,
            Outcome = "Failed",
            PipelineTokens = new TokenCount(),
            ControlTokens = new TokenCount(),
            FinalArtifactHashes = new Dictionary<string, string>
            {
                ["requirements"] = "sha256:abc"
                // contract and later phases omitted — they failed/didn't run
            }
        };

        var json = JsonSerializer.Serialize(run);
        var doc = JsonDocument.Parse(json);
        var hashes = doc.RootElement.GetProperty("finalArtifactHashes");

        Assert.True(hashes.TryGetProperty("requirements", out _));
        Assert.False(hashes.TryGetProperty("contract", out _));
    }

    [Fact]
    public void PhaseRunTrace_Serialization_MatchesContract()
    {
        var phaseRun = new PhaseRunTrace
        {
            PhaseId = "requirements",
            ArtifactType = "requirements",
            StateTransitions = new[]
            {
                new StateTransition { From = null, To = "Pending", At = DateTime.UtcNow },
                new StateTransition { From = "Pending", To = "Running", At = DateTime.UtcNow }
            },
            Attempts = new[]
            {
                new AttemptTrace
                {
                    AttemptNumber = 1,
                    Status = "Accepted",
                    ArtifactHash = "sha256:abc123",
                    ValidatorResults = Array.Empty<ValidatorResultTrace>(),
                    Tokens = new TokenCount { In = 100, Out = 200 },
                    LatencyMs = 1500,
                    Model = "claude-sonnet-4.5",
                    StartedAt = DateTime.UtcNow
                }
            },
            InputArtifactHashes = Array.Empty<string>(),
            OutputArtifactHashes = new[] { "sha256:abc123" }
        };

        var json = JsonSerializer.Serialize(phaseRun);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("requirements", root.GetProperty("phaseId").GetString());
        Assert.Equal(2, root.GetProperty("stateTransitions").GetArrayLength());
        Assert.Null(root.GetProperty("stateTransitions")[0].GetProperty("from").GetString());
        Assert.Equal("sha256:abc123", root.GetProperty("attempts")[0].GetProperty("artifactHash").GetString());
    }

    [Fact]
    public void AttemptTrace_NullArtifactHash_SerializedAsNull()
    {
        var attempt = new AttemptTrace
        {
            AttemptNumber = 1,
            Status = "Rejected",
            ArtifactHash = null,
            ValidatorResults = Array.Empty<ValidatorResultTrace>(),
            Tokens = new TokenCount(),
            Model = "claude-sonnet-4.5",
            StartedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(attempt);
        var doc = JsonDocument.Parse(json);

        // artifactHash should be present but null (never omitted per contract)
        Assert.True(doc.RootElement.TryGetProperty("artifactHash", out var prop));
        Assert.Equal(JsonValueKind.Null, prop.ValueKind);
    }

    [Fact]
    public void ValidatorResultTrace_Serialization_MatchesContract()
    {
        var result = new ValidatorResultTrace
        {
            Phase = "structural",
            Code = "SCHEMA_MISMATCH",
            Severity = "error",
            Blocking = true,
            Path = "$.payload.items[0]",
            Evidence = "missing required field 'id'",
            AttemptNumber = 1,
            BlockingReason = "Schema validation failed"
        };

        var json = JsonSerializer.Serialize(result);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("structural", root.GetProperty("phase").GetString());
        Assert.Equal("SCHEMA_MISMATCH", root.GetProperty("code").GetString());
        Assert.True(root.GetProperty("blocking").GetBoolean());
        Assert.Equal("$.payload.items[0]", root.GetProperty("path").GetString());
    }

    [Fact]
    public void ValidatorResultTrace_OptionalFields_OmittedWhenNull()
    {
        var result = new ValidatorResultTrace
        {
            Phase = "semantic",
            Code = "FR_NOT_TESTABLE",
            Severity = "warning",
            Blocking = false,
            AttemptNumber = 1
        };

        var json = JsonSerializer.Serialize(result);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("path", out _));
        Assert.False(root.TryGetProperty("evidence", out _));
        Assert.False(root.TryGetProperty("advisoryReason", out _));
        Assert.False(root.TryGetProperty("blockingReason", out _));
    }

    [Fact]
    public void ArtifactEnvelope_Serialization()
    {
        var payload = JsonDocument.Parse("""{"task_summary":"test","user_outcomes":[]}""").RootElement;
        var envelope = new ArtifactEnvelope
        {
            ArtifactType = "requirements",
            SchemaVersion = "1",
            ProducedByPhase = "requirements",
            Payload = payload.Clone()
        };

        var json = JsonSerializer.Serialize(envelope);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("requirements", root.GetProperty("artifactType").GetString());
        Assert.Equal("1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("test", root.GetProperty("payload").GetProperty("task_summary").GetString());
    }

    [Fact]
    public void ArtifactMeta_NoHashKey()
    {
        var meta = new ArtifactMeta
        {
            DerivedFrom = new[] { "sha256:aaa" },
            InputHashes = new[] { "sha256:bbb" },
            ProducedAt = DateTime.UtcNow,
            AttemptNumber = 2
        };

        var json = JsonSerializer.Serialize(meta);
        var doc = JsonDocument.Parse(json);

        // Per contract: no hash key in meta — filename stem IS the hash
        Assert.False(doc.RootElement.TryGetProperty("hash", out _));
        Assert.False(doc.RootElement.TryGetProperty("artifactHash", out _));
    }

    [Fact]
    public void MethodologyDefinition_Deserialization()
    {
        var json = """
        {
            "id": "spike-default-v1",
            "max_attempts_default": 3,
            "phases": [
                {
                    "id": "requirements",
                    "goal": "Decompose task",
                    "inputs": [],
                    "output_schema": "requirements/v1",
                    "instructions": "Read carefully"
                }
            ]
        }
        """;

        var methodology = JsonSerializer.Deserialize<MethodologyDefinition>(json);

        Assert.NotNull(methodology);
        Assert.Equal("spike-default-v1", methodology.Id);
        Assert.Equal(3, methodology.MaxAttemptsDefault);
        Assert.Single(methodology.Phases);
        Assert.Equal("requirements", methodology.Phases[0].ArtifactType);
        Assert.Equal("1", methodology.Phases[0].SchemaVersion);
        Assert.Null(methodology.ModelDefaults);
        Assert.Null(methodology.Phases[0].Model);
        Assert.Null(methodology.Phases[0].JudgeModel);
    }

    [Fact]
    public void MethodologyDefinition_Deserialization_WithModelConfig()
    {
        var json = """
        {
            "id": "test-v2",
            "max_attempts_default": 2,
            "model_defaults": {
                "generation": "o3",
                "judge": "gpt-4o-mini"
            },
            "phases": [
                {
                    "id": "requirements",
                    "goal": "Decompose task",
                    "inputs": [],
                    "output_schema": "requirements/v1",
                    "instructions": "Read carefully",
                    "model": "phase-override",
                    "judge_model": "judge-override"
                }
            ]
        }
        """;

        var methodology = JsonSerializer.Deserialize<MethodologyDefinition>(json);

        Assert.NotNull(methodology);
        Assert.NotNull(methodology.ModelDefaults);
        Assert.Equal("o3", methodology.ModelDefaults.Generation);
        Assert.Equal("gpt-4o-mini", methodology.ModelDefaults.Judge);
        Assert.Equal("phase-override", methodology.Phases[0].Model);
        Assert.Equal("judge-override", methodology.Phases[0].JudgeModel);
    }

    [Fact]
    public void PhaseDefinition_ArtifactType_ExtractsFromOutputSchema()
    {
        var phase = new PhaseDefinition
        {
            Id = "implement",
            Goal = "test",
            Inputs = Array.Empty<string>(),
            OutputSchema = "implementation/v1",
            Instructions = "test"
        };

        Assert.Equal("implementation", phase.ArtifactType);
        Assert.Equal("1", phase.SchemaVersion);
    }
}
