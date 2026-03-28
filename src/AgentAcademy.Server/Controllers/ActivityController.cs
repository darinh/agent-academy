using System.Text.Json;
using System.Threading.Channels;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Activity event endpoints — includes both REST and SSE streaming.
/// </summary>
[ApiController]
[Route("api/activity")]
public class ActivityController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ActivityBroadcaster _broadcaster;

    public ActivityController(ActivityBroadcaster broadcaster)
    {
        _broadcaster = broadcaster;
    }

    /// <summary>
    /// GET /api/activity/recent — recent activity events.
    /// </summary>
    [HttpGet("recent")]
    public ActionResult<IReadOnlyList<ActivityEvent>> GetRecentActivity()
    {
        var events = _broadcaster.GetRecentActivity();
        return Ok(events);
    }

    /// <summary>
    /// GET /api/activity/stream — SSE stream of activity events.
    /// Alternative to SignalR for environments without WebSocket support.
    /// Subscribes first, then replays recent events to avoid a race window
    /// where events broadcast between snapshot and subscribe would be lost.
    /// </summary>
    [HttpGet("stream")]
    public async Task GetActivityStream(CancellationToken ct)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no"; // Disable nginx buffering

        var channel = Channel.CreateBounded<ActivityEvent>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        // Subscribe BEFORE replaying to avoid a race where events broadcast
        // between GetRecentActivity() and Subscribe() are silently dropped.
        // Events arriving during replay are buffered in the channel and
        // delivered after replay finishes — duplicates are harmless.
        var unsubscribe = _broadcaster.Subscribe(evt =>
        {
            channel.Writer.TryWrite(evt);
        });

        try
        {
            // Replay recent events so the client starts with current state.
            var recent = _broadcaster.GetRecentActivity();
            foreach (var evt in recent)
            {
                await WriteEventAsync(Response, evt, ct);
            }
            await Response.Body.FlushAsync(ct);

            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
            {
                await WriteEventAsync(Response, evt, ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal shutdown.
        }
        finally
        {
            unsubscribe();
            channel.Writer.TryComplete();
        }
    }

    private static async Task WriteEventAsync(HttpResponse response, ActivityEvent evt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(evt, JsonOptions);
        await response.WriteAsync($"event: activityEvent\ndata: {json}\n\n", ct);
    }
}
