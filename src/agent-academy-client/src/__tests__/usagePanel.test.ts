import { describe, expect, it, vi, beforeEach } from "vitest";
import { formatTokenCount, formatCost } from "../panelUtils";

vi.mock("../api", () => ({
  getGlobalUsage: vi.fn(),
  getGlobalUsageRecords: vi.fn(),
}));

import { getGlobalUsage, getGlobalUsageRecords } from "../api";
import type { UsageSummary, LlmUsageRecord } from "../api";

const mockGetGlobalUsage = vi.mocked(getGlobalUsage);
const mockGetGlobalUsageRecords = vi.mocked(getGlobalUsageRecords);

function makeSummary(overrides: Partial<UsageSummary> = {}): UsageSummary {
  return {
    totalInputTokens: 150_000,
    totalOutputTokens: 50_000,
    totalCost: 1.23,
    requestCount: 42,
    models: ["gpt-4o", "claude-sonnet-4"],
    ...overrides,
  };
}

function makeRecord(overrides: Partial<LlmUsageRecord> = {}): LlmUsageRecord {
  return {
    id: "rec-001",
    agentId: "architect",
    roomId: "room-1",
    model: "gpt-4o",
    inputTokens: 3_000,
    outputTokens: 1_200,
    cacheReadTokens: 500,
    cacheWriteTokens: 200,
    cost: 0.015,
    durationMs: 2340,
    reasoningEffort: null,
    recordedAt: "2026-04-04T12:00:00Z",
    ...overrides,
  };
}

describe("UsagePanel", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  describe("API integration types", () => {
    it("getGlobalUsage is callable with no args", async () => {
      mockGetGlobalUsage.mockResolvedValue(makeSummary());
      const result = await getGlobalUsage();
      expect(result.totalInputTokens).toBe(150_000);
      expect(result.totalOutputTokens).toBe(50_000);
      expect(result.totalCost).toBe(1.23);
      expect(result.requestCount).toBe(42);
      expect(result.models).toEqual(["gpt-4o", "claude-sonnet-4"]);
    });

    it("getGlobalUsage accepts hoursBack parameter", async () => {
      mockGetGlobalUsage.mockResolvedValue(makeSummary({ requestCount: 10 }));
      const result = await getGlobalUsage(24);
      expect(mockGetGlobalUsage).toHaveBeenCalledWith(24);
      expect(result.requestCount).toBe(10);
    });

    it("getGlobalUsageRecords returns array of records", async () => {
      const records = [makeRecord(), makeRecord({ id: "rec-002", agentId: "engineer" })];
      mockGetGlobalUsageRecords.mockResolvedValue(records);
      const result = await getGlobalUsageRecords();
      expect(result).toHaveLength(2);
      expect(result[0].agentId).toBe("architect");
      expect(result[1].agentId).toBe("engineer");
    });

    it("getGlobalUsageRecords accepts agentId filter", async () => {
      mockGetGlobalUsageRecords.mockResolvedValue([makeRecord()]);
      await getGlobalUsageRecords("architect", 50);
      expect(mockGetGlobalUsageRecords).toHaveBeenCalledWith("architect", 50);
    });
  });

  describe("UsageSummary shape", () => {
    it("handles zero usage", () => {
      const empty = makeSummary({
        totalInputTokens: 0,
        totalOutputTokens: 0,
        totalCost: 0,
        requestCount: 0,
        models: [],
      });
      expect(empty.totalInputTokens).toBe(0);
      expect(empty.models).toEqual([]);
    });

    it("handles large token counts", () => {
      const large = makeSummary({
        totalInputTokens: 12_345_678,
        totalOutputTokens: 4_567_890,
        totalCost: 45.67,
        requestCount: 1_234,
      });
      expect(large.totalInputTokens).toBe(12_345_678);
    });
  });

  describe("LlmUsageRecord shape", () => {
    it("represents a record with all fields", () => {
      const rec = makeRecord();
      expect(rec.id).toBe("rec-001");
      expect(rec.agentId).toBe("architect");
      expect(rec.roomId).toBe("room-1");
      expect(rec.model).toBe("gpt-4o");
      expect(rec.inputTokens).toBe(3_000);
      expect(rec.outputTokens).toBe(1_200);
      expect(rec.cacheReadTokens).toBe(500);
      expect(rec.cacheWriteTokens).toBe(200);
      expect(rec.cost).toBe(0.015);
      expect(rec.durationMs).toBe(2340);
    });

    it("handles nullable fields", () => {
      const rec = makeRecord({
        roomId: null,
        model: null,
        cost: null,
        durationMs: null,
        reasoningEffort: null,
      });
      expect(rec.roomId).toBeNull();
      expect(rec.model).toBeNull();
      expect(rec.cost).toBeNull();
      expect(rec.durationMs).toBeNull();
    });

    it("handles records with reasoning effort", () => {
      const rec = makeRecord({ reasoningEffort: "high" });
      expect(rec.reasoningEffort).toBe("high");
    });
  });

  describe("per-agent aggregation logic", () => {
    it("groups records by agentId and sums metrics", () => {
      const records = [
        makeRecord({ agentId: "architect", inputTokens: 1000, outputTokens: 500, cost: 0.01 }),
        makeRecord({ id: "rec-002", agentId: "architect", inputTokens: 2000, outputTokens: 800, cost: 0.02 }),
        makeRecord({ id: "rec-003", agentId: "engineer", inputTokens: 3000, outputTokens: 1000, cost: 0.03 }),
      ];

      // Simulate the aggregation logic from UsagePanel
      const map = new Map<string, { input: number; output: number; cost: number; count: number }>();
      for (const r of records) {
        const entry = map.get(r.agentId) ?? { input: 0, output: 0, cost: 0, count: 0 };
        entry.input += r.inputTokens;
        entry.output += r.outputTokens;
        entry.cost += r.cost ?? 0;
        entry.count += 1;
        map.set(r.agentId, entry);
      }

      const breakdown = [...map.entries()]
        .map(([agentId, stats]) => ({ agentId, ...stats }))
        .sort((a, b) => b.cost - a.cost || b.count - a.count);

      expect(breakdown).toHaveLength(2);
      expect(breakdown[0].agentId).toBe("architect");
      expect(breakdown[0].input).toBe(3000);
      expect(breakdown[0].output).toBe(1300);
      expect(breakdown[0].cost).toBeCloseTo(0.03);
      expect(breakdown[0].count).toBe(2);

      expect(breakdown[1].agentId).toBe("engineer");
      expect(breakdown[1].input).toBe(3000);
      expect(breakdown[1].cost).toBeCloseTo(0.03);
      expect(breakdown[1].count).toBe(1);
    });

    it("handles null cost in aggregation", () => {
      const records = [
        makeRecord({ agentId: "agent-a", cost: null }),
        makeRecord({ id: "rec-002", agentId: "agent-a", cost: 0.05 }),
      ];

      const map = new Map<string, { cost: number; count: number }>();
      for (const r of records) {
        const entry = map.get(r.agentId) ?? { cost: 0, count: 0 };
        entry.cost += r.cost ?? 0;
        entry.count += 1;
        map.set(r.agentId, entry);
      }

      const result = map.get("agent-a")!;
      expect(result.cost).toBeCloseTo(0.05);
      expect(result.count).toBe(2);
    });
  });

  describe("formatting helpers (imported from panelUtils)", () => {
    it("formats small token counts as plain numbers", () => {
      expect(formatTokenCount(0)).toBe("0");
      expect(formatTokenCount(999)).toBe("999");
    });

    it("formats thousands as K", () => {
      expect(formatTokenCount(1_000)).toBe("1.0K");
      expect(formatTokenCount(42_500)).toBe("42.5K");
      expect(formatTokenCount(999_900)).toBe("999.9K");
    });

    it("formats millions as M", () => {
      expect(formatTokenCount(1_000_000)).toBe("1.0M");
      expect(formatTokenCount(12_345_678)).toBe("12.3M");
    });

    it("formats zero cost", () => {
      expect(formatCost(0)).toBe("$0.00");
    });

    it("formats small costs with 4 decimal places", () => {
      expect(formatCost(0.0012)).toBe("$0.0012");
      expect(formatCost(0.0099)).toBe("$0.0099");
    });

    it("formats normal costs with 2 decimal places", () => {
      expect(formatCost(1.23)).toBe("$1.23");
      expect(formatCost(45.6)).toBe("$45.60");
    });
  });
});
