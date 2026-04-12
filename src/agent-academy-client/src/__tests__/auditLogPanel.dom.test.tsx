// @vitest-environment jsdom
/**
 * Interactive RTL tests for AuditLogPanel.
 *
 * Uses @testing-library/react + jsdom.
 * Covers: loading state, error states, zero-command clean state, summary stat cards,
 * breakdown tables (by agent, by command), audit records table, server-side pagination,
 * refresh, sparkline, and partial-failure resilience.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../api", () => ({
  getAuditLog: vi.fn(),
  getAuditStats: vi.fn(),
}));

vi.mock("../V3Badge", () => ({
  default: ({
    children,
    color,
  }: {
    children: React.ReactNode;
    color: string;
  }) => createElement("span", { "data-testid": `badge-${color}` }, children),
}));

vi.mock("../Sparkline", () => ({
  default: ({ data }: { data: number[] }) =>
    createElement("svg", { "data-testid": "sparkline", "data-points": data.length }),
}));

import AuditLogPanel from "../AuditLogPanel";
import type { AuditLogEntry, AuditStatsResponse, AuditLogResponse } from "../api";
import { getAuditLog, getAuditStats } from "../api";

const mockGetAuditLog = vi.mocked(getAuditLog);
const mockGetAuditStats = vi.mocked(getAuditStats);

// ── Factories ──────────────────────────────────────────────────────────

function makeStats(overrides: Partial<AuditStatsResponse> = {}): AuditStatsResponse {
  return {
    totalCommands: 42,
    byStatus: { Success: 35, Error: 5, Denied: 2 },
    byAgent: { architect: 20, engineer: 15, planner: 7 },
    byCommand: { RUN_BUILD: 18, RUN_TESTS: 12, SHOW_DIFF: 8, LIST_TASKS: 4 },
    windowHours: null,
    ...overrides,
  };
}

function makeEntry(overrides: Partial<AuditLogEntry> = {}): AuditLogEntry {
  return {
    id: `audit-${Math.random().toString(36).slice(2, 8)}`,
    correlationId: "corr-1",
    agentId: "architect",
    source: null,
    command: "RUN_BUILD",
    status: "Success",
    errorMessage: null,
    errorCode: null,
    roomId: "room-1",
    timestamp: "2026-04-04T12:00:00Z",
    ...overrides,
  };
}

function makeLogResponse(
  records: AuditLogEntry[],
  total: number,
  limit = 15,
  offset = 0,
): AuditLogResponse {
  return { records, total, limit, offset };
}

function makeEntries(count: number): AuditLogEntry[] {
  return Array.from({ length: count }, (_, i) =>
    makeEntry({
      id: `audit-${i}`,
      agentId: `agent-${i % 3}`,
      command: i % 2 === 0 ? "RUN_BUILD" : "RUN_TESTS",
      status: i % 5 === 0 ? "Error" : "Success",
      timestamp: `2026-04-04T${String(i % 24).padStart(2, "0")}:00:00Z`,
    }),
  );
}

// ── Helpers ─────────────────────────────────────────────────────────────

function renderPanel(props: { hoursBack?: number } = {}) {
  return render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(AuditLogPanel, props),
    ),
  );
}

/** Set up default successful mocks for both API calls. */
function setupDefaultMocks(
  stats: AuditStatsResponse = makeStats(),
  records: AuditLogEntry[] = makeEntries(5),
  total = 5,
) {
  mockGetAuditStats.mockResolvedValue(stats);
  // getAuditLog is called twice on initial load: once for page data, once for trend (200 limit)
  mockGetAuditLog.mockResolvedValue(makeLogResponse(records, total));
}

// ── Tests ───────────────────────────────────────────────────────────────

describe("AuditLogPanel (interactive)", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  afterEach(() => {
    cleanup();
  });

  // ── Loading ──

  describe("loading state", () => {
    it("shows spinner while fetching data", () => {
      mockGetAuditStats.mockReturnValue(new Promise(() => {}));
      mockGetAuditLog.mockReturnValue(new Promise(() => {}));
      renderPanel();
      expect(screen.getByText("Loading audit data…")).toBeInTheDocument();
    });
  });

  // ── Error states ──

  describe("error states", () => {
    it("shows error when both API calls fail", async () => {
      mockGetAuditStats.mockRejectedValue(new Error("Stats down"));
      mockGetAuditLog.mockRejectedValue(new Error("Log down"));
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Stats down")).toBeInTheDocument();
      });
    });

    it("shows stats even if log fails", async () => {
      mockGetAuditStats.mockResolvedValue(makeStats());
      mockGetAuditLog.mockRejectedValue(new Error("Log unavailable"));
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("42")).toBeInTheDocument(); // totalCommands
      });
      expect(screen.getByText("Log unavailable")).toBeInTheDocument();
    });

    it("shows records even if stats fail", async () => {
      mockGetAuditStats.mockRejectedValue(new Error("Stats unavailable"));
      mockGetAuditLog.mockResolvedValue(makeLogResponse([makeEntry()], 1));
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("RUN_BUILD")).toBeInTheDocument();
      });
      expect(screen.getByText("Stats unavailable")).toBeInTheDocument();
    });

    it("shows generic error message for non-Error rejections", async () => {
      mockGetAuditStats.mockRejectedValue("oops");
      mockGetAuditLog.mockRejectedValue("oops");
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Failed to load audit stats")).toBeInTheDocument();
      });
    });
  });

  // ── Zero commands ──

  describe("zero-command state", () => {
    it("shows clean state message when no commands recorded", async () => {
      mockGetAuditStats.mockResolvedValue(makeStats({ totalCommands: 0, byStatus: {}, byAgent: {}, byCommand: {} }));
      mockGetAuditLog.mockResolvedValue(makeLogResponse([], 0));
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/No commands recorded/)).toBeInTheDocument();
      });
    });
  });

  // ── Summary stat cards ──

  describe("summary stat cards", () => {
    it("renders total, success, error, and denied counts", async () => {
      setupDefaultMocks();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Total")).toBeInTheDocument();
      });
      // Labels and values may appear in multiple DOM nodes (stat cards + breakdown + records)
      expect(screen.getAllByText("Success").length).toBeGreaterThanOrEqual(1);
      expect(screen.getByText("Errors")).toBeInTheDocument();
      expect(screen.getByText("Denied")).toBeInTheDocument();
      expect(screen.getAllByText("42").length).toBeGreaterThanOrEqual(1);
      expect(screen.getAllByText("35").length).toBeGreaterThanOrEqual(1);
    });
  });

  // ── Breakdown tables ──

  describe("breakdown tables", () => {
    it("renders by-agent breakdown", async () => {
      setupDefaultMocks();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("By Agent")).toBeInTheDocument();
      });
      expect(screen.getByText("architect")).toBeInTheDocument();
      expect(screen.getByText("engineer")).toBeInTheDocument();
      expect(screen.getByText("planner")).toBeInTheDocument();
    });

    it("renders top commands breakdown", async () => {
      setupDefaultMocks();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Top Commands")).toBeInTheDocument();
      });
    });

    it("hides breakdowns when empty", async () => {
      setupDefaultMocks(makeStats({ byAgent: {}, byCommand: {} }));
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("42")).toBeInTheDocument();
      });
      expect(screen.queryByText("By Agent")).not.toBeInTheDocument();
      expect(screen.queryByText("Top Commands")).not.toBeInTheDocument();
    });
  });

  // ── Audit records table ──

  describe("audit records table", () => {
    it("shows records with agent, command, status, error, and time columns", async () => {
      const entries = [
        makeEntry({ agentId: "test-arch", command: "RUN_BUILD", status: "Success" }),
        makeEntry({ agentId: "test-eng", command: "RUN_TESTS", status: "Error", errorMessage: "Build failed" }),
      ];
      setupDefaultMocks(makeStats(), entries, 2);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Recent Commands")).toBeInTheDocument();
      });
      expect(screen.getByText("test-arch")).toBeInTheDocument();
      expect(screen.getByText("test-eng")).toBeInTheDocument();
      expect(screen.getByText("Build failed")).toBeInTheDocument();
    });

    it("shows human-ui source badge for human-initiated commands", async () => {
      setupDefaultMocks(makeStats(), [makeEntry({ source: "human-ui" })], 1);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Recent Commands")).toBeInTheDocument();
      });
      // human-ui commands get "warn" color badge
      expect(screen.getByTestId("badge-warn")).toBeInTheDocument();
    });

    it("shows dash for records without error message", async () => {
      setupDefaultMocks(makeStats(), [makeEntry({ errorMessage: null })], 1);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("—")).toBeInTheDocument();
      });
    });

    it("shows error code prefix in error column", async () => {
      setupDefaultMocks(
        makeStats(),
        [makeEntry({ errorMessage: "Not found", errorCode: "E404" })],
        1,
      );
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("[E404] Not found")).toBeInTheDocument();
      });
    });

    it("shows total count badge in header", async () => {
      setupDefaultMocks(makeStats(), makeEntries(5), 99);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Recent Commands")).toBeInTheDocument();
      });
      // Total count shown as badge — 99 is unique enough
      expect(screen.getByText("99")).toBeInTheDocument();
    });

    it("shows 'No command records found.' when records empty but stats exist", async () => {
      setupDefaultMocks(makeStats(), [], 0);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("No command records found.")).toBeInTheDocument();
      });
    });
  });

  // ── Server-side pagination ──

  describe("pagination", () => {
    it("shows page info for multi-page results", async () => {
      setupDefaultMocks(makeStats(), makeEntries(15), 30);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/1–15 of 30/)).toBeInTheDocument();
      });
    });

    it("fetches next page from server on Older click", async () => {
      setupDefaultMocks(makeStats(), makeEntries(15), 30);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/1–15 of 30/)).toBeInTheDocument();
      });

      // Set up the mock for the next page fetch
      const page2Entries = [makeEntry({ command: "SHOW_DIFF", agentId: "page2-agent" })];
      mockGetAuditLog.mockResolvedValueOnce(makeLogResponse(page2Entries, 30, 15, 15));

      await user.click(screen.getByText("Older →"));
      await waitFor(() => {
        expect(screen.getByText("page2-agent")).toBeInTheDocument();
      });
    });

    it("disables Newer button on first page", async () => {
      setupDefaultMocks(makeStats(), makeEntries(15), 30);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/1–15 of 30/)).toBeInTheDocument();
      });
      expect(screen.getByText("← Newer")).toBeDisabled();
    });

    it("disables Older button on last page", async () => {
      setupDefaultMocks(makeStats(), makeEntries(15), 30);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/1–15 of 30/)).toBeInTheDocument();
      });

      const page2Entries = makeEntries(15);
      mockGetAuditLog.mockResolvedValueOnce(makeLogResponse(page2Entries, 30, 15, 15));
      await user.click(screen.getByText("Older →"));
      await waitFor(() => {
        expect(screen.getByText("Older →")).toBeDisabled();
      });
    });

    it("hides pager when all records fit on one page", async () => {
      setupDefaultMocks(makeStats(), makeEntries(5), 5);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Recent Commands")).toBeInTheDocument();
      });
      expect(screen.queryByText("← Newer")).not.toBeInTheDocument();
      expect(screen.queryByText("Older →")).not.toBeInTheDocument();
    });
  });

  // ── Refresh ──

  describe("refresh", () => {
    it("re-fetches all data when Refresh is clicked", async () => {
      setupDefaultMocks();
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Recent Commands")).toBeInTheDocument();
      });

      const initialStatsCalls = mockGetAuditStats.mock.calls.length;

      setupDefaultMocks(makeStats({ totalCommands: 100 }));
      await user.click(screen.getByText("Refresh"));
      await waitFor(() => {
        expect(screen.getByText("100")).toBeInTheDocument();
      });
      expect(mockGetAuditStats.mock.calls.length).toBeGreaterThan(initialStatsCalls);
    });
  });

  // ── Sparkline ──

  describe("sparkline", () => {
    it("renders sparkline when 2+ trend records exist", async () => {
      setupDefaultMocks(makeStats(), makeEntries(5), 5);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByTestId("audit-sparkline")).toBeInTheDocument();
      });
    });

    it("hides sparkline when fewer than 2 trend records", async () => {
      mockGetAuditStats.mockResolvedValue(makeStats());
      // Return 1 record for trend (limit=200) and 1 for page (limit=15)
      mockGetAuditLog.mockResolvedValue(makeLogResponse([makeEntry()], 1));
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Recent Commands")).toBeInTheDocument();
      });
      expect(screen.queryByTestId("audit-sparkline")).not.toBeInTheDocument();
    });
  });

  // ── hoursBack prop ──

  describe("hoursBack prop", () => {
    it("passes hoursBack to API calls", async () => {
      setupDefaultMocks();
      renderPanel({ hoursBack: 48 });
      await waitFor(() => {
        expect(mockGetAuditStats).toHaveBeenCalledWith(48);
      });
      // getAuditLog called with hoursBack in the options
      expect(mockGetAuditLog).toHaveBeenCalledWith(
        expect.objectContaining({ hoursBack: 48 }),
      );
    });
  });

  // ── Status badge mapping ──

  describe("status badges", () => {
    it("renders Success badge with ok color", async () => {
      setupDefaultMocks(makeStats(), [makeEntry({ status: "Success" })], 1);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByTestId("badge-ok")).toBeInTheDocument();
      });
    });

    it("renders Error badge with err color", async () => {
      setupDefaultMocks(makeStats(), [makeEntry({ status: "Error", errorMessage: "fail" })], 1);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByTestId("badge-err")).toBeInTheDocument();
      });
    });

    it("renders Denied badge with warn color", async () => {
      setupDefaultMocks(makeStats(), [makeEntry({ status: "Denied" })], 1);
      renderPanel();
      await waitFor(() => {
        // Two warn badges: one for agent source, one for status (if source is null)
        // With null source we get "info" for agent + "warn" for denied status
        const warnBadges = screen.getAllByTestId("badge-warn");
        expect(warnBadges.length).toBeGreaterThanOrEqual(1);
      });
    });

    it("renders unknown status with bug color", async () => {
      setupDefaultMocks(makeStats(), [makeEntry({ status: "Unknown" })], 1);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByTestId("badge-bug")).toBeInTheDocument();
      });
    });
  });
});
