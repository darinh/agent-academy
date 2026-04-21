using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Notification provider that delivers notifications via the Slack Web API.
/// Channel management delegated to <see cref="SlackChannelManager"/>.
/// Message formatting delegated to <see cref="SlackMessageBuilder"/>.
/// </summary>
public sealed class SlackNotificationProvider : INotificationProvider, IDisposable
{
    private readonly ILogger<SlackNotificationProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SlackChannelManager _channels;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private SlackApiClient? _apiClient;
    private string? _botToken;
    private string? _defaultChannelId;
    private volatile bool _isConfigured;
    private volatile bool _isConnected;
    private string? _botUserId;

    public SlackNotificationProvider(
        ILogger<SlackNotificationProvider> logger,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _channels = new SlackChannelManager(
            loggerFactory.CreateLogger<SlackChannelManager>(), scopeFactory);
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
            await _channels.RebuildChannelMappingAsync(_apiClient, cancellationToken);
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
            _channels.ClearMappings();
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
                targetChannel = await _channels.ResolveRoomChannelAsync(message.RoomId, _defaultChannelId!, _apiClient, cancellationToken);
            }
            else
            {
                targetChannel = _defaultChannelId!;
            }

            var blocks = SlackMessageBuilder.BuildMessageBlocks(message);
            var fallbackText = $"[{message.Type}] {message.Title}: {message.Body}";
            var agentName = message.AgentName;
            var emoji = SlackMessageBuilder.GetAgentEmoji(agentName);

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
            var channelId = await _channels.ResolveRoomChannelAsync(question.RoomId, _defaultChannelId!, _apiClient, cancellationToken);

            // Post the question as a message with agent identity
            var blocks = SlackMessageBuilder.BuildQuestionBlocks(question.AgentName, question.Question, question.RoomName);

            var result = await _apiClient.PostMessageAsync(
                channel: channelId,
                text: $"❓ {question.AgentName} asks: {SlackMessageBuilder.EscapeSlackText(question.Question)}",
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
            var channelId = await _channels.ResolveRoomChannelAsync(dm.RoomId, _defaultChannelId!, _apiClient, cancellationToken);

            var result = await _apiClient.PostMessageAsync(
                channel: channelId,
                text: SlackMessageBuilder.EscapeSlackText(dm.Question),
                username: dm.AgentName,
                iconEmoji: SlackMessageBuilder.GetAgentEmoji(dm.AgentName),
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
        await _channels.RenameRoomChannelAsync(roomId, newName, _apiClient, cancellationToken);
    }

    /// <inheritdoc />
    public async Task OnRoomClosedAsync(string roomId, CancellationToken cancellationToken = default)
    {
        if (!_isConnected || _apiClient is null) return;
        await _channels.ArchiveRoomChannelAsync(roomId, _apiClient, cancellationToken);
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
    }
}

/// <summary>
/// Null-object IHttpClientFactory for test constructors.
/// </summary>
internal sealed class NullHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}