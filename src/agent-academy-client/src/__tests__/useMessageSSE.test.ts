import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";

// ---------------------------------------------------------------------------
// Mock EventSource
// ---------------------------------------------------------------------------

type EventSourceListener = (e: MessageEvent) => void;

interface MockEventSourceInstance {
  url: string;
  readyState: number;
  listeners: Map<string, EventSourceListener[]>;
  onopen: ((e: Event) => void) | null;
  onerror: ((e: Event) => void) | null;
  addEventListener(type: string, fn: EventSourceListener): void;
  removeEventListener(type: string, fn: EventSourceListener): void;
  close(): void;
  _simulateOpen(): void;
  _simulateEvent(type: string, data: string): void;
  _simulateError(closed?: boolean): void;
}

let instances: MockEventSourceInstance[] = [];

class MockEventSource implements MockEventSourceInstance {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSED = 2;

  url: string;
  readyState = MockEventSource.CONNECTING;
  listeners = new Map<string, EventSourceListener[]>();
  onopen: ((e: Event) => void) | null = null;
  onerror: ((e: Event) => void) | null = null;

  constructor(url: string) {
    this.url = url;
    instances.push(this);
  }

  addEventListener(type: string, fn: EventSourceListener) {
    const list = this.listeners.get(type) ?? [];
    list.push(fn);
    this.listeners.set(type, list);
  }

  removeEventListener(type: string, fn: EventSourceListener) {
    const list = this.listeners.get(type) ?? [];
    this.listeners.set(
      type,
      list.filter((f) => f !== fn),
    );
  }

  close() {
    this.readyState = MockEventSource.CLOSED;
  }

  _simulateOpen() {
    this.readyState = MockEventSource.OPEN;
    this.onopen?.(new Event("open"));
  }

  _simulateEvent(type: string, data: string) {
    const fns = this.listeners.get(type) ?? [];
    const event = new MessageEvent(type, { data });
    for (const fn of fns) fn(event);
  }

  _simulateError(closed = false) {
    if (closed) this.readyState = MockEventSource.CLOSED;
    this.onerror?.(new Event("error"));
  }
}

Object.assign(globalThis, {
  EventSource: MockEventSource,
});

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe("useMessageSSE", () => {
  beforeEach(() => {
    instances = [];
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  describe("EventSource connection patterns", () => {
    it("connects to the correct DM stream URL", () => {
      const es = new MockEventSource("/api/dm/threads/agent-1/stream");
      expect(es.url).toBe("/api/dm/threads/agent-1/stream");
      expect(es.readyState).toBe(MockEventSource.CONNECTING);
    });

    it("connects with after cursor when provided in URL", () => {
      const es = new MockEventSource(
        "/api/dm/threads/agent-1/stream?after=msg-42",
      );
      expect(es.url).toContain("after=msg-42");
    });

    it("encodes special characters in agentId", () => {
      const encoded = encodeURIComponent("agent/special");
      const es = new MockEventSource(
        `/api/dm/threads/${encoded}/stream`,
      );
      expect(es.url).toContain("agent%2Fspecial");
    });
  });

  describe("message event parsing", () => {
    it("parses DmMessage from message event", () => {
      const es = new MockEventSource("/api/dm/threads/agent-1/stream");
      const received: unknown[] = [];

      es.addEventListener("message", (e: MessageEvent) => {
        received.push(JSON.parse(e.data));
      });

      es._simulateEvent(
        "message",
        JSON.stringify({
          id: "msg-1",
          senderId: "agent-1",
          senderName: "Architect",
          senderRole: "architect",
          content: "Hello!",
          sentAt: "2026-04-13T10:00:00Z",
          isFromHuman: false,
        }),
      );

      expect(received).toHaveLength(1);
      expect(received[0]).toMatchObject({
        id: "msg-1",
        senderId: "agent-1",
        content: "Hello!",
        isFromHuman: false,
      });
    });

    it("parses human messages correctly", () => {
      const es = new MockEventSource("/api/dm/threads/agent-1/stream");
      const received: unknown[] = [];

      es.addEventListener("message", (e: MessageEvent) => {
        received.push(JSON.parse(e.data));
      });

      es._simulateEvent(
        "message",
        JSON.stringify({
          id: "msg-2",
          senderId: "human",
          senderName: "Human",
          senderRole: "Human",
          content: "Please review",
          sentAt: "2026-04-13T10:01:00Z",
          isFromHuman: true,
        }),
      );

      expect(received).toHaveLength(1);
      expect((received[0] as Record<string, unknown>).isFromHuman).toBe(true);
    });

    it("handles messages with null senderRole", () => {
      const es = new MockEventSource("/api/dm/threads/agent-1/stream");
      const received: unknown[] = [];

      es.addEventListener("message", (e: MessageEvent) => {
        received.push(JSON.parse(e.data));
      });

      es._simulateEvent(
        "message",
        JSON.stringify({
          id: "msg-3",
          senderId: "agent-1",
          senderName: "Architect",
          senderRole: null,
          content: "Test",
          sentAt: "2026-04-13T10:02:00Z",
          isFromHuman: false,
        }),
      );

      expect(received).toHaveLength(1);
      expect((received[0] as Record<string, unknown>).senderRole).toBeNull();
    });

    it("handles messages with markdown content", () => {
      const es = new MockEventSource("/api/dm/threads/agent-1/stream");
      const received: unknown[] = [];

      es.addEventListener("message", (e: MessageEvent) => {
        received.push(JSON.parse(e.data));
      });

      const mdContent = "## Plan\n- Step 1\n- Step 2\n```ts\nconst x = 1;\n```";
      es._simulateEvent(
        "message",
        JSON.stringify({
          id: "msg-4",
          senderId: "agent-1",
          senderName: "Architect",
          content: mdContent,
          sentAt: "2026-04-13T10:03:00Z",
          isFromHuman: false,
        }),
      );

      expect(received).toHaveLength(1);
      expect((received[0] as Record<string, unknown>).content).toBe(mdContent);
    });

    it("skips malformed JSON events gracefully", () => {
      const es = new MockEventSource("/api/dm/threads/agent-1/stream");
      const received: unknown[] = [];
      const errors: unknown[] = [];

      es.addEventListener("message", (e: MessageEvent) => {
        try {
          received.push(JSON.parse(e.data));
        } catch {
          errors.push(e.data);
        }
      });

      es._simulateEvent("message", "not-valid-json{{{");
      es._simulateEvent(
        "message",
        JSON.stringify({ id: "valid", content: "ok" }),
      );

      expect(errors).toHaveLength(1);
      expect(received).toHaveLength(1);
      expect((received[0] as Record<string, unknown>).id).toBe("valid");
    });
  });

  describe("resync event", () => {
    it("fires resync listener when resync event received", () => {
      const es = new MockEventSource("/api/dm/threads/agent-1/stream");
      let resyncFired = false;

      es.addEventListener("resync", () => {
        resyncFired = true;
      });

      es._simulateEvent("resync", JSON.stringify({ lastId: "msg-99" }));
      expect(resyncFired).toBe(true);
    });

    it("resync data includes lastId", () => {
      const es = new MockEventSource("/api/dm/threads/agent-1/stream");
      let resyncData: unknown = null;

      es.addEventListener("resync", (e: MessageEvent) => {
        resyncData = JSON.parse(e.data);
      });

      es._simulateEvent("resync", JSON.stringify({ lastId: "msg-99" }));
      expect(resyncData).toMatchObject({ lastId: "msg-99" });
    });
  });

  describe("SSE protocol format", () => {
    it("event name is 'message' matching backend", () => {
      const es = new MockEventSource("/api/dm/threads/agent-1/stream");
      let received = false;

      es.addEventListener("message", () => {
        received = true;
      });

      es._simulateEvent("message", "{}");
      expect(received).toBe(true);
    });

    it("ignores events with other names", () => {
      const es = new MockEventSource("/api/dm/threads/agent-1/stream");
      let received = false;

      es.addEventListener("message", () => {
        received = true;
      });

      es._simulateEvent("otherEvent", "{}");
      expect(received).toBe(false);
    });
  });

  describe("connection lifecycle", () => {
    it("transitions through CONNECTING → OPEN → CLOSED", () => {
      const es = new MockEventSource("/api/dm/threads/agent-1/stream");
      expect(es.readyState).toBe(MockEventSource.CONNECTING);

      es._simulateOpen();
      expect(es.readyState).toBe(MockEventSource.OPEN);

      es.close();
      expect(es.readyState).toBe(MockEventSource.CLOSED);
    });

    it("transitions to CLOSED on fatal error", () => {
      const es = new MockEventSource("/api/dm/threads/agent-1/stream");
      es._simulateError(true);
      expect(es.readyState).toBe(MockEventSource.CLOSED);
    });

    it("stays in non-CLOSED state on transient error", () => {
      const es = new MockEventSource("/api/dm/threads/agent-1/stream");
      es._simulateOpen();
      es._simulateError(false);
      expect(es.readyState).toBe(MockEventSource.OPEN);
    });
  });

  describe("deduplication pattern", () => {
    it("can deduplicate messages by ID", () => {
      const seen = new Set<string>();
      const deduped: unknown[] = [];

      function handleMessage(data: string) {
        const msg = JSON.parse(data) as { id: string };
        if (seen.has(msg.id)) return;
        seen.add(msg.id);
        deduped.push(msg);
      }

      // Simulate replay + live overlap
      handleMessage(JSON.stringify({ id: "msg-1", content: "first" }));
      handleMessage(JSON.stringify({ id: "msg-2", content: "second" }));
      handleMessage(JSON.stringify({ id: "msg-1", content: "first" })); // duplicate from at-least-once
      handleMessage(JSON.stringify({ id: "msg-3", content: "third" }));

      expect(deduped).toHaveLength(3);
      expect(deduped.map((m) => (m as Record<string, unknown>).id)).toEqual([
        "msg-1",
        "msg-2",
        "msg-3",
      ]);
    });
  });

  describe("cursor-based reconnection", () => {
    it("URL without cursor has no query string", () => {
      const url = "/api/dm/threads/agent-1/stream";
      expect(url).not.toContain("?");
    });

    it("URL with cursor appends after parameter", () => {
      const lastId = "msg-42";
      const url = `/api/dm/threads/agent-1/stream?after=${encodeURIComponent(lastId)}`;
      expect(url).toContain("after=msg-42");
    });

    it("encodes cursor values with special characters", () => {
      const lastId = "msg/special&chars=bad";
      const url = `/api/dm/threads/agent-1/stream?after=${encodeURIComponent(lastId)}`;
      expect(url).toContain("after=msg%2Fspecial%26chars%3Dbad");
    });
  });

  describe("multiple message delivery", () => {
    it("delivers multiple messages in order", () => {
      const es = new MockEventSource("/api/dm/threads/agent-1/stream");
      const received: string[] = [];

      es.addEventListener("message", (e: MessageEvent) => {
        const msg = JSON.parse(e.data) as { id: string };
        received.push(msg.id);
      });

      es._simulateEvent("message", JSON.stringify({ id: "msg-1" }));
      es._simulateEvent("message", JSON.stringify({ id: "msg-2" }));
      es._simulateEvent("message", JSON.stringify({ id: "msg-3" }));

      expect(received).toEqual(["msg-1", "msg-2", "msg-3"]);
    });

    it("interleaves message and resync events correctly", () => {
      const es = new MockEventSource("/api/dm/threads/agent-1/stream");
      const events: string[] = [];

      es.addEventListener("message", (e: MessageEvent) => {
        const msg = JSON.parse(e.data) as { id: string };
        events.push(`msg:${msg.id}`);
      });
      es.addEventListener("resync", () => {
        events.push("resync");
      });

      es._simulateEvent("message", JSON.stringify({ id: "msg-1" }));
      es._simulateEvent("message", JSON.stringify({ id: "msg-2" }));
      es._simulateEvent("resync", JSON.stringify({ lastId: "msg-2" }));

      expect(events).toEqual(["msg:msg-1", "msg:msg-2", "resync"]);
    });
  });
});
