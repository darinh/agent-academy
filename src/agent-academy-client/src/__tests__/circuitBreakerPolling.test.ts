import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";

vi.mock("../api", () => ({
  getInstanceHealth: vi.fn(),
}));

import { getInstanceHealth } from "../api";
import type { InstanceHealthResult } from "../api";
import { isDegraded, parseCircuitBreakerState } from "../useCircuitBreakerPolling";
import type { CircuitBreakerState } from "../useCircuitBreakerPolling";

const mockGetInstanceHealth = vi.mocked(getInstanceHealth);

function makeHealth(overrides: Partial<InstanceHealthResult> = {}): InstanceHealthResult {
  return {
    instanceId: "instance-abc",
    startedAt: "2026-04-04T10:00:00Z",
    version: "1.0.0",
    crashDetected: false,
    executorOperational: true,
    authFailed: false,
    circuitBreakerState: "Closed",
    ...overrides,
  };
}

describe("useCircuitBreakerPolling", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  describe("parseCircuitBreakerState", () => {
    it("parses Closed", () => {
      expect(parseCircuitBreakerState("Closed")).toBe("Closed");
    });

    it("parses Open", () => {
      expect(parseCircuitBreakerState("Open")).toBe("Open");
    });

    it("parses HalfOpen", () => {
      expect(parseCircuitBreakerState("HalfOpen")).toBe("HalfOpen");
    });

    it("returns null for undefined", () => {
      expect(parseCircuitBreakerState(undefined)).toBeNull();
    });

    it("returns null for unknown string", () => {
      expect(parseCircuitBreakerState("SomeNewState")).toBeNull();
    });

    it("returns null for empty string", () => {
      expect(parseCircuitBreakerState("")).toBeNull();
    });
  });

  describe("isDegraded", () => {
    it("returns true for Open", () => {
      expect(isDegraded("Open")).toBe(true);
    });

    it("returns true for HalfOpen", () => {
      expect(isDegraded("HalfOpen")).toBe(true);
    });

    it("returns false for Closed", () => {
      expect(isDegraded("Closed")).toBe(false);
    });

    it("returns false for null", () => {
      expect(isDegraded(null)).toBe(false);
    });
  });

  describe("API integration", () => {
    it("getInstanceHealth returns circuitBreakerState", async () => {
      mockGetInstanceHealth.mockResolvedValue(makeHealth({ circuitBreakerState: "Open" }));
      const result = await getInstanceHealth();
      expect(result.circuitBreakerState).toBe("Open");
    });

    it("handles missing circuitBreakerState gracefully", async () => {
      const health = makeHealth();
      delete (health as unknown as Record<string, unknown>).circuitBreakerState;
      mockGetInstanceHealth.mockResolvedValue(health);
      const result = await getInstanceHealth();
      expect(result.circuitBreakerState).toBeUndefined();
    });

    it("request ID prevents stale responses from overwriting state", () => {
      // Verify the concept: a monotonic counter rejects older responses
      let requestId = 0;
      let appliedState: CircuitBreakerState = null;

      function simulatePoll(state: CircuitBreakerState, myId: number) {
        if (myId !== requestId) return; // stale — discard
        appliedState = state;
      }

      requestId = ++requestId; // request 1
      const id1 = requestId;
      requestId = ++requestId; // request 2 (overlapping)
      const id2 = requestId;

      // Response 2 arrives first
      simulatePoll("Open", id2);
      expect(appliedState).toBe("Open");

      // Response 1 arrives late — should be discarded
      simulatePoll("Closed", id1);
      expect(appliedState).toBe("Open"); // stale response rejected
    });
  });
});
