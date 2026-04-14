namespace AgentAcademy.Server.Services;

/// <summary>
/// Maps LLM model names to their known context window sizes (in tokens).
/// Uses substring matching to handle version suffixes and provider prefixes.
/// </summary>
public static class ModelContextLimits
{
    private const long DefaultLimit = 128_000;

    private static readonly (string Pattern, long Limit)[] KnownModels =
    [
        ("claude-3-5-sonnet", 200_000),
        ("claude-3.5-sonnet", 200_000),
        ("claude-3-opus", 200_000),
        ("claude-sonnet-4", 200_000),
        ("claude-opus-4", 200_000),
        ("claude-haiku", 200_000),
        ("gpt-4o", 128_000),
        ("gpt-4-turbo", 128_000),
        ("gpt-4.1", 1_000_000),
        ("gpt-5", 1_000_000),
        ("o1", 200_000),
        ("o3", 200_000),
        ("o4-mini", 200_000),
        ("gemini-2", 1_000_000),
        ("gemini-3", 1_000_000),
    ];

    /// <summary>
    /// Returns the context window limit for the given model name.
    /// Falls back to <see cref="DefaultLimit"/> for unknown models.
    /// </summary>
    public static long GetLimit(string? model)
    {
        if (string.IsNullOrEmpty(model))
            return DefaultLimit;

        var lower = model.ToLowerInvariant();
        foreach (var (pattern, limit) in KnownModels)
        {
            if (lower.Contains(pattern))
                return limit;
        }

        return DefaultLimit;
    }
}
