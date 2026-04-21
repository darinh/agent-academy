// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { renderHook, act } from "@testing-library/react";
import { useDmThreadSSE } from "../useDmThreadSSE";

// ---------------------------------------------------------------------------
// Mock EventSource (same pattern as useMessageSSE tests)
// ---------------------------------------------------------------------------

type EventSourceListener = (e: MessageEvent) => void;

interface MockEventSourceInstance {
  url: string;
  readyState: number;
  withCredentials: boolean;
  listeners: Map<string, EventSourceListener[]>;
  onopen: ((e: Event) => void) | null;
  onerror: ((e: Event) => void) | null;
  addEventListener(type: string, fn: EventSourceListener): void;
  removeEventListener(type: string, fn: EventSourceListener): void;
  close(): void;
  _simulateEvent(type: string, data?: string): void;
  _simulateError(): void;
}

let instances: MockEventSourceInstance[] = [];

class MockEventSource implements MockEventSourceInstance {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSED = 2;

  url: string;
  withCredentials: boolean;
  readyState = MockEventSource.CONNECTING;
  listeners = new Map<string, EventSourceListener[]>();
  onopen: ((e: Event) => void) | null = null;
  onerror: ((e: Event) => void) | null = null;

  constructor(url: string, opts?: { withCredentials?: boolean }) {
    this.url = url;
    this.withCredentials = opts?.withCredentials ?? false;
    instances.push(this);
  }

  addEventListener(type: string, fn: EventSourceListener) {
    const list = this.listeners.get(type) ?? [];
    list.push(fn);
    this.listeners.set(type, list);
  }

  removeEventListener(type: string, fn: EventSourceListener) {
    const list = this.listeners.get(type) ?? [];
    this.listeners.set(type, list.filter((f) => f !== fn));
  }

  close() {
    this.readyState = MockEventSource.CLOSED;
  }

  _simulateEvent(type: string, data = "{}") {
    const fns = this.listeners.get(type) ?? [];
    const event = new MessageEvent(type, { data });
    for (const fn of fns) fn(event);
  }

  _simulateError() {
    this.readyState = MockEventSource.CLOSED;
    this.onerror?.(new Event("error"));
  }
}

// @ts-expect-error -- mock for tests
globalThis.EventSource = MockEventSource;

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe("useDmThreadSSE", () => {
  beforeEach(() => {
    instances = [];
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("connects to /api/dm/threads/stream", () => {
    const onInvalidate = vi.fn();
    renderHook(() => useDmThreadSSE(onInvalidate));

    expect(instances).toHaveLength(1);
    expect(instances[0].url).toContain("/api/dm/threads/stream");
  });

  it("uses withCredentials", () => {
    const onInvalidate = vi.fn();
    renderHook(() => useDmThreadSSE(onInvalidate));

    expect(instances[0].withCredentials).toBe(true);
  });

  it("returns 'connecting' initially", () => {
    const onInvalidate = vi.fn();
    const { result } = renderHook(() => useDmThreadSSE(onInvalidate));

    expect(result.current).toBe("connecting");
  });

  it("returns 'connected' after connected event", () => {
    const onInvalidate = vi.fn();
    const { result } = renderHook(() => useDmThreadSSE(onInvalidate));

    act(() => {
      instances[0]._simulateEvent("connected");
    });

    expect(result.current).toBe("connected");
  });

  it("calls onInvalidate on thread-updated event", () => {
    const onInvalidate = vi.fn();
    renderHook(() => useDmThreadSSE(onInvalidate));

    act(() => {
      instances[0]._simulateEvent("connected");
    });
    onInvalidate.mockClear();

    act(() => {
      instances[0]._simulateEvent("thread-updated", '{"agentId":"agent-1"}');
    });

    expect(onInvalidate).toHaveBeenCalledTimes(1);
  });

  it("calls onInvalidate on resync event", () => {
    const onInvalidate = vi.fn();
    renderHook(() => useDmThreadSSE(onInvalidate));

    act(() => {
      instances[0]._simulateEvent("connected");
    });
    onInvalidate.mockClear();

    act(() => {
      instances[0]._simulateEvent("resync");
    });

    expect(onInvalidate).toHaveBeenCalledTimes(1);
  });

  it("returns 'disconnected' when disabled", () => {
    const onInvalidate = vi.fn();
    const { result } = renderHook(() =>
      useDmThreadSSE(onInvalidate, false),
    );

    expect(result.current).toBe("disconnected");
    expect(instances).toHaveLength(0);
  });

  it("does not create EventSource when disabled", () => {
    const onInvalidate = vi.fn();
    renderHook(() => useDmThreadSSE(onInvalidate, false));

    expect(instances).toHaveLength(0);
  });

  it("reconnects on error with backoff", () => {
    const onInvalidate = vi.fn();
    const { result } = renderHook(() => useDmThreadSSE(onInvalidate));

    // First connection
    expect(instances).toHaveLength(1);

    // Simulate error
    act(() => {
      instances[0]._simulateError();
    });

    // Should attempt immediate reconnect (delay 0 for first retry)
    act(() => {
      vi.advanceTimersByTime(100);
    });

    expect(instances).toHaveLength(2);
    expect(result.current).toBe("connecting");
  });

  it("returns 'reconnecting' on second+ error", () => {
    const onInvalidate = vi.fn();
    const { result } = renderHook(() => useDmThreadSSE(onInvalidate));

    // First error
    act(() => {
      instances[0]._simulateError();
    });

    act(() => {
      vi.advanceTimersByTime(100);
    });

    // Second error
    act(() => {
      instances[1]._simulateError();
    });

    expect(result.current).toBe("reconnecting");
  });

  it("closes EventSource on unmount", () => {
    const onInvalidate = vi.fn();
    const { unmount } = renderHook(() => useDmThreadSSE(onInvalidate));

    const es = instances[0];
    unmount();

    expect(es.readyState).toBe(MockEventSource.CLOSED);
  });

  it("resets attempt counter on successful connection", () => {
    const onInvalidate = vi.fn();
    const { result } = renderHook(() => useDmThreadSSE(onInvalidate));

    // First error + reconnect
    act(() => {
      instances[0]._simulateError();
    });
    act(() => {
      vi.advanceTimersByTime(100);
    });

    // Successful reconnection
    act(() => {
      instances[1]._simulateEvent("connected");
    });

    expect(result.current).toBe("connected");

    // Another error — should use attempt=0 delay (immediate), not attempt=2
    act(() => {
      instances[1]._simulateError();
    });
    act(() => {
      vi.advanceTimersByTime(100);
    });

    expect(instances).toHaveLength(3);
  });

  it("handles multiple thread-updated events", () => {
    const onInvalidate = vi.fn();
    renderHook(() => useDmThreadSSE(onInvalidate));

    act(() => {
      instances[0]._simulateEvent("connected");
    });

    // Reset count after the initial connected invalidation
    onInvalidate.mockClear();

    act(() => {
      instances[0]._simulateEvent("thread-updated", '{"agentId":"agent-1"}');
      instances[0]._simulateEvent("thread-updated", '{"agentId":"agent-2"}');
      instances[0]._simulateEvent("thread-updated", '{"agentId":"agent-3"}');
    });

    expect(onInvalidate).toHaveBeenCalledTimes(3);
  });

  it("triggers onInvalidate on connected event to catch missed updates", () => {
    const onInvalidate = vi.fn();
    renderHook(() => useDmThreadSSE(onInvalidate));

    act(() => {
      instances[0]._simulateEvent("connected");
    });

    expect(onInvalidate).toHaveBeenCalledTimes(1);
  });

  it("triggers onInvalidate on reconnect to catch missed updates", () => {
    const onInvalidate = vi.fn();
    renderHook(() => useDmThreadSSE(onInvalidate));

    // First connect
    act(() => {
      instances[0]._simulateEvent("connected");
    });
    onInvalidate.mockClear();

    // Disconnect
    act(() => {
      instances[0]._simulateError();
    });

    // Reconnect
    act(() => {
      vi.advanceTimersByTime(100);
    });

    act(() => {
      instances[1]._simulateEvent("connected");
    });

    // Should have triggered a refetch on reconnect
    expect(onInvalidate).toHaveBeenCalledTimes(1);
  });
});
