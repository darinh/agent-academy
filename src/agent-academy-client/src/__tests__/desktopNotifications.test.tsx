// @vitest-environment jsdom
/**
 * Tests for useDesktopNotifications hook.
 *
 * Covers: enable/disable toggle, localStorage persistence, permission request,
 * event filtering, document.hidden gating, deduplication, notification creation,
 * auto-close, and click-to-focus behavior.
 */
import "@testing-library/jest-dom/vitest";
import { renderHook, act } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useDesktopNotifications } from "../useDesktopNotifications";
import type { ActivityEvent } from "../api";

// ── Mocks ──────────────────────────────────────────────────────────────

let storageMap: Record<string, string> = {};

beforeEach(() => {
  storageMap = {};
  vi.stubGlobal("localStorage", {
    getItem: (k: string) => storageMap[k] ?? null,
    setItem: (k: string, v: string) => { storageMap[k] = v; },
    removeItem: (k: string) => { delete storageMap[k]; },
  });
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

function makeEvent(overrides: Partial<ActivityEvent> = {}): ActivityEvent {
  return {
    id: `evt-${Math.random().toString(36).slice(2, 8)}`,
    type: "DirectMessageSent",
    severity: "Info",
    message: "Agent sent you a message",
    occurredAt: new Date().toISOString(),
    ...overrides,
  };
}

// ── Permission States ──────────────────────────────────────────────────

describe("useDesktopNotifications", () => {
  describe("when Notification API is unavailable", () => {
    beforeEach(() => {
      // Remove Notification from window
      vi.stubGlobal("Notification", undefined);
    });

    it("reports unsupported and not enabled", () => {
      const { result } = renderHook(() => useDesktopNotifications());
      expect(result.current.supported).toBe(false);
      expect(result.current.permission).toBe("unsupported");
      expect(result.current.enabled).toBe(false);
    });

    it("does not throw when notify is called", () => {
      const { result } = renderHook(() => useDesktopNotifications());
      expect(() => result.current.notify(makeEvent())).not.toThrow();
    });
  });

  describe("when Notification API is available", () => {
    let mockNotification: ReturnType<typeof vi.fn> & { requestPermission: ReturnType<typeof vi.fn> };
    let instances: Array<{ close: ReturnType<typeof vi.fn>; onclick?: (() => void) | null }>;

    beforeEach(() => {
      instances = [];
      const fn = vi.fn().mockImplementation(function (this: { close: ReturnType<typeof vi.fn>; onclick?: (() => void) | null }) {
        this.close = vi.fn();
        this.onclick = null;
        instances.push(this);
      });
      Object.defineProperty(fn, "permission", {
        value: "default",
        writable: true,
        configurable: true,
      });
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (fn as any).requestPermission = vi.fn().mockResolvedValue("granted");
      mockNotification = fn as typeof mockNotification;
      vi.stubGlobal("Notification", mockNotification);
    });

    // ── Enable/Disable ──────────────────────────

    it("defaults to disabled", () => {
      const { result } = renderHook(() => useDesktopNotifications());
      expect(result.current.enabled).toBe(false);
      expect(result.current.supported).toBe(true);
    });

    it("loads enabled state from localStorage", () => {
      storageMap["aa-desktop-notifications"] = "true";
      const { result } = renderHook(() => useDesktopNotifications());
      expect(result.current.enabled).toBe(true);
    });

    it("persists enabled state to localStorage", async () => {
      Object.defineProperty(mockNotification, "permission", { value: "granted" });
      const { result } = renderHook(() => useDesktopNotifications());
      await act(async () => result.current.setEnabled(true));
      expect(storageMap["aa-desktop-notifications"]).toBe("true");
      await act(async () => result.current.setEnabled(false));
      expect(storageMap["aa-desktop-notifications"]).toBe("false");
    });

    // ── Permission Request ──────────────────────

    it("requests permission when enabling and permission is default", async () => {
      const { result } = renderHook(() => useDesktopNotifications());
      await act(async () => result.current.setEnabled(true));
      expect(mockNotification.requestPermission).toHaveBeenCalledOnce();
    });

    it("does not request permission when already granted", async () => {
      Object.defineProperty(mockNotification, "permission", { value: "granted" });
      const { result } = renderHook(() => useDesktopNotifications());
      await act(async () => result.current.setEnabled(true));
      expect(mockNotification.requestPermission).not.toHaveBeenCalled();
    });

    it("stays disabled when permission is denied", async () => {
      mockNotification.requestPermission = vi.fn().mockResolvedValue("denied");
      const { result } = renderHook(() => useDesktopNotifications());
      await act(async () => result.current.setEnabled(true));
      expect(result.current.enabled).toBe(false);
      expect(storageMap["aa-desktop-notifications"]).toBe("false");
    });

    // ── Notification Dispatch ───────────────────

    describe("when enabled and granted", () => {
      beforeEach(() => {
        Object.defineProperty(mockNotification, "permission", { value: "granted" });
        storageMap["aa-desktop-notifications"] = "true";
        // Simulate tab hidden
        Object.defineProperty(document, "hidden", { value: true, configurable: true });
      });

      afterEach(() => {
        Object.defineProperty(document, "hidden", { value: false, configurable: true });
      });

      it("creates notification for DirectMessageSent", () => {
        const { result } = renderHook(() => useDesktopNotifications());
        result.current.notify(makeEvent({ type: "DirectMessageSent" }));
        expect(mockNotification).toHaveBeenCalledOnce();
        expect(mockNotification).toHaveBeenCalledWith("New message", expect.objectContaining({ body: expect.any(String) }));
      });

      it("creates notification for AgentErrorOccurred", () => {
        const { result } = renderHook(() => useDesktopNotifications());
        result.current.notify(makeEvent({ type: "AgentErrorOccurred", message: "Rate limit hit" }));
        expect(mockNotification).toHaveBeenCalledWith("Agent error", expect.objectContaining({ body: "Rate limit hit" }));
      });

      it("creates notification for SubagentFailed", () => {
        const { result } = renderHook(() => useDesktopNotifications());
        result.current.notify(makeEvent({ type: "SubagentFailed" }));
        expect(mockNotification).toHaveBeenCalledOnce();
      });

      it("creates notification for SprintCompleted", () => {
        const { result } = renderHook(() => useDesktopNotifications());
        result.current.notify(makeEvent({ type: "SprintCompleted", message: "Sprint #3 done" }));
        expect(mockNotification).toHaveBeenCalledWith("Sprint completed", expect.objectContaining({ body: "Sprint #3 done" }));
      });

      it("creates notification for SprintCancelled", () => {
        const { result } = renderHook(() => useDesktopNotifications());
        result.current.notify(makeEvent({ type: "SprintCancelled" }));
        expect(mockNotification).toHaveBeenCalledOnce();
      });

      it("creates notification for TaskCreated", () => {
        const { result } = renderHook(() => useDesktopNotifications());
        result.current.notify(makeEvent({ type: "TaskCreated", message: "Implement auth" }));
        expect(mockNotification).toHaveBeenCalledWith("Task created", expect.objectContaining({ body: "Implement auth" }));
      });

      it("creates notification for TaskUnblocked", () => {
        const { result } = renderHook(() => useDesktopNotifications());
        result.current.notify(makeEvent({ type: "TaskUnblocked", message: "Task #5 is now unblocked" }));
        expect(mockNotification).toHaveBeenCalledWith("Task unblocked", expect.objectContaining({ body: "Task #5 is now unblocked" }));
      });

      it("uses event type as body when message is empty", () => {
        const { result } = renderHook(() => useDesktopNotifications());
        result.current.notify(makeEvent({ type: "AgentErrorOccurred", message: "" }));
        expect(mockNotification).toHaveBeenCalledWith("Agent error", expect.objectContaining({ body: "AgentErrorOccurred" }));
      });

      it("uses event type as body when message is undefined", () => {
        const { result } = renderHook(() => useDesktopNotifications());
        result.current.notify(makeEvent({ type: "SprintCompleted", message: undefined }));
        expect(mockNotification).toHaveBeenCalledWith("Sprint completed", expect.objectContaining({ body: "SprintCompleted" }));
      });

      it("sets tag, icon, and silent options", () => {
        const { result } = renderHook(() => useDesktopNotifications());
        result.current.notify(makeEvent({ id: "evt-42", type: "DirectMessageSent" }));
        expect(mockNotification).toHaveBeenCalledWith(
          expect.any(String),
          expect.objectContaining({
            tag: "evt-42",
            icon: "/agent-academy-icon.png",
            silent: false,
          }),
        );
      });

      // ── Event Filtering ───────────────────

      it("ignores events not in the notify set", () => {
        const { result } = renderHook(() => useDesktopNotifications());
        result.current.notify(makeEvent({ type: "AgentThinking" }));
        result.current.notify(makeEvent({ type: "MessagePosted" }));
        result.current.notify(makeEvent({ type: "PhaseChanged" }));
        expect(mockNotification).not.toHaveBeenCalled();
      });

      // ── Tab Visibility ────────────────────

      it("does not notify when tab is visible", () => {
        Object.defineProperty(document, "hidden", { value: false, configurable: true });
        const { result } = renderHook(() => useDesktopNotifications());
        result.current.notify(makeEvent({ type: "DirectMessageSent" }));
        expect(mockNotification).not.toHaveBeenCalled();
      });

      // ── Deduplication ─────────────────────

      it("deduplicates events with the same ID", () => {
        const { result } = renderHook(() => useDesktopNotifications());
        const evt = makeEvent({ id: "same-id", type: "DirectMessageSent" });
        result.current.notify(evt);
        result.current.notify(evt);
        result.current.notify(evt);
        expect(mockNotification).toHaveBeenCalledOnce();
      });

      it("evicts old dedup entries after 100 events", () => {
        const { result } = renderHook(() => useDesktopNotifications());
        // Send 101 unique events to trigger eviction (prunes to last 50)
        for (let i = 0; i < 101; i++) {
          result.current.notify(makeEvent({ id: `evt-${i}`, type: "DirectMessageSent" }));
        }
        expect(mockNotification).toHaveBeenCalledTimes(101);
        // Replay an early event that should have been evicted — should create a new notification
        result.current.notify(makeEvent({ id: "evt-0", type: "DirectMessageSent" }));
        expect(mockNotification).toHaveBeenCalledTimes(102);
        // Replay a recent event that should still be in the set — should be deduped
        result.current.notify(makeEvent({ id: "evt-100", type: "DirectMessageSent" }));
        expect(mockNotification).toHaveBeenCalledTimes(102);
      });

      // ── Click-to-focus ────────────────────

      it("focuses window on notification click", () => {
        const focusSpy = vi.spyOn(window, "focus").mockImplementation(() => {});
        const { result } = renderHook(() => useDesktopNotifications());
        result.current.notify(makeEvent({ type: "DirectMessageSent" }));
        expect(instances).toHaveLength(1);
        instances[0].onclick?.();
        expect(focusSpy).toHaveBeenCalledOnce();
        expect(instances[0].close).toHaveBeenCalledOnce();
      });

      // ── Auto-close ────────────────────────

      it("auto-closes notification after timeout", () => {
        vi.useFakeTimers();
        const { result } = renderHook(() => useDesktopNotifications());
        result.current.notify(makeEvent({ type: "DirectMessageSent" }));
        expect(instances[0].close).not.toHaveBeenCalled();
        vi.advanceTimersByTime(8_000);
        expect(instances[0].close).toHaveBeenCalledOnce();
        vi.useRealTimers();
      });
    });

    // ── Disabled State ──────────────────────────

    it("does not notify when disabled even if granted", () => {
      Object.defineProperty(mockNotification, "permission", { value: "granted" });
      Object.defineProperty(document, "hidden", { value: true, configurable: true });
      const { result } = renderHook(() => useDesktopNotifications());
      result.current.notify(makeEvent({ type: "DirectMessageSent" }));
      expect(mockNotification).not.toHaveBeenCalled();
      Object.defineProperty(document, "hidden", { value: false, configurable: true });
    });

    it("does not enable when browser is unsupported", async () => {
      vi.stubGlobal("Notification", undefined);
      const { result } = renderHook(() => useDesktopNotifications());
      await act(async () => result.current.setEnabled(true));
      expect(result.current.enabled).toBe(false);
      expect(result.current.permission).toBe("unsupported");
    });

    it("stays disabled when requestPermission throws", async () => {
      Object.defineProperty(mockNotification, "permission", { value: "default" });
      mockNotification.requestPermission = vi.fn().mockRejectedValue(new Error("browser error"));
      const { result } = renderHook(() => useDesktopNotifications());
      await act(async () => result.current.setEnabled(true));
      expect(result.current.enabled).toBe(false);
      expect(storageMap["aa-desktop-notifications"]).toBe("false");
    });

    it("applies default title for unmapped event types via PascalCase splitting", () => {
      Object.defineProperty(mockNotification, "permission", { value: "granted" });
      storageMap["aa-desktop-notifications"] = "true";
      Object.defineProperty(document, "hidden", { value: true, configurable: true });
      const { result } = renderHook(() => useDesktopNotifications());
      // Event types not in NOTIFY_EVENT_TYPES are filtered before eventTitle runs.
      // Currently all set members have explicit switch cases, so the default
      // title path (PascalCase split) is unreachable via public API.
      // This test confirms the filter still holds for non-set event types.
      result.current.notify(makeEvent({ type: "AgentThinking" }));
      expect(mockNotification).not.toHaveBeenCalled();
      Object.defineProperty(document, "hidden", { value: false, configurable: true });
    });
  });
});
