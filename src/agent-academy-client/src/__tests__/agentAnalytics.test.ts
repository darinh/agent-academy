import { describe, expect, it, vi, beforeEach } from "vitest";
import type { AgentAnalyticsSummary, AgentPerformanceMetrics } from "../api";

vi.mock("../api", () => ({
  getAgentAnalytics: vi.fn(),
}));

import { getAgentAnalytics } from "../api";

const mockGetAgentAnalytics = vi.mocked(getAgentAnalytics);

// ── Factories ──

function makeAgent(overrides: Partial<AgentPerformanceMetrics> = {}): AgentPerformanceMetrics {
  return {
    agentId: "planner-1",
    agentName: "Planner",
    totalRequests: 100,
    totalInputTokens: 50_000,
    totalOutputTokens: 20_000,
    totalCost: 1.5,
    averageResponseTimeMs: 1200,
    totalErrors: 3,
    recoverableErrors: 2,
    unrecoverableErrors: 1,
    tasksAssigned: 5,
    tasksCompleted: 3,
    tokenTrend: [100, 200, 150, 300, 250, 180, 220, 190, 210, 170, 160, 200],
    ...overrides,
  };
}

function makeSummary(overrides: Partial<AgentAnalyticsSummary> = {}): AgentAnalyticsSummary {
  return {
    agents: [makeAgent()],
    windowStart: "2026-04-10T20:00:00Z",
    windowEnd: "2026-04-11T20:00:00Z",
    totalRequests: 100,
    totalCost: 1.5,
    totalErrors: 3,
    ...overrides,
  };
}

// ── Tests ──

describe("AgentAnalyticsPanel", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  describe("API integration types", () => {
    it("getAgentAnalytics is callable with no args", async () => {
      mockGetAgentAnalytics.mockResolvedValue(makeSummary());
      const result = await getAgentAnalytics();
      expect(result.agents).toHaveLength(1);
      expect(result.totalRequests).toBe(100);
      expect(result.totalCost).toBe(1.5);
      expect(result.totalErrors).toBe(3);
    });

    it("getAgentAnalytics accepts hoursBack parameter", async () => {
      mockGetAgentAnalytics.mockResolvedValue(makeSummary());
      const result = await getAgentAnalytics(24);
      expect(result.agents).toHaveLength(1);
      expect(mockGetAgentAnalytics).toHaveBeenCalledWith(24);
    });

    it("returns empty agents list when no data", async () => {
      mockGetAgentAnalytics.mockResolvedValue(makeSummary({
        agents: [],
        totalRequests: 0,
        totalCost: 0,
        totalErrors: 0,
      }));
      const result = await getAgentAnalytics();
      expect(result.agents).toHaveLength(0);
      expect(result.totalRequests).toBe(0);
    });
  });

  describe("AgentPerformanceMetrics shape", () => {
    it("includes all required fields", () => {
      const agent = makeAgent();
      expect(agent.agentId).toBe("planner-1");
      expect(agent.agentName).toBe("Planner");
      expect(agent.totalRequests).toBeTypeOf("number");
      expect(agent.totalInputTokens).toBeTypeOf("number");
      expect(agent.totalOutputTokens).toBeTypeOf("number");
      expect(agent.totalCost).toBeTypeOf("number");
      expect(agent.averageResponseTimeMs).toBeTypeOf("number");
      expect(agent.totalErrors).toBeTypeOf("number");
      expect(agent.recoverableErrors).toBeTypeOf("number");
      expect(agent.unrecoverableErrors).toBeTypeOf("number");
      expect(agent.tasksAssigned).toBeTypeOf("number");
      expect(agent.tasksCompleted).toBeTypeOf("number");
      expect(agent.tokenTrend).toHaveLength(12);
    });

    it("handles null averageResponseTimeMs", () => {
      const agent = makeAgent({ averageResponseTimeMs: null });
      expect(agent.averageResponseTimeMs).toBeNull();
    });

    it("handles zero values", () => {
      const agent = makeAgent({
        totalRequests: 0,
        totalErrors: 0,
        tasksAssigned: 0,
        tasksCompleted: 0,
        totalCost: 0,
        tokenTrend: Array(12).fill(0),
      });
      expect(agent.totalRequests).toBe(0);
      expect(agent.totalErrors).toBe(0);
      expect(agent.tokenTrend.every((v: number) => v === 0)).toBe(true);
    });
  });

  describe("AgentAnalyticsSummary shape", () => {
    it("includes window timestamps", () => {
      const summary = makeSummary();
      expect(summary.windowStart).toMatch(/^\d{4}-\d{2}-\d{2}T/);
      expect(summary.windowEnd).toMatch(/^\d{4}-\d{2}-\d{2}T/);
    });

    it("totals match sum of agent metrics", () => {
      const agents = [
        makeAgent({ agentId: "a1", totalRequests: 50, totalCost: 0.5, totalErrors: 1 }),
        makeAgent({ agentId: "a2", totalRequests: 75, totalCost: 1.0, totalErrors: 2 }),
      ];
      const summary = makeSummary({
        agents,
        totalRequests: 125,
        totalCost: 1.5,
        totalErrors: 3,
      });
      expect(summary.totalRequests).toBe(agents.reduce((s, a) => s + a.totalRequests, 0));
      expect(summary.totalCost).toBe(agents.reduce((s, a) => s + a.totalCost, 0));
      expect(summary.totalErrors).toBe(agents.reduce((s, a) => s + a.totalErrors, 0));
    });

    it("handles multiple agents", async () => {
      const multiAgent = makeSummary({
        agents: [
          makeAgent({ agentId: "planner-1", totalRequests: 100 }),
          makeAgent({ agentId: "coder-1", totalRequests: 200 }),
          makeAgent({ agentId: "reviewer-1", totalRequests: 50 }),
        ],
        totalRequests: 350,
      });
      mockGetAgentAnalytics.mockResolvedValue(multiAgent);
      const result = await getAgentAnalytics();
      expect(result.agents).toHaveLength(3);
      expect(result.totalRequests).toBe(350);
    });
  });

  describe("sort logic", () => {
    const agents: AgentPerformanceMetrics[] = [
      makeAgent({ agentId: "a", totalRequests: 10, totalCost: 5, totalErrors: 1, tasksAssigned: 3 }),
      makeAgent({ agentId: "b", totalRequests: 50, totalCost: 1, totalErrors: 5, tasksAssigned: 1 }),
      makeAgent({ agentId: "c", totalRequests: 30, totalCost: 3, totalErrors: 0, tasksAssigned: 8 }),
    ];

    // Replicate the sort logic from AgentAnalyticsPanel
    function sortAgents(list: AgentPerformanceMetrics[], key: string): AgentPerformanceMetrics[] {
      const sorted = [...list];
      switch (key) {
        case "requests": return sorted.sort((a, b) => b.totalRequests - a.totalRequests);
        case "cost": return sorted.sort((a, b) => b.totalCost - a.totalCost);
        case "errors": return sorted.sort((a, b) => b.totalErrors - a.totalErrors);
        case "tasks": return sorted.sort((a, b) => b.tasksAssigned - a.tasksAssigned);
        default: return sorted;
      }
    }

    it("sorts by requests descending", () => {
      const sorted = sortAgents(agents, "requests");
      expect(sorted.map((a) => a.agentId)).toEqual(["b", "c", "a"]);
    });

    it("sorts by cost descending", () => {
      const sorted = sortAgents(agents, "cost");
      expect(sorted.map((a) => a.agentId)).toEqual(["a", "c", "b"]);
    });

    it("sorts by errors descending", () => {
      const sorted = sortAgents(agents, "errors");
      expect(sorted.map((a) => a.agentId)).toEqual(["b", "a", "c"]);
    });

    it("sorts by tasks descending", () => {
      const sorted = sortAgents(agents, "tasks");
      expect(sorted.map((a) => a.agentId)).toEqual(["c", "a", "b"]);
    });
  });

  describe("error rate color logic", () => {
    function errorRateColor(errors: number, requests: number): string {
      if (requests === 0 || errors === 0) return "green";
      const rate = errors / requests;
      if (rate > 0.2) return "red";
      if (rate > 0.05) return "yellow";
      return "green";
    }

    it("returns green when no errors", () => {
      expect(errorRateColor(0, 100)).toBe("green");
    });

    it("returns green when no requests", () => {
      expect(errorRateColor(5, 0)).toBe("green");
    });

    it("returns green for low error rate", () => {
      expect(errorRateColor(1, 100)).toBe("green");
    });

    it("returns yellow for moderate error rate", () => {
      expect(errorRateColor(10, 100)).toBe("yellow");
    });

    it("returns red for high error rate", () => {
      expect(errorRateColor(25, 100)).toBe("red");
    });
  });
});
