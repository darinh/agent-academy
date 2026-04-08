import { describe, expect, it, vi, beforeEach } from "vitest";

// Mock api module before importing healthCheck
vi.mock("../api", () => ({
  getInstanceHealth: vi.fn(),
}));

import { getInstanceHealth } from "../api";
import { evaluateReconnect, RECONNECTING_BANNER } from "../healthCheck";
import type { InstanceHealthResult } from "../api";

const mockGetInstanceHealth = vi.mocked(getInstanceHealth);

function makeHealth(overrides: Partial<InstanceHealthResult> = {}): InstanceHealthResult {
  return {
    instanceId: "instance-abc",
    startedAt: "2026-04-04T10:00:00Z",
    version: "1.0.0",
    crashDetected: false,
    executorOperational: true,
    authFailed: false,
    ...overrides,
  };
}

describe("healthCheck", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  describe("RECONNECTING_BANNER", () => {
    it("has reconnecting tone", () => {
      expect(RECONNECTING_BANNER.tone).toBe("reconnecting");
    });
  });

  describe("evaluateReconnect", () => {
    it("returns resume-success when instance ID unchanged", async () => {
      mockGetInstanceHealth.mockResolvedValue(makeHealth({ instanceId: "same-id" }));

      const result = await evaluateReconnect("same-id");

      expect(result.state).toBe("resume-success");
      expect(result.banner).toBeNull();
      expect(result.health?.instanceId).toBe("same-id");
    });

    it("returns instance-mismatch when instance ID changed", async () => {
      mockGetInstanceHealth.mockResolvedValue(makeHealth({ instanceId: "new-id" }));

      const result = await evaluateReconnect("old-id");

      expect(result.state).toBe("instance-mismatch");
      expect(result.banner?.tone).toBe("syncing");
      expect(result.health?.instanceId).toBe("new-id");
    });

    it("returns crash-recovered when crashDetected is true", async () => {
      mockGetInstanceHealth.mockResolvedValue(
        makeHealth({ instanceId: "new-id", crashDetected: true }),
      );

      const result = await evaluateReconnect("old-id");

      expect(result.state).toBe("crash-recovered");
      expect(result.banner?.tone).toBe("crash");
    });

    it("crash-recovered takes priority over instance-mismatch", async () => {
      mockGetInstanceHealth.mockResolvedValue(
        makeHealth({ instanceId: "new-id", crashDetected: true }),
      );

      const result = await evaluateReconnect("old-id");

      // crashDetected should be reported even if instance also changed
      expect(result.state).toBe("crash-recovered");
    });

    it("returns resume-success when previousInstanceId is null (first connect)", async () => {
      mockGetInstanceHealth.mockResolvedValue(makeHealth({ instanceId: "first-id" }));

      const result = await evaluateReconnect(null);

      expect(result.state).toBe("resume-success");
      expect(result.banner).toBeNull();
    });

    it("returns refresh-failed when health check throws", async () => {
      mockGetInstanceHealth.mockRejectedValue(new Error("Network error"));

      const result = await evaluateReconnect("old-id");

      expect(result.state).toBe("refresh-failed");
      expect(result.banner?.tone).toBe("error");
      expect(result.health).toBeNull();
    });
  });
});
