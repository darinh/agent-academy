using System.Text;
using System.Text.Json;
using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Tests;

public class ActivityControllerTests
{
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ActivityBroadcaster _broadcaster = new();
    private readonly ActivityController _controller;

    public ActivityControllerTests()
    {
        _controller = new ActivityController(_broadcaster);
    }

    #region GetRecentActivity

    [Fact]
    public void GetRecentActivity_ReturnsEmptyListWhenNoEvents()
    {
        var result = _controller.GetRecentActivity();
        var events = ExtractOkValue<IReadOnlyList<ActivityEvent>>(result.Result!);
        Assert.Empty(events);
    }

    [Fact]
    public void GetRecentActivity_ReturnsBufferedEvents()
    {
        _broadcaster.Broadcast(MakeEvent("evt-1", "First"));
        _broadcaster.Broadcast(MakeEvent("evt-2", "Second"));

        var result = _controller.GetRecentActivity();
        var events = ExtractOkValue<IReadOnlyList<ActivityEvent>>(result.Result!);
        Assert.Equal(2, events.Count);
        Assert.Equal("evt-1", events[0].Id);
        Assert.Equal("evt-2", events[1].Id);
    }

    [Fact]
    public void GetRecentActivity_IncludesMetadata()
    {
        var meta = new Dictionary<string, object?> { ["sprintId"] = "s1", ["stage"] = "review" };
        _broadcaster.Broadcast(MakeEvent("evt-m", "With metadata", meta));

        var result = _controller.GetRecentActivity();
        var events = ExtractOkValue<IReadOnlyList<ActivityEvent>>(result.Result!);
        Assert.Single(events);
        Assert.NotNull(events[0].Metadata);
        Assert.Equal("s1", events[0].Metadata!["sprintId"]?.ToString());
    }

    #endregion

    #region SSE Stream

    [Fact]
    public async Task GetActivityStream_SetsCorrectHeaders()
    {
        using var cts = new CancellationTokenSource();
        var (httpContext, _) = CreateSseContext();
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Cancel immediately so the stream ends after replay
        cts.Cancel();
        await _controller.GetActivityStream(cts.Token);

        Assert.Equal("text/event-stream", httpContext.Response.Headers.ContentType.ToString());
        Assert.Equal("no-cache", httpContext.Response.Headers.CacheControl.ToString());
        Assert.Equal("keep-alive", httpContext.Response.Headers.Connection.ToString());
        Assert.Equal("no", httpContext.Response.Headers["X-Accel-Buffering"].ToString());
    }

    [Fact]
    public async Task GetActivityStream_ReplaysRecentEventsOnConnect()
    {
        _broadcaster.Broadcast(MakeEvent("evt-1", "First"));
        _broadcaster.Broadcast(MakeEvent("evt-2", "Second"));

        using var cts = new CancellationTokenSource();
        var (httpContext, body) = CreateSseContext();
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Cancel after a brief delay to allow replay to complete
        cts.CancelAfter(200);
        await _controller.GetActivityStream(cts.Token);

        var output = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("evt-1", output);
        Assert.Contains("evt-2", output);
    }

    [Fact]
    public async Task GetActivityStream_SerializesMetadataInCamelCase()
    {
        var meta = new Dictionary<string, object?> { ["SprintId"] = "s1", ["Stage"] = "review" };
        _broadcaster.Broadcast(MakeEvent("evt-m", "With metadata", meta));

        using var cts = new CancellationTokenSource();
        var (httpContext, body) = CreateSseContext();
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        cts.CancelAfter(200);
        await _controller.GetActivityStream(cts.Token);

        var output = Encoding.UTF8.GetString(body.ToArray());
        // The SSE format: "event: activityEvent\ndata: {json}\n\n"
        Assert.Contains("event: activityEvent", output);
        Assert.Contains("\"metadata\":", output);
    }

    [Fact]
    public async Task GetActivityStream_DeliversLiveEventsAfterReplay()
    {
        using var cts = new CancellationTokenSource();
        var body = new SyncMemoryStream();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = body;
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Start the stream in background
        var streamTask = Task.Run(() => _controller.GetActivityStream(cts.Token));

        // Wait for the subscribe to happen (the endpoint subscribes before replay)
        await Task.Delay(100);
        _broadcaster.Broadcast(MakeEvent("live-1", "Live event"));

        // Let it write and flush
        await Task.Delay(200);
        cts.Cancel();
        await streamTask;

        var output = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("live-1", output);
    }

    [Fact]
    public async Task GetActivityStream_LiveEventsIncludeMetadata()
    {
        using var cts = new CancellationTokenSource();
        var body = new SyncMemoryStream();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = body;
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var streamTask = Task.Run(() => _controller.GetActivityStream(cts.Token));

        await Task.Delay(100);
        var meta = new Dictionary<string, object?> { ["action"] = "stage-transition", ["from"] = "draft" };
        _broadcaster.Broadcast(MakeEvent("live-m", "Live with meta", meta));

        await Task.Delay(200);
        cts.Cancel();
        await streamTask;

        var output = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("live-m", output);
        Assert.Contains("stage-transition", output);
    }

    [Fact]
    public async Task GetActivityStream_GracefullyHandlesClientDisconnect()
    {
        using var cts = new CancellationTokenSource();
        var (httpContext, _) = CreateSseContext();
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var streamTask = _controller.GetActivityStream(cts.Token);

        // Simulate client disconnect
        cts.Cancel();
        // Should complete without throwing
        await streamTask;
    }

    [Fact]
    public async Task GetActivityStream_SseFormatHasCorrectStructure()
    {
        _broadcaster.Broadcast(MakeEvent("fmt-1", "Format test"));

        using var cts = new CancellationTokenSource();
        var (httpContext, body) = CreateSseContext();
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        cts.CancelAfter(200);
        await _controller.GetActivityStream(cts.Token);

        var output = Encoding.UTF8.GetString(body.ToArray());
        var lines = output.Split('\n');

        // SSE format: "event: activityEvent\ndata: {json}\n\n"
        var eventLines = lines.Where(l => l.StartsWith("event: ")).ToList();
        var dataLines = lines.Where(l => l.StartsWith("data: ")).ToList();

        Assert.Single(eventLines);
        Assert.Equal("event: activityEvent", eventLines[0]);
        Assert.Single(dataLines);

        // The data line should be valid JSON
        var json = dataLines[0]["data: ".Length..];
        var parsed = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.Equal("fmt-1", parsed.GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetActivityStream_NullMetadataOmittedOrNull()
    {
        _broadcaster.Broadcast(MakeEvent("no-meta", "No metadata"));

        using var cts = new CancellationTokenSource();
        var (httpContext, body) = CreateSseContext();
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        cts.CancelAfter(200);
        await _controller.GetActivityStream(cts.Token);

        var output = Encoding.UTF8.GetString(body.ToArray());
        var dataLine = output.Split('\n').First(l => l.StartsWith("data: "));
        var json = dataLine["data: ".Length..];
        var parsed = JsonSerializer.Deserialize<JsonElement>(json);

        // Metadata should be null (not crash)
        Assert.True(
            !parsed.TryGetProperty("metadata", out var mp) || mp.ValueKind == JsonValueKind.Null,
            "Null metadata should serialize as null or be absent");
    }

    #endregion

    #region JSON Serialization

    [Fact]
    public void Serialization_CamelCasePropertyNames()
    {
        var evt = MakeEvent("ser-1", "Test", new Dictionary<string, object?> { ["key"] = "val" });
        var json = JsonSerializer.Serialize(evt, CamelCase);
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("occurredAt", out _));
        Assert.True(doc.RootElement.TryGetProperty("roomId", out _));
        Assert.True(doc.RootElement.TryGetProperty("actorId", out _));
        Assert.True(doc.RootElement.TryGetProperty("correlationId", out _));
        Assert.True(doc.RootElement.TryGetProperty("metadata", out _));
    }

    [Fact]
    public void Serialization_MetadataPreservesNestedValues()
    {
        var meta = new Dictionary<string, object?>
        {
            ["sprintId"] = "sprint-42",
            ["stage"] = "review",
            ["count"] = 5,
            ["isComplete"] = true,
            ["details"] = null,
        };
        var evt = MakeEvent("nested", "Nested meta", meta);
        var json = JsonSerializer.Serialize(evt, CamelCase);
        var doc = JsonDocument.Parse(json);

        var metaProp = doc.RootElement.GetProperty("metadata");
        Assert.Equal("sprint-42", metaProp.GetProperty("sprintId").GetString());
        Assert.Equal("review", metaProp.GetProperty("stage").GetString());
        Assert.Equal(5, metaProp.GetProperty("count").GetInt32());
        Assert.True(metaProp.GetProperty("isComplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, metaProp.GetProperty("details").ValueKind);
    }

    #endregion

    #region Helpers

    private static ActivityEvent MakeEvent(
        string id, string message, Dictionary<string, object?>? metadata = null)
    {
        return new ActivityEvent(
            Id: id,
            Type: ActivityEventType.RoomCreated,
            Severity: ActivitySeverity.Info,
            RoomId: "room-1",
            ActorId: "agent-1",
            TaskId: null,
            Message: message,
            CorrelationId: null,
            OccurredAt: DateTime.UtcNow,
            Metadata: metadata);
    }

    private static T ExtractOkValue<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return (T)ok.Value!;
    }

    private static (HttpContext, MemoryStream) CreateSseContext()
    {
        var body = new MemoryStream();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = body;
        return (httpContext, body);
    }

    /// <summary>
    /// Thread-safe MemoryStream for use in SSE tests where writes happen on
    /// a background thread and reads happen on the test thread.
    /// </summary>
    private sealed class SyncMemoryStream : MemoryStream
    {
        private readonly object _syncLock = new();

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_syncLock) { base.Write(buffer, offset, count); }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            lock (_syncLock) { base.Write(buffer, offset, count); }
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            lock (_syncLock) { base.Write(buffer.Span); }
            return ValueTask.CompletedTask;
        }

        public override byte[] ToArray()
        {
            lock (_syncLock) { return base.ToArray(); }
        }

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    #endregion
}
