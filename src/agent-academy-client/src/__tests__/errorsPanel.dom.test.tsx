// @vitest-environment jsdom
/**
 * Interactive RTL tests for ErrorsPanel.
 *
 * Uses @testing-library/react + jsdom.
 * Covers: loading state, error states, zero-error clean state, summary stat cards,
 * error breakdown tables, error records table, pagination, refresh, circuit breaker
 * status display, sparkline rendering, and partial-failure resilience.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../api", () => ({
  getGlobalErrorSummary: vi.fn(),
  getGlobalErrorRecords: vi.fn(),
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

import ErrorsPanel from "../ErrorsPanel";
import type { ErrorSummary, ErrorRecord } from "../api";
import { getGlobalErrorSummary, getGlobalErrorRecords } from "../api";
import type { CircuitBreakerState } from "../useCircuitBreakerPolling";

const mockGetSummary = vi.mocked(getGlobalErrorSummary);
const mockGetRecords = vi.mocked(getGlobalErrorRecords);

// ── Factories ──────────────────────────────────────────────────────────

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

function makeRecords(count: number): ErrorRecord[] {
  return Array.from({ length: count }, (_, i) =>
    makeRecord({
      agentId: `agent-${i}`,
      message: `Error message ${i}`,
      timestamp: `2026-04-04T${String(i % 24).padStart(2, "0")}:00:00Z`,
    }),
  );
}

// ── Helpers ─────────────────────────────────────────────────────────────

function renderPanel(props: {
  hoursBack?: number;
  circuitBreakerState?: CircuitBreakerState;
} = {}) {
  return render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(ErrorsPanel, props),
    ),
  );
}

// ── Tests ───────────────────────────────────────────────────────────────

describe("ErrorsPanel (interactive)", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  afterEach(() => {
    cleanup();
  });

  // ── Loading ──

  describe("loading state", () => {
    it("shows spinner while fetching data", () => {
      mockGetSummary.mockReturnValue(new Promise(() => {}));
      mockGetRecords.mockReturnValue(new Promise(() => {}));
      renderPanel();
      expect(screen.getByText("Loading error data…")).toBeInTheDocument();
    });
  });

  // ── Error states ──

  describe("error states", () => {
    it("shows error when both API calls fail", async () => {
      mockGetSummary.mockRejectedValue(new Error("Server down"));
      mockGetRecords.mockRejectedValue(new Error("Server down"));
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Server down")).toBeInTheDocument();
      });
    });

    it("shows summary even if records fail", async () => {
      mockGetSummary.mockResolvedValue(makeSummary());
      mockGetRecords.mockRejectedValue(new Error("Records unavailable"));
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("5")).toBeInTheDocument(); // total errors
      });
      expect(screen.getByText("Records unavailable")).toBeInTheDocument();
    });

    it("shows records even if summary fails", async () => {
      mockGetSummary.mockRejectedValue(new Error("Summary unavailable"));
      mockGetRecords.mockResolvedValue([makeRecord()]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Token expired")).toBeInTheDocument();
      });
      expect(screen.getByText("Summary unavailable")).toBeInTheDocument();
    });

    it("shows generic error message for non-Error rejections", async () => {
      mockGetSummary.mockRejectedValue("oops");
      mockGetRecords.mockRejectedValue("oops");
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Failed to load error summary")).toBeInTheDocument();
      });
    });
  });

  // ── Zero errors ──

  describe("zero-error state", () => {
    it("shows clean state message when no errors", async () => {
      mockGetSummary.mockResolvedValue(
        makeSummary({ totalErrors: 0, recoverableErrors: 0, unrecoverableErrors: 0, byType: [], byAgent: [] }),
      );
      mockGetRecords.mockResolvedValue([]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/No errors recorded/)).toBeInTheDocument();
      });
    });

    it("shows circuit breaker when non-Closed in zero-error state", async () => {
      mockGetSummary.mockResolvedValue(
        makeSummary({ totalErrors: 0, recoverableErrors: 0, unrecoverableErrors: 0, byType: [], byAgent: [] }),
      );
      mockGetRecords.mockResolvedValue([]);
      renderPanel({ circuitBreakerState: "Open" });
      await waitFor(() => {
        expect(screen.getByTestId("circuit-breaker-status")).toBeInTheDocument();
      });
      expect(screen.getByText("Circuit Open")).toBeInTheDocument();
    });

    it("hides circuit breaker when Closed in zero-error state", async () => {
      mockGetSummary.mockResolvedValue(
        makeSummary({ totalErrors: 0, recoverableErrors: 0, unrecoverableErrors: 0, byType: [], byAgent: [] }),
      );
      mockGetRecords.mockResolvedValue([]);
      renderPanel({ circuitBreakerState: "Closed" });
      await waitFor(() => {
        expect(screen.getByText(/No errors recorded/)).toBeInTheDocument();
      });
      expect(screen.queryByTestId("circuit-breaker-status")).not.toBeInTheDocument();
    });
  });

  // ── Summary stat cards ──

  describe("summary stat cards", () => {
    it("renders total, recoverable, and unrecoverable counts", async () => {
      mockGetSummary.mockResolvedValue(makeSummary());
      mockGetRecords.mockResolvedValue([makeRecord()]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Total Errors")).toBeInTheDocument();
      });
      expect(screen.getByText("Recoverable")).toBeInTheDocument();
      expect(screen.getByText("Unrecoverable")).toBeInTheDocument();
      // Stat values may appear in both cards and breakdown tables — verify labels exist
      expect(screen.getAllByText("5").length).toBeGreaterThanOrEqual(1);
      expect(screen.getAllByText("3").length).toBeGreaterThanOrEqual(1);
      expect(screen.getAllByText("2").length).toBeGreaterThanOrEqual(1);
    });
  });

  // ── Breakdown tables ──

  describe("breakdown tables", () => {
    it("renders by-type breakdown with badges", async () => {
      mockGetSummary.mockResolvedValue(makeSummary());
      mockGetRecords.mockResolvedValue([makeRecord()]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("By Type")).toBeInTheDocument();
      });
    });

    it("renders by-agent breakdown", async () => {
      mockGetSummary.mockResolvedValue(makeSummary());
      mockGetRecords.mockResolvedValue([makeRecord()]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("By Agent")).toBeInTheDocument();
      });
    });

    it("hides breakdowns when empty", async () => {
      mockGetSummary.mockResolvedValue(makeSummary({ byType: [], byAgent: [] }));
      mockGetRecords.mockResolvedValue([makeRecord()]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("5")).toBeInTheDocument();
      });
      expect(screen.queryByText("By Type")).not.toBeInTheDocument();
      expect(screen.queryByText("By Agent")).not.toBeInTheDocument();
    });
  });

  // ── Error records table ──

  describe("error records table", () => {
    it("shows records with agent, type, message, recovery, and time columns", async () => {
      mockGetSummary.mockResolvedValue(makeSummary());
      mockGetRecords.mockResolvedValue([
        makeRecord(),
        makeRecord({ agentId: "engineer", errorType: "quota", message: "Rate limit", recoverable: true }),
      ]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Recent Errors")).toBeInTheDocument();
      });
      // Agent names may appear in both breakdown and records — verify at least 1 exists
      expect(screen.getAllByText("architect").length).toBeGreaterThanOrEqual(1);
      expect(screen.getAllByText("engineer").length).toBeGreaterThanOrEqual(1);
      expect(screen.getByText("Token expired")).toBeInTheDocument();
      expect(screen.getByText("Rate limit")).toBeInTheDocument();
    });

    it("shows 'No error records found.' when records empty but summary has errors", async () => {
      mockGetSummary.mockResolvedValue(makeSummary());
      mockGetRecords.mockResolvedValue([]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("No error records found.")).toBeInTheDocument();
      });
    });
  });

  // ── Pagination ──

  describe("pagination", () => {
    it("paginates with 15 records per page", async () => {
      const records = makeRecords(20);
      mockGetSummary.mockResolvedValue(makeSummary({ totalErrors: 20 }));
      mockGetRecords.mockResolvedValue(records);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/1–15 of 20/)).toBeInTheDocument();
      });
      // First page shows first 15 records
      expect(screen.getByText("Error message 0")).toBeInTheDocument();
      expect(screen.getByText("Error message 14")).toBeInTheDocument();
      expect(screen.queryByText("Error message 15")).not.toBeInTheDocument();
    });

    it("navigates to next page", async () => {
      const records = makeRecords(20);
      mockGetSummary.mockResolvedValue(makeSummary({ totalErrors: 20 }));
      mockGetRecords.mockResolvedValue(records);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/1–15 of 20/)).toBeInTheDocument();
      });
      await user.click(screen.getByText("Older →"));
      expect(screen.getByText("Error message 15")).toBeInTheDocument();
      expect(screen.getByText(/16–20 of 20/)).toBeInTheDocument();
    });

    it("navigates back to previous page", async () => {
      const records = makeRecords(20);
      mockGetSummary.mockResolvedValue(makeSummary({ totalErrors: 20 }));
      mockGetRecords.mockResolvedValue(records);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/1–15 of 20/)).toBeInTheDocument();
      });
      await user.click(screen.getByText("Older →"));
      await user.click(screen.getByText("← Newer"));
      expect(screen.getByText("Error message 0")).toBeInTheDocument();
      expect(screen.getByText(/1–15 of 20/)).toBeInTheDocument();
    });

    it("disables Newer button on first page", async () => {
      const records = makeRecords(20);
      mockGetSummary.mockResolvedValue(makeSummary({ totalErrors: 20 }));
      mockGetRecords.mockResolvedValue(records);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/1–15 of 20/)).toBeInTheDocument();
      });
      expect(screen.getByText("← Newer")).toBeDisabled();
    });

    it("disables Older button on last page", async () => {
      const records = makeRecords(20);
      mockGetSummary.mockResolvedValue(makeSummary({ totalErrors: 20 }));
      mockGetRecords.mockResolvedValue(records);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/1–15 of 20/)).toBeInTheDocument();
      });
      await user.click(screen.getByText("Older →"));
      expect(screen.getByText("Older →")).toBeDisabled();
    });

    it("hides pager when records fit on one page", async () => {
      mockGetSummary.mockResolvedValue(makeSummary());
      mockGetRecords.mockResolvedValue(makeRecords(10));
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Recent Errors")).toBeInTheDocument();
      });
      expect(screen.queryByText("← Newer")).not.toBeInTheDocument();
      expect(screen.queryByText("Older →")).not.toBeInTheDocument();
    });
  });

  // ── Refresh ──

  describe("refresh", () => {
    it("re-fetches data when Refresh is clicked", async () => {
      mockGetSummary.mockResolvedValue(makeSummary());
      mockGetRecords.mockResolvedValue([makeRecord()]);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Recent Errors")).toBeInTheDocument();
      });
      expect(mockGetSummary).toHaveBeenCalledTimes(1);
      expect(mockGetRecords).toHaveBeenCalledTimes(1);

      mockGetSummary.mockResolvedValue(makeSummary({ totalErrors: 10 }));
      mockGetRecords.mockResolvedValue([makeRecord({ message: "New error" })]);
      await user.click(screen.getByText("Refresh"));
      await waitFor(() => {
        expect(screen.getByText("10")).toBeInTheDocument();
      });
      expect(mockGetSummary).toHaveBeenCalledTimes(2);
    });
  });

  // ── Circuit breaker ──

  describe("circuit breaker", () => {
    it("shows Open state with blocking detail", async () => {
      mockGetSummary.mockResolvedValue(makeSummary());
      mockGetRecords.mockResolvedValue([makeRecord()]);
      renderPanel({ circuitBreakerState: "Open" });
      await waitFor(() => {
        expect(screen.getByTestId("circuit-breaker-status")).toBeInTheDocument();
      });
      expect(screen.getByText("Circuit Open")).toBeInTheDocument();
      expect(screen.getByText(/blocked/)).toBeInTheDocument();
    });

    it("shows HalfOpen state with probing detail", async () => {
      mockGetSummary.mockResolvedValue(makeSummary());
      mockGetRecords.mockResolvedValue([makeRecord()]);
      renderPanel({ circuitBreakerState: "HalfOpen" });
      await waitFor(() => {
        expect(screen.getByText("Circuit Half-Open")).toBeInTheDocument();
      });
      expect(screen.getByText(/Probing/)).toBeInTheDocument();
    });

    it("shows Closed state with normal detail", async () => {
      mockGetSummary.mockResolvedValue(makeSummary());
      mockGetRecords.mockResolvedValue([makeRecord()]);
      renderPanel({ circuitBreakerState: "Closed" });
      await waitFor(() => {
        expect(screen.getByText("Circuit Closed")).toBeInTheDocument();
      });
      expect(screen.getByText(/normal/)).toBeInTheDocument();
    });

    it("hides circuit breaker when prop is undefined", async () => {
      mockGetSummary.mockResolvedValue(makeSummary());
      mockGetRecords.mockResolvedValue([makeRecord()]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Recent Errors")).toBeInTheDocument();
      });
      expect(screen.queryByTestId("circuit-breaker-status")).not.toBeInTheDocument();
    });
  });

  // ── Sparkline ──

  describe("sparkline", () => {
    it("renders sparkline when 2+ records exist", async () => {
      mockGetSummary.mockResolvedValue(makeSummary());
      mockGetRecords.mockResolvedValue(makeRecords(5));
      renderPanel();
      await waitFor(() => {
        expect(screen.getByTestId("errors-sparkline")).toBeInTheDocument();
      });
    });

    it("hides sparkline when fewer than 2 records", async () => {
      mockGetSummary.mockResolvedValue(makeSummary());
      mockGetRecords.mockResolvedValue([makeRecord()]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Recent Errors")).toBeInTheDocument();
      });
      expect(screen.queryByTestId("errors-sparkline")).not.toBeInTheDocument();
    });
  });

  // ── hoursBack prop ──

  describe("hoursBack prop", () => {
    it("passes hoursBack to API calls", async () => {
      mockGetSummary.mockResolvedValue(makeSummary());
      mockGetRecords.mockResolvedValue([]);
      renderPanel({ hoursBack: 24 });
      await waitFor(() => {
        expect(mockGetSummary).toHaveBeenCalledWith(24);
      });
      expect(mockGetRecords).toHaveBeenCalledWith(undefined, 24, 100);
    });
  });

  // ── Pagination resets on refresh ──

  describe("refresh resets pagination", () => {
    it("returns to first page after Refresh", async () => {
      const records = makeRecords(20);
      mockGetSummary.mockResolvedValue(makeSummary({ totalErrors: 20 }));
      mockGetRecords.mockResolvedValue(records);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/1–15 of 20/)).toBeInTheDocument();
      });
      // Navigate to page 2
      await user.click(screen.getByText("Older →"));
      expect(screen.getByText(/16–20 of 20/)).toBeInTheDocument();

      // Refresh — should return to page 1 with fresh data
      mockGetSummary.mockResolvedValue(makeSummary({ totalErrors: 3 }));
      mockGetRecords.mockResolvedValue(makeRecords(3));
      await user.click(screen.getByText("Refresh"));
      await waitFor(() => {
        expect(screen.getByText("Error message 0")).toBeInTheDocument();
      });
      // No pager since < 15 records
      expect(screen.queryByText("Older →")).not.toBeInTheDocument();
    });
  });
});
