using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Tests;

public sealed class DmControllerTests : IDisposable
{
    private readonly TestServiceGraph _svc;
    private readonly DmController _controller;

    private static readonly AgentDefinition TestAgent = new(
        Id: "test-agent",
        Name: "Test Agent",
        Role: "Engineer",
        Summary: "A helpful test agent",
        StartupPrompt: "You are a test agent.",
        Model: null,
        CapabilityTags: [],
        EnabledTools: [],
        AutoJoinDefaultRoom: true,
        Permissions: null);

    public DmControllerTests()
    {
        _svc = new TestServiceGraph([TestAgent]);

        _controller = new DmController(
            _svc.MessageService, _svc.RoomService, _svc.MessageBroadcaster, _svc.Catalog,
            _svc.Orchestrator, NullLogger<DmController>.Instance);

        SetUser(isConsultant: false);

        // Seed a default room so SendMessage can resolve context
        _svc.Db.Rooms.Add(new RoomEntity
        {
            Id = "main", Name = "Main Room", Status = "Active",
            CurrentPhase = "Intake", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            WorkspacePath = "/tmp/test"
        });
        _svc.Db.SaveChanges();
    }

    public void Dispose() => _svc.Dispose();

    private void SetUser(bool isConsultant, string? login = null)
    {
        var nameClaim = login ?? (isConsultant ? "consultant" : "human");
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, nameClaim),
        };
        if (isConsultant)
            claims.Add(new Claim(ClaimTypes.Role, "Consultant"));

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"))
            }
        };
    }

    // ── GetThreads ───────────────────────────────────────────────

    [Fact]
    public async Task GetThreads_Empty_ReturnsEmptyList()
    {
        var result = await _controller.GetThreads();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var threads = Assert.IsType<List<DmThreadSummary>>(ok.Value);
        Assert.Empty(threads);
    }

    // ── GetThreadMessages ────────────────────────────────────────

    [Fact]
    public async Task GetThreadMessages_UnknownAgent_ReturnsNotFound()
    {
        var result = await _controller.GetThreadMessages("nonexistent");
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetThreadMessages_ValidAgent_ReturnsEmptyForNoMessages()
    {
        var result = await _controller.GetThreadMessages("test-agent");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var messages = Assert.IsType<List<DmMessage>>(ok.Value);
        Assert.Empty(messages);
    }

    // ── SendMessage ──────────────────────────────────────────────

    [Fact]
    public async Task SendMessage_NullBody_ReturnsBadRequest()
    {
        var result = await _controller.SendMessage("test-agent", null!);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SendMessage_EmptyMessage_ReturnsBadRequest()
    {
        var result = await _controller.SendMessage("test-agent",
            new SendDmRequest(""));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SendMessage_UnknownAgent_ReturnsNotFound()
    {
        var result = await _controller.SendMessage("nonexistent",
            new SendDmRequest("Hello"));
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task SendMessage_AsHuman_UsesGitHubLoginAsSenderName()
    {
        SetUser(isConsultant: false, login: "octocat");

        var result = await _controller.SendMessage("test-agent",
            new SendDmRequest("Hello agent"));

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, obj.StatusCode);

        var msg = Assert.IsType<DmMessage>(obj.Value);
        // senderId stays as the canonical "human" principal so DM threading
        // (HumanSideSenderIds) and IsFromHuman classification still work.
        Assert.Equal("human", msg.SenderId);
        // senderName carries the GitHub login so the agent sees a real identity.
        Assert.Equal("octocat", msg.SenderName);
        Assert.Equal("Human", msg.SenderRole);
        Assert.Equal("Hello agent", msg.Content);
        Assert.True(msg.IsFromHuman);
    }

    [Fact]
    public async Task SendMessage_Unauthenticated_FallsBackToGenericHuman()
    {
        // Replace the default authenticated test user with an anonymous one
        // (the constructor calls SetUser; override that here).
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity()) // no auth type → IsAuthenticated == false
            }
        };

        var result = await _controller.SendMessage("test-agent",
            new SendDmRequest("Hello agent"));

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, obj.StatusCode);

        var msg = Assert.IsType<DmMessage>(obj.Value);
        Assert.Equal("human", msg.SenderId);
        Assert.Equal("Human", msg.SenderName);
        Assert.True(msg.IsFromHuman);
    }

    [Fact]
    public async Task SendMessage_AsConsultant_UsesConsultantIdentity()
    {
        SetUser(isConsultant: true);

        var result = await _controller.SendMessage("test-agent",
            new SendDmRequest("Consultant message"));

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, obj.StatusCode);

        var msg = Assert.IsType<DmMessage>(obj.Value);
        Assert.Equal("consultant", msg.SenderId);
        Assert.Equal("Consultant", msg.SenderName);
    }

    [Fact]
    public async Task SendMessage_AgentIdIsCaseInsensitive()
    {
        var result = await _controller.SendMessage("TEST-AGENT",
            new SendDmRequest("Case test"));

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, obj.StatusCode);
    }

    [Fact]
    public async Task SendMessage_MessageAppearsInThread()
    {
        await _controller.SendMessage("test-agent",
            new SendDmRequest("Thread message"));

        var threadResult = await _controller.GetThreadMessages("test-agent");
        var ok = Assert.IsType<OkObjectResult>(threadResult.Result);
        var messages = Assert.IsType<List<DmMessage>>(ok.Value);
        Assert.Single(messages);
        Assert.Equal("Thread message", messages[0].Content);
    }

    // ── DM Broadcast Integration ────────────────────────────────

    [Fact]
    public async Task SendMessage_BroadcastsDmToSubscribers()
    {
        DmMessage? received = null;
        _svc.MessageBroadcaster.SubscribeDm("test-agent", msg => received = msg);

        await _controller.SendMessage("test-agent",
            new SendDmRequest("Broadcast test"));

        Assert.NotNull(received);
        Assert.Equal("Broadcast test", received.Content);
        Assert.True(received.IsFromHuman);
        Assert.Equal("human", received.SenderId);
    }

    [Fact]
    public async Task SendMessage_ConsultantBroadcastsDmWithCorrectIdentity()
    {
        SetUser(isConsultant: true);
        DmMessage? received = null;
        _svc.MessageBroadcaster.SubscribeDm("test-agent", msg => received = msg);

        await _controller.SendMessage("test-agent",
            new SendDmRequest("Consultant broadcast"));

        Assert.NotNull(received);
        Assert.Equal("consultant", received.SenderId);
        Assert.True(received.IsFromHuman);
    }

    // ── GetThreadListStream (SSE) ───────────────────────────────

    private static DmMessage MakeDm(string agentId, string id = "dm-1") =>
        new(id, "human", "Human", "Human", "hello", DateTime.UtcNow, true);

    private void SetSseContext(Stream body)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = body;
        var existingUser = _controller.ControllerContext.HttpContext?.User;
        if (existingUser != null) httpContext.User = existingUser;
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    /// <summary>
    /// Waits until the global DM subscriber count reaches the expected value,
    /// replacing racy Task.Delay-based synchronization.
    /// </summary>
    private async Task WaitForSubscribers(int expected, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (_svc.MessageBroadcaster.GetGlobalDmSubscriberCount() < expected)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Subscriber count did not reach {expected} within {timeoutMs}ms " +
                    $"(actual: {_svc.MessageBroadcaster.GetGlobalDmSubscriberCount()})");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task GetThreadListStream_SetsCorrectSseHeaders()
    {
        using var cts = new CancellationTokenSource();
        var body = new SyncMemoryStream();
        SetSseContext(body);

        cts.Cancel();
        await _controller.GetThreadListStream(cts.Token);

        var headers = _controller.Response.Headers;
        Assert.Equal("text/event-stream", headers.ContentType.ToString());
        Assert.Equal("no-cache", headers.CacheControl.ToString());
        Assert.Equal("keep-alive", headers.Connection.ToString());
        Assert.Equal("no", headers["X-Accel-Buffering"].ToString());
    }

    [Fact]
    public async Task GetThreadListStream_SendsConnectedEventOnStart()
    {
        using var cts = new CancellationTokenSource();
        var body = new SyncMemoryStream();
        SetSseContext(body);

        cts.CancelAfter(500);
        await _controller.GetThreadListStream(cts.Token);

        var output = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("event: connected", output);
        Assert.Contains("data: {}", output);
    }

    [Fact]
    public async Task GetThreadListStream_DeliversThreadUpdatedEvent()
    {
        using var cts = new CancellationTokenSource();
        var body = new SyncMemoryStream();
        SetSseContext(body);

        var countBefore = _svc.MessageBroadcaster.GetGlobalDmSubscriberCount();
        var streamTask = Task.Run(() => _controller.GetThreadListStream(cts.Token));
        await WaitForSubscribers(countBefore + 1);

        _svc.MessageBroadcaster.BroadcastDm("test-agent", MakeDm("test-agent", "dm-sse-1"));

        await Task.Delay(100);
        cts.Cancel();
        await streamTask;

        var output = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("event: thread-updated", output);
        Assert.Contains("dm-sse-1", output);
    }

    [Fact]
    public async Task GetThreadListStream_ThreadUpdatedEventContainsAgentId()
    {
        using var cts = new CancellationTokenSource();
        var body = new SyncMemoryStream();
        SetSseContext(body);

        var countBefore = _svc.MessageBroadcaster.GetGlobalDmSubscriberCount();
        var streamTask = Task.Run(() => _controller.GetThreadListStream(cts.Token));
        await WaitForSubscribers(countBefore + 1);

        _svc.MessageBroadcaster.BroadcastDm("test-agent", MakeDm("test-agent", "dm-sse-2"));

        await Task.Delay(100);
        cts.Cancel();
        await streamTask;

        var output = Encoding.UTF8.GetString(body.ToArray());
        var dataLines = output.Split('\n').Where(l => l.StartsWith("data: ") && l.Contains("agentId")).ToList();
        Assert.NotEmpty(dataLines);

        var json = dataLines[0]["data: ".Length..];
        var parsed = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.Equal("test-agent", parsed.GetProperty("agentId").GetString());
        Assert.Equal("dm-sse-2", parsed.GetProperty("messageId").GetString());
    }

    [Fact]
    public async Task GetThreadListStream_DeliversMultipleEvents()
    {
        using var cts = new CancellationTokenSource();
        var body = new SyncMemoryStream();
        SetSseContext(body);

        var countBefore = _svc.MessageBroadcaster.GetGlobalDmSubscriberCount();
        var streamTask = Task.Run(() => _controller.GetThreadListStream(cts.Token));
        await WaitForSubscribers(countBefore + 1);

        _svc.MessageBroadcaster.BroadcastDm("test-agent", MakeDm("test-agent", "dm-m-1"));
        _svc.MessageBroadcaster.BroadcastDm("test-agent", MakeDm("test-agent", "dm-m-2"));

        await Task.Delay(100);
        cts.Cancel();
        await streamTask;

        var output = Encoding.UTF8.GetString(body.ToArray());
        var threadUpdated = output.Split('\n').Count(l => l.StartsWith("event: thread-updated"));
        Assert.Equal(2, threadUpdated);
        Assert.Contains("dm-m-1", output);
        Assert.Contains("dm-m-2", output);
    }

    [Fact]
    public async Task GetThreadListStream_EventsFromDifferentAgentsIncludeCorrectId()
    {
        using var cts = new CancellationTokenSource();
        var body = new SyncMemoryStream();
        SetSseContext(body);

        var countBefore = _svc.MessageBroadcaster.GetGlobalDmSubscriberCount();
        var streamTask = Task.Run(() => _controller.GetThreadListStream(cts.Token));
        await WaitForSubscribers(countBefore + 1);

        _svc.MessageBroadcaster.BroadcastDm("test-agent", MakeDm("test-agent", "dm-a1"));
        _svc.MessageBroadcaster.BroadcastDm("agent-other", MakeDm("agent-other", "dm-a2"));

        await Task.Delay(100);
        cts.Cancel();
        await streamTask;

        var output = Encoding.UTF8.GetString(body.ToArray());
        var dataLines = output.Split('\n').Where(l => l.StartsWith("data: ") && l.Contains("agentId")).ToList();
        Assert.Equal(2, dataLines.Count);

        var json1 = JsonSerializer.Deserialize<JsonElement>(dataLines[0]["data: ".Length..]);
        var json2 = JsonSerializer.Deserialize<JsonElement>(dataLines[1]["data: ".Length..]);
        Assert.Equal("test-agent", json1.GetProperty("agentId").GetString());
        Assert.Equal("agent-other", json2.GetProperty("agentId").GetString());
    }

    [Fact]
    public async Task GetThreadListStream_UsesMessageIdAsSseId()
    {
        using var cts = new CancellationTokenSource();
        var body = new SyncMemoryStream();
        SetSseContext(body);

        var countBefore = _svc.MessageBroadcaster.GetGlobalDmSubscriberCount();
        var streamTask = Task.Run(() => _controller.GetThreadListStream(cts.Token));
        await WaitForSubscribers(countBefore + 1);

        _svc.MessageBroadcaster.BroadcastDm("test-agent", MakeDm("test-agent", "dm-id-test"));

        await Task.Delay(100);
        cts.Cancel();
        await streamTask;

        var output = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("id: dm-id-test", output);
    }

    [Fact]
    public async Task GetThreadListStream_GracefullyHandlesClientDisconnect()
    {
        using var cts = new CancellationTokenSource();
        var body = new SyncMemoryStream();
        SetSseContext(body);

        var streamTask = _controller.GetThreadListStream(cts.Token);

        cts.Cancel();
        await streamTask;
    }

    [Fact]
    public async Task GetThreadListStream_UnsubscribesOnDisconnect()
    {
        using var cts = new CancellationTokenSource();
        var body = new SyncMemoryStream();
        SetSseContext(body);

        var countBefore = _svc.MessageBroadcaster.GetGlobalDmSubscriberCount();

        var streamTask = Task.Run(() => _controller.GetThreadListStream(cts.Token));
        await WaitForSubscribers(countBefore + 1);

        Assert.Equal(countBefore + 1, _svc.MessageBroadcaster.GetGlobalDmSubscriberCount());

        cts.Cancel();
        await streamTask;

        Assert.Equal(countBefore, _svc.MessageBroadcaster.GetGlobalDmSubscriberCount());
    }

    [Fact]
    public async Task GetThreadListStream_SseFormatHasCorrectStructure()
    {
        using var cts = new CancellationTokenSource();
        var body = new SyncMemoryStream();
        SetSseContext(body);

        var countBefore = _svc.MessageBroadcaster.GetGlobalDmSubscriberCount();
        var streamTask = Task.Run(() => _controller.GetThreadListStream(cts.Token));
        await WaitForSubscribers(countBefore + 1);

        _svc.MessageBroadcaster.BroadcastDm("test-agent", MakeDm("test-agent", "dm-fmt"));

        await Task.Delay(100);
        cts.Cancel();
        await streamTask;

        var output = Encoding.UTF8.GetString(body.ToArray());
        var events = output.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("connected", events[0]);
        Assert.Contains("thread-updated", events[1]);
        Assert.Contains("id: dm-fmt", events[1]);

        var dataLine = events[1].Split('\n').First(l => l.StartsWith("data: "));
        var json = dataLine["data: ".Length..];
        var parsed = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.Equal("test-agent", parsed.GetProperty("agentId").GetString());
    }

    [Fact]
    public async Task GetThreadListStream_OverflowSendsResyncEvent()
    {
        // The endpoint uses a bounded channel of 256. When TryWrite fails,
        // it sets overflowed=true and completes the channel. After the reader
        // drains, a "resync" event is emitted.
        //
        // To trigger overflow: use a SlowWriteStream that blocks on each write,
        // preventing the reader from draining the channel while we flood it.
        using var cts = new CancellationTokenSource(5000);
        var body = new SlowWriteStream();
        SetSseContext(body);

        var countBefore = _svc.MessageBroadcaster.GetGlobalDmSubscriberCount();
        var streamTask = Task.Run(() => _controller.GetThreadListStream(cts.Token));
        await WaitForSubscribers(countBefore + 1);

        // Unblock the "connected" event write so the stream enters ReadAllAsync
        body.UnblockOne();  // "event: connected..." write
        body.UnblockOne();  // FlushAsync after connected
        await Task.Delay(50);

        // Now flood: channel is 256, send 260 messages.
        // The reader is blocked in WriteAsync (SlowWriteStream holds it),
        // so the channel fills up and TryWrite returns false.
        for (var i = 0; i < 260; i++)
            _svc.MessageBroadcaster.BroadcastDm("test-agent", MakeDm("test-agent", $"flood-{i}"));

        // Unblock all remaining writes so the stream can finish
        body.UnblockAll();
        await streamTask;

        var output = Encoding.UTF8.GetString(body.GetAllBytes());
        Assert.Contains("event: resync", output);
    }

    /// <summary>
    /// Thread-safe MemoryStream for SSE tests where writes happen on a background
    /// thread and reads happen on the test thread.
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

    /// <summary>
    /// A MemoryStream that blocks each write until explicitly unblocked, allowing tests
    /// to control the pace at which the SSE response is consumed. This creates backpressure
    /// on the channel reader, enabling the overflow/resync path to be tested.
    /// </summary>
    private sealed class SlowWriteStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private readonly object _syncLock = new();
        private readonly SemaphoreSlim _gate = new(0);
        private volatile bool _unblocked;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void UnblockOne() => _gate.Release();

        public void UnblockAll()
        {
            _unblocked = true;
            // Release enough permits to wake any thread already awaiting the gate
            _gate.Release(1000);
        }

        public byte[] GetAllBytes()
        {
            lock (_syncLock) { return _inner.ToArray(); }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (!_unblocked) await _gate.WaitAsync(ct);
            lock (_syncLock) { _inner.Write(buffer, offset, count); }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (!_unblocked) await _gate.WaitAsync(ct);
            lock (_syncLock) { _inner.Write(buffer.Span); }
        }

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_syncLock) { _inner.Write(buffer, offset, count); }
        }
    }
}
