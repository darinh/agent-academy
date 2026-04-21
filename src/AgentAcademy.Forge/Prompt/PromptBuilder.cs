using System.Text;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Schemas;

namespace AgentAcademy.Forge.Prompt;

/// <summary>
/// Renders the frozen §6.2 prompt envelope. Pure function — no IO, no async.
/// Template matches docs/forge-spike/prompt-envelope.md verbatim.
/// </summary>
public sealed class PromptBuilder
{
    private readonly SchemaRegistry _schemas;

    public PromptBuilder(SchemaRegistry schemas)
    {
        _schemas = schemas;
    }

    /// <summary>
    /// Frozen system message — constant across all phases and attempts.
    /// </summary>
    public const string SystemMessage =
        """
        You are a phase executor in a software engineering pipeline.
        You execute exactly one phase per session. You have no memory of prior sessions.
        You produce a single JSON object matching the schema declared in the user message.
        You do not produce prose, markdown, code fences, or commentary.
        If the inputs are insufficient, produce the JSON with `open_questions` populated where the schema permits, never refuse.
        You will be evaluated by automated validators. Failed validations will cause your output to be discarded; a fresh agent will be asked to retry with your validator failures as guidance.
        """;

    /// <summary>
    /// Build the user message for a phase attempt.
    /// </summary>
    /// <param name="task">Task brief (source of task_summary).</param>
    /// <param name="phase">Phase definition from methodology.</param>
    /// <param name="inputs">Resolved input artifacts, in methodology-declared order.</param>
    /// <param name="amendmentNotes">Amendment notes from the previous rejected attempt, or null/empty for first attempt.</param>
    public string BuildUserMessage(
        TaskBrief task,
        PhaseDefinition phase,
        IReadOnlyList<ResolvedInput> inputs,
        IReadOnlyList<AmendmentNote>? amendmentNotes = null)
    {
        var schemaEntry = _schemas.GetSchema(phase.OutputSchema);
        var sb = new StringBuilder();

        // === TASK ===
        sb.AppendLine("=== TASK ===");
        sb.AppendLine(task.Description);
        sb.AppendLine();

        // === PHASE ===
        sb.AppendLine("=== PHASE ===");
        sb.AppendLine($"phase_id: {phase.Id}");
        sb.AppendLine($"goal: {phase.Goal}");
        sb.AppendLine();

        // === OUTPUT CONTRACT ===
        sb.AppendLine("=== OUTPUT CONTRACT ===");
        sb.AppendLine($"You must produce a JSON object whose root field `body` matches schema `{phase.OutputSchema}`.");
        sb.AppendLine("The schema body is:");
        sb.AppendLine(schemaEntry.SchemaBodyJson);
        sb.AppendLine();
        sb.AppendLine("The schema's semantic rules (you will be judged on these):");
        sb.AppendLine(schemaEntry.SemanticRules);
        sb.AppendLine();

        // === INPUTS ===
        sb.AppendLine("=== INPUTS ===");
        sb.AppendLine("The following artifacts from prior phases are provided verbatim. Do not summarize them; treat them as ground truth.");
        sb.AppendLine();

        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            sb.AppendLine($"--- input[{i}]: {input.SchemaId} (from phase `{input.PhaseId}`) ---");
            sb.AppendLine(input.BodyJson);
            sb.AppendLine();
        }

        // === INSTRUCTIONS ===
        sb.AppendLine("=== INSTRUCTIONS ===");
        sb.AppendLine(phase.Instructions);
        sb.AppendLine();

        // === AMENDMENT NOTES ===
        sb.AppendLine("=== AMENDMENT NOTES ===");
        if (amendmentNotes is { Count: > 0 })
        {
            sb.AppendLine("Your previous attempt was rejected by validators. Their messages, in full:");
            foreach (var note in amendmentNotes)
            {
                sb.AppendLine($"- [{note.Validator}] {note.Message}");
            }
            sb.AppendLine();
            sb.AppendLine("You are NOT being shown your previous output. Produce a NEW response from scratch that satisfies the schema AND addresses every failure above.");
        }
        else
        {
            sb.AppendLine("(none — this is the first attempt)");
        }

        sb.AppendLine();

        // === RESPONSE FORMAT ===
        sb.AppendLine("=== RESPONSE FORMAT ===");
        sb.Append($"Respond with a single JSON object on one line or pretty-printed, no surrounding text:");
        sb.AppendLine();
        sb.Append($"{{ \"body\": {{ ...matches schema {phase.OutputSchema}... }} }}");

        return sb.ToString();
    }
}
