using AgentAcademy.Server.Services;

namespace AgentAcademy.Server.Tests;

public sealed class ModelContextLimitsTests
{
    // ── Known model matches ──────────────────────────────────────────

    [Theory]
    [InlineData("claude-3-5-sonnet-20241022", 200_000)]
    [InlineData("claude-3.5-sonnet", 200_000)]
    [InlineData("claude-3-opus-20240229", 200_000)]
    [InlineData("claude-sonnet-4-20260101", 200_000)]
    [InlineData("claude-opus-4-latest", 200_000)]
    [InlineData("claude-haiku-3.5", 200_000)]
    [InlineData("gpt-4o-mini", 128_000)]
    [InlineData("gpt-4o-2024-05-13", 128_000)]
    [InlineData("gpt-4-turbo-preview", 128_000)]
    [InlineData("gpt-4.1-nano", 1_000_000)]
    [InlineData("gpt-5-turbo", 1_000_000)]
    [InlineData("o1-preview", 200_000)]
    [InlineData("o3-mini", 200_000)]
    [InlineData("o4-mini-2026", 200_000)]
    [InlineData("gemini-2-flash", 1_000_000)]
    [InlineData("gemini-3-pro", 1_000_000)]
    public void GetLimit_KnownModel_ReturnsExpectedLimit(string model, long expected)
    {
        Assert.Equal(expected, ModelContextLimits.GetLimit(model));
    }

    // ── Case insensitivity ───────────────────────────────────────────

    [Theory]
    [InlineData("Claude-Sonnet-4-Latest", 200_000)]
    [InlineData("CLAUDE-SONNET-4", 200_000)]
    [InlineData("GPT-4O", 128_000)]
    [InlineData("GEMINI-2-FLASH", 1_000_000)]
    public void GetLimit_CaseInsensitive(string model, long expected)
    {
        Assert.Equal(expected, ModelContextLimits.GetLimit(model));
    }

    // ── Unknown model fallback ───────────────────────────────────────

    [Theory]
    [InlineData("llama-3.1-70b")]
    [InlineData("mixtral-8x7b")]
    [InlineData("totally-unknown-model")]
    public void GetLimit_UnknownModel_ReturnsDefault128k(string model)
    {
        Assert.Equal(128_000, ModelContextLimits.GetLimit(model));
    }

    // ── Null / empty handling ────────────────────────────────────────

    [Fact]
    public void GetLimit_Null_ReturnsDefault()
    {
        Assert.Equal(128_000, ModelContextLimits.GetLimit(null));
    }

    [Fact]
    public void GetLimit_Empty_ReturnsDefault()
    {
        Assert.Equal(128_000, ModelContextLimits.GetLimit(""));
    }

    [Fact]
    public void GetLimit_Whitespace_ReturnsDefault()
    {
        Assert.Equal(128_000, ModelContextLimits.GetLimit("   "));
    }

    // ── Substring matching ───────────────────────────────────────────

    [Fact]
    public void GetLimit_ModelWithProviderPrefix_StillMatches()
    {
        // Some systems prefix model names with provider
        Assert.Equal(200_000, ModelContextLimits.GetLimit("anthropic/claude-3-5-sonnet-latest"));
    }

    [Fact]
    public void GetLimit_ModelWithVersionSuffix_StillMatches()
    {
        Assert.Equal(128_000, ModelContextLimits.GetLimit("gpt-4o:2024-11-20"));
    }
}
