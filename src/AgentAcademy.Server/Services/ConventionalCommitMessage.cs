using System.Text.RegularExpressions;

namespace AgentAcademy.Server.Services;

internal static class ConventionalCommitMessage
{
    private const string AllowedTypes =
        "feat, fix, docs, refactor, test, ci, style, perf, build, chore";

    private static readonly Regex SubjectPattern = new(
        @"^(feat|fix|docs|refactor|test|ci|style|perf|build|chore)(\([^)]+\))?\!?: .+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryValidate(string? message, out string? error)
    {
        var subject = GetSubject(message);
        if (!SubjectPattern.IsMatch(subject))
        {
            error =
                "Commit message must follow Conventional Commits on the first line: " +
                "<type>[optional scope][!]: <description>. " +
                $"Allowed types: {AllowedTypes}.";
            return false;
        }

        error = null;
        return true;
    }

    private static string GetSubject(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return string.Empty;

        var firstLine = message.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')[0];
        return firstLine.Trim();
    }
}
