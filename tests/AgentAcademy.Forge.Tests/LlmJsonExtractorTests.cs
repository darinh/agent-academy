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

    [Fact]
    public void Sanitize_ProsePreamble_BeforeJson_StripsLeadingProse()
    {
        var input = "Here is the JSON:\n{\"a\":1}";
        Assert.Equal("{\"a\":1}", LlmJsonExtractor.Sanitize(input));
    }

    [Fact]
    public void Sanitize_ProsePreamble_BeforeCodeFence_StripsLeadingProse()
    {
        var input = "Sure, here's the result:\n```json\n{\"a\":1}\n```";
        Assert.Equal("{\"a\":1}", LlmJsonExtractor.Sanitize(input));
    }

    [Fact]
    public void Sanitize_ProsePreamble_MultipleLines_StripsAll()
    {
        var input = "I've analyzed the requirements.\nHere is the output:\n{\"findings\":[]}";
        Assert.Equal("{\"findings\":[]}", LlmJsonExtractor.Sanitize(input));
    }

    [Fact]
    public void Sanitize_ProsePreamble_BeforeArray_StripsLeadingProse()
    {
        var input = "The result is:\n[1, 2, 3]";
        Assert.Equal("[1, 2, 3]", LlmJsonExtractor.Sanitize(input));
    }

    [Fact]
    public void Sanitize_ProsePreamble_IndentedJson_StripsProseAndLeadingWhitespace()
    {
        var input = "Here you go:\n  {\"a\":1}";
        Assert.Equal("{\"a\":1}", LlmJsonExtractor.Sanitize(input));
    }

    [Fact]
    public void Sanitize_NoPreamble_JsonOnly_Unchanged()
    {
        var input = "{\"already\":\"clean\"}";
        Assert.Equal("{\"already\":\"clean\"}", LlmJsonExtractor.Sanitize(input));
    }
}
