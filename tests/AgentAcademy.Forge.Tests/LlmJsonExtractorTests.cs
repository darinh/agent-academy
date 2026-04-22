using AgentAcademy.Forge.Validation;

namespace AgentAcademy.Forge.Tests;

public sealed class LlmJsonExtractorTests
{
    [Fact]
    public void Sanitize_PlainJson_ReturnsTrimmed()
    {
        Assert.Equal("{\"a\":1}", LlmJsonExtractor.Sanitize("  {\"a\":1}  "));
    }

    [Fact]
    public void Sanitize_BackslashFenceWithLanguage_StripsFence()
    {
        var input = "```json\n{\"findings\":[]}\n```";
        Assert.Equal("{\"findings\":[]}", LlmJsonExtractor.Sanitize(input));
    }

    [Fact]
    public void Sanitize_BareFence_StripsFence()
    {
        var input = "```\n{\"a\":1}\n```";
        Assert.Equal("{\"a\":1}", LlmJsonExtractor.Sanitize(input));
    }

    [Fact]
    public void Sanitize_FenceWithCrlf_StripsCleanly()
    {
        var input = "```json\r\n{\"a\":1}\r\n```";
        Assert.Equal("{\"a\":1}", LlmJsonExtractor.Sanitize(input));
    }

    [Fact]
    public void Sanitize_LeadingWhitespaceBeforeFence_StripsFence()
    {
        var input = "   ```json\n{\"a\":1}\n```   ";
        Assert.Equal("{\"a\":1}", LlmJsonExtractor.Sanitize(input));
    }

    [Fact]
    public void Sanitize_NoClosingFence_ReturnsInner()
    {
        // Some LLMs forget the closing fence
        var input = "```json\n{\"a\":1}";
        Assert.Equal("{\"a\":1}", LlmJsonExtractor.Sanitize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_NullOrWhitespace_ReturnsInputOrEmpty(string? input)
    {
        var result = LlmJsonExtractor.Sanitize(input);
        Assert.True(string.IsNullOrEmpty(result) || string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public void Sanitize_NoFence_ReturnsTrimmedUnchanged()
    {
        var input = "  not even json  ";
        Assert.Equal("not even json", LlmJsonExtractor.Sanitize(input));
    }
}
