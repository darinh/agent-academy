using AgentAcademy.Server.Services;

namespace AgentAcademy.Server.Tests;

public class ConventionalCommitMessageTests
{
    // ── Valid messages ───────────────────────────────────────────

    [Theory]
    [InlineData("feat: add login page")]
    [InlineData("fix: resolve null reference in parser")]
    [InlineData("docs: update README")]
    [InlineData("refactor: extract helper method")]
    [InlineData("test: add unit tests for auth")]
    [InlineData("ci: add GitHub Actions workflow")]
    [InlineData("style: fix formatting")]
    [InlineData("perf: optimize database query")]
    [InlineData("build: upgrade to .NET 9")]
    [InlineData("chore: update dependencies")]
    public void TryValidate_ValidType_ReturnsTrue(string message)
    {
        var result = ConventionalCommitMessage.TryValidate(message, out var error);

        Assert.True(result);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("feat(auth): add OAuth support")]
    [InlineData("fix(ui): correct button alignment")]
    [InlineData("refactor(services): split GitService")]
    [InlineData("test(api): add controller tests")]
    public void TryValidate_WithScope_ReturnsTrue(string message)
    {
        var result = ConventionalCommitMessage.TryValidate(message, out var error);

        Assert.True(result);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("feat!: remove deprecated API")]
    [InlineData("fix(auth)!: change token format")]
    public void TryValidate_BreakingChange_ReturnsTrue(string message)
    {
        var result = ConventionalCommitMessage.TryValidate(message, out var error);

        Assert.True(result);
        Assert.Null(error);
    }

    [Fact]
    public void TryValidate_MultilineMessage_ValidatesFirstLineOnly()
    {
        var message = "feat: add feature\n\nThis is the body\nwith multiple lines";

        var result = ConventionalCommitMessage.TryValidate(message, out var error);

        Assert.True(result);
        Assert.Null(error);
    }

    [Fact]
    public void TryValidate_WindowsLineEndings_ValidatesFirstLineOnly()
    {
        var message = "fix: resolve bug\r\n\r\nBody with CRLF";

        var result = ConventionalCommitMessage.TryValidate(message, out var error);

        Assert.True(result);
        Assert.Null(error);
    }

    // ── Invalid messages ────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public void TryValidate_EmptyOrWhitespace_ReturnsFalse(string? message)
    {
        var result = ConventionalCommitMessage.TryValidate(message, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Contains("Conventional Commits", error);
    }

    [Theory]
    [InlineData("Update README")]
    [InlineData("Fixed the bug")]
    [InlineData("WIP")]
    [InlineData("initial commit")]
    public void TryValidate_NoTypePrefix_ReturnsFalse(string message)
    {
        var result = ConventionalCommitMessage.TryValidate(message, out var error);

        Assert.False(result);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("feature: add login")]
    [InlineData("bugfix: resolve issue")]
    [InlineData("doc: update README")]
    [InlineData("tests: add coverage")]
    [InlineData("FEAT: uppercase type")]
    [InlineData("Fix: capitalized type")]
    public void TryValidate_InvalidType_ReturnsFalse(string message)
    {
        var result = ConventionalCommitMessage.TryValidate(message, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Contains("Allowed types", error);
    }

    [Theory]
    [InlineData("feat:missing space")]
    [InlineData("fix:no space after colon")]
    public void TryValidate_MissingSpaceAfterColon_ReturnsFalse(string message)
    {
        var result = ConventionalCommitMessage.TryValidate(message, out var error);

        Assert.False(result);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("feat: ")]
    [InlineData("fix: ")]
    public void TryValidate_EmptyDescription_ReturnsFalse(string message)
    {
        // The regex requires ".+" after the colon+space, so "feat: " with trailing whitespace
        // results in "feat:" after trim — which has no description
        var result = ConventionalCommitMessage.TryValidate(message, out var error);

        Assert.False(result);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryValidate_ErrorMessage_ListsAllowedTypes()
    {
        ConventionalCommitMessage.TryValidate("bad message", out var error);

        Assert.NotNull(error);
        Assert.Contains("feat", error);
        Assert.Contains("fix", error);
        Assert.Contains("docs", error);
        Assert.Contains("refactor", error);
        Assert.Contains("test", error);
    }
}
