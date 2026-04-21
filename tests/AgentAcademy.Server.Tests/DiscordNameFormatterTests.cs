using AgentAcademy.Server.Notifications;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for DiscordNameFormatter — pure formatting utilities for Discord channel names,
/// category names, agent display names, and avatar URLs.
/// </summary>
public class DiscordNameFormatterTests
{
    // ── SanitizeChannelName ─────────────────────────────────────

    [Theory]
    [InlineData("general", "general")]
    [InlineData("General", "general")]
    [InlineData("My Room", "my-room")]
    [InlineData("my_room", "my-room")]
    [InlineData("Agent Academy", "agent-academy")]
    public void SanitizeChannelName_BasicCases(string input, string expected)
    {
        Assert.Equal(expected, DiscordNameFormatter.SanitizeChannelName(input));
    }

    [Theory]
    [InlineData("hello!@#world", "helloworld")]
    [InlineData("café-lounge", "caf-lounge")]
    [InlineData("test & verify", "test--verify")]  // & removed, spaces→hyphens, will collapse
    public void SanitizeChannelName_StripsDisallowedChars(string input, string _)
    {
        var result = DiscordNameFormatter.SanitizeChannelName(input);
        Assert.Matches(@"^[a-z0-9\-]+$", result);
    }

    [Fact]
    public void SanitizeChannelName_CollapsesMultipleHyphens()
    {
        var result = DiscordNameFormatter.SanitizeChannelName("hello---world");
        Assert.Equal("hello-world", result);
    }

    [Fact]
    public void SanitizeChannelName_TrimsLeadingAndTrailingHyphens()
    {
        var result = DiscordNameFormatter.SanitizeChannelName("-leading-trailing-");
        Assert.Equal("leading-trailing", result);
    }

    [Fact]
    public void SanitizeChannelName_EmptyInput_UsesFallbackId()
    {
        var result = DiscordNameFormatter.SanitizeChannelName("", fallbackId: "abc12345-long-id");
        Assert.Equal("agent-abc12345", result);
    }

    [Fact]
    public void SanitizeChannelName_EmptyInput_NoFallback_ReturnsUnknown()
    {
        var result = DiscordNameFormatter.SanitizeChannelName("");
        Assert.Equal("unknown", result);
    }

    [Fact]
    public void SanitizeChannelName_AllDisallowedChars_UsesFallbackId()
    {
        var result = DiscordNameFormatter.SanitizeChannelName("!@#$%^&*()", fallbackId: "short");
        Assert.Equal("agent-short", result);
    }

    [Fact]
    public void SanitizeChannelName_ShortFallbackId_UsesFullId()
    {
        var result = DiscordNameFormatter.SanitizeChannelName("!!!", fallbackId: "abc");
        Assert.Equal("agent-abc", result);
    }

    [Fact]
    public void SanitizeChannelName_TruncatesAt100Chars()
    {
        var longName = new string('a', 150);
        var result = DiscordNameFormatter.SanitizeChannelName(longName);
        Assert.Equal(100, result.Length);
    }

    [Fact]
    public void SanitizeChannelName_ExactlyAt100Chars_NoTruncation()
    {
        var name = new string('a', 100);
        var result = DiscordNameFormatter.SanitizeChannelName(name);
        Assert.Equal(100, result.Length);
    }

    [Theory]
    [InlineData("Hello World", "hello-world")]
    [InlineData("Hello_World", "hello-world")]
    [InlineData("Hello  World", "hello-world")]  // double space → double hyphen → collapsed
    public void SanitizeChannelName_SpacesAndUnderscoresBecomeSingleHyphens(string input, string expected)
    {
        Assert.Equal(expected, DiscordNameFormatter.SanitizeChannelName(input));
    }

    // ── SanitizeCategoryName ────────────────────────────────────

    [Theory]
    [InlineData("Agent Academy Rooms", "Agent Academy Rooms")]
    [InlineData("Rooms", "Rooms")]
    [InlineData("Mixed CASE with spaces", "Mixed CASE with spaces")]
    public void SanitizeCategoryName_PreservesValidNames(string input, string expected)
    {
        Assert.Equal(expected, DiscordNameFormatter.SanitizeCategoryName(input));
    }

    [Fact]
    public void SanitizeCategoryName_EmptyInput_ReturnsFallback()
    {
        Assert.Equal("General", DiscordNameFormatter.SanitizeCategoryName(""));
    }

    [Fact]
    public void SanitizeCategoryName_WhitespaceOnly_ReturnsFallback()
    {
        Assert.Equal("General", DiscordNameFormatter.SanitizeCategoryName("   "));
    }

    [Fact]
    public void SanitizeCategoryName_TruncatesAt100Chars()
    {
        var longName = new string('A', 110);
        var result = DiscordNameFormatter.SanitizeCategoryName(longName);
        Assert.Equal(100, result.Length);
    }

    [Fact]
    public void SanitizeCategoryName_ExactlyAt100Chars_NoTruncation()
    {
        var name = new string('B', 100);
        Assert.Equal(100, DiscordNameFormatter.SanitizeCategoryName(name).Length);
    }

    // ── FormatAgentDisplayName ──────────────────────────────────

    [Theory]
    [InlineData("mr-test-agent", "Mr Test Agent")]
    [InlineData("multi-word-name", "Multi Word Name")]
    [InlineData("hello-world", "Hello World")]
    public void FormatAgentDisplayName_KebabCase_TitleCases(string input, string expected)
    {
        Assert.Equal(expected, DiscordNameFormatter.FormatAgentDisplayName(input));
    }

    [Theory]
    [InlineData("Aristotle", "Aristotle")]
    [InlineData("aristotle", "aristotle")]      // no hyphens → passthrough even if lowercase
    [InlineData("socrates", "socrates")]
    [InlineData("Mr Test", "Mr Test")]
    [InlineData("Already Formatted", "Already Formatted")]
    public void FormatAgentDisplayName_NonKebabCase_ReturnsAsIs(string input, string expected)
    {
        Assert.Equal(expected, DiscordNameFormatter.FormatAgentDisplayName(input));
    }

    [Fact]
    public void FormatAgentDisplayName_EmptyInput_ReturnsFallback()
    {
        Assert.Equal("Agent Academy", DiscordNameFormatter.FormatAgentDisplayName(""));
    }

    [Fact]
    public void FormatAgentDisplayName_WhitespaceOnly_ReturnsFallback()
    {
        Assert.Equal("Agent Academy", DiscordNameFormatter.FormatAgentDisplayName("   "));
    }

    [Theory]
    [InlineData("has-UPPER-case")]  // mixed case kebab → not detected as all-lowercase kebab
    [InlineData("MixedCase-with-hyphen")]
    public void FormatAgentDisplayName_MixedCaseWithHyphens_ReturnsAsIs(string input)
    {
        Assert.Equal(input, DiscordNameFormatter.FormatAgentDisplayName(input));
    }

    [Fact]
    public void FormatAgentDisplayName_SingleCharSegments()
    {
        var result = DiscordNameFormatter.FormatAgentDisplayName("a-b-c");
        Assert.Equal("A B C", result);
    }

    // ── GetAgentAvatarUrl ───────────────────────────────────────

    [Fact]
    public void GetAgentAvatarUrl_ReturnsDiceBearUrl()
    {
        var url = DiscordNameFormatter.GetAgentAvatarUrl("aristotle");
        Assert.StartsWith("https://api.dicebear.com/9.x/identicon/png?seed=", url);
        Assert.Contains("aristotle", url);
        Assert.Contains("size=128", url);
    }

    [Fact]
    public void GetAgentAvatarUrl_LowercasesSeed()
    {
        var url = DiscordNameFormatter.GetAgentAvatarUrl("Aristotle");
        Assert.Contains("seed=aristotle", url);
    }

    [Fact]
    public void GetAgentAvatarUrl_EscapesSpecialChars()
    {
        var url = DiscordNameFormatter.GetAgentAvatarUrl("agent with spaces");
        Assert.DoesNotContain(" ", url.Split("seed=")[1].Split("&")[0]);
    }

    [Fact]
    public void GetAgentAvatarUrl_DifferentAgents_DifferentUrls()
    {
        var url1 = DiscordNameFormatter.GetAgentAvatarUrl("aristotle");
        var url2 = DiscordNameFormatter.GetAgentAvatarUrl("socrates");
        Assert.NotEqual(url1, url2);
    }
}
