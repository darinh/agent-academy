using System.Net;
using System.Text.Json;
using AgentAcademy.Server.Notifications;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public class SlackApiClientTests : IDisposable
{
    private readonly MockHttpMessageHandler _handler;
    private readonly SlackApiClient _client;
    private readonly ILogger<SlackApiClient> _logger;

    public SlackApiClientTests()
    {
        _handler = new MockHttpMessageHandler();
        var http = new HttpClient(_handler);
        _logger = Substitute.For<ILogger<SlackApiClient>>();
        _client = new SlackApiClient(http, _logger, ownsHttpClient: false);
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    #region SetBotToken

    [Fact]
    public void SetBotToken_ThrowsOnNullOrEmpty()
    {
        Assert.Throws<ArgumentNullException>(() => _client.SetBotToken(null!));
        Assert.Throws<ArgumentException>(() => _client.SetBotToken(""));
        Assert.Throws<ArgumentException>(() => _client.SetBotToken("   "));
    }

    [Fact]
    public void SetBotToken_SetsAuthorizationHeader()
    {
        _client.SetBotToken("xoxb-test");
        // No exception = success; we can't inspect private state, but auth.test will use it
    }

    #endregion

    #region AuthTestAsync

    [Fact]
    public async Task AuthTestAsync_ParsesSuccessResponse()
    {
        _handler.SetResponse("""{"ok":true,"user_id":"U123","user":"testbot","team_id":"T456","team":"TestTeam","bot_id":"B789"}""");
        _client.SetBotToken("xoxb-test");

        var result = await _client.AuthTestAsync();

        Assert.True(result.Ok);
        Assert.Equal("U123", result.UserId);
        Assert.Equal("testbot", result.User);
        Assert.Equal("T456", result.TeamId);
        Assert.Equal("TestTeam", result.Team);
        Assert.Equal("B789", result.BotId);
    }

    [Fact]
    public async Task AuthTestAsync_ParsesFailureResponse()
    {
        _handler.SetResponse("""{"ok":false,"error":"invalid_auth"}""");
        _client.SetBotToken("xoxb-bad-token");

        var result = await _client.AuthTestAsync();

        Assert.False(result.Ok);
        Assert.Equal("invalid_auth", result.Error);
    }

    #endregion

    #region PostMessageAsync

    [Fact]
    public async Task PostMessageAsync_ReturnsTimestamp()
    {
        _handler.SetResponse("""{"ok":true,"ts":"1234567890.123456","channel":"C0123456789"}""");
        _client.SetBotToken("xoxb-test");

        var result = await _client.PostMessageAsync("C0123456789", text: "Hello Slack");

        Assert.True(result.Ok);
        Assert.Equal("1234567890.123456", result.Ts);
        Assert.Equal("C0123456789", result.Channel);
    }

    [Fact]
    public async Task PostMessageAsync_SendsCorrectPayload()
    {
        _handler.SetResponse("""{"ok":true,"ts":"1","channel":"C1"}""");
        _client.SetBotToken("xoxb-test");

        await _client.PostMessageAsync(
            channel: "C999",
            text: "Test message",
            username: "TestBot",
            iconEmoji: ":robot_face:",
            threadTs: "111.222");

        var lastBody = _handler.LastRequestBody;
        Assert.NotNull(lastBody);
        Assert.Contains("C999", lastBody);
        Assert.Contains("Test message", lastBody);
        Assert.Contains("TestBot", lastBody);
        Assert.Contains(":robot_face:", lastBody);
        Assert.Contains("111.222", lastBody);
    }

    [Fact]
    public async Task PostMessageAsync_HandlesChannelNotFound()
    {
        _handler.SetResponse("""{"ok":false,"error":"channel_not_found"}""");
        _client.SetBotToken("xoxb-test");

        var result = await _client.PostMessageAsync("C_INVALID", text: "test");

        Assert.False(result.Ok);
        Assert.Equal("channel_not_found", result.Error);
    }

    #endregion

    #region CreateChannelAsync

    [Fact]
    public async Task CreateChannelAsync_ReturnsChannel()
    {
        _handler.SetResponse("""{"ok":true,"channel":{"id":"C999","name":"new-channel","is_archived":false}}""");
        _client.SetBotToken("xoxb-test");

        var result = await _client.CreateChannelAsync("new-channel");

        Assert.True(result.Ok);
        Assert.NotNull(result.Channel);
        Assert.Equal("C999", result.Channel.Id);
        Assert.Equal("new-channel", result.Channel.Name);
    }

    [Fact]
    public async Task CreateChannelAsync_HandlesNameTaken()
    {
        _handler.SetResponse("""{"ok":false,"error":"name_taken"}""");
        _client.SetBotToken("xoxb-test");

        var result = await _client.CreateChannelAsync("existing-channel");

        Assert.False(result.Ok);
        Assert.Equal("name_taken", result.Error);
    }

    #endregion

    #region SetChannelTopicAsync

    [Fact]
    public async Task SetChannelTopicAsync_ReturnsOk()
    {
        _handler.SetResponse("""{"ok":true}""");
        _client.SetBotToken("xoxb-test");

        var result = await _client.SetChannelTopicAsync("C123", "Agent Academy room · ID: abc");

        Assert.True(result.Ok);
    }

    #endregion

    #region RenameChannelAsync

    [Fact]
    public async Task RenameChannelAsync_ReturnsChannel()
    {
        _handler.SetResponse("""{"ok":true,"channel":{"id":"C123","name":"renamed","is_archived":false}}""");
        _client.SetBotToken("xoxb-test");

        var result = await _client.RenameChannelAsync("C123", "renamed");

        Assert.True(result.Ok);
        Assert.Equal("renamed", result.Channel?.Name);
    }

    #endregion

    #region ArchiveChannelAsync

    [Fact]
    public async Task ArchiveChannelAsync_ReturnsOk()
    {
        _handler.SetResponse("""{"ok":true}""");
        _client.SetBotToken("xoxb-test");

        var result = await _client.ArchiveChannelAsync("C123");

        Assert.True(result.Ok);
    }

    #endregion

    #region ListChannelsAsync

    [Fact]
    public async Task ListChannelsAsync_ParsesChannelList()
    {
        _handler.SetResponse("""
        {
            "ok": true,
            "channels": [
                {"id":"C1","name":"general","is_archived":false,"topic":{"value":""}},
                {"id":"C2","name":"aa-room","is_archived":false,"topic":{"value":"Agent Academy room · ID: room123"}}
            ],
            "response_metadata": {"next_cursor": ""}
        }
        """);
        _client.SetBotToken("xoxb-test");

        var result = await _client.ListChannelsAsync();

        Assert.True(result.Ok);
        Assert.NotNull(result.Channels);
        Assert.Equal(2, result.Channels.Count);
        Assert.Equal("C1", result.Channels[0].Id);
        Assert.Equal("C2", result.Channels[1].Id);
    }

    [Fact]
    public async Task ListChannelsAsync_HandlesPaginationCursor()
    {
        _handler.SetResponse("""
        {
            "ok": true,
            "channels": [{"id":"C3","name":"test","is_archived":false}],
            "response_metadata": {"next_cursor": "abc123"}
        }
        """);
        _client.SetBotToken("xoxb-test");

        var result = await _client.ListChannelsAsync();

        Assert.True(result.Ok);
        Assert.Equal("abc123", result.ResponseMetadata?.NextCursor);
    }

    #endregion

    #region JoinChannelAsync

    [Fact]
    public async Task JoinChannelAsync_ReturnsOk()
    {
        _handler.SetResponse("""{"ok":true}""");
        _client.SetBotToken("xoxb-test");

        var result = await _client.JoinChannelAsync("C123");

        Assert.True(result.Ok);
    }

    #endregion

    #region Error handling

    [Fact]
    public async Task PostAsync_ThrowsOnHttpError()
    {
        _handler.SetStatusCode(HttpStatusCode.InternalServerError);
        _client.SetBotToken("xoxb-test");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => _client.PostMessageAsync("C1", text: "test"));
    }

    #endregion

    /// <summary>
    /// Mock HTTP handler for intercepting Slack API calls in tests.
    /// </summary>
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private string _responseBody = """{"ok":true}""";
        private HttpStatusCode _statusCode = HttpStatusCode.OK;

        public string? LastRequestBody { get; private set; }

        public void SetResponse(string json) => _responseBody = json;
        public void SetStatusCode(HttpStatusCode code) => _statusCode = code;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
