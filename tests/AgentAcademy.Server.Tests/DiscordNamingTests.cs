using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Services;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for Discord category and channel naming conventions.
/// Ensures project names are displayed in Pascal Case with spaces,
/// categories use "{ProjectName} Rooms" and "{ProjectName} Messages" format,
/// and no AA:/aa- prefixes appear.
/// </summary>
public class DiscordNamingTests
{
    // ── HumanizeProjectName ─────────────────────────────────────

    [Theory]
    [InlineData("agent-academy", "Agent Academy")]
    [InlineData("my-cool-app", "My Cool App")]
    [InlineData("my_cool_app", "My Cool App")]
    [InlineData("nonogram", "Nonogram")]
    [InlineData("ALLCAPS", "ALLCAPS")]
    [InlineData("Agent Academy", "Agent Academy")]
    [InlineData("@scope/my-lib", "My Lib")]
    [InlineData("a-b-c", "A B C")]
    public void HumanizeProjectName_ProducesPascalCaseWithSpaces(string input, string expected)
    {
        var result = ProjectScanner.HumanizeProjectName(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void HumanizeProjectName_EmptyInput_ReturnsSameString()
    {
        var result = ProjectScanner.HumanizeProjectName("");
        Assert.Equal("", result);
    }

    // ── SanitizeCategoryName ────────────────────────────────────

    [Theory]
    [InlineData("Agent Academy Rooms", "Agent Academy Rooms")]
    [InlineData("Agent Academy Messages", "Agent Academy Messages")]
    [InlineData("Nonogram Rooms", "Nonogram Rooms")]
    [InlineData("Rooms", "Rooms")]
    public void SanitizeCategoryName_PreservesValidNames(string input, string expected)
    {
        var result = DiscordNotificationProvider.SanitizeCategoryName(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeCategoryName_TruncatesAt100Chars()
    {
        var longName = new string('A', 110) + " Rooms";
        var result = DiscordNotificationProvider.SanitizeCategoryName(longName);
        Assert.Equal(100, result.Length);
    }

    [Fact]
    public void SanitizeCategoryName_EmptyInput_ReturnsFallback()
    {
        var result = DiscordNotificationProvider.SanitizeCategoryName("");
        Assert.Equal("General", result);
    }

    [Fact]
    public void SanitizeCategoryName_WhitespaceOnly_ReturnsFallback()
    {
        var result = DiscordNotificationProvider.SanitizeCategoryName("   ");
        Assert.Equal("General", result);
    }

    // ── Category naming integration (end-to-end format) ─────────

    [Theory]
    [InlineData("agent-academy", "Agent Academy Rooms")]
    [InlineData("my-cool-app", "My Cool App Rooms")]
    [InlineData("nonogram", "Nonogram Rooms")]
    [InlineData("Agent Academy", "Agent Academy Rooms")]
    public void RoomCategoryName_IsPascalCaseWithRoomsSuffix(string projectName, string expected)
    {
        var categoryName = DiscordNotificationProvider.SanitizeCategoryName(
            $"{ProjectScanner.HumanizeProjectName(projectName)} Rooms");
        Assert.Equal(expected, categoryName);
    }

    [Theory]
    [InlineData("agent-academy", "Agent Academy Messages")]
    [InlineData("my-cool-app", "My Cool App Messages")]
    [InlineData("nonogram", "Nonogram Messages")]
    [InlineData("Agent Academy", "Agent Academy Messages")]
    public void MessageCategoryName_IsPascalCaseWithMessagesSuffix(string projectName, string expected)
    {
        var categoryName = DiscordNotificationProvider.SanitizeCategoryName(
            $"{ProjectScanner.HumanizeProjectName(projectName)} Messages");
        Assert.Equal(expected, categoryName);
    }

    // ── Regression guards ───────────────────────────────────────

    [Theory]
    [InlineData("agent-academy")]
    [InlineData("my-cool-app")]
    [InlineData("nonogram")]
    public void RoomCategoryName_NeverContainsAaPrefix(string projectName)
    {
        var categoryName = DiscordNotificationProvider.SanitizeCategoryName(
            $"{ProjectScanner.HumanizeProjectName(projectName)} Rooms");
        Assert.DoesNotContain("AA:", categoryName);
        Assert.DoesNotContain("aa-", categoryName);
    }

    [Theory]
    [InlineData("agent-academy")]
    [InlineData("my-cool-app")]
    [InlineData("nonogram")]
    public void MessageCategoryName_NeverContainsAaPrefix(string projectName)
    {
        var categoryName = DiscordNotificationProvider.SanitizeCategoryName(
            $"{ProjectScanner.HumanizeProjectName(projectName)} Messages");
        Assert.DoesNotContain("AA:", categoryName);
        Assert.DoesNotContain("aa-", categoryName);
    }

    [Theory]
    [InlineData("agent-academy")]
    [InlineData("my-cool-app")]
    public void CategoryName_NeverContainsKebabCase(string projectName)
    {
        var rooms = DiscordNotificationProvider.SanitizeCategoryName(
            $"{ProjectScanner.HumanizeProjectName(projectName)} Rooms");
        var messages = DiscordNotificationProvider.SanitizeCategoryName(
            $"{ProjectScanner.HumanizeProjectName(projectName)} Messages");

        // The project portion should not contain hyphens (Pascal Case uses spaces)
        var roomProject = rooms.Replace(" Rooms", "");
        var msgProject = messages.Replace(" Messages", "");
        Assert.DoesNotContain("-", roomProject);
        Assert.DoesNotContain("-", msgProject);
    }
}
