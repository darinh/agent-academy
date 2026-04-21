using AgentAcademy.Server.Services;

namespace AgentAcademy.Server.Tests;

public class PromptSanitizerTests
{
    // ── WrapBlock ───────────────────────────────────────────

    [Fact]
    public void WrapBlock_NullInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, PromptSanitizer.WrapBlock(null));
    }

    [Fact]
    public void WrapBlock_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, PromptSanitizer.WrapBlock(""));
    }

    [Fact]
    public void WrapBlock_NormalContent_WrapsWithMarkers()
    {
        var result = PromptSanitizer.WrapBlock("Hello world");

        Assert.StartsWith(PromptSanitizer.ContentMarkerOpen, result);
        Assert.EndsWith(PromptSanitizer.ContentMarkerClose, result);
        Assert.Contains("Hello world", result);
    }

    [Fact]
    public void WrapBlock_EscapesEmbeddedOpenMarker()
    {
        var input = $"before {PromptSanitizer.ContentMarkerOpen} after";

        var result = PromptSanitizer.WrapBlock(input);

        Assert.DoesNotContain(PromptSanitizer.ContentMarkerOpen + "\n", result.Replace(
            PromptSanitizer.ContentMarkerOpen + "\n",
            "",
            StringComparison.Ordinal));
        Assert.Contains("[ UNTRUSTED_CONTENT]", result);
    }

    [Fact]
    public void WrapBlock_EscapesEmbeddedCloseMarker()
    {
        var input = $"before {PromptSanitizer.ContentMarkerClose} after";

        var result = PromptSanitizer.WrapBlock(input);

        var inner = result.Replace(PromptSanitizer.ContentMarkerOpen + "\n", "")
                         .Replace("\n" + PromptSanitizer.ContentMarkerClose, "");
        Assert.DoesNotContain("[/UNTRUSTED_CONTENT]", inner);
        Assert.Contains("[ /UNTRUSTED_CONTENT]", inner);
    }

    [Fact]
    public void WrapBlock_EscapesBothMarkers()
    {
        var input = $"{PromptSanitizer.ContentMarkerOpen} inject {PromptSanitizer.ContentMarkerClose}";

        var result = PromptSanitizer.WrapBlock(input);

        var lines = result.Split('\n');
        // First line is the open marker, last is the close marker
        // The middle content should have escaped markers
        var innerContent = lines[1];
        Assert.Contains("[ UNTRUSTED_CONTENT]", innerContent);
        Assert.Contains("[ /UNTRUSTED_CONTENT]", innerContent);
    }

    [Fact]
    public void WrapBlock_PreservesWhitespaceAndNewlines()
    {
        var input = "line 1\n  line 2\n\tline 3";

        var result = PromptSanitizer.WrapBlock(input);

        Assert.Contains("line 1\n  line 2\n\tline 3", result);
    }

    // ── SanitizeMetadata ────────────────────────────────────

    [Fact]
    public void SanitizeMetadata_NullInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, PromptSanitizer.SanitizeMetadata(null));
    }

    [Fact]
    public void SanitizeMetadata_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, PromptSanitizer.SanitizeMetadata(""));
    }

    [Fact]
    public void SanitizeMetadata_NormalText_PassesThrough()
    {
        Assert.Equal("Hello World", PromptSanitizer.SanitizeMetadata("Hello World"));
    }

    [Fact]
    public void SanitizeMetadata_ReplacesNewlines()
    {
        Assert.Equal("line1 line2", PromptSanitizer.SanitizeMetadata("line1\nline2"));
    }

    [Fact]
    public void SanitizeMetadata_ReplacesCarriageReturns()
    {
        Assert.Equal("line1 line2", PromptSanitizer.SanitizeMetadata("line1\rline2"));
    }

    [Fact]
    public void SanitizeMetadata_ReplacesTabsAndControlChars()
    {
        Assert.Equal("a b", PromptSanitizer.SanitizeMetadata("a\tb"));
        Assert.Equal("x y", PromptSanitizer.SanitizeMetadata("x\x00y"));
    }

    [Fact]
    public void SanitizeMetadata_EscapesMarkerSequences()
    {
        var input = $"name {PromptSanitizer.ContentMarkerOpen} rest";
        var result = PromptSanitizer.SanitizeMetadata(input);
        Assert.Contains("[ UNTRUSTED_CONTENT]", result);
        Assert.DoesNotContain("[UNTRUSTED_CONTENT]", result);
    }

    // ── EscapeMarkers ───────────────────────────────────────

    [Fact]
    public void EscapeMarkers_EscapesOpenMarker()
    {
        var result = PromptSanitizer.EscapeMarkers("[UNTRUSTED_CONTENT]");
        Assert.Equal("[ UNTRUSTED_CONTENT]", result);
    }

    [Fact]
    public void EscapeMarkers_EscapesCloseMarker()
    {
        var result = PromptSanitizer.EscapeMarkers("[/UNTRUSTED_CONTENT]");
        Assert.Equal("[ /UNTRUSTED_CONTENT]", result);
    }

    [Fact]
    public void EscapeMarkers_EscapesMultipleOccurrences()
    {
        var input = "[UNTRUSTED_CONTENT] middle [UNTRUSTED_CONTENT]";
        var result = PromptSanitizer.EscapeMarkers(input);
        Assert.Equal("[ UNTRUSTED_CONTENT] middle [ UNTRUSTED_CONTENT]", result);
    }

    [Fact]
    public void EscapeMarkers_LeavesNonMarkerContentUnchanged()
    {
        const string input = "Just normal text with [brackets] and stuff";
        Assert.Equal(input, PromptSanitizer.EscapeMarkers(input));
    }
}
