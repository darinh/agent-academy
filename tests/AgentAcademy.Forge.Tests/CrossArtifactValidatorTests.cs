using System.Text.Json;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Validation;

namespace AgentAcademy.Forge.Tests;

public sealed class CrossArtifactValidatorTests
{
    private readonly CrossArtifactValidator _validator = new();

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

    // --- Requirements (first phase, no cross-refs) ---

    [Fact]
    public void Requirements_NoCrossRefs_AlwaysPasses()
    {
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

        var results = _validator.Validate(envelope, new Dictionary<string, ArtifactEnvelope>(), 1);
        Assert.Empty(results);
    }

    // --- Contract → Requirements ---

    [Fact]
    public void Contract_ValidFrReferences_Passes()
    {
        var req = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "test",
          "user_outcomes": [{"id": "U1", "outcome": "test", "priority": "must"}],
          "functional_requirements": [
            {"id": "FR1", "statement": "test", "outcome_ids": ["U1"]},
            {"id": "FR2", "statement": "test2", "outcome_ids": ["U1"]}
          ],
          "non_functional_requirements": [],
          "out_of_scope": [],
          "open_questions": []
        }
        """);
        var contract = MakeEnvelope("contract", "1", """
        {
          "interfaces": [
            {"name": "startServer", "kind": "function", "signature": "() => void",
             "description": "starts", "preconditions": [], "postconditions": [],
             "errors": [], "satisfies_fr_ids": ["FR1", "FR2"]}
          ],
          "data_shapes": [],
          "invariants": [],
          "examples": [{"scenario": "test", "input": {}, "output": {}, "fr_id": "FR1"}]
        }
        """);

        var inputs = new Dictionary<string, ArtifactEnvelope> { ["requirements"] = req };
        var results = _validator.Validate(contract, inputs, 1);

        Assert.Empty(results);
    }

    [Fact]
    public void Contract_DanglingFrRef_ReportsError()
    {
        var req = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "test",
          "user_outcomes": [{"id": "U1", "outcome": "test", "priority": "must"}],
          "functional_requirements": [{"id": "FR1", "statement": "test", "outcome_ids": ["U1"]}],
          "non_functional_requirements": [],
          "out_of_scope": [],
          "open_questions": []
        }
        """);
        var contract = MakeEnvelope("contract", "1", """
        {
          "interfaces": [
            {"name": "startServer", "kind": "function", "signature": "() => void",
             "description": "starts", "preconditions": [], "postconditions": [],
             "errors": [], "satisfies_fr_ids": ["FR1", "FR_NONEXISTENT"]}
          ],
          "data_shapes": [],
          "invariants": [],
          "examples": []
        }
        """);

        var inputs = new Dictionary<string, ArtifactEnvelope> { ["requirements"] = req };
        var results = _validator.Validate(contract, inputs, 1);

        Assert.Single(results);
        Assert.Equal("cross-artifact", results[0].Phase);
        Assert.Equal("CROSS_ARTIFACT_DANGLING_REF", results[0].Code);
        Assert.True(results[0].Blocking);
        Assert.Contains("FR_NONEXISTENT", results[0].Evidence!);
    }

    [Fact]
    public void Contract_DanglingExampleFrId_ReportsError()
    {
        var req = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "test",
          "user_outcomes": [{"id": "U1", "outcome": "test", "priority": "must"}],
          "functional_requirements": [{"id": "FR1", "statement": "test", "outcome_ids": ["U1"]}],
          "non_functional_requirements": [],
          "out_of_scope": [],
          "open_questions": []
        }
        """);
        var contract = MakeEnvelope("contract", "1", """
        {
          "interfaces": [
            {"name": "startServer", "kind": "function", "signature": "() => void",
             "description": "starts", "preconditions": [], "postconditions": [],
             "errors": [], "satisfies_fr_ids": ["FR1"]}
          ],
          "data_shapes": [],
          "invariants": [],
          "examples": [{"scenario": "test", "input": {}, "output": {}, "fr_id": "FR_MISSING"}]
        }
        """);

        var inputs = new Dictionary<string, ArtifactEnvelope> { ["requirements"] = req };
        var results = _validator.Validate(contract, inputs, 1);

        Assert.Single(results);
        Assert.Contains("FR_MISSING", results[0].Evidence!);
    }

    [Fact]
    public void Contract_MissingRequirementsInput_ReportsError()
    {
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

        var results = _validator.Validate(contract, new Dictionary<string, ArtifactEnvelope>(), 1);

        Assert.Single(results);
        Assert.Equal("CROSS_ARTIFACT_INPUT_MISSING", results[0].Code);
    }

    // --- FunctionDesign → Contract ---

    [Fact]
    public void FunctionDesign_ValidImplementsRefs_Passes()
    {
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
        var fd = MakeEnvelope("function_design", "1", """
        {
          "components": [
            {"id": "C1", "name": "Server", "responsibility": "HTTP listener",
             "depends_on": [], "implements": ["startServer"]}
          ],
          "data_flow": [],
          "error_handling": [],
          "deferred_decisions": []
        }
        """);

        var inputs = new Dictionary<string, ArtifactEnvelope> { ["contract"] = contract };
        var results = _validator.Validate(fd, inputs, 1);

        Assert.Empty(results);
    }

    [Fact]
    public void FunctionDesign_DanglingImplementsRef_ReportsError()
    {
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
        var fd = MakeEnvelope("function_design", "1", """
        {
          "components": [
            {"id": "C1", "name": "Server", "responsibility": "HTTP listener",
             "depends_on": [], "implements": ["startServer", "nonExistentInterface"]}
          ],
          "data_flow": [],
          "error_handling": [],
          "deferred_decisions": []
        }
        """);

        var inputs = new Dictionary<string, ArtifactEnvelope> { ["contract"] = contract };
        var results = _validator.Validate(fd, inputs, 1);

        Assert.Single(results);
        Assert.Contains("nonExistentInterface", results[0].Evidence!);
    }

    // --- Implementation → FunctionDesign ---

    [Fact]
    public void Implementation_ValidComponentRefs_Passes()
    {
        var fd = MakeEnvelope("function_design", "1", """
        {
          "components": [
            {"id": "C1", "name": "Server", "responsibility": "test", "depends_on": [], "implements": []},
            {"id": "C2", "name": "Handler", "responsibility": "test", "depends_on": ["C1"], "implements": []}
          ],
          "data_flow": [],
          "error_handling": [],
          "deferred_decisions": []
        }
        """);
        var impl = MakeEnvelope("implementation", "1", """
        {
          "files": [
            {"path": "src/server.ts", "language": "typescript", "content": "export class Server {}",
             "implements_component_ids": ["C1", "C2"]}
          ],
          "build_command": "npm run build",
          "notes": "test"
        }
        """);

        var inputs = new Dictionary<string, ArtifactEnvelope> { ["function_design"] = fd };
        var results = _validator.Validate(impl, inputs, 1);

        Assert.Empty(results);
    }

    [Fact]
    public void Implementation_DanglingComponentRef_ReportsError()
    {
        var fd = MakeEnvelope("function_design", "1", """
        {
          "components": [
            {"id": "C1", "name": "Server", "responsibility": "test", "depends_on": [], "implements": []}
          ],
          "data_flow": [],
          "error_handling": [],
          "deferred_decisions": []
        }
        """);
        var impl = MakeEnvelope("implementation", "1", """
        {
          "files": [
            {"path": "src/server.ts", "language": "typescript", "content": "export class Server {}",
             "implements_component_ids": ["C1", "C99"]}
          ],
          "build_command": "npm run build",
          "notes": "test"
        }
        """);

        var inputs = new Dictionary<string, ArtifactEnvelope> { ["function_design"] = fd };
        var results = _validator.Validate(impl, inputs, 1);

        Assert.Single(results);
        Assert.Contains("C99", results[0].Evidence!);
    }

    // --- Review → Upstream ---

    [Fact]
    public void Review_ValidTargetIds_Passes()
    {
        var req = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "test",
          "user_outcomes": [{"id": "U1", "outcome": "test", "priority": "must"}],
          "functional_requirements": [{"id": "FR1", "statement": "test", "outcome_ids": ["U1"]}],
          "non_functional_requirements": [],
          "out_of_scope": [],
          "open_questions": []
        }
        """);
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
        var review = MakeEnvelope("review", "1", """
        {
          "verdict": "pass",
          "summary": "All good",
          "checks": [
            {"id": "CHK1", "kind": "fr_satisfied", "target_id": "FR1", "result": "pass", "evidence": "Verified"},
            {"id": "CHK2", "kind": "contract_satisfied", "target_id": "startServer", "result": "pass", "evidence": "OK"}
          ],
          "defects": [],
          "improvements_for_next_iteration": []
        }
        """);

        var inputs = new Dictionary<string, ArtifactEnvelope>
        {
            ["requirements"] = req,
            ["contract"] = contract
        };
        var results = _validator.Validate(review, inputs, 1);

        Assert.Empty(results);
    }

    [Fact]
    public void Review_DanglingFrTargetId_ReportsError()
    {
        var req = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "test",
          "user_outcomes": [{"id": "U1", "outcome": "test", "priority": "must"}],
          "functional_requirements": [{"id": "FR1", "statement": "test", "outcome_ids": ["U1"]}],
          "non_functional_requirements": [],
          "out_of_scope": [],
          "open_questions": []
        }
        """);
        var review = MakeEnvelope("review", "1", """
        {
          "verdict": "pass",
          "summary": "All good",
          "checks": [
            {"id": "CHK1", "kind": "fr_satisfied", "target_id": "FR999", "result": "pass", "evidence": "Verified"}
          ],
          "defects": [],
          "improvements_for_next_iteration": []
        }
        """);

        var inputs = new Dictionary<string, ArtifactEnvelope> { ["requirements"] = req };
        var results = _validator.Validate(review, inputs, 1);

        Assert.Single(results);
        Assert.Contains("FR999", results[0].Evidence!);
    }

    [Fact]
    public void Review_InvariantCheckKind_SkipsResolution()
    {
        // invariant_held and example_matches use string-based targets, not IDs
        var review = MakeEnvelope("review", "1", """
        {
          "verdict": "pass",
          "summary": "Checked invariants",
          "checks": [
            {"id": "CHK1", "kind": "invariant_held", "target_id": "Some invariant text", "result": "pass", "evidence": "Holds"}
          ],
          "defects": [],
          "improvements_for_next_iteration": []
        }
        """);

        var results = _validator.Validate(review, new Dictionary<string, ArtifactEnvelope>(), 1);

        Assert.Empty(results); // invariant_held skips cross-ref resolution
    }

    // --- Unknown artifact type ---

    [Fact]
    public void UnknownArtifactType_ReturnsEmpty()
    {
        var envelope = MakeEnvelope("something_unknown", "1", "{}");
        var results = _validator.Validate(envelope, new Dictionary<string, ArtifactEnvelope>(), 1);
        Assert.Empty(results);
    }
}
