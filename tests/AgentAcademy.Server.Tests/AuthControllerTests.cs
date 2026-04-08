using System.Security.Claims;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public class AuthControllerTests
{
    [Fact]
    public void GetStatus_WhenAuthDisabled_ReturnsUnavailable()
    {
        var controller = CreateController(authEnabled: false);

        var result = Assert.IsType<OkObjectResult>(controller.GetStatus());
        var payload = Assert.IsType<AuthStatusResult>(result.Value);

        Assert.False(payload.AuthEnabled);
        Assert.False(payload.Authenticated);
        Assert.Equal(CopilotStatusValues.Unavailable, payload.CopilotStatus);
        Assert.Null(payload.User);
    }

    [Fact]
    public void GetStatus_WhenCookieMissing_ReturnsUnavailable_EvenIfTokenExists()
    {
        var controller = CreateController(authEnabled: true, token: "gho_test");

        var result = Assert.IsType<OkObjectResult>(controller.GetStatus());
        var payload = Assert.IsType<AuthStatusResult>(result.Value);

        Assert.True(payload.AuthEnabled);
        Assert.False(payload.Authenticated);
        Assert.Equal(CopilotStatusValues.Unavailable, payload.CopilotStatus);
        Assert.Null(payload.User);
    }

    [Fact]
    public void GetStatus_WhenCookieAndSdkAreHealthy_ReturnsOperational()
    {
        var controller = CreateController(
            authEnabled: true,
            token: "gho_test",
            user: CreateAuthenticatedUser());

        var result = Assert.IsType<OkObjectResult>(controller.GetStatus());
        var payload = Assert.IsType<AuthStatusResult>(result.Value);

        Assert.True(payload.AuthEnabled);
        Assert.True(payload.Authenticated);
        Assert.Equal(CopilotStatusValues.Operational, payload.CopilotStatus);
        Assert.NotNull(payload.User);
        Assert.Equal("hephaestus", payload.User!.Login);
    }

    [Fact]
    public void GetStatus_WhenCookieExistsButTokenMissing_ReturnsDegraded()
    {
        var controller = CreateController(
            authEnabled: true,
            user: CreateAuthenticatedUser());

        var result = Assert.IsType<OkObjectResult>(controller.GetStatus());
        var payload = Assert.IsType<AuthStatusResult>(result.Value);

        Assert.True(payload.AuthEnabled);
        Assert.False(payload.Authenticated);
        Assert.Equal(CopilotStatusValues.Degraded, payload.CopilotStatus);
        Assert.NotNull(payload.User);
    }

    [Fact]
    public void GetStatus_WhenExecutorAuthFailed_ReturnsDegraded()
    {
        var controller = CreateController(
            authEnabled: true,
            token: "gho_test",
            isAuthFailed: true,
            user: CreateAuthenticatedUser());

        var result = Assert.IsType<OkObjectResult>(controller.GetStatus());
        var payload = Assert.IsType<AuthStatusResult>(result.Value);

        Assert.True(payload.AuthEnabled);
        Assert.False(payload.Authenticated);
        Assert.Equal(CopilotStatusValues.Degraded, payload.CopilotStatus);
        Assert.NotNull(payload.User);
    }

    private static AuthController CreateController(
        bool authEnabled,
        string? token = null,
        bool isAuthFailed = false,
        ClaimsPrincipal? user = null)
    {
        var tokenProvider = new CopilotTokenProvider();
        if (!string.IsNullOrWhiteSpace(token))
            tokenProvider.SetToken(token);

        var executor = Substitute.For<IAgentExecutor>();
        executor.IsAuthFailed.Returns(isAuthFailed);

        var tokenPersistence = new TokenPersistenceService(
            tokenProvider,
            Substitute.For<IServiceScopeFactory>(),
            new EphemeralDataProtectionProvider(),
            NullLogger<TokenPersistenceService>.Instance);

        var controller = new AuthController(
            new GitHubAuthOptions(authEnabled),
            tokenProvider,
            executor,
            tokenPersistence);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = user ?? new ClaimsPrincipal(new ClaimsIdentity())
            }
        };

        return controller;
    }

    private static ClaimsPrincipal CreateAuthenticatedUser()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "hephaestus"),
            new Claim("urn:github:name", "Hephaestus"),
            new Claim("urn:github:avatar", "https://example.test/avatar.png"),
        ], "Cookies");

        return new ClaimsPrincipal(identity);
    }
}
