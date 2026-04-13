// @vitest-environment jsdom
/**
 * DOM-based RTL tests for TaskAnalyticsPanel.
 *
 * Covers: loading, error, empty state, summary KPIs, status badges,
 * throughput sparkline, type breakdown, agent effectiveness table,
 * sort, refresh, and auto-refresh.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../Sparkline", () => ({
  default: () => createElement("svg", { "data-testid": "sparkline" }),
}));

vi.mock("../V3Badge", () => ({
  default: ({ children }: any) =>
    createElement("span", { "data-testid": "v3-badge" }, children),
}));

vi.mock("../api", () => ({
  getTaskCycleAnalytics: vi.fn(),
}));

import TaskAnalyticsPanel from "../TaskAnalyticsPanel";
import type { TaskCycleAnalytics } from "../api";
import { getTaskCycleAnalytics } from "../api";

const mockGetTaskCycleAnalytics = vi.mocked(getTaskCycleAnalytics);

// ── Fixture ────────────────────────────────────────────────────────────

function makeBuckets(n = 12): TaskCycleAnalytics["throughputBuckets"] {
  const now = new Date();
  return Array.from({ length: n }, (_, i) => ({
    bucketStart: new Date(now.getTime() - (n - i) * 3600_000).toISOString(),
    bucketEnd: new Date(now.getTime() - (n - i - 1) * 3600_000).toISOString(),
    completed: i === n - 1 ? 2 : 0,
    created: i === 0 ? 3 : 0,
  }));
}

const SAMPLE: TaskCycleAnalytics = {
  overview: {
    totalTasks: 10,
    statusCounts: {
      queued: 1, active: 2, blocked: 1, awaitingValidation: 0,
      inReview: 1, changesRequested: 0, approved: 1, merging: 0,
      completed: 3, cancelled: 1,
    },
    completionRate: 0.3,
    avgCycleTimeHours: 12.5,
    avgQueueTimeHours: 2.0,
    avgExecutionSpanHours: 10.5,
    avgReviewRounds: 1.5,
    reworkRate: 0.33,
    totalCommits: 15,
  },
  agentEffectiveness: [
    {
      agentId: "agent-1", agentName: "Hephaestus",
      assigned: 5, completed: 3, cancelled: 0,
      completionRate: 0.6,
      avgCycleTimeHours: 10.0, avgQueueTimeHours: 1.5,
      avgExecutionSpanHours: 8.5, avgReviewRounds: 1.2,
      avgCommitsPerTask: 3.0, firstPassApprovalRate: 0.67,
      reworkRate: 0.33,
    },
    {
      agentId: "agent-2", agentName: "Athena",
      assigned: 3, completed: 1, cancelled: 1,
      completionRate: 0.33,
      avgCycleTimeHours: 15.0, avgQueueTimeHours: 3.0,
      avgExecutionSpanHours: 12.0, avgReviewRounds: 2.0,
      avgCommitsPerTask: 5.0, firstPassApprovalRate: 0.0,
      reworkRate: 1.0,
    },
  ],
  throughputBuckets: makeBuckets(),
  typeBreakdown: { feature: 5, bug: 3, chore: 1, spike: 1 },
  windowStart: new Date(Date.now() - 86400_000).toISOString(),
  windowEnd: new Date().toISOString(),
};

// ── Helpers ────────────────────────────────────────────────────────────

function renderPanel(hoursBack?: number | undefined) {
  return render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(TaskAnalyticsPanel, { hoursBack }),
    ),
  );
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

// ── Tests ──────────────────────────────────────────────────────────────

describe("TaskAnalyticsPanel", () => {
  it("shows loading spinner initially", () => {
    mockGetTaskCycleAnalytics.mockReturnValue(new Promise(() => {})); // never resolves
    renderPanel();
    expect(screen.getByText(/loading task analytics/i)).toBeInTheDocument();
  });

  it("shows error message on fetch failure", async () => {
    mockGetTaskCycleAnalytics.mockRejectedValue(new Error("network error"));
    renderPanel();
    await waitFor(() => expect(screen.getByText("network error")).toBeInTheDocument());
  });

  it("shows empty state when no data", async () => {
    const empty: TaskCycleAnalytics = {
      ...SAMPLE,
      overview: {
        ...SAMPLE.overview,
        totalTasks: 0, completionRate: 0, reworkRate: 0, totalCommits: 0,
        statusCounts: {
          queued: 0, active: 0, blocked: 0, awaitingValidation: 0,
          inReview: 0, changesRequested: 0, approved: 0, merging: 0,
          completed: 0, cancelled: 0,
        },
        avgCycleTimeHours: null, avgQueueTimeHours: null,
        avgExecutionSpanHours: null, avgReviewRounds: null,
      },
      agentEffectiveness: [],
      throughputBuckets: makeBuckets().map((b) => ({ ...b, completed: 0, created: 0 })),
      typeBreakdown: { feature: 0, bug: 0, chore: 0, spike: 0 },
    };
    mockGetTaskCycleAnalytics.mockResolvedValue(empty);
    renderPanel();
    await waitFor(() => expect(screen.getByText("0 tasks total")).toBeInTheDocument());
  });

  it("renders summary KPIs correctly", async () => {
    mockGetTaskCycleAnalytics.mockResolvedValue(SAMPLE);
    renderPanel();
    await waitFor(() => expect(screen.getByText("30%")).toBeInTheDocument()); // completionRate
    expect(screen.getByText("12.5h")).toBeInTheDocument(); // avgCycleTimeHours
    expect(screen.getByText("2.0h")).toBeInTheDocument(); // avgQueueTimeHours
    expect(screen.getByText("1.5")).toBeInTheDocument(); // avgReviewRounds
    // reworkRate "33%" appears in both KPI and agent table — use getAllByText
    expect(screen.getAllByText("33%").length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText("15")).toBeInTheDocument(); // totalCommits
  });

  it("renders status badges for non-zero statuses", async () => {
    mockGetTaskCycleAnalytics.mockResolvedValue(SAMPLE);
    renderPanel();
    await waitFor(() => expect(screen.getByText("10 tasks total")).toBeInTheDocument());
    const badges = screen.getAllByTestId("v3-badge");
    const badgeTexts = badges.map((b) => b.textContent);
    expect(badgeTexts).toContain("Active: 2");
    expect(badgeTexts).toContain("Completed: 3");
    expect(badgeTexts).toContain("Blocked: 1");
    // awaitingValidation is 0, should NOT appear
    expect(badgeTexts).not.toContain(expect.stringContaining("Awaiting Val"));
  });

  it("renders throughput sparkline when data exists", async () => {
    mockGetTaskCycleAnalytics.mockResolvedValue(SAMPLE);
    renderPanel();
    await waitFor(() => expect(screen.getByTestId("sparkline")).toBeInTheDocument());
    expect(screen.getByText("Completed tasks over time")).toBeInTheDocument();
  });

  it("hides throughput sparkline when all zero", async () => {
    const noThroughput = {
      ...SAMPLE,
      throughputBuckets: makeBuckets().map((b) => ({ ...b, completed: 0, created: 0 })),
    };
    mockGetTaskCycleAnalytics.mockResolvedValue(noThroughput);
    renderPanel();
    await waitFor(() => expect(screen.getByText("10 tasks total")).toBeInTheDocument());
    expect(screen.queryByText("Completed tasks over time")).not.toBeInTheDocument();
  });

  it("renders type breakdown chips", async () => {
    mockGetTaskCycleAnalytics.mockResolvedValue(SAMPLE);
    renderPanel();
    await waitFor(() => expect(screen.getByText("Feature")).toBeInTheDocument());
    expect(screen.getByText("Bug")).toBeInTheDocument();
    expect(screen.getByText("Chore")).toBeInTheDocument();
    expect(screen.getByText("Spike")).toBeInTheDocument();
  });

  it("renders agent effectiveness table", async () => {
    mockGetTaskCycleAnalytics.mockResolvedValue(SAMPLE);
    renderPanel();
    await waitFor(() => expect(screen.getByText("Hephaestus")).toBeInTheDocument());
    expect(screen.getByText("Athena")).toBeInTheDocument();
    // Check table headers exist (use getAllByText for duplicates)
    expect(screen.getAllByText(/^Done/).length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText(/^Rate/).length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText(/^Cycle/).length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText(/1st Pass/).length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText(/^Rework/).length).toBeGreaterThanOrEqual(1);
  });

  it("supports sorting by clicking column headers", async () => {
    mockGetTaskCycleAnalytics.mockResolvedValue(SAMPLE);
    renderPanel();
    await waitFor(() => expect(screen.getByText("Hephaestus")).toBeInTheDocument());

    // Default sort is by completed desc — Hephaestus (3) should be first
    const rows = screen.getAllByRole("row");
    // Row 0 is header, row 1 is first data row
    expect(within(rows[1]).getByText("Hephaestus")).toBeInTheDocument();

    // Click "Rate" to sort by completionRate
    const user = userEvent.setup();
    await user.click(screen.getByText(/^Rate/));

    // After sorting by completionRate desc: Hephaestus (0.6) > Athena (0.33)
    const sortedRows = screen.getAllByRole("row");
    expect(within(sortedRows[1]).getByText("Hephaestus")).toBeInTheDocument();
  });

  it("refresh button re-fetches data", async () => {
    mockGetTaskCycleAnalytics.mockResolvedValue(SAMPLE);
    renderPanel(24);
    await waitFor(() => expect(screen.getByText("10 tasks total")).toBeInTheDocument());
    const callsBefore = mockGetTaskCycleAnalytics.mock.calls.length;

    const user = userEvent.setup();
    await user.click(screen.getByTitle("Refresh"));
    await waitFor(() =>
      expect(mockGetTaskCycleAnalytics.mock.calls.length).toBeGreaterThan(callsBefore),
    );
  });

  it("sets up auto-refresh interval", async () => {
    vi.useFakeTimers();
    mockGetTaskCycleAnalytics.mockResolvedValue(SAMPLE);
    renderPanel();

    // Let initial fetch resolve
    await vi.advanceTimersByTimeAsync(100);
    const callsAfterInit = mockGetTaskCycleAnalytics.mock.calls.length;

    // Advance 60 seconds — should trigger one more call
    await vi.advanceTimersByTimeAsync(60_000);
    expect(mockGetTaskCycleAnalytics.mock.calls.length).toBeGreaterThan(callsAfterInit);

    vi.useRealTimers();
  });

  it("passes hoursBack to API call", async () => {
    vi.useFakeTimers();
    mockGetTaskCycleAnalytics.mockResolvedValue(SAMPLE);
    renderPanel(168);
    await vi.advanceTimersByTimeAsync(100);
    expect(mockGetTaskCycleAnalytics).toHaveBeenCalledWith(168);
    vi.useRealTimers();
  });

  it("passes undefined hoursBack to API when no time filter", async () => {
    vi.useFakeTimers();
    mockGetTaskCycleAnalytics.mockResolvedValue(SAMPLE);
    renderPanel(undefined);
    await vi.advanceTimersByTimeAsync(100);
    expect(mockGetTaskCycleAnalytics).toHaveBeenCalledWith(undefined);
    vi.useRealTimers();
  });
});
