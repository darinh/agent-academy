using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Thin HTTP wrapper for Slack Web API methods used by the notification provider.
/// Uses raw HttpClient — no external Slack NuGet dependency required.
/// </summary>
public sealed class SlackApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<SlackApiClient> _logger;
    private readonly bool _ownsHttpClient;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SlackApiClient(HttpClient httpClient, ILogger<SlackApiClient> logger, bool ownsHttpClient = true)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ownsHttpClient = ownsHttpClient;
    }

    /// <summary>
    /// Configures the client with a bot token for Bearer auth.
    /// </summary>
    public void SetBotToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// Validates the bot token via auth.test and returns the bot user info.
    /// </summary>
    public async Task<SlackAuthTestResponse> AuthTestAsync(CancellationToken ct = default)
    {
        return await PostAsync<SlackAuthTestResponse>("auth.test", new { }, ct);
    }

    /// <summary>
    /// Posts a message to a Slack channel. Returns the message timestamp (ts) for threading.
    /// </summary>
    public async Task<SlackPostMessageResponse> PostMessageAsync(
        string channel,
        string? text = null,
        object? blocks = null,
        string? threadTs = null,
        string? username = null,
        string? iconEmoji = null,
        bool unfurlLinks = false,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["channel"] = channel,
            ["text"] = text,
            ["unfurl_links"] = unfurlLinks
        };

        if (blocks is not null) payload["blocks"] = blocks;
        if (threadTs is not null) payload["thread_ts"] = threadTs;
        if (username is not null) payload["username"] = username;
        if (iconEmoji is not null) payload["icon_emoji"] = iconEmoji;

        return await PostAsync<SlackPostMessageResponse>("chat.postMessage", payload, ct);
    }

    /// <summary>
    /// Creates a new Slack channel. Returns the channel info.
    /// </summary>
    public async Task<SlackChannelResponse> CreateChannelAsync(
        string name,
        bool isPrivate = false,
        CancellationToken ct = default)
    {
        var payload = new { name, is_private = isPrivate };
        return await PostAsync<SlackChannelResponse>("conversations.create", payload, ct);
    }

    /// <summary>
    /// Sets the topic for a Slack channel.
    /// </summary>
    public async Task<SlackBaseResponse> SetChannelTopicAsync(
        string channelId,
        string topic,
        CancellationToken ct = default)
    {
        var payload = new { channel = channelId, topic };
        return await PostAsync<SlackBaseResponse>("conversations.setTopic", payload, ct);
    }

    /// <summary>
    /// Renames a Slack channel.
    /// </summary>
    public async Task<SlackChannelResponse> RenameChannelAsync(
        string channelId,
        string name,
        CancellationToken ct = default)
    {
        var payload = new { channel = channelId, name };
        return await PostAsync<SlackChannelResponse>("conversations.rename", payload, ct);
    }

    /// <summary>
    /// Archives a Slack channel.
    /// </summary>
    public async Task<SlackBaseResponse> ArchiveChannelAsync(
        string channelId,
        CancellationToken ct = default)
    {
        var payload = new { channel = channelId };
        return await PostAsync<SlackBaseResponse>("conversations.archive", payload, ct);
    }

    /// <summary>
    /// Lists Slack channels matching criteria. Used for startup recovery.
    /// </summary>
    public async Task<SlackChannelListResponse> ListChannelsAsync(
        int limit = 200,
        string? cursor = null,
        bool excludeArchived = true,
        CancellationToken ct = default)
    {
        var query = $"conversations.list?limit={limit}&exclude_archived={excludeArchived.ToString().ToLowerInvariant()}&types=public_channel,private_channel";
        if (cursor is not null)
            query += $"&cursor={Uri.EscapeDataString(cursor)}";

        return await GetAsync<SlackChannelListResponse>(query, ct);
    }

    /// <summary>
    /// Invites the bot to a channel (required after creating a channel in some Slack setups).
    /// </summary>
    public async Task<SlackBaseResponse> JoinChannelAsync(
        string channelId,
        CancellationToken ct = default)
    {
        var payload = new { channel = channelId };
        return await PostAsync<SlackBaseResponse>("conversations.join", payload, ct);
    }

    private const int MaxRateLimitRetries = 2;

    private async Task<T> PostAsync<T>(string method, object payload, CancellationToken ct)
        where T : SlackBaseResponse
    {
        for (var attempt = 0; ; attempt++)
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _http.PostAsync($"https://slack.com/api/{method}", content, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                _logger.LogWarning("Slack API {Method} rate limited, retrying after {Delay}s (attempt {Attempt})",
                    method, retryAfter.TotalSeconds, attempt + 1);
                await Task.Delay(retryAfter, ct);
                continue;
            }

            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<T>(body, JsonOptions);

            if (result is null)
                throw new InvalidOperationException($"Slack API {method} returned null response");

            if (!result.Ok)
            {
                _logger.LogWarning("Slack API {Method} failed: {Error}", method, result.Error);
            }

            return result;
        }
    }

    private async Task<T> GetAsync<T>(string methodAndQuery, CancellationToken ct)
        where T : SlackBaseResponse
    {
        for (var attempt = 0; ; attempt++)
        {
            using var response = await _http.GetAsync($"https://slack.com/api/{methodAndQuery}", ct);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                _logger.LogWarning("Slack API {Method} rate limited, retrying after {Delay}s (attempt {Attempt})",
                    methodAndQuery.Split('?')[0], retryAfter.TotalSeconds, attempt + 1);
                await Task.Delay(retryAfter, ct);
                continue;
            }

            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<T>(body, JsonOptions);

            if (result is null)
                throw new InvalidOperationException($"Slack API returned null response for {methodAndQuery}");

            if (!result.Ok)
            {
                _logger.LogWarning("Slack API {Method} failed: {Error}", methodAndQuery.Split('?')[0], result.Error);
            }

            return result;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}

#region Slack API response types

public class SlackBaseResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class SlackAuthTestResponse : SlackBaseResponse
{
    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("team_id")]
    public string? TeamId { get; set; }

    [JsonPropertyName("team")]
    public string? Team { get; set; }

    [JsonPropertyName("bot_id")]
    public string? BotId { get; set; }
}

public class SlackPostMessageResponse : SlackBaseResponse
{
    [JsonPropertyName("ts")]
    public string? Ts { get; set; }

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }
}

public class SlackChannelResponse : SlackBaseResponse
{
    [JsonPropertyName("channel")]
    public SlackChannel? Channel { get; set; }
}

public class SlackChannelListResponse : SlackBaseResponse
{
    [JsonPropertyName("channels")]
    public List<SlackChannel>? Channels { get; set; }

    [JsonPropertyName("response_metadata")]
    public SlackResponseMetadata? ResponseMetadata { get; set; }
}

public class SlackChannel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("topic")]
    public SlackChannelTopic? Topic { get; set; }

    [JsonPropertyName("is_archived")]
    public bool IsArchived { get; set; }
}

public class SlackChannelTopic
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

public class SlackResponseMetadata
{
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }
}

#endregion
