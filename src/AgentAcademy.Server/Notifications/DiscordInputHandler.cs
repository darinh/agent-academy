using AgentAcademy.Shared.Models;
using Discord;
using Discord.WebSocket;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Handles Discord-specific input collection mechanics — button interactions and freeform text replies.
/// Stateless: receives all runtime context via method parameters. Owns the serialization lock
/// for freeform input to prevent concurrent prompt collisions.
/// Extracted from DiscordNotificationProvider to separate input collection from notification delivery.
/// </summary>
public sealed class DiscordInputHandler : IDisposable
{
    private readonly ILogger<DiscordInputHandler> _logger;
    private readonly SemaphoreSlim _inputLock = new(1, 1);

    public DiscordInputHandler(ILogger<DiscordInputHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Presents numbered choice buttons in a Discord channel and waits for a button click.
    /// Subscribes to the client's InteractionCreated event for the duration of the request
    /// and unsubscribes on completion, cancellation, or disposal.
    /// </summary>
    /// <remarks>
    /// Choice input is NOT owner-scoped — any user can click a button.
    /// This matches the original DiscordNotificationProvider behavior.
    /// </remarks>
    public async Task<UserResponse?> RequestChoiceInputAsync(
        DiscordSocketClient client,
        IMessageChannel channel,
        EmbedBuilder embed,
        List<string> choices,
        string providerId,
        CancellationToken cancellationToken)
    {
        embed.AddField("Choices", string.Join(" | ", choices.Select((c, i) => $"`{i + 1}` {c}")));

        var components = new ComponentBuilder();
        for (var i = 0; i < choices.Count; i++)
        {
            components.WithButton(choices[i], $"input-choice:{i}", ButtonStyle.Primary);
        }

        var sentMessage = await channel.SendMessageAsync(embed: embed.Build(), components: components.Build());

        var tcs = new TaskCompletionSource<UserResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task OnInteractionCreated(SocketInteraction interaction)
        {
            if (interaction is not SocketMessageComponent component)
                return Task.CompletedTask;

            if (component.Message.Id != sentMessage.Id)
                return Task.CompletedTask;

            if (!component.Data.CustomId.StartsWith("input-choice:"))
                return Task.CompletedTask;

            var indexStr = component.Data.CustomId["input-choice:".Length..];
            if (!int.TryParse(indexStr, out var choiceIndex) || choiceIndex < 0 || choiceIndex >= choices.Count)
                return Task.CompletedTask;

            var selected = choices[choiceIndex];
            tcs.TrySetResult(new UserResponse(selected, selected, providerId));

            _ = component.DeferAsync();
            return Task.CompletedTask;
        }

        client.InteractionCreated += OnInteractionCreated;
        try
        {
            var result = await tcs.Task.WaitAsync(cancellationToken);
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Input request timed out waiting for Discord choice selection");
            return null;
        }
        finally
        {
            client.InteractionCreated -= OnInteractionCreated;
        }
    }

    /// <summary>
    /// Sends a freeform input prompt to a Discord channel and waits for a text reply.
    /// Serialized via an internal lock — only one freeform input can be active at a time.
    /// </summary>
    /// <remarks>
    /// Freeform input IS owner-scoped when <paramref name="ownerId"/> is set —
    /// only messages from the configured owner user are accepted.
    /// This matches the original DiscordNotificationProvider behavior.
    /// </remarks>
    public async Task<UserResponse?> RequestFreeformInputAsync(
        DiscordSocketClient client,
        IMessageChannel channel,
        EmbedBuilder embed,
        ulong channelId,
        ulong? ownerId,
        string providerId,
        CancellationToken cancellationToken)
    {
        await _inputLock.WaitAsync(cancellationToken);
        try
        {
            embed.WithFooter("Reply in this channel to respond");
            await channel.SendMessageAsync(embed: embed.Build());

            var tcs = new TaskCompletionSource<UserResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);

            Task OnMessageReceived(SocketMessage msg)
            {
                if (msg.Channel.Id != channelId)
                    return Task.CompletedTask;

                if (msg.Author.IsBot)
                    return Task.CompletedTask;

                if (ownerId.HasValue && msg.Author.Id != ownerId.Value)
                    return Task.CompletedTask;

                tcs.TrySetResult(new UserResponse(msg.Content, ProviderId: providerId));
                return Task.CompletedTask;
            }

            client.MessageReceived += OnMessageReceived;
            try
            {
                var result = await tcs.Task.WaitAsync(cancellationToken);
                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Input request timed out waiting for Discord freeform reply");
                return null;
            }
            finally
            {
                client.MessageReceived -= OnMessageReceived;
            }
        }
        finally
        {
            _inputLock.Release();
        }
    }

    public void Dispose() => _inputLock.Dispose();
}
