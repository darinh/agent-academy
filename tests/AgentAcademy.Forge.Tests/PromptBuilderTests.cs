using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Prompt;
using AgentAcademy.Forge.Schemas;

namespace AgentAcademy.Forge.Tests;

public sealed class PromptBuilderTests
{
    private readonly PromptBuilder _builder = new(new SchemaRegistry());

    private static TaskBrief TestTask => new()
    {
        TaskId = "T1",
        Title = "Build MCP Server",
        Description = "Build a small MCP server with 2 tools"
    };

    private static PhaseDefinition RequirementsPhase => new()
    {
        Id = "requirements",
        Goal = "Decompose the task brief into testable requirements.",
        Inputs = [],
        OutputSchema = "requirements/v1",
        Instructions = "Read the task brief carefully."
    };

    private static PhaseDefinition ContractPhase => new()
    {
        Id = "contract",
        Goal = "Define the external interface.",
        Inputs = ["requirements"],
        OutputSchema = "contract/v1",
        Instructions = "Treat the requirements as ground truth."
    };

    [Fact]
    public void SystemMessage_MatchesFrozenSpec()
    {
        Assert.Contains("You are a phase executor in a software engineering pipeline.", PromptBuilder.SystemMessage);
        Assert.Contains("You produce a single JSON object matching the schema", PromptBuilder.SystemMessage);
        Assert.Contains("You do not produce prose, markdown, code fences, or commentary.", PromptBuilder.SystemMessage);
    }

    [Fact]
    public void BuildUserMessage_ContainsAllSections()
    {
        var msg = _builder.BuildUserMessage(TestTask, RequirementsPhase, []);

        Assert.Contains("=== TASK ===", msg);
        Assert.Contains("=== PHASE ===", msg);
        Assert.Contains("=== OUTPUT CONTRACT ===", msg);
        Assert.Contains("=== INPUTS ===", msg);
        Assert.Contains("=== INSTRUCTIONS ===", msg);
        Assert.Contains("=== AMENDMENT NOTES ===", msg);
        Assert.Contains("=== RESPONSE FORMAT ===", msg);
    }

    [Fact]
    public void BuildUserMessage_TaskSection_ContainsDescription()
    {
        var msg = _builder.BuildUserMessage(TestTask, RequirementsPhase, []);
        Assert.Contains("Build a small MCP server with 2 tools", msg);
    }

    [Fact]
    public void BuildUserMessage_PhaseSection_ContainsIdAndGoal()
    {
        var msg = _builder.BuildUserMessage(TestTask, RequirementsPhase, []);

        Assert.Contains("phase_id: requirements", msg);
        Assert.Contains("Decompose the task brief into testable requirements.", msg);
    }

    [Fact]
    public void BuildUserMessage_OutputContract_ContainsSchemaBody()
    {
        var msg = _builder.BuildUserMessage(TestTask, RequirementsPhase, []);

        Assert.Contains("requirements/v1", msg);
        Assert.Contains("task_summary", msg); // From the schema body
    }

    [Fact]
    public void BuildUserMessage_Inputs_RendersVerbatimArtifacts()
    {
        var inputs = new List<ResolvedInput>
        {
            new()
            {
                PhaseId = "requirements",
                SchemaId = "requirements/v1",
                BodyJson = """{"task_summary":"test","user_outcomes":[]}"""
            }
        };

        var msg = _builder.BuildUserMessage(TestTask, ContractPhase, inputs);

        Assert.Contains("--- input[0]: requirements/v1 (from phase `requirements`) ---", msg);
        Assert.Contains("""{"task_summary":"test","user_outcomes":[]}""", msg);
    }

    [Fact]
    public void BuildUserMessage_NoInputs_EmptyInputsSection()
    {
        var msg = _builder.BuildUserMessage(TestTask, RequirementsPhase, []);

        Assert.Contains("=== INPUTS ===", msg);
        // Should still contain the section header but no input blocks
        Assert.DoesNotContain("--- input[", msg);
    }

    [Fact]
    public void BuildUserMessage_FirstAttempt_ShowsNoneAmendmentNotes()
    {
        var msg = _builder.BuildUserMessage(TestTask, RequirementsPhase, []);

        Assert.Contains("(none — this is the first attempt)", msg);
        Assert.DoesNotContain("Your previous attempt was rejected", msg);
    }

    [Fact]
    public void BuildUserMessage_WithAmendmentNotes_ShowsFailures()
    {
        var notes = new List<AmendmentNote>
        {
            new() { Validator = "structural", Message = "[MISSING_REQUIRED_FIELD] at /user_outcomes: required field missing" },
            new() { Validator = "semantic", Message = "[VAGUE_REQUIREMENT] FR2 uses vague term 'fast'" }
        };

        var msg = _builder.BuildUserMessage(TestTask, RequirementsPhase, [], notes);

        Assert.Contains("Your previous attempt was rejected by validators.", msg);
        Assert.Contains("- [structural] [MISSING_REQUIRED_FIELD]", msg);
        Assert.Contains("- [semantic] [VAGUE_REQUIREMENT]", msg);
        Assert.Contains("Produce a NEW response from scratch", msg);
    }

    [Fact]
    public void BuildUserMessage_ResponseFormat_ContainsSchemaId()
    {
        var msg = _builder.BuildUserMessage(TestTask, RequirementsPhase, []);

        Assert.Contains("{ \"body\": { ...matches schema requirements/v1... } }", msg);
    }

    [Fact]
    public void BuildUserMessage_Instructions_ContainsPhaseInstructions()
    {
        var msg = _builder.BuildUserMessage(TestTask, RequirementsPhase, []);
        Assert.Contains("Read the task brief carefully.", msg);
    }
}

public sealed class AmendmentNoteTests
{
    [Fact]
    public void FromValidatorResults_OnlyIncludesBlocking()
    {
        var results = new List<ValidatorResultTrace>
        {
            new()
            {
                Phase = "structural",
                Code = "MISSING_REQUIRED_FIELD",
                Severity = "error",
                Blocking = true,
                AttemptNumber = 1,
                BlockingReason = "Missing user_outcomes"
            },
            new()
            {
                Phase = "semantic",
                Code = "VAGUE_TERM",
                Severity = "warning",
                Blocking = false,
                AttemptNumber = 1,
                AdvisoryReason = "FR uses vague term"
            }
        };

        var notes = AmendmentNote.FromValidatorResults(results);

        Assert.Single(notes);
        Assert.Equal("structural", notes[0].Validator);
        Assert.Contains("MISSING_REQUIRED_FIELD", notes[0].Message);
    }

    [Fact]
    public void FromValidatorResults_UsesBlockingReasonWhenAvailable()
    {
        var results = new List<ValidatorResultTrace>
        {
            new()
            {
                Phase = "structural",
                Code = "SCHEMA_FAIL",
                Severity = "error",
                Blocking = true,
                AttemptNumber = 1,
                Path = "/field",
                BlockingReason = "Field is wrong type"
            }
        };

        var notes = AmendmentNote.FromValidatorResults(results);

        Assert.Contains("Field is wrong type", notes[0].Message);
    }

    [Fact]
    public void FromValidatorResults_FallsBackToEvidence()
    {
        var results = new List<ValidatorResultTrace>
        {
            new()
            {
                Phase = "structural",
                Code = "TYPE_MISMATCH",
                Severity = "error",
                Blocking = true,
                AttemptNumber = 1,
                Path = "/x",
                Evidence = "expected string, got number"
            }
        };

        var notes = AmendmentNote.FromValidatorResults(results);

        Assert.Contains("expected string, got number", notes[0].Message);
    }
}
