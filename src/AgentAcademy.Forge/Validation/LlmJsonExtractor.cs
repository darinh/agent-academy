namespace AgentAcademy.Forge.Validation;

/// <summary>
/// Strips markdown code fences from LLM responses before JSON parsing.
/// LLMs frequently wrap JSON in ```json … ``` fences despite explicit
/// instructions not to, so the parser must be tolerant.
/// </summary>
public static class LlmJsonExtractor
{
    /// <summary>
    /// Returns the content with surrounding markdown code fences removed.
    /// If no fence is detected, returns the trimmed input unchanged.
    /// Handles ```json, ``` <lang>, and bare ``` fences. Tolerates
    /// optional language tag, leading/trailing whitespace, and CRLF.
    /// </summary>
    public static string Sanitize(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content ?? string.Empty;

        var s = content.Trim();

        if (!s.StartsWith("```"))
            return s;

        // Skip the opening fence (3 backticks) plus an optional language tag
        // up to the first newline. Tolerate \r, \n, or \r\n line endings.
        var i = 3;
        while (i < s.Length && s[i] != '\r' && s[i] != '\n')
            i++;
        if (i < s.Length && s[i] == '\r') i++;
        if (i < s.Length && s[i] == '\n') i++;

        var inner = s[i..].TrimEnd();
        if (inner.EndsWith("```"))
            inner = inner[..^3].TrimEnd();

        return inner;
    }
}
