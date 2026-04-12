// @vitest-environment jsdom
/**
 * DOM-based RTL tests for AgentAnalyticsPanel.
 *
 * Covers: loading, error, empty, summary row, agent cards, sort, refresh,
 * export CSV, detail view toggle, badges, and auto-refresh.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../AgentDetailView", () => ({
  default: (props: any) =>
    createElement("div", { "data-testid": "agent-detail-view" }, props.agentId),
}));

vi.mock("../Sparkline", () => ({
  default: () => createElement("svg", { "data-testid": "sparkline" }),
}));

vi.mock("../V3Badge", () => ({
  default: ({ children }: any) =>
    createElement("span", { "data-testid": "v3-badge" }, children),
}));

vi.mock("../api", () => ({
  getAgentAnalytics: vi.fn(),
  exportAgentAnalytics: vi.fn(),
}));

import AgentAnalyticsPanel from "../AgentAnalyticsPanel";
import type { AgentPerformanceMetrics, AgentAnalyticsSummary } from "../api";
import { getAgentAnalytics, exportAgentAnalytics } from "../api";

const mockGetAgentAnalytics = vi.mocked(getAgentAnalytics);
const mockExportAgentAnalytics = vi.mocked(exportAgentAnalytics);

// ── Factories ──────────────────────────────────────────────────────────

function makeAgent(
  overrides: Partial<AgentPerformanceMetrics> = {},
): AgentPerformanceMetrics {
  return {
    agentId: "architect",
    agentName: "Athena",
    totalRequests: 100,
    totalInputTokens: 50000,
    totalOutputTokens: 25000,
    totalCost: 1.5,
    averageResponseTimeMs: 3500,
    totalErrors: 2,
    recoverableErrors: 1,
    unrecoverableErrors: 1,
    tasksAssigned: 5,
    tasksCompleted: 4,
    tokenTrend: [100, 200, 150],
    ...overrides,
  };
}

function makeSummary(
  agents: AgentPerformanceMetrics[] = [makeAgent()],
): AgentAnalyticsSummary {
  return {
    agents,
    windowStart: "2026-04-10T00:00:00Z",
    windowEnd: "2026-04-11T00:00:00Z",
    totalRequests: agents.reduce((s, a) => s + a.totalRequests, 0),
    totalCost: agents.reduce((s, a) => s + a.totalCost, 0),
    totalErrors: agents.reduce((s, a) => s + a.totalErrors, 0),
  };
}

// ── Helpers ─────────────────────────────────────────────────────────────

function renderPanel(props: { hoursBack?: number } = {}) {
  return render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(AgentAnalyticsPanel, props),
    ),
  );
}

// ── Lifecycle ──────────────────────────────────────────────────────────

afterEach(() => {
  cleanup();
  document.body.innerHTML = "";
  vi.restoreAllMocks();
});

// ── Tests ──────────────────────────────────────────────────────────────

describe("AgentAnalyticsPanel", () => {
  it("shows spinner while loading", () => {
    mockGetAgentAnalytics.mockReturnValue(new Promise(() => {}));
    renderPanel();
    expect(screen.getByText("Loading analytics…")).toBeInTheDocument();
  });

  it("shows error message when fetch fails", async () => {
    mockGetAgentAnalytics.mockRejectedValueOnce(new Error("Network error"));
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Network error")).toBeInTheDocument();
    });
  });

  it("shows empty state when no agents", async () => {
    mockGetAgentAnalytics.mockResolvedValueOnce(makeSummary([]));
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText(/No agent activity recorded/)).toBeInTheDocument();
    });
  });

  it("shows empty state with hoursBack text when provided", async () => {
    mockGetAgentAnalytics.mockResolvedValueOnce(makeSummary([]));
    renderPanel({ hoursBack: 24 });
    await waitFor(() => {
      expect(
        screen.getByText(/No agent activity recorded in the last 24h/),
      ).toBeInTheDocument();
    });
  });

  it("shows summary row with agent count, requests, cost, errors", async () => {
    const summary = makeSummary([
      makeAgent({ totalRequests: 200, totalCost: 3.5, totalErrors: 5 }),
      makeAgent({
        agentId: "coder",
        agentName: "Coder",
        totalRequests: 100,
        totalCost: 1.0,
        totalErrors: 1,
      }),
    ]);
    mockGetAgentAnalytics.mockResolvedValueOnce(summary);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("2")).toBeInTheDocument(); // agents count
    });
    expect(screen.getByText("Agents")).toBeInTheDocument();
    // "Requests" label appears in both summary row and agent card metrics rows
    expect(screen.getAllByText("Requests").length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText("Total Cost")).toBeInTheDocument();
    // "Errors" appears in summary and agent card error sections
    expect(screen.getAllByText(/Errors/).length).toBeGreaterThanOrEqual(1);
  });

  it("shows agent card with name and metrics", async () => {
    mockGetAgentAnalytics.mockResolvedValueOnce(makeSummary());
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Athena")).toBeInTheDocument();
    });
    expect(screen.getByText("architect")).toBeInTheDocument();
    // "100" appears in both summary row and agent card — use getAllByText
    expect(screen.getAllByText("100").length).toBeGreaterThanOrEqual(1);
  });

  it("shows sort select with 5 options", async () => {
    mockGetAgentAnalytics.mockResolvedValueOnce(makeSummary());
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Athena")).toBeInTheDocument();
    });
    const select = screen.getByRole("combobox");
    const options = select.querySelectorAll("option");
    expect(options).toHaveLength(5);
    expect(options[0]).toHaveTextContent("Requests");
    expect(options[1]).toHaveTextContent("Tokens");
    expect(options[2]).toHaveTextContent("Cost");
    expect(options[3]).toHaveTextContent("Errors");
    expect(options[4]).toHaveTextContent("Tasks");
  });

  it("refresh button triggers re-fetch", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    mockGetAgentAnalytics.mockResolvedValue(makeSummary());
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Athena")).toBeInTheDocument();
    });
    const callsBefore = mockGetAgentAnalytics.mock.calls.length;

    await user.click(screen.getByText("Refresh"));
    await waitFor(() => {
      expect(mockGetAgentAnalytics).toHaveBeenCalledTimes(callsBefore + 1);
    });
    vi.useRealTimers();
  });

  it("clicking agent card shows AgentDetailView", async () => {
    const user = userEvent.setup();
    mockGetAgentAnalytics.mockResolvedValueOnce(makeSummary());
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Athena")).toBeInTheDocument();
    });
    expect(screen.queryByTestId("agent-detail-view")).not.toBeInTheDocument();

    await user.click(screen.getByText("Athena"));
    expect(screen.getByTestId("agent-detail-view")).toHaveTextContent("architect");
  });

  it("clicking selected agent card hides AgentDetailView", async () => {
    const user = userEvent.setup();
    mockGetAgentAnalytics.mockResolvedValueOnce(makeSummary());
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Athena")).toBeInTheDocument();
    });

    await user.click(screen.getByText("Athena"));
    expect(screen.getByTestId("agent-detail-view")).toBeInTheDocument();

    await user.click(screen.getByText("Athena"));
    expect(screen.queryByTestId("agent-detail-view")).not.toBeInTheDocument();
  });

  it("shows error badge on agent card when errors > 0", async () => {
    mockGetAgentAnalytics.mockResolvedValueOnce(
      makeSummary([makeAgent({ totalErrors: 3, recoverableErrors: 2 })]),
    );
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Athena")).toBeInTheDocument();
    });
    const badges = screen.getAllByTestId("v3-badge");
    const errBadge = badges.find((b) => b.textContent?.includes("err"));
    expect(errBadge).toBeDefined();
    expect(errBadge).toHaveTextContent("3 err");
  });

  it("shows task completion badge on agent card", async () => {
    mockGetAgentAnalytics.mockResolvedValueOnce(
      makeSummary([makeAgent({ tasksAssigned: 5, tasksCompleted: 4 })]),
    );
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Athena")).toBeInTheDocument();
    });
    const badges = screen.getAllByTestId("v3-badge");
    const taskBadge = badges.find((b) => b.textContent?.includes("80%"));
    expect(taskBadge).toBeDefined();
  });

  it("passes hoursBack to getAgentAnalytics", async () => {
    mockGetAgentAnalytics.mockResolvedValueOnce(makeSummary());
    renderPanel({ hoursBack: 48 });
    await waitFor(() => {
      expect(mockGetAgentAnalytics).toHaveBeenCalledWith(48);
    });
  });

  it("export CSV button calls exportAgentAnalytics", async () => {
    const user = userEvent.setup();
    mockExportAgentAnalytics.mockResolvedValueOnce(undefined);
    mockGetAgentAnalytics.mockResolvedValueOnce(makeSummary());
    renderPanel({ hoursBack: 12 });
    await waitFor(() => {
      expect(screen.getByText("Athena")).toBeInTheDocument();
    });

    await user.click(screen.getByText("Export CSV"));
    expect(mockExportAgentAnalytics).toHaveBeenCalledWith(12, "csv");
  });
});
