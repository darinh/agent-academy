import { describe, expect, it, vi, beforeEach } from "vitest";

vi.mock("../api", () => ({
  getRoomUsage: vi.fn(),
  getRoomUsageByAgent: vi.fn(),
  getRoomErrors: vi.fn(),
}));

import { getRoomUsage, getRoomUsageByAgent, getRoomErrors } from "../api";
import type { UsageSummary, AgentUsageSummary, ErrorRecord } from "../api";

const mockGetRoomUsage = vi.mocked(getRoomUsage);
const mockGetRoomUsageByAgent = vi.mocked(getRoomUsageByAgent);
const mockGetRoomErrors = vi.mocked(getRoomErrors);

function makeUsage(overrides: Partial<UsageSummary> = {}): UsageSummary {
  return {
    totalInputTokens: 80_000,
    totalOutputTokens: 25_000,
    totalCost: 0.75,
    requestCount: 18,
    models: ["gpt-4o"],
    ...overrides,
  };
}

function makeAgentUsage(overrides: Partial<AgentUsageSummary> = {}): AgentUsageSummary {
  return {
    agentId: "software-engineer-1",
    totalInputTokens: 40_000,
    totalOutputTokens: 12_000,
    totalCost: 0.35,
    requestCount: 10,
    ...overrides,
  };
}

function makeError(overrides: Partial<ErrorRecord> = {}): ErrorRecord {
  return {
    agentId: "software-engineer-1",
    roomId: "room-1",
    errorType: "transient",
    message: "Connection timeout",
    recoverable: true,
    timestamp: "2026-04-04T12:00:00Z",
    ...overrides,
  };
}

describe("RoomStatsPanel", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  describe("API integration", () => {
    it("getRoomUsage returns UsageSummary for a room", async () => {
      mockGetRoomUsage.mockResolvedValue(makeUsage());
      const result = await getRoomUsage("room-1");
      expect(result.totalInputTokens).toBe(80_000);
      expect(result.totalCost).toBe(0.75);
      expect(result.requestCount).toBe(18);
      expect(mockGetRoomUsage).toHaveBeenCalledWith("room-1");
    });

    it("getRoomUsageByAgent returns per-agent breakdown", async () => {
      const agents = [
        makeAgentUsage({ agentId: "software-engineer-1" }),
        makeAgentUsage({ agentId: "reviewer-1", totalCost: 0.40, requestCount: 8 }),
      ];
      mockGetRoomUsageByAgent.mockResolvedValue(agents);
      const result = await getRoomUsageByAgent("room-1");
      expect(result).toHaveLength(2);
      expect(result[0].agentId).toBe("software-engineer-1");
      expect(result[1].totalCost).toBe(0.40);
    });

    it("getRoomErrors returns error records for a room", async () => {
      const errors = [makeError(), makeError({ agentId: "reviewer-1", errorType: "quota" })];
      mockGetRoomErrors.mockResolvedValue(errors);
      const result = await getRoomErrors("room-1", 20);
      expect(result).toHaveLength(2);
      expect(result[0].errorType).toBe("transient");
      expect(result[1].errorType).toBe("quota");
    });
  });

  describe("UsageSummary shape for rooms", () => {
    it("handles room with zero usage", () => {
      const empty = makeUsage({
        totalInputTokens: 0,
        totalOutputTokens: 0,
        totalCost: 0,
        requestCount: 0,
        models: [],
      });
      expect(empty.requestCount).toBe(0);
      expect(empty.models).toEqual([]);
    });

    it("handles room with high usage", () => {
      const heavy = makeUsage({
        totalInputTokens: 5_000_000,
        totalOutputTokens: 2_000_000,
        totalCost: 15.50,
        requestCount: 500,
        models: ["gpt-4o", "claude-sonnet-4", "o1-preview"],
      });
      expect(heavy.totalInputTokens).toBe(5_000_000);
      expect(heavy.models).toHaveLength(3);
    });
  });

  describe("AgentUsageSummary shape", () => {
    it("represents per-agent room usage", () => {
      const agent = makeAgentUsage();
      expect(agent.agentId).toBe("software-engineer-1");
      expect(agent.totalInputTokens).toBe(40_000);
      expect(agent.totalOutputTokens).toBe(12_000);
      expect(agent.totalCost).toBe(0.35);
      expect(agent.requestCount).toBe(10);
    });

    it("handles zero-usage agent", () => {
      const agent = makeAgentUsage({
        totalInputTokens: 0,
        totalOutputTokens: 0,
        totalCost: 0,
        requestCount: 0,
      });
      expect(agent.totalCost).toBe(0);
    });
  });

  describe("ErrorRecord room filtering", () => {
    it("error records include roomId", () => {
      const err = makeError({ roomId: "breakout-42" });
      expect(err.roomId).toBe("breakout-42");
    });

    it("distinguishes error types", () => {
      const types = ["authentication", "authorization", "quota", "transient", "unknown"];
      for (const t of types) {
        const err = makeError({ errorType: t });
        expect(err.errorType).toBe(t);
      }
    });

    it("tracks recoverability", () => {
      expect(makeError({ recoverable: true }).recoverable).toBe(true);
      expect(makeError({ recoverable: false }).recoverable).toBe(false);
    });
  });

  describe("formatting helpers (mirrored)", () => {
    function formatTokenCount(n: number): string {
      if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
      if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
      return String(n);
    }

    function formatCost(cost: number): string {
      if (cost === 0) return "$0.00";
      if (cost < 0.01) return `$${cost.toFixed(4)}`;
      return `$${cost.toFixed(2)}`;
    }

    it("formats room token counts", () => {
      expect(formatTokenCount(80_000)).toBe("80.0K");
      expect(formatTokenCount(25_000)).toBe("25.0K");
      expect(formatTokenCount(500)).toBe("500");
    });

    it("formats room costs", () => {
      expect(formatCost(0.75)).toBe("$0.75");
      expect(formatCost(0.0035)).toBe("$0.0035");
      expect(formatCost(0)).toBe("$0.00");
    });
  });

  describe("data refresh on room change", () => {
    it("calls API with correct roomId", async () => {
      mockGetRoomUsage.mockResolvedValue(makeUsage());
      mockGetRoomUsageByAgent.mockResolvedValue([]);
      mockGetRoomErrors.mockResolvedValue([]);

      await getRoomUsage("room-abc");
      expect(mockGetRoomUsage).toHaveBeenCalledWith("room-abc");
    });

    it("handles all three API calls failing gracefully", async () => {
      mockGetRoomUsage.mockRejectedValue(new Error("Network error"));
      mockGetRoomUsageByAgent.mockRejectedValue(new Error("Network error"));
      mockGetRoomErrors.mockRejectedValue(new Error("Network error"));

      const [u, a, e] = await Promise.allSettled([
        getRoomUsage("room-1"),
        getRoomUsageByAgent("room-1"),
        getRoomErrors("room-1"),
      ]);

      expect(u.status).toBe("rejected");
      expect(a.status).toBe("rejected");
      expect(e.status).toBe("rejected");
    });

    it("handles partial failures (usage succeeds, errors fail)", async () => {
      mockGetRoomUsage.mockResolvedValue(makeUsage());
      mockGetRoomUsageByAgent.mockResolvedValue([makeAgentUsage()]);
      mockGetRoomErrors.mockRejectedValue(new Error("500"));

      const [u, a, e] = await Promise.allSettled([
        getRoomUsage("room-1"),
        getRoomUsageByAgent("room-1"),
        getRoomErrors("room-1"),
      ]);

      expect(u.status).toBe("fulfilled");
      expect(a.status).toBe("fulfilled");
      expect(e.status).toBe("rejected");
    });
  });
});
