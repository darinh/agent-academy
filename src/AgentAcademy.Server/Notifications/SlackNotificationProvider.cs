using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Notification provider that delivers notifications via the Slack Web API.
/// Uses raw HTTP (no Slack NuGet dependency). Supports room-based channel routing,
/// agent-identity messages, threaded agent questions, and channel lifecycle management.
/// </summary>
public sealed class SlackNotificationProvider : INotificationProvider, IDisposable
{
    private readonly ILogger<SlackNotificationProvider> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _channelCreateLock = new(1, 1);

    // Maps AA roomId → Slack channel ID
    private readonly ConcurrentDictionary<string, string> _roomChannels = new();

    private SlackApiClient? _apiClient;
    private string? _botToken;
    private string? _defaultChannelId;
    private volatile bool _isConfigured;
    private volatile bool _isConnected;
    private string? _botUserId;

    // Agent role → emoji for visual identity in messages
    private static readonly Dictionary<string, string> AgentEmoji = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Planner"] = ":crystal_ball:",
        ["Architect"] = ":building_construction:",
        ["SoftwareEngineer"] = ":computer:",
        ["Reviewer"] = ":mag:",
        ["Validator"] = ":white_check_mark:",
        ["TechnicalWriter"] = ":pencil:",
        ["Human"] = ":bust_in_silhouette:"
    };

    private readonly ILoggerFactory _loggerFactory;

    public SlackNotificationProvider(
        ILogger<SlackNotificationProvider> logger,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    /// <summary>For unit testing: inject a pre-built API client.</summary>
    internal SlackNotificationProvider(
        ILogger<SlackNotificationProvider> logger,
        IServiceScopeFactory scopeFactory,
        SlackApiClient apiClient,
        ILoggerFactory? loggerFactory = null,
        IHttpClientFactory? httpClientFactory = null)
        : this(logger, scopeFactory,
            loggerFactory ?? NullLoggerFactory.Instance,
            httpClientFactory ?? new NullHttpClientFactory())
    {
        _apiClient = apiClient;
    }

    /// <inheritdoc />
    public string ProviderId => "slack";

    /// <inheritdoc />
    public string DisplayName => "Slack";

    /// <inheritdoc />
    public bool IsConfigured => _isConfigured;

    /// <inheritdoc />
    public bool IsConnected => _isConnected;

    /// <inheritdoc />
    public string? LastError { get; private set; }

    /// <inheritdoc />
    public Task ConfigureAsync(Dictionary<string, string> configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.TryGetValue("BotToken", out var token) || string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("BotToken is required.", nameof(configuration));

        if (!configuration.TryGetValue("DefaultChannelId", out var channelId) || string.IsNullOrWhiteSpace(channelId))
            throw new ArgumentException("DefaultChannelId is required.", nameof(configuration));

        _botToken = token;
        _defaultChannelId = channelId;
        _isConfigured = true;

        _logger.LogInformation("Slack provider configured with default channel {ChannelId}", _defaultChannelId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!_isConfigured)
            throw new InvalidOperationException("Slack provider must be configured before connecting. Call ConfigureAsync first.");

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            LastError = null; // Clear previous error on each connect attempt

            if (_isConnected)
            {
                _logger.LogDebug("Slack provider is already connected");
                return;
            }

            // Create API client if not injected via test constructor
            if (_apiClient is null)
            {
                var http = _httpClientFactory.CreateClient("Slack");
                _apiClient = new SlackApiClient(http, _loggerFactory.CreateLogger<SlackApiClient>(), ownsHttpClient: false);
            }

            _apiClient.SetBotToken(_botToken!);

            // Validate token
            var authResult = await _apiClient.AuthTestAsync(cancellationToken);
            if (!authResult.Ok)
            {
                LastError = $"Slack auth.test failed: {authResult.Error}";
                throw new InvalidOperationException(LastError);
            }

            _botUserId = authResult.UserId;
            _isConnected = true;
            LastError = null;
            _logger.LogInformation("Slack provider connected as {BotUser} (team: {Team})",
                authResult.User, authResult.Team);

            // Rebuild channel mapping from existing Slack channels
            await RebuildChannelMappingAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError ??= ex.Message;
            throw;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            _isConnected = false;
            _roomChannels.Clear();
            _logger.LogInformation("Slack provider disconnected");
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendNotificationAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!_isConnected || _apiClient is null)
        {
            _logger.LogWarning("Cannot send notification — Slack provider is not connected");
            return false;
        }

        try
        {
            string targetChannel;

            // Route to room-specific channel if available
            if (!string.IsNullOrEmpty(message.RoomId))
            {
                targetChannel = await ResolveRoomChannelAsync(message.RoomId, cancellationToken);
            }
            else
            {
                targetChannel = _defaultChannelId!;
            }

            var blocks = BuildMessageBlocks(message);
            var fallbackText = $"[{message.Type}] {message.Title}: {message.Body}";
            var agentName = message.AgentName;
            var emoji = GetAgentEmoji(agentName);

            var result = await _apiClient.PostMessageAsync(
                channel: targetChannel,
                text: fallbackText,
                blocks: blocks,
                username: agentName,
                iconEmoji: emoji,
                ct: cancellationToken);

            if (!result.Ok)
            {
                _logger.LogWarning("Failed to send Slack notification: {Error}", result.Error);
                return false;
            }

            _logger.LogDebug("Sent Slack notification to {Channel}: {Title}", targetChannel, message.Title);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to send Slack notification: {Title}", message.Title);
            return false;
        }
    }

    /// <inheritdoc />
    public Task<UserResponse?> RequestInputAsync(InputRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Slack provider cannot collect input without Events API / Socket Mode.
        _logger.LogDebug("Slack provider cannot collect input; returning null for prompt: {Prompt}", request.Prompt);
        return Task.FromResult<UserResponse?>(null);
    }

    /// <inheritdoc />
    public async Task<bool> SendAgentQuestionAsync(AgentQuestion question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);

        if (!_isConnected || _apiClient is null)
        {
            _logger.LogWarning("Cannot send agent question — Slack provider is not connected");
            return false;
        }

        try
        {
            var channelId = await ResolveRoomChannelAsync(question.RoomId, cancellationToken);

            // Post the question as a message with agent identity
            var blocks = new object[]
            {
                new { type = "header", text = new { type = "plain_text", text = $"❓ {question.AgentName} asks:", emoji = true } },
                new { type = "section", text = new { type = "mrkdwn", text = EscapeSlackText(question.Question) } },
                new { type = "context", elements = new object[]
                {
                    new { type = "mrkdwn", text = $"*Room:* {EscapeSlackText(question.RoomName)} · *Agent:* {EscapeSlackText(question.AgentName)}" }
                }},
                new { type = "divider" }
            };

            var result = await _apiClient.PostMessageAsync(
                channel: channelId,
                text: $"❓ {question.AgentName} asks: {EscapeSlackText(question.Question)}",
                blocks: blocks,
                username: question.AgentName,
                iconEmoji: ":question:",
                ct: cancellationToken);

            if (!result.Ok)
            {
                _logger.LogWarning("Failed to send agent question to Slack: {Error}", result.Error);
                return false;
            }

            _logger.LogInformation("Agent question sent to Slack #{Channel}: {AgentName}",
                channelId, question.AgentName);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to send agent question via Slack from '{AgentName}'", question.AgentName);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendDirectMessageAsync(AgentQuestion dm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dm);

        if (!_isConnected || _apiClient is null)
        {
            _logger.LogWarning("Cannot send DM — Slack provider is not connected");
            return false;
        }

        try
        {
            var channelId = await ResolveRoomChannelAsync(dm.RoomId, cancellationToken);

            var result = await _apiClient.PostMessageAsync(
                channel: channelId,
                text: EscapeSlackText(dm.Question),
                username: dm.AgentName,
                iconEmoji: GetAgentEmoji(dm.AgentName),
                ct: cancellationToken);

            if (!result.Ok)
            {
                _logger.LogWarning("Failed to send DM to Slack: {Error}", result.Error);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to send DM via Slack from '{AgentName}'", dm.AgentName);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task OnRoomRenamedAsync(string roomId, string newName, CancellationToken cancellationToken = default)
    {
        if (!_isConnected || _apiClient is null) return;

        if (_roomChannels.TryGetValue(roomId, out var channelId))
        {
            try
            {
                var slackName = ToSlackChannelName(newName);
                var result = await _apiClient.RenameChannelAsync(channelId, slackName, cancellationToken);
                if (result.Ok)
                {
                    _logger.LogInformation("Renamed Slack channel {ChannelId} to {NewName}", channelId, slackName);
                }
                else
                {
                    _logger.LogWarning("Failed to rename Slack channel {ChannelId}: {Error}", channelId, result.Error);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error renaming Slack channel for room {RoomId}", roomId);
            }
        }
    }

    /// <inheritdoc />
    public async Task OnRoomClosedAsync(string roomId, CancellationToken cancellationToken = default)
    {
        if (!_isConnected || _apiClient is null) return;

        if (_roomChannels.TryRemove(roomId, out var channelId))
        {
            try
            {
                var result = await _apiClient.ArchiveChannelAsync(channelId, cancellationToken);
                if (result.Ok)
                {
                    _logger.LogInformation("Archived Slack channel {ChannelId} for room {RoomId}", channelId, roomId);
                }
                else
                {
                    _logger.LogWarning("Failed to archive Slack channel {ChannelId}: {Error}", channelId, result.Error);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error archiving Slack channel for room {RoomId}", roomId);
            }
        }
    }

    /// <inheritdoc />
    public ProviderConfigSchema GetConfigSchema() => new(
        ProviderId: "slack",
        DisplayName: "Slack",
        Description: "Send notifications to Slack channels via the Slack Web API. Requires a Slack Bot with chat:write, channels:manage, and channels:read scopes.",
        Fields: new List<ConfigField>
        {
            new("BotToken", "Bot Token", "secret", true,
                "Slack Bot Token (starts with xoxb-). Create at https://api.slack.com/apps",
                "xoxb-..."),
            new("DefaultChannelId", "Default Channel ID", "string", true,
                "Slack channel ID for fallback notifications. Right-click channel → View channel details → copy ID at bottom.",
                "C0123456789")
        }
    );

    public void Dispose()
    {
        _apiClient?.Dispose();
        _connectLock.Dispose();
        _channelCreateLock.Dispose();
    }

    #region Private helpers

    /// <summary>
    /// Resolves the Slack channel ID for a given AA room.
    /// Creates the channel if it doesn't exist.
    /// Falls back to default channel on failure.
    /// </summary>
    private async Task<string> ResolveRoomChannelAsync(string roomId, CancellationToken ct)
    {
        // Return cached mapping
        if (_roomChannels.TryGetValue(roomId, out var cached))
            return cached;

        await _channelCreateLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_roomChannels.TryGetValue(roomId, out cached))
                return cached;

            // Resolve project name for channel naming
            string? projectName = null;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();
                projectName = await roomService.GetProjectNameForRoomAsync(roomId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve project name for room {RoomId}", roomId);
            }

            var prefix = projectName is not null ? ToSlackChannelName(projectName) : "aa";
            var channelName = $"{prefix}-{roomId[..Math.Min(8, roomId.Length)]}";
            channelName = ToSlackChannelName(channelName);

            // Truncate to Slack's 80-char channel name limit
            if (channelName.Length > 80)
                channelName = channelName[..80];

            try
            {
                var createResult = await _apiClient!.CreateChannelAsync(channelName, ct: ct);
                if (createResult.Ok && createResult.Channel is not null)
                {
                    var newChannelId = createResult.Channel.Id;
                    _roomChannels[roomId] = newChannelId;

                    // Set topic with room ID for startup recovery
                    var topic = $"Agent Academy room · ID: {roomId}";
                    await _apiClient.SetChannelTopicAsync(newChannelId, topic, ct);

                    _logger.LogInformation("Created Slack channel #{Name} ({Id}) for room {RoomId}",
                        channelName, newChannelId, roomId);
                    return newChannelId;
                }

                // name_taken means the channel already exists — find it
                if (createResult.Error == "name_taken")
                {
                    var existing = await FindChannelByNameAsync(channelName, ct);
                    if (existing is not null)
                    {
                        _roomChannels[roomId] = existing;
                        return existing;
                    }
                }

                _logger.LogWarning("Failed to create Slack channel '{Name}': {Error}. Falling back to default.",
                    channelName, createResult.Error);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error creating Slack channel for room {RoomId}. Falling back to default.", roomId);
            }

            // Cache the fallback to avoid retrying on every message
            _roomChannels[roomId] = _defaultChannelId!;
            return _defaultChannelId!;
        }
        finally
        {
            _channelCreateLock.Release();
        }
    }

    /// <summary>
    /// Scans existing Slack channels to rebuild the room → channel mapping on startup.
    /// Looks for channels with "Agent Academy room · ID: {roomId}" in the topic.
    /// </summary>
    private async Task RebuildChannelMappingAsync(CancellationToken ct)
    {
        try
        {
            string? cursor = null;
            var recovered = 0;

            do
            {
                var result = await _apiClient!.ListChannelsAsync(cursor: cursor, ct: ct);
                if (!result.Ok || result.Channels is null)
                    break;

                foreach (var channel in result.Channels)
                {
                    var roomId = ExtractRoomIdFromTopic(channel.Topic?.Value);
                    if (roomId is not null)
                    {
                        _roomChannels[roomId] = channel.Id;
                        recovered++;
                    }
                }

                cursor = result.ResponseMetadata?.NextCursor;
            } while (!string.IsNullOrEmpty(cursor));

            if (recovered > 0)
            {
                _logger.LogInformation("Recovered {Count} Slack channel mappings from existing channels", recovered);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to rebuild Slack channel mapping — new channels will be created as needed");
        }
    }

    /// <summary>
    /// Extracts a room ID from a Slack channel topic containing "ID: {roomId}".
    /// </summary>
    internal static string? ExtractRoomIdFromTopic(string? topic)
    {
        if (string.IsNullOrEmpty(topic)) return null;

        var match = Regex.Match(topic, @"ID:\s*(\S+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Finds a Slack channel by name via conversations.list pagination.
    /// </summary>
    private async Task<string?> FindChannelByNameAsync(string name, CancellationToken ct)
    {
        string? cursor = null;
        do
        {
            var result = await _apiClient!.ListChannelsAsync(cursor: cursor, ct: ct);
            if (!result.Ok || result.Channels is null) break;

            var match = result.Channels.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.Id;

            cursor = result.ResponseMetadata?.NextCursor;
        } while (!string.IsNullOrEmpty(cursor));

        return null;
    }

    /// <summary>
    /// Builds Slack Block Kit blocks for a notification message.
    /// </summary>
    private static object[] BuildMessageBlocks(NotificationMessage message)
    {
        var emoji = GetTypeEmoji(message.Type);

        var blocks = new List<object>
        {
            new
            {
                type = "section",
                text = new { type = "mrkdwn", text = $"{emoji} *{EscapeSlackText(message.Title)}*" }
            }
        };

        if (!string.IsNullOrEmpty(message.Body))
        {
            // Split long messages to respect Slack's 3000-char block text limit
            var body = message.Body;
            if (body.Length > 2900)
                body = body[..2900] + "…";

            blocks.Add(new
            {
                type = "section",
                text = new { type = "mrkdwn", text = EscapeSlackText(body) }
            });
        }

        // Context line with agent name and room
        var contextParts = new List<object>();
        if (!string.IsNullOrEmpty(message.AgentName))
            contextParts.Add(new { type = "mrkdwn", text = $"*Agent:* {EscapeSlackText(message.AgentName)}" });
        if (!string.IsNullOrEmpty(message.RoomId))
            contextParts.Add(new { type = "mrkdwn", text = $"*Room:* {EscapeSlackText(message.RoomId)}" });

        if (contextParts.Count > 0)
        {
            blocks.Add(new { type = "context", elements = contextParts.ToArray() });
        }

        return blocks.ToArray();
    }

    /// <summary>
    /// Converts a name to a valid Slack channel name (lowercase, no spaces, max 80 chars).
    /// </summary>
    internal static string ToSlackChannelName(string name)
    {
        // Slack channel names: lowercase, hyphens/underscores allowed, no spaces, max 80 chars
        var result = name.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace('.', '-')
            .Replace('/', '-');

        // Remove invalid chars (keep alphanumeric, hyphens, underscores)
        result = Regex.Replace(result, @"[^a-z0-9\-_]", "");

        // Collapse multiple hyphens
        result = Regex.Replace(result, @"-{2,}", "-");

        // Trim leading/trailing hyphens
        result = result.Trim('-');

        // Slack requires non-empty
        return string.IsNullOrEmpty(result) ? "agent-academy" : result;
    }

    private static string GetTypeEmoji(NotificationType type) => type switch
    {
        NotificationType.Error => "🔴",
        NotificationType.TaskFailed => "❌",
        NotificationType.TaskComplete => "✅",
        NotificationType.NeedsInput => "💬",
        NotificationType.SpecReview => "📋",
        NotificationType.AgentThinking => "🤔",
        _ => "ℹ️"
    };

    private static string? GetAgentEmoji(string? agentName)
    {
        if (agentName is null) return null;

        // Try to match by known role keywords in the agent name
        foreach (var (role, emoji) in AgentEmoji)
        {
            if (agentName.Contains(role, StringComparison.OrdinalIgnoreCase))
                return emoji;
        }

        return ":robot_face:";
    }

    /// <summary>
    /// Escapes special Slack mrkdwn characters to prevent unintended formatting.
    /// </summary>
    internal static string EscapeSlackText(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    #endregion
}

/// <summary>
/// Null-object IHttpClientFactory for test constructors.
/// </summary>
internal sealed class NullHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}