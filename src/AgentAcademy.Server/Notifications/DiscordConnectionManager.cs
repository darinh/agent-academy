using Discord;
using Discord.WebSocket;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Owns the <see cref="DiscordSocketClient"/> lifecycle: client creation,
/// login/start/Ready handshake, disconnect handling, and disposal.
///
/// <para>
/// Extracted from <see cref="DiscordNotificationProvider"/> so that the
/// provider can focus on orchestration (channel rebuild, inbound message
/// routing, input collection) while this class handles the raw Discord
/// connection.
/// </para>
///
/// <para>
/// This type is NOT thread-safe on its own — callers (i.e. the provider)
/// are expected to serialize <see cref="ConnectAsync"/> and
/// <see cref="DisposeClientAsync"/> with their own lock.
/// </para>
/// </summary>
public sealed class DiscordConnectionManager : IDiscordConnectionManager
{
    private readonly ILogger<DiscordConnectionManager> _logger;

    private DiscordSocketClient? _client;
    private TaskCompletionSource<bool>? _readyTcs;
    private Func<Task>? _readyHandler;
    private Func<Exception, Task>? _disconnectedHandler;
    private Func<LogMessage, Task>? _logHandler;
    private string? _lastError;

    public DiscordConnectionManager(ILogger<DiscordConnectionManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// The live Discord client, or <see langword="null"/> when not connected.
    /// Consumers read this after <see cref="ConnectAsync"/> returns successfully
    /// to perform guild lookups and hook additional event handlers.
    /// </summary>
    public DiscordSocketClient? Client => _client;

    /// <summary>True when a client exists and reports <see cref="ConnectionState.Connected"/>.</summary>
    public bool IsConnected => _client?.ConnectionState == ConnectionState.Connected;

    /// <summary>
    /// Last user-facing error encountered during <see cref="ConnectAsync"/>,
    /// or <see langword="null"/> after a successful connect.
    /// </summary>
    public string? LastError => _lastError;

    /// <summary>
    /// Connects a fresh Discord client using the supplied bot token. Any
    /// existing client is torn down first. On failure the partially-created
    /// client is always disposed before the exception propagates.
    /// </summary>
    public async Task ConnectAsync(string botToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(botToken))
            throw new ArgumentException("Bot token is required.", nameof(botToken));

        _lastError = null;

        if (_client is not null)
            await DisposeClientAsync();

        _client = new DiscordSocketClient(CreateClientConfig());

        _logHandler = OnDiscordLog;
        _client.Log += _logHandler;

        _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? disconnectReason = null;
        AttachConnectionHandlers(reason => disconnectReason = reason);

        await LoginStartAndAwaitReadyAsync(botToken, () => disconnectReason, cancellationToken);

        _lastError = null;
        _logger.LogInformation("Discord client connected as {BotUser}",
            _client.CurrentUser?.Username ?? "unknown");
    }

    /// <summary>
    /// Unwires the handlers this manager owns, stops the client, disposes it,
    /// and clears the reference. Safe to call when no client exists.
    /// Consumers that attached their own handlers on <see cref="Client"/>
    /// (e.g. <c>MessageReceived</c>) should unhook them BEFORE calling this
    /// method — this manager only unwires its own subscriptions.
    /// </summary>
    public async ValueTask DisposeClientAsync()
    {
        if (_client is null) return;

        try
        {
            if (_logHandler is not null)
                _client.Log -= _logHandler;
            if (_readyHandler is not null)
                _client.Ready -= _readyHandler;
            if (_disconnectedHandler is not null)
                _client.Disconnected -= _disconnectedHandler;
            _logHandler = null;
            _readyHandler = null;
            _disconnectedHandler = null;

            await _client.StopAsync();
            await _client.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing Discord client");
        }
        finally
        {
            _client = null;
            _readyTcs = null;
        }
    }

    /// <inheritdoc cref="IAsyncDisposable.DisposeAsync" />
    public async ValueTask DisposeAsync() => await DisposeClientAsync();

    internal static DiscordSocketConfig CreateClientConfig() => new()
    {
        GatewayIntents = GatewayIntents.Guilds
            | GatewayIntents.GuildMessages
            | GatewayIntents.MessageContent,
        LogLevel = LogSeverity.Info
    };

    private void AttachConnectionHandlers(Action<string?> setDisconnectReason)
    {
        _readyHandler = () =>
        {
            _readyTcs?.TrySetResult(true);
            return Task.CompletedTask;
        };

        _disconnectedHandler = ex =>
        {
            _logger.LogWarning(ex, "Discord client disconnected");
            setDisconnectReason(DiscordDisconnectReasonResolver.Resolve(ex));
            _readyTcs?.TrySetResult(false);
            return Task.CompletedTask;
        };

        _client!.Ready += _readyHandler;
        _client.Disconnected += _disconnectedHandler;
    }

    private async Task LoginStartAndAwaitReadyAsync(
        string botToken,
        Func<string?> disconnectReasonProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            await _client!.LoginAsync(TokenType.Bot, botToken);
        }
        catch (ArgumentException ex)
        {
            _lastError = $"Invalid bot token: {ex.Message}";
            await DisposeClientAsync();
            throw;
        }

        await _client.StartAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var ready = await _readyTcs!.Task.WaitAsync(cts.Token);
            if (!ready)
            {
                _lastError = disconnectReasonProvider()
                    ?? "Discord client failed to reach Ready state. Check bot token and Message Content Intent in Discord Developer Portal.";
                await DisposeClientAsync();
                throw new InvalidOperationException(_lastError);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await DisposeClientAsync();
            _lastError = "Discord client did not become ready within 30 seconds. Check bot token and network connectivity.";
            throw new TimeoutException("Discord client did not become ready within 30 seconds.");
        }
        catch (Exception ex) when (_client is not null)
        {
            _lastError ??= $"Discord connection failed: {ex.Message}";
            await DisposeClientAsync();
            throw;
        }
    }

    private Task OnDiscordLog(LogMessage logMessage)
    {
        var level = DiscordLogSeverityMapper.ToLogLevel(logMessage.Severity);
        _logger.Log(level, logMessage.Exception, "Discord: {Message}", logMessage.Message);
        return Task.CompletedTask;
    }
}
