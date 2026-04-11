using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Services;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for <see cref="GitHubController"/> — the GET /api/github/status endpoint.
/// Covers all three auth source states (oauth/cli/none) and error handling.
/// </summary>
public class GitHubControllerTests
{
    private readonly IGitHubService _gitHubService = Substitute.For<IGitHubService>();

    private GitHubController CreateController(string? oauthToken = null)
    {
        var tokenProvider = new CopilotTokenProvider();
        if (oauthToken is not null)
            tokenProvider.SetToken(oauthToken);
        return new GitHubController(_gitHubService, tokenProvider);
    }

    [Fact]
    public async Task GetStatus_ConfiguredWithOAuth_ReturnsOAuthAuthSource()
    {
        _gitHubService.IsConfiguredAsync().Returns(true);
        _gitHubService.GetRepositorySlugAsync().Returns("owner/repo");
        var controller = CreateController("gho_valid_token");

        var result = await controller.GetStatus();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var status = Assert.IsType<GitHubStatusResponse>(ok.Value);
        Assert.True(status.IsConfigured);
        Assert.Equal("owner/repo", status.Repository);
        Assert.Equal("oauth", status.AuthSource);
    }

    [Fact]
    public async Task GetStatus_ConfiguredWithCli_ReturnsCliAuthSource()
    {
        _gitHubService.IsConfiguredAsync().Returns(true);
        _gitHubService.GetRepositorySlugAsync().Returns("owner/repo");
        var controller = CreateController(); // no OAuth token

        var result = await controller.GetStatus();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var status = Assert.IsType<GitHubStatusResponse>(ok.Value);
        Assert.True(status.IsConfigured);
        Assert.Equal("owner/repo", status.Repository);
        Assert.Equal("cli", status.AuthSource);
    }

    [Fact]
    public async Task GetStatus_NotConfigured_ReturnsNoneAuthSource()
    {
        _gitHubService.IsConfiguredAsync().Returns(false);
        var controller = CreateController();

        var result = await controller.GetStatus();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var status = Assert.IsType<GitHubStatusResponse>(ok.Value);
        Assert.False(status.IsConfigured);
        Assert.Null(status.Repository);
        Assert.Equal("none", status.AuthSource);
    }

    [Fact]
    public async Task GetStatus_NotConfigured_DoesNotFetchRepoSlug()
    {
        _gitHubService.IsConfiguredAsync().Returns(false);
        var controller = CreateController();

        await controller.GetStatus();

        await _gitHubService.DidNotReceive().GetRepositorySlugAsync();
    }

    [Fact]
    public async Task GetStatus_NullRepoSlug_ReturnsNullRepository()
    {
        _gitHubService.IsConfiguredAsync().Returns(true);
        _gitHubService.GetRepositorySlugAsync().Returns((string?)null);
        var controller = CreateController("gho_token");

        var result = await controller.GetStatus();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var status = Assert.IsType<GitHubStatusResponse>(ok.Value);
        Assert.True(status.IsConfigured);
        Assert.Null(status.Repository);
        Assert.Equal("oauth", status.AuthSource);
    }

    [Fact]
    public async Task GetStatus_EmptyOAuthToken_ReturnsCliAuthSource()
    {
        _gitHubService.IsConfiguredAsync().Returns(true);
        _gitHubService.GetRepositorySlugAsync().Returns("owner/repo");
        // SetToken with empty string — provider stores it, but controller checks IsNullOrWhiteSpace
        var tokenProvider = new CopilotTokenProvider();
        tokenProvider.SetToken("  "); // whitespace-only
        var controller = new GitHubController(_gitHubService, tokenProvider);

        var result = await controller.GetStatus();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var status = Assert.IsType<GitHubStatusResponse>(ok.Value);
        Assert.Equal("cli", status.AuthSource);
    }
}
