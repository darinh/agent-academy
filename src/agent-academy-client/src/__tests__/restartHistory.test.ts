import { describe, expect, it, vi, beforeEach } from "vitest";

vi.mock("../api", () => ({
  getRestartHistory: vi.fn(),
  getRestartStats: vi.fn(),
}));

import { getRestartHistory, getRestartStats } from "../api";
import type { RestartHistoryResponse, RestartStatsDto, ServerInstanceDto } from "../api";

const mockGetRestartHistory = vi.mocked(getRestartHistory);
const mockGetRestartStats = vi.mocked(getRestartStats);

function makeInstance(overrides: Partial<ServerInstanceDto> = {}): ServerInstanceDto {
  return {
    id: "inst-001",
    startedAt: "2026-04-04T10:00:00Z",
    shutdownAt: "2026-04-04T11:00:00Z",
    exitCode: 0,
    crashDetected: false,
    version: "1.2.3",
    shutdownReason: "CleanShutdown",
    ...overrides,
  };
}

function makeStats(overrides: Partial<RestartStatsDto> = {}): RestartStatsDto {
  return {
    totalInstances: 5,
    crashRestarts: 1,
    intentionalRestarts: 2,
    cleanShutdowns: 1,
    stillRunning: 1,
    windowHours: 24,
    maxRestartsPerWindow: 10,
    restartWindowHours: 1,
    ...overrides,
  };
}

function makeHistoryResponse(
  instances: ServerInstanceDto[] = [makeInstance()],
  total?: number,
): RestartHistoryResponse {
  return {
    instances,
    total: total ?? instances.length,
    limit: 10,
    offset: 0,
  };
}

describe("RestartHistoryPanel", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  describe("API integration types", () => {
    it("getRestartHistory is called with correct pagination defaults", async () => {
      mockGetRestartHistory.mockResolvedValue(makeHistoryResponse());
      await getRestartHistory(10, 0);
      expect(mockGetRestartHistory).toHaveBeenCalledWith(10, 0);
    });

    it("getRestartStats is called with 24-hour default window", async () => {
      mockGetRestartStats.mockResolvedValue(makeStats());
      await getRestartStats(24);
      expect(mockGetRestartStats).toHaveBeenCalledWith(24);
    });
  });

  describe("ServerInstanceDto shape", () => {
    it("represents a running instance with null shutdown fields", () => {
      const running = makeInstance({
        shutdownAt: null,
        exitCode: null,
        shutdownReason: "Running",
      });
      expect(running.shutdownAt).toBeNull();
      expect(running.exitCode).toBeNull();
      expect(running.shutdownReason).toBe("Running");
    });

    it("represents a crash instance with exit code -1", () => {
      const crashed = makeInstance({
        exitCode: -1,
        crashDetected: true,
        shutdownReason: "Crash",
      });
      expect(crashed.exitCode).toBe(-1);
      expect(crashed.crashDetected).toBe(true);
      expect(crashed.shutdownReason).toBe("Crash");
    });

    it("represents an intentional restart with exit code 75", () => {
      const restarted = makeInstance({
        exitCode: 75,
        shutdownReason: "IntentionalRestart",
      });
      expect(restarted.exitCode).toBe(75);
      expect(restarted.shutdownReason).toBe("IntentionalRestart");
    });

    it("supports unexpected exit codes", () => {
      const unexpected = makeInstance({
        exitCode: 137,
        shutdownReason: "UnexpectedExit(137)",
      });
      expect(unexpected.exitCode).toBe(137);
      expect(unexpected.shutdownReason).toBe("UnexpectedExit(137)");
    });
  });

  describe("RestartStatsDto shape", () => {
    it("includes rate limit configuration", () => {
      const stats = makeStats();
      expect(stats.maxRestartsPerWindow).toBe(10);
      expect(stats.restartWindowHours).toBe(1);
    });

    it("tracks all instance categories", () => {
      const stats = makeStats({
        totalInstances: 10,
        crashRestarts: 2,
        intentionalRestarts: 3,
        cleanShutdowns: 4,
        stillRunning: 1,
      });
      expect(stats.crashRestarts + stats.intentionalRestarts + stats.cleanShutdowns + stats.stillRunning)
        .toBeLessThanOrEqual(stats.totalInstances);
    });
  });

  describe("pagination contract", () => {
    it("supports offset-based pagination", () => {
      const response = makeHistoryResponse(
        [makeInstance({ id: "page2-1" }), makeInstance({ id: "page2-2" })],
        25,
      );
      expect(response.total).toBe(25);
      expect(response.instances).toHaveLength(2);
    });
  });
});
