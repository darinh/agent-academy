using AgentAcademy.Forge.Models;

namespace AgentAcademy.Forge.Prompt;

/// <summary>
/// Input artifact resolved for prompt rendering.
/// </summary>
public sealed record ResolvedInput
{
    /// <summary>Phase that produced this artifact (e.g. "requirements").</summary>
    public required string PhaseId { get; init; }

    /// <summary>Schema identifier (e.g. "requirements/v1").</summary>
    public required string SchemaId { get; init; }

    /// <summary>Artifact payload as a JSON string.</summary>
    public required string BodyJson { get; init; }
}

/// <summary>
/// Amendment note from a rejected attempt, formatted for the prompt template.
/// Maps from ValidatorResultTrace to the template's [validator] message format.
/// </summary>
public sealed record AmendmentNote
{
    /// <summary>Validator identifier (e.g. "structural", "semantic").</summary>
    public required string Validator { get; init; }

    /// <summary>Human-readable failure message.</summary>
    public required string Message { get; init; }

    /// <summary>
    /// Create amendment notes from validator results.
    /// Only includes blocking results (non-blocking are advisory).
    /// </summary>
    public static IReadOnlyList<AmendmentNote> FromValidatorResults(IEnumerable<ValidatorResultTrace> results)
    {
        return results
            .Where(r => r.Blocking)
            .Select(r => new AmendmentNote
            {
                Validator = r.Phase,
                Message = FormatMessage(r)
            })
            .ToList();
    }

    private static string FormatMessage(ValidatorResultTrace r)
    {
        var parts = new List<string> { $"[{r.Code}]" };

        if (r.Path is not null)
            parts.Add($"at {r.Path}");

        if (r.BlockingReason is not null)
            parts.Add(r.BlockingReason);
        else if (r.Evidence is not null)
            parts.Add(r.Evidence);
        else
            parts.Add($"{r.Severity}: {r.Code}");

        return string.Join(" ", parts);
    }
}
