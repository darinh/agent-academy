namespace AgentAcademy.Forge.Validation;

/// <summary>
/// Strips markdown code fences from LLM responses before JSON parsing.
/// LLMs frequently wrap JSON in ```json … ``` fences despite explicit
/// instructions not to, so the parser must be tolerant.
/// </summary>
public static class LlmJsonExtractor
{
    /// <summary>
    /// Returns the content with surrounding markdown code fences and leading
    /// prose preambles removed.
    /// If no fence is detected, returns the trimmed input unchanged.
    /// Handles ```json, ``` <lang>, and bare ``` fences. Tolerates
    /// optional language tag, leading/trailing whitespace, and CRLF.
    /// Also strips prose lines that precede a JSON object/array or code fence
    /// (e.g. "Here is the JSON:\n{...}").
    /// </summary>
    public static string Sanitize(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content ?? string.Empty;

        var s = content.Trim();

        // Strip leading prose preamble: lines before the first { [ or ``` fence.
        s = StripProsePreamble(s);

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

    /// <summary>
    /// Removes leading prose lines that precede the actual JSON or code fence.
    /// Scans forward for the first line starting with {, [, or ```.
    /// Only strips if such a line is found; otherwise returns the input unchanged.
    /// </summary>
    private static string StripProsePreamble(string s)
    {
        if (s.Length == 0)
            return s;

        var firstChar = s[0];
        if (firstChar is '{' or '[' or '`')
            return s;

        // Scan line by line for the JSON/fence start
        var pos = 0;
        while (pos < s.Length)
        {
            // Skip whitespace at start of line
            var lineStart = pos;
            while (lineStart < s.Length && s[lineStart] is ' ' or '\t')
                lineStart++;

            if (lineStart < s.Length && s[lineStart] is '{' or '[' or '`')
                return s[lineStart..];

            // Advance to next line
            pos = s.IndexOf('\n', pos);
            if (pos < 0) break;
            pos++;
        }

        return s;
    }
}
