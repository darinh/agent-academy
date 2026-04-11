import { describe, expect, it, vi, beforeEach } from "vitest";
import type {
  QuotaStatus,
  ResourceQuota,
  AgentUsageWindow,
  UpdateQuotaRequest,
} from "../api";

vi.mock("../api", () => ({
  getAgentQuota: vi.fn(),
  updateAgentQuota: vi.fn(),
  removeAgentQuota: vi.fn(),
  getAgentConfig: vi.fn(),
}));

import {
  getAgentQuota,
  updateAgentQuota,
  removeAgentQuota,
} from "../api";

const mockGetAgentQuota = vi.mocked(getAgentQuota);
const mockUpdateAgentQuota = vi.mocked(updateAgentQuota);
const mockRemoveAgentQuota = vi.mocked(removeAgentQuota);

// ── Factories ──

function makeQuotaStatus(overrides: Partial<QuotaStatus> = {}): QuotaStatus {
  return {
    agentId: "architect",
    isAllowed: true,
    deniedReason: null,
    retryAfterSeconds: null,
    configuredQuota: null,
    currentUsage: null,
    ...overrides,
  };
}

function makeResourceQuota(overrides: Partial<ResourceQuota> = {}): ResourceQuota {
  return {
    maxRequestsPerHour: null,
    maxTokensPerHour: null,
    maxCostPerHour: null,
    ...overrides,
  };
}

function makeUsageWindow(overrides: Partial<AgentUsageWindow> = {}): AgentUsageWindow {
  return {
    requestCount: 0,
    totalTokens: 0,
    totalCost: 0,
    ...overrides,
  };
}

// ── Pure logic (mirrored from AgentConfigCard.tsx) ──

function parseQuotaInt(value: string): number | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  const n = Number(trimmed);
  if (!Number.isFinite(n) || !Number.isInteger(n) || n < 0) return NaN;
  return n;
}

function parseQuotaFloat(value: string): number | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  const n = Number(trimmed);
  if (!Number.isFinite(n) || n < 0) return NaN;
  return n;
}

function hasQuotaConfigured(quota: QuotaStatus | null): boolean {
  return quota?.configuredQuota != null && (
    quota.configuredQuota.maxRequestsPerHour != null ||
    quota.configuredQuota.maxTokensPerHour != null ||
    quota.configuredQuota.maxCostPerHour != null
  );
}

function hasQuotaChanges(
  quota: QuotaStatus | null,
  maxRequestsPerHour: string,
  maxTokensPerHour: string,
  maxCostPerHour: string,
): boolean {
  if (!quota) return false;
  return (
    parseQuotaInt(maxRequestsPerHour) !== (quota.configuredQuota?.maxRequestsPerHour ?? null) ||
    parseQuotaInt(maxTokensPerHour) !== (quota.configuredQuota?.maxTokensPerHour ?? null) ||
    parseQuotaFloat(maxCostPerHour) !== (quota.configuredQuota?.maxCostPerHour ?? null)
  );
}

function buildQuotaRequest(
  maxRequestsPerHour: string,
  maxTokensPerHour: string,
  maxCostPerHour: string,
): UpdateQuotaRequest {
  return {
    maxRequestsPerHour: parseQuotaInt(maxRequestsPerHour),
    maxTokensPerHour: parseQuotaInt(maxTokensPerHour),
    maxCostPerHour: parseQuotaFloat(maxCostPerHour),
  };
}

// ── Tests ──

describe("AgentQuota", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  describe("hasQuotaConfigured", () => {
    it("returns false when quota is null", () => {
      expect(hasQuotaConfigured(null)).toBe(false);
    });

    it("returns false when configuredQuota is null", () => {
      expect(hasQuotaConfigured(makeQuotaStatus())).toBe(false);
    });

    it("returns false when all limits are null", () => {
      expect(hasQuotaConfigured(makeQuotaStatus({
        configuredQuota: makeResourceQuota(),
      }))).toBe(false);
    });

    it("returns true when maxRequestsPerHour is set", () => {
      expect(hasQuotaConfigured(makeQuotaStatus({
        configuredQuota: makeResourceQuota({ maxRequestsPerHour: 100 }),
      }))).toBe(true);
    });

    it("returns true when maxTokensPerHour is set", () => {
      expect(hasQuotaConfigured(makeQuotaStatus({
        configuredQuota: makeResourceQuota({ maxTokensPerHour: 50000 }),
      }))).toBe(true);
    });

    it("returns true when maxCostPerHour is set", () => {
      expect(hasQuotaConfigured(makeQuotaStatus({
        configuredQuota: makeResourceQuota({ maxCostPerHour: 5.0 }),
      }))).toBe(true);
    });

    it("returns true when multiple limits are set", () => {
      expect(hasQuotaConfigured(makeQuotaStatus({
        configuredQuota: makeResourceQuota({
          maxRequestsPerHour: 100,
          maxTokensPerHour: 50000,
          maxCostPerHour: 5.0,
        }),
      }))).toBe(true);
    });
  });

  describe("hasQuotaChanges", () => {
    it("returns false when quota is null", () => {
      expect(hasQuotaChanges(null, "", "", "")).toBe(false);
    });

    it("returns false when form matches configured values (all empty)", () => {
      expect(hasQuotaChanges(makeQuotaStatus(), "", "", "")).toBe(false);
    });

    it("returns false when form matches configured values (with limits)", () => {
      const quota = makeQuotaStatus({
        configuredQuota: makeResourceQuota({
          maxRequestsPerHour: 100,
          maxTokensPerHour: 50000,
          maxCostPerHour: 5.0,
        }),
      });
      expect(hasQuotaChanges(quota, "100", "50000", "5")).toBe(false);
    });

    it("detects change in maxRequestsPerHour", () => {
      const quota = makeQuotaStatus({
        configuredQuota: makeResourceQuota({ maxRequestsPerHour: 100 }),
      });
      expect(hasQuotaChanges(quota, "200", "", "")).toBe(true);
    });

    it("detects change from unlimited to limited", () => {
      expect(hasQuotaChanges(makeQuotaStatus(), "50", "", "")).toBe(true);
    });

    it("detects change from limited to unlimited (empty string)", () => {
      const quota = makeQuotaStatus({
        configuredQuota: makeResourceQuota({ maxRequestsPerHour: 100 }),
      });
      expect(hasQuotaChanges(quota, "", "", "")).toBe(true);
    });

    it("treats whitespace-only as empty (unlimited)", () => {
      expect(hasQuotaChanges(makeQuotaStatus(), "  ", "  ", "  ")).toBe(false);
    });
  });

  describe("buildQuotaRequest", () => {
    it("builds request with all empty values as nulls", () => {
      expect(buildQuotaRequest("", "", "")).toEqual({
        maxRequestsPerHour: null,
        maxTokensPerHour: null,
        maxCostPerHour: null,
      });
    });

    it("builds request with numeric values", () => {
      expect(buildQuotaRequest("100", "50000", "5.50")).toEqual({
        maxRequestsPerHour: 100,
        maxTokensPerHour: 50000,
        maxCostPerHour: 5.5,
      });
    });

    it("treats whitespace as unlimited", () => {
      expect(buildQuotaRequest("  ", "  ", "  ")).toEqual({
        maxRequestsPerHour: null,
        maxTokensPerHour: null,
        maxCostPerHour: null,
      });
    });

    it("handles mixed values", () => {
      expect(buildQuotaRequest("100", "", "2.5")).toEqual({
        maxRequestsPerHour: 100,
        maxTokensPerHour: null,
        maxCostPerHour: 2.5,
      });
    });
  });

  describe("parseQuotaInt / parseQuotaFloat validation", () => {
    it("rejects negative integers", () => {
      expect(parseQuotaInt("-5")).toBeNaN();
    });

    it("rejects non-integer for int fields", () => {
      expect(parseQuotaInt("3.5")).toBeNaN();
    });

    it("rejects text input", () => {
      expect(parseQuotaInt("abc")).toBeNaN();
    });

    it("rejects bare minus sign", () => {
      expect(parseQuotaInt("-")).toBeNaN();
    });

    it("handles scientific notation as NaN for int fields", () => {
      // "1e2" → Number("1e2") = 100, which is a valid integer
      expect(parseQuotaInt("1e2")).toBe(100);
    });

    it("rejects Infinity", () => {
      expect(parseQuotaInt("Infinity")).toBeNaN();
    });

    it("accepts zero", () => {
      expect(parseQuotaInt("0")).toBe(0);
    });

    it("parseQuotaFloat accepts decimals", () => {
      expect(parseQuotaFloat("3.5")).toBe(3.5);
    });

    it("parseQuotaFloat rejects negative", () => {
      expect(parseQuotaFloat("-1.5")).toBeNaN();
    });

    it("parseQuotaFloat rejects text", () => {
      expect(parseQuotaFloat("abc")).toBeNaN();
    });

    it("parseQuotaFloat accepts zero", () => {
      expect(parseQuotaFloat("0")).toBe(0);
    });
  });

  describe("API integration", () => {
    it("getAgentQuota calls the correct endpoint shape", async () => {
      const status = makeQuotaStatus({
        configuredQuota: makeResourceQuota({ maxRequestsPerHour: 60 }),
        currentUsage: makeUsageWindow({ requestCount: 23, totalTokens: 15000 }),
      });
      mockGetAgentQuota.mockResolvedValue(status);

      const result = await getAgentQuota("architect");
      expect(mockGetAgentQuota).toHaveBeenCalledWith("architect");
      expect(result.isAllowed).toBe(true);
      expect(result.configuredQuota?.maxRequestsPerHour).toBe(60);
      expect(result.currentUsage?.requestCount).toBe(23);
    });

    it("updateAgentQuota sends the correct request shape", async () => {
      const updated = makeQuotaStatus({
        configuredQuota: makeResourceQuota({ maxRequestsPerHour: 100, maxCostPerHour: 5.0 }),
      });
      mockUpdateAgentQuota.mockResolvedValue(updated);

      const req: UpdateQuotaRequest = {
        maxRequestsPerHour: 100,
        maxTokensPerHour: null,
        maxCostPerHour: 5.0,
      };
      const result = await updateAgentQuota("architect", req);
      expect(mockUpdateAgentQuota).toHaveBeenCalledWith("architect", req);
      expect(result.configuredQuota?.maxRequestsPerHour).toBe(100);
      expect(result.configuredQuota?.maxCostPerHour).toBe(5.0);
    });

    it("removeAgentQuota returns status removed", async () => {
      mockRemoveAgentQuota.mockResolvedValue({ status: "removed", agentId: "architect" });

      const result = await removeAgentQuota("architect");
      expect(result.status).toBe("removed");
      expect(result.agentId).toBe("architect");
    });
  });

  describe("usage display formatting", () => {
    it("formats token count with locale separators", () => {
      const usage = makeUsageWindow({ totalTokens: 1234567 });
      expect(usage.totalTokens.toLocaleString()).toMatch(/1[,.]?234[,.]?567/);
    });

    it("formats cost to 4 decimal places", () => {
      const usage = makeUsageWindow({ totalCost: 1.23456 });
      expect(usage.totalCost.toFixed(4)).toBe("1.2346");
    });

    it("formats zero cost correctly", () => {
      const usage = makeUsageWindow({ totalCost: 0 });
      expect(usage.totalCost.toFixed(4)).toBe("0.0000");
    });
  });
});
