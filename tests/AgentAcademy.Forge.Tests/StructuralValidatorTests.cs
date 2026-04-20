using System.Text.Json;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Schemas;
using AgentAcademy.Forge.Validation;

namespace AgentAcademy.Forge.Tests;

public sealed class StructuralValidatorTests
{
    private readonly StructuralValidator _validator = new(new SchemaRegistry());

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

    // --- requirements/v1 ---

    [Fact]
    public void Requirements_ValidPayload_PassesValidation()
    {
        var envelope = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "Build an MCP server",
          "user_outcomes": [
            {"id": "U1", "outcome": "Server starts", "priority": "must"}
          ],
          "functional_requirements": [
            {"id": "FR1", "statement": "Server starts on port 3000", "outcome_ids": ["U1"]}
          ],
          "non_functional_requirements": [],
          "out_of_scope": ["Authentication"],
          "open_questions": []
        }
        """);

        var results = _validator.Validate(envelope, 1);
        Assert.Empty(results);
    }

    [Fact]
    public void Requirements_MissingRequiredField_Fails()
    {
        var envelope = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "test"
        }
        """);

        var results = _validator.Validate(envelope, 1);

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Blocking && r.Code == "MISSING_REQUIRED_FIELD");
    }

    [Fact]
    public void Requirements_DuplicateOutcomeIds_Fails()
    {
        var envelope = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "test",
          "user_outcomes": [
            {"id": "U1", "outcome": "A", "priority": "must"},
            {"id": "U1", "outcome": "B", "priority": "should"}
          ],
          "functional_requirements": [
            {"id": "FR1", "statement": "X", "outcome_ids": ["U1"]}
          ],
          "non_functional_requirements": [],
          "out_of_scope": ["nothing"],
          "open_questions": []
        }
        """);

        var results = _validator.Validate(envelope, 1);

        Assert.Contains(results, r => r.Code == "DUPLICATE_ID" && r.Evidence!.Contains("U1"));
    }

    [Fact]
    public void Requirements_DanglingOutcomeRef_Fails()
    {
        var envelope = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "test",
          "user_outcomes": [
            {"id": "U1", "outcome": "A", "priority": "must"}
          ],
          "functional_requirements": [
            {"id": "FR1", "statement": "X", "outcome_ids": ["U99"]}
          ],
          "non_functional_requirements": [],
          "out_of_scope": ["nothing"],
          "open_questions": []
        }
        """);

        var results = _validator.Validate(envelope, 1);

        Assert.Contains(results, r => r.Code == "DANGLING_REFERENCE" && r.Evidence!.Contains("U99"));
    }

    [Fact]
    public void Requirements_EmptyOutOfScope_FailsMinItems()
    {
        var envelope = MakeEnvelope("requirements", "1", """
        {
          "task_summary": "test",
          "user_outcomes": [{"id": "U1", "outcome": "A", "priority": "must"}],
          "functional_requirements": [{"id": "FR1", "statement": "X", "outcome_ids": ["U1"]}],
          "non_functional_requirements": [],
          "out_of_scope": [],
          "open_questions": []
        }
        """);

        var results = _validator.Validate(envelope, 1);

        Assert.Contains(results, r => r.Blocking && r.Code == "ARRAY_TOO_SHORT");
    }

    // --- contract/v1 ---

    [Fact]
    public void Contract_ValidPayload_Passes()
    {
        var envelope = MakeEnvelope("contract", "1", """
        {
          "interfaces": [
            {
              "name": "search",
              "kind": "function",
              "signature": "fn search(q: string) -> Result[]",
              "description": "Search for code",
              "preconditions": [],
              "postconditions": ["returns array"],
              "errors": [],
              "satisfies_fr_ids": ["FR1"]
            }
          ],
          "data_shapes": [],
          "invariants": [],
          "examples": [
            {"scenario": "basic search", "input": {"q": "test"}, "output": [], "fr_id": "FR1"}
          ]
        }
        """);

        var results = _validator.Validate(envelope, 1);
        Assert.Empty(results);
    }

    [Fact]
    public void Contract_DuplicateInterfaceNames_Fails()
    {
        var envelope = MakeEnvelope("contract", "1", """
        {
          "interfaces": [
            {"name": "search", "kind": "function", "signature": "fn search()", "description": "a", "preconditions": [], "postconditions": [], "errors": [], "satisfies_fr_ids": ["FR1"]},
            {"name": "search", "kind": "function", "signature": "fn search()", "description": "b", "preconditions": [], "postconditions": [], "errors": [], "satisfies_fr_ids": ["FR2"]}
          ],
          "data_shapes": [],
          "invariants": [],
          "examples": []
        }
        """);

        var results = _validator.Validate(envelope, 1);
        Assert.Contains(results, r => r.Code == "DUPLICATE_VALUE");
    }

    // --- function_design/v1 ---

    [Fact]
    public void FunctionDesign_ValidPayload_Passes()
    {
        var envelope = MakeEnvelope("function_design", "1", """
        {
          "components": [
            {"id": "C1", "name": "SearchEngine", "responsibility": "Search files", "depends_on": [], "implements": ["search"]},
            {"id": "C2", "name": "FileReader", "responsibility": "Read files", "depends_on": ["C1"], "implements": ["read"]}
          ],
          "data_flow": [
            {"from": "C1", "to": "C2", "carries": "file path", "trigger": "search hit"}
          ],
          "error_handling": [
            {"component_id": "C1", "failure": "no matches", "response": "return empty"}
          ],
          "deferred_decisions": []
        }
        """);

        var results = _validator.Validate(envelope, 1);
        Assert.Empty(results);
    }

    [Fact]
    public void FunctionDesign_DanglingDependsOn_Fails()
    {
        var envelope = MakeEnvelope("function_design", "1", """
        {
          "components": [
            {"id": "C1", "name": "A", "responsibility": "R", "depends_on": ["C99"], "implements": []}
          ],
          "data_flow": [],
          "error_handling": [],
          "deferred_decisions": []
        }
        """);

        var results = _validator.Validate(envelope, 1);
        Assert.Contains(results, r => r.Code == "DANGLING_REFERENCE" && r.Evidence!.Contains("C99"));
    }

    [Fact]
    public void FunctionDesign_DependencyCycle_Fails()
    {
        var envelope = MakeEnvelope("function_design", "1", """
        {
          "components": [
            {"id": "C1", "name": "A", "responsibility": "R", "depends_on": ["C2"], "implements": []},
            {"id": "C2", "name": "B", "responsibility": "R", "depends_on": ["C1"], "implements": []}
          ],
          "data_flow": [],
          "error_handling": [],
          "deferred_decisions": []
        }
        """);

        var results = _validator.Validate(envelope, 1);
        Assert.Contains(results, r => r.Code == "DEPENDENCY_CYCLE");
    }

    [Fact]
    public void FunctionDesign_DataFlowDanglingRef_Fails()
    {
        var envelope = MakeEnvelope("function_design", "1", """
        {
          "components": [
            {"id": "C1", "name": "A", "responsibility": "R", "depends_on": [], "implements": []}
          ],
          "data_flow": [
            {"from": "C1", "to": "C99", "carries": "data", "trigger": "event"}
          ],
          "error_handling": [],
          "deferred_decisions": []
        }
        """);

        var results = _validator.Validate(envelope, 1);
        Assert.Contains(results, r => r.Code == "DANGLING_REFERENCE" && r.Evidence!.Contains("C99"));
    }

    // --- implementation/v1 ---

    [Fact]
    public void Implementation_ValidPayload_Passes()
    {
        var envelope = MakeEnvelope("implementation", "1", """
        {
          "files": [
            {"path": "src/main.ts", "language": "typescript", "content": "console.log('hello');", "implements_component_ids": ["C1"]}
          ],
          "build_command": "npm run build",
          "test_command": null,
          "notes": "Basic implementation"
        }
        """);

        var results = _validator.Validate(envelope, 1);
        Assert.Empty(results);
    }

    [Fact]
    public void Implementation_AbsolutePath_Fails()
    {
        var envelope = MakeEnvelope("implementation", "1", """
        {
          "files": [
            {"path": "/etc/passwd", "language": "other", "content": "x", "implements_component_ids": []}
          ],
          "build_command": "make",
          "test_command": null,
          "notes": ""
        }
        """);

        var results = _validator.Validate(envelope, 1);
        Assert.Contains(results, r => r.Code == "ABSOLUTE_PATH");
    }

    [Fact]
    public void Implementation_PathTraversal_Fails()
    {
        var envelope = MakeEnvelope("implementation", "1", """
        {
          "files": [
            {"path": "src/../../../etc/passwd", "language": "other", "content": "x", "implements_component_ids": []}
          ],
          "build_command": "make",
          "test_command": null,
          "notes": ""
        }
        """);

        var results = _validator.Validate(envelope, 1);
        Assert.Contains(results, r => r.Code == "PATH_TRAVERSAL");
    }

    [Fact]
    public void Implementation_DoubleDotInFilename_NotFalsePositive()
    {
        var envelope = MakeEnvelope("implementation", "1", """
        {
          "files": [
            {"path": "src/config..backup.json", "language": "json", "content": "{}", "implements_component_ids": ["C1"]}
          ],
          "build_command": "make",
          "test_command": null,
          "notes": ""
        }
        """);

        var results = _validator.Validate(envelope, 1);
        Assert.DoesNotContain(results, r => r.Code == "PATH_TRAVERSAL");
    }

    // --- review/v1 ---

    [Fact]
    public void Review_ValidPayload_Passes()
    {
        var envelope = MakeEnvelope("review", "1", """
        {
          "verdict": "pass",
          "summary": "All checks passed",
          "checks": [
            {"id": "CHK1", "kind": "fr_satisfied", "target_id": "FR1", "result": "pass", "evidence": "src/main.ts:1-5"}
          ],
          "defects": [],
          "improvements_for_next_iteration": []
        }
        """);

        var results = _validator.Validate(envelope, 1);
        Assert.Empty(results);
    }

    [Fact]
    public void Review_VerdictPassWithCriticalDefect_Fails()
    {
        var envelope = MakeEnvelope("review", "1", """
        {
          "verdict": "pass",
          "summary": "Looks good",
          "checks": [],
          "defects": [
            {"id": "D1", "severity": "critical", "description": "Missing auth", "location": "src/api.ts"}
          ],
          "improvements_for_next_iteration": []
        }
        """);

        var results = _validator.Validate(envelope, 1);
        Assert.Contains(results, r => r.Code == "VERDICT_DEFECT_MISMATCH");
    }

    [Fact]
    public void Review_VerdictFailWithCriticalDefect_Passes()
    {
        var envelope = MakeEnvelope("review", "1", """
        {
          "verdict": "fail",
          "summary": "Critical issues",
          "checks": [],
          "defects": [
            {"id": "D1", "severity": "critical", "description": "Missing auth", "location": "src/api.ts"}
          ],
          "improvements_for_next_iteration": ["Add auth"]
        }
        """);

        var results = _validator.Validate(envelope, 1);
        Assert.DoesNotContain(results, r => r.Code == "VERDICT_DEFECT_MISMATCH");
    }

    // --- General tests ---

    [Fact]
    public void Validate_AllResultsHaveCorrectPhase()
    {
        // Invalid payload to trigger errors
        var envelope = MakeEnvelope("requirements", "1", """{"task_summary": "x"}""");
        var results = _validator.Validate(envelope, 2);

        Assert.All(results, r => Assert.Equal("structural", r.Phase));
        Assert.All(results, r => Assert.Equal(2, r.AttemptNumber));
    }

    [Fact]
    public void Validate_SchemaFailure_StopsBeforeRefChecks()
    {
        // Missing required fields → schema fails, so ref checks shouldn't run
        var envelope = MakeEnvelope("requirements", "1", """{}""");
        var results = _validator.Validate(envelope, 1);

        // Should have schema errors but no DANGLING_REFERENCE errors
        Assert.NotEmpty(results);
        Assert.DoesNotContain(results, r => r.Code == "DANGLING_REFERENCE");
    }
}
