using System.Net;
using System.Text;
using AgentAcademy.Server.Hubs;
using AgentAcademy.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Authorization;

namespace AgentAcademy.Server.Tests;

public sealed class ActivityHubAuthorizationTests : IClassFixture<ApiContractFixture>
{
    private readonly ApiContractFixture _fixture;

    public ActivityHubAuthorizationTests(ApiContractFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void ActivityHub_HasAuthorizeAttribute()
    {
        var hasAuthorize = Attribute.IsDefined(typeof(ActivityHub), typeof(AuthorizeAttribute), inherit: true);
        Assert.True(hasAuthorize);
    }

    [Fact]
    public async Task Negotiate_WhenAuthDisabled_AllowsAnonymous()
    {
        using var client = _fixture.CreateClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/hubs/activity/negotiate?negotiateVersion=1", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
