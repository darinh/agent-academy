// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { renderHook, act } from "@testing-library/react";
import type { ActivityEvent } from "../api";

type Handler = (...args: unknown[]) => void;

interface MockConnection {
  state: string;
  handlers: Map<string, Handler[]>;
  reconnectingCb: Handler | null;
  reconnectedCb: Handler | null;
  closeCb: Handler | null;
  start: ReturnType<typeof vi.fn>;
  stop: ReturnType<typeof vi.fn>;
  on(event: string, handler: Handler): void;
  onreconnecting(cb: Handler): void;
  onreconnected(cb: Handler): void;
  onclose(cb: Handler): void;
  _emit(event: string, ...args: unknown[]): void;
  _triggerReconnecting(): void;
  _triggerReconnected(): void;
  _triggerClose(): void;
}

const { mockConnections, createMockConnection, MockHubConnectionBuilder } =
  vi.hoisted(() => {
    const mockConnections: MockConnection[] = [];

    function createMockConnection(): MockConnection {
      const conn: MockConnection = {
        state: "Disconnected",
        handlers: new Map(),
        reconnectingCb: null,
        reconnectedCb: null,
        closeCb: null,
        start: vi.fn().mockImplementation(async () => {
          conn.state = "Connected";
        }),
        stop: vi.fn().mockImplementation(async () => {
          conn.state = "Disconnected";
        }),
        on(event: string, handler: Handler) {
          const list = conn.handlers.get(event) ?? [];
          list.push(handler);
          conn.handlers.set(event, list);
        },
        onreconnecting(cb: Handler) { conn.reconnectingCb = cb; },
        onreconnected(cb: Handler) { conn.reconnectedCb = cb; },
        onclose(cb: Handler) { conn.closeCb = cb; },
        _emit(event: string, ...args: unknown[]) {
          for (const h of conn.handlers.get(event) ?? []) h(...args);
        },
        _triggerReconnecting() { conn.reconnectingCb?.(); },
        _triggerReconnected() {
          conn.state = "Connected";
          conn.reconnectedCb?.();
        },
        _triggerClose() {
          conn.state = "Disconnected";
          conn.closeCb?.();
        },
      };
      mockConnections.push(conn);
      return conn;
    }

    // Must be a class (not arrow fn) so it works with `new`
    class MockHubConnectionBuilder {
      _url = "";
      withUrl(url: string) {
        this._url = url;
        return this;
      }
      withAutomaticReconnect() { return this; }
      configureLogging() { return this; }
      build() { return createMockConnection(); }
    }

    return { mockConnections, createMockConnection, MockHubConnectionBuilder };
  });

vi.mock("@microsoft/signalr", () => ({
  HubConnectionBuilder: MockHubConnectionBuilder,
  HubConnectionState: {
    Disconnected: "Disconnected",
    Connecting: "Connecting",
    Connected: "Connected",
    Disconnecting: "Disconnecting",
    Reconnecting: "Reconnecting",
  },
  LogLevel: { Warning: 3 },
}));

import { useActivityHub } from "../useActivityHub";

beforeEach(() => {
  mockConnections.length = 0;
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

describe("useActivityHub", () => {
  describe("connection lifecycle", () => {
    it("returns 'connecting' initially and 'connected' after start", async () => {
      const { result } = renderHook(() => useActivityHub(vi.fn()));

      expect(result.current).toBe("connecting");

      await act(async () => { await vi.runAllTimersAsync(); });

      expect(result.current).toBe("connected");
    });

    it("returns 'disconnected' when not enabled", () => {
      const { result } = renderHook(() => useActivityHub(vi.fn(), false));
      expect(result.current).toBe("disconnected");
    });

    it("stops connection on unmount", async () => {
      const { unmount } = renderHook(() => useActivityHub(vi.fn()));

      await act(async () => { await vi.runAllTimersAsync(); });

      const conn = mockConnections[0];
      unmount();
      expect(conn.stop).toHaveBeenCalled();
    });

    it("does not create connection when disabled", () => {
      renderHook(() => useActivityHub(vi.fn(), false));
      expect(mockConnections).toHaveLength(0);
    });
  });

  describe("event handling", () => {
    it("registers an activityEvent handler", async () => {
      renderHook(() => useActivityHub(vi.fn()));

      await act(async () => { await vi.runAllTimersAsync(); });

      expect(mockConnections[0].handlers.has("activityEvent")).toBe(true);
    });

    it("forwards activityEvent to onEvent callback", async () => {
      const onEvent = vi.fn();
      renderHook(() => useActivityHub(onEvent));

      await act(async () => { await vi.runAllTimersAsync(); });

      const evt: ActivityEvent = {
        id: "evt-1",
        type: "RoomCreated",
        severity: "Info",
        message: "Room created",
        timestamp: "2026-04-01T00:00:00Z",
        metadata: null,
      };

      act(() => { mockConnections[0]._emit("activityEvent", evt); });

      expect(onEvent).toHaveBeenCalledWith(evt);
    });

    it("uses latest onEvent ref (no stale closures)", async () => {
      const onEvent1 = vi.fn();
      const onEvent2 = vi.fn();

      const { rerender } = renderHook(
        ({ cb }) => useActivityHub(cb),
        { initialProps: { cb: onEvent1 } },
      );

      await act(async () => { await vi.runAllTimersAsync(); });

      rerender({ cb: onEvent2 });

      act(() => {
        mockConnections[0]._emit("activityEvent", { id: "e1" } as ActivityEvent);
      });

      expect(onEvent1).not.toHaveBeenCalled();
      expect(onEvent2).toHaveBeenCalledWith({ id: "e1" });
    });
  });

  describe("reconnection status", () => {
    it("reports 'reconnecting' status", async () => {
      const { result } = renderHook(() => useActivityHub(vi.fn()));

      await act(async () => { await vi.runAllTimersAsync(); });
      expect(result.current).toBe("connected");

      act(() => { mockConnections[0]._triggerReconnecting(); });
      expect(result.current).toBe("reconnecting");
    });

    it("reports 'connected' after successful reconnection", async () => {
      const { result } = renderHook(() => useActivityHub(vi.fn()));

      await act(async () => { await vi.runAllTimersAsync(); });

      act(() => { mockConnections[0]._triggerReconnecting(); });
      expect(result.current).toBe("reconnecting");

      act(() => { mockConnections[0]._triggerReconnected(); });
      expect(result.current).toBe("connected");
    });

    it("reports 'disconnected' after close", async () => {
      const { result } = renderHook(() => useActivityHub(vi.fn()));

      await act(async () => { await vi.runAllTimersAsync(); });

      act(() => { mockConnections[0]._triggerClose(); });
      expect(result.current).toBe("disconnected");
    });
  });

  describe("initial connection retry", () => {
    it("retries on failure with backoff", async () => {
      let callCount = 0;

      const origBuild = MockHubConnectionBuilder.prototype.build;
      MockHubConnectionBuilder.prototype.build = function () {
        const conn = createMockConnection();
        conn.start = vi.fn().mockImplementation(async () => {
          callCount++;
          if (callCount <= 2) throw new Error("Network error");
          conn.state = "Connected";
        });
        return conn;
      };

      const { result } = renderHook(() => useActivityHub(vi.fn()));

      // Let initial attempt + first retry (delay[0]=0) fire
      await act(async () => { await vi.advanceTimersByTimeAsync(0); });
      const afterFirstBatch = callCount;
      expect(afterFirstBatch).toBeGreaterThanOrEqual(1);

      // Advance past retry delay (delay[1]=2000ms)
      await act(async () => { await vi.advanceTimersByTimeAsync(5000); });

      // Should have retried at least once more
      expect(callCount).toBeGreaterThan(afterFirstBatch);
      expect(result.current).toBe("connected");

      MockHubConnectionBuilder.prototype.build = origBuild;
    });
  });
});
