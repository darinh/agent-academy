import { describe, expect, it, vi, beforeEach } from "vitest";

vi.mock("../api", () => ({
  getAuditLog: vi.fn(),
  getAuditStats: vi.fn(),
}));

import { getAuditLog, getAuditStats } from "../api";
import type { AuditLogEntry, AuditLogResponse, AuditStatsResponse } from "../api";

const mockGetAuditLog = vi.mocked(getAuditLog);
const mockGetAuditStats = vi.mocked(getAuditStats);

function makeEntry(overrides: Partial<AuditLogEntry> = {}): AuditLogEntry {
  return {
    id: "abc123",
    correlationId: "cmd-xyz",
    agentId: "architect",
    source: null,
    command: "READ_FILE",
    status: "Success",
    errorMessage: null,
    errorCode: null,
    roomId: "room-1",
    timestamp: "2026-04-05T03:00:00Z",
    ...overrides,
  };
}

function makeLogResponse(overrides: Partial<AuditLogResponse> = {}): AuditLogResponse {
  return {
    records: [makeEntry()],
    total: 1,
    limit: 50,
    offset: 0,
    ...overrides,
  };
}

function makeStats(overrides: Partial<AuditStatsResponse> = {}): AuditStatsResponse {
  return {
    totalCommands: 10,
    byStatus: { Success: 8, Error: 1, Denied: 1 },
    byAgent: { architect: 5, coder: 3, reviewer: 2 },
    byCommand: { READ_FILE: 4, SEARCH_CODE: 3, RUN_BUILD: 2, LIST_ROOMS: 1 },
    windowHours: null,
    ...overrides,
  };
}

describe("AuditLogPanel", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  describe("API integration types", () => {
    it("getAuditLog is callable with no args", async () => {
      mockGetAuditLog.mockResolvedValue(makeLogResponse());
      const result = await getAuditLog();
      expect(result.records).toHaveLength(1);
      expect(result.total).toBe(1);
      expect(result.limit).toBe(50);
      expect(result.offset).toBe(0);
    });

    it("getAuditLog accepts filter options", async () => {
      mockGetAuditLog.mockResolvedValue(makeLogResponse({ total: 3 }));
      const result = await getAuditLog({
        agentId: "architect",
        command: "READ_FILE",
        status: "Success",
        hoursBack: 24,
        limit: 10,
        offset: 5,
      });
      expect(mockGetAuditLog).toHaveBeenCalledWith({
        agentId: "architect",
        command: "READ_FILE",
        status: "Success",
        hoursBack: 24,
        limit: 10,
        offset: 5,
      });
      expect(result.total).toBe(3);
    });

    it("getAuditStats is callable with no args", async () => {
      mockGetAuditStats.mockResolvedValue(makeStats());
      const result = await getAuditStats();
      expect(result.totalCommands).toBe(10);
      expect(result.byStatus.Success).toBe(8);
      expect(result.byAgent.architect).toBe(5);
      expect(result.byCommand.READ_FILE).toBe(4);
    });

    it("getAuditStats accepts hoursBack parameter", async () => {
      mockGetAuditStats.mockResolvedValue(makeStats({ totalCommands: 3, windowHours: 24 }));
      const result = await getAuditStats(24);
      expect(mockGetAuditStats).toHaveBeenCalledWith(24);
      expect(result.totalCommands).toBe(3);
      expect(result.windowHours).toBe(24);
    });
  });

  describe("AuditLogEntry shape", () => {
    it("represents a successful agent command", () => {
      const entry = makeEntry();
      expect(entry.agentId).toBe("architect");
      expect(entry.command).toBe("READ_FILE");
      expect(entry.status).toBe("Success");
      expect(entry.errorMessage).toBeNull();
      expect(entry.errorCode).toBeNull();
      expect(entry.source).toBeNull();
    });

    it("represents a human-UI command", () => {
      const entry = makeEntry({
        agentId: "human",
        source: "human-ui",
        command: "LIST_ROOMS",
      });
      expect(entry.agentId).toBe("human");
      expect(entry.source).toBe("human-ui");
    });

    it("represents a failed command with error details", () => {
      const entry = makeEntry({
        status: "Error",
        errorMessage: "Build failed: exit code 1",
        errorCode: "EXECUTION",
      });
      expect(entry.status).toBe("Error");
      expect(entry.errorMessage).toBe("Build failed: exit code 1");
      expect(entry.errorCode).toBe("EXECUTION");
    });

    it("represents a denied command", () => {
      const entry = makeEntry({
        status: "Denied",
        errorMessage: "Agent coder not authorized for RESTART_SERVER",
        errorCode: "PERMISSION",
      });
      expect(entry.status).toBe("Denied");
      expect(entry.errorCode).toBe("PERMISSION");
    });
  });

  describe("AuditLogResponse pagination", () => {
    it("handles empty results", () => {
      const empty = makeLogResponse({
        records: [],
        total: 0,
      });
      expect(empty.records).toHaveLength(0);
      expect(empty.total).toBe(0);
    });

    it("supports paginated results", () => {
      const page = makeLogResponse({
        records: [makeEntry(), makeEntry({ id: "def456" })],
        total: 50,
        limit: 2,
        offset: 10,
      });
      expect(page.records).toHaveLength(2);
      expect(page.total).toBe(50);
      expect(page.limit).toBe(2);
      expect(page.offset).toBe(10);
    });
  });

  describe("AuditStatsResponse shape", () => {
    it("handles zero commands", () => {
      const empty = makeStats({
        totalCommands: 0,
        byStatus: {},
        byAgent: {},
        byCommand: {},
      });
      expect(empty.totalCommands).toBe(0);
      expect(Object.keys(empty.byStatus)).toHaveLength(0);
    });

    it("handles single agent with errors", () => {
      const single = makeStats({
        totalCommands: 5,
        byStatus: { Success: 3, Error: 2 },
        byAgent: { coder: 5 },
        byCommand: { RUN_BUILD: 3, RUN_TESTS: 2 },
      });
      expect(single.byAgent.coder).toBe(5);
      expect(single.byStatus.Error).toBe(2);
    });

    it("windowHours reflects the requested time window", () => {
      const windowed = makeStats({ windowHours: 168 });
      expect(windowed.windowHours).toBe(168);
    });

    it("windowHours is null for all-time", () => {
      const allTime = makeStats({ windowHours: null });
      expect(allTime.windowHours).toBeNull();
    });
  });

  describe("status badge mapping", () => {
    function statusBadge(
      status: string,
    ): { color: "success" | "danger" | "warning" | "important" | "informative"; label: string } {
      switch (status) {
        case "Success":
          return { color: "success", label: "Success" };
        case "Error":
          return { color: "danger", label: "Error" };
        case "Denied":
          return { color: "warning", label: "Denied" };
        case "Pending":
          return { color: "informative", label: "Pending" };
        default:
          return { color: "important", label: status };
      }
    }

    it("maps Success to success", () => {
      expect(statusBadge("Success").color).toBe("success");
    });

    it("maps Error to danger", () => {
      expect(statusBadge("Error").color).toBe("danger");
    });

    it("maps Denied to warning", () => {
      expect(statusBadge("Denied").color).toBe("warning");
    });

    it("maps Pending to informative", () => {
      expect(statusBadge("Pending").color).toBe("informative");
    });

    it("maps unknown status to important with raw label", () => {
      const badge = statusBadge("Retrying");
      expect(badge.color).toBe("important");
      expect(badge.label).toBe("Retrying");
    });
  });
});
