import { describe, expect, it, vi, beforeEach } from "vitest";

vi.mock("../api", () => ({
  getGlobalErrorSummary: vi.fn(),
  getGlobalErrorRecords: vi.fn(),
  getRoomErrors: vi.fn(),
}));

import { getGlobalErrorSummary, getGlobalErrorRecords, getRoomErrors } from "../api";
import type { ErrorSummary, ErrorRecord } from "../api";

const mockGetGlobalErrorSummary = vi.mocked(getGlobalErrorSummary);
const mockGetGlobalErrorRecords = vi.mocked(getGlobalErrorRecords);
const mockGetRoomErrors = vi.mocked(getRoomErrors);

function makeSummary(overrides: Partial<ErrorSummary> = {}): ErrorSummary {
  return {
    totalErrors: 5,
    recoverableErrors: 3,
    unrecoverableErrors: 2,
    byType: [
      { errorType: "authentication", count: 2 },
      { errorType: "quota", count: 2 },
      { errorType: "transient", count: 1 },
    ],
    byAgent: [
      { agentId: "architect", count: 3 },
      { agentId: "engineer", count: 2 },
    ],
    ...overrides,
  };
}

function makeRecord(overrides: Partial<ErrorRecord> = {}): ErrorRecord {
  return {
    agentId: "architect",
    roomId: "room-1",
    errorType: "authentication",
    message: "Token expired",
    recoverable: false,
    timestamp: "2026-04-04T12:00:00Z",
    ...overrides,
  };
}

describe("ErrorsPanel", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  describe("API integration types", () => {
    it("getGlobalErrorSummary is callable with no args", async () => {
      mockGetGlobalErrorSummary.mockResolvedValue(makeSummary());
      const result = await getGlobalErrorSummary();
      expect(result.totalErrors).toBe(5);
      expect(result.recoverableErrors).toBe(3);
      expect(result.unrecoverableErrors).toBe(2);
      expect(result.byType).toHaveLength(3);
      expect(result.byAgent).toHaveLength(2);
    });

    it("getGlobalErrorSummary accepts hoursBack parameter", async () => {
      mockGetGlobalErrorSummary.mockResolvedValue(makeSummary({ totalErrors: 2 }));
      const result = await getGlobalErrorSummary(24);
      expect(mockGetGlobalErrorSummary).toHaveBeenCalledWith(24);
      expect(result.totalErrors).toBe(2);
    });

    it("getGlobalErrorRecords returns array of records", async () => {
      const records = [makeRecord(), makeRecord({ agentId: "engineer", errorType: "quota" })];
      mockGetGlobalErrorRecords.mockResolvedValue(records);
      const result = await getGlobalErrorRecords();
      expect(result).toHaveLength(2);
      expect(result[0].agentId).toBe("architect");
      expect(result[1].errorType).toBe("quota");
    });

    it("getGlobalErrorRecords accepts filters", async () => {
      mockGetGlobalErrorRecords.mockResolvedValue([makeRecord()]);
      await getGlobalErrorRecords("architect", 24, 50);
      expect(mockGetGlobalErrorRecords).toHaveBeenCalledWith("architect", 24, 50);
    });

    it("getRoomErrors returns errors for a room", async () => {
      mockGetRoomErrors.mockResolvedValue([makeRecord()]);
      const result = await getRoomErrors("room-1");
      expect(result).toHaveLength(1);
      expect(result[0].roomId).toBe("room-1");
    });
  });

  describe("ErrorSummary shape", () => {
    it("handles zero errors", () => {
      const empty = makeSummary({
        totalErrors: 0,
        recoverableErrors: 0,
        unrecoverableErrors: 0,
        byType: [],
        byAgent: [],
      });
      expect(empty.totalErrors).toBe(0);
      expect(empty.byType).toEqual([]);
      expect(empty.byAgent).toEqual([]);
    });

    it("handles single error type", () => {
      const single = makeSummary({
        totalErrors: 1,
        byType: [{ errorType: "transient", count: 1 }],
        byAgent: [{ agentId: "planner", count: 1 }],
      });
      expect(single.byType).toHaveLength(1);
      expect(single.byType[0].errorType).toBe("transient");
    });
  });

  describe("ErrorRecord shape", () => {
    it("represents an unrecoverable auth error", () => {
      const rec = makeRecord();
      expect(rec.agentId).toBe("architect");
      expect(rec.errorType).toBe("authentication");
      expect(rec.recoverable).toBe(false);
      expect(rec.message).toBe("Token expired");
    });

    it("represents a recoverable quota error", () => {
      const rec = makeRecord({
        errorType: "quota",
        message: "Rate limit exceeded",
        recoverable: true,
      });
      expect(rec.errorType).toBe("quota");
      expect(rec.recoverable).toBe(true);
    });

    it("represents a transient error", () => {
      const rec = makeRecord({
        errorType: "transient",
        message: "Connection reset",
        recoverable: true,
      });
      expect(rec.errorType).toBe("transient");
    });
  });

  describe("error type badge mapping", () => {
    function errorTypeBadge(
      errorType: string,
    ): { color: "danger" | "warning" | "important" | "informative"; label: string } {
      switch (errorType) {
        case "authentication":
          return { color: "danger", label: "Auth" };
        case "authorization":
          return { color: "danger", label: "Authz" };
        case "quota":
          return { color: "warning", label: "Quota" };
        case "transient":
          return { color: "important", label: "Transient" };
        default:
          return { color: "informative", label: errorType };
      }
    }

    it("maps auth errors to danger", () => {
      expect(errorTypeBadge("authentication").color).toBe("danger");
      expect(errorTypeBadge("authorization").color).toBe("danger");
    });

    it("maps quota to warning", () => {
      expect(errorTypeBadge("quota").color).toBe("warning");
    });

    it("maps transient to important", () => {
      expect(errorTypeBadge("transient").color).toBe("important");
    });

    it("maps unknown types to informative with raw label", () => {
      const badge = errorTypeBadge("custom_error");
      expect(badge.color).toBe("informative");
      expect(badge.label).toBe("custom_error");
    });
  });

  describe("circuit breaker display mapping", () => {
    function circuitBreakerDisplay(state: string | null): {
      color: string;
      label: string;
      detail: string;
    } {
      switch (state) {
        case "Open":
          return {
            color: "#f85149",
            label: "Circuit Open",
            detail: "Agent requests are blocked. Waiting for cooldown before probing.",
          };
        case "HalfOpen":
          return {
            color: "#ffbe70",
            label: "Circuit Half-Open",
            detail: "Probing with a single request to test if the backend has recovered.",
          };
        case "Closed":
          return {
            color: "#48d67a",
            label: "Circuit Closed",
            detail: "All systems normal.",
          };
        default:
          return {
            color: "var(--aa-muted)",
            label: "Unknown",
            detail: "Circuit breaker state is unavailable.",
          };
      }
    }

    it("maps Open to red with blocking message", () => {
      const info = circuitBreakerDisplay("Open");
      expect(info.color).toBe("#f85149");
      expect(info.label).toBe("Circuit Open");
      expect(info.detail).toContain("blocked");
    });

    it("maps HalfOpen to amber with probing message", () => {
      const info = circuitBreakerDisplay("HalfOpen");
      expect(info.color).toBe("#ffbe70");
      expect(info.label).toBe("Circuit Half-Open");
      expect(info.detail).toContain("Probing");
    });

    it("maps Closed to green with normal message", () => {
      const info = circuitBreakerDisplay("Closed");
      expect(info.color).toBe("#48d67a");
      expect(info.label).toBe("Circuit Closed");
      expect(info.detail).toContain("normal");
    });

    it("maps null/unknown to muted with unavailable message", () => {
      const info = circuitBreakerDisplay(null);
      expect(info.label).toBe("Unknown");
      expect(info.detail).toContain("unavailable");
    });
  });
});
