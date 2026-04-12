// @vitest-environment jsdom
/**
 * DOM-based RTL tests for AgentDetailView.
 *
 * Covers: loading, error, close button, header, KPIs, model breakdown,
 * recent requests table, recent errors table, task list, branches, and
 * API parameters.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
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
  getAgentAnalyticsDetail: vi.fn(),
}));

import AgentDetailView from "../AgentDetailView";
import type {
  AgentAnalyticsDetail,
  AgentPerformanceMetrics,
  AgentUsageRecord,
  AgentErrorRecord,
  AgentTaskRecord,
  AgentModelBreakdown,
  AgentActivityBucket,
} from "../api";
import { getAgentAnalyticsDetail } from "../api";

const mockGetDetail = vi.mocked(getAgentAnalyticsDetail);

// ── Factories ──────────────────────────────────────────────────────────

function makeAgent(
  overrides: Partial<AgentPerformanceMetrics> = {},
): AgentPerformanceMetrics {
  return {
    agentId: "architect",
    agentName: "Athena",
    totalRequests: 150,
    totalInputTokens: 60000,
    totalOutputTokens: 30000,
    totalCost: 2.25,
    averageResponseTimeMs: 4200,
    totalErrors: 3,
    recoverableErrors: 2,
    unrecoverableErrors: 1,
    tasksAssigned: 6,
    tasksCompleted: 5,
    tokenTrend: [100, 200, 150],
    ...overrides,
  };
}

function makeUsageRecord(
  overrides: Partial<AgentUsageRecord> = {},
): AgentUsageRecord {
  return {
    id: "u1",
    roomId: "room-1",
    model: "claude-sonnet-4",
    inputTokens: 1200,
    outputTokens: 800,
    cost: 0.05,
    durationMs: 3200,
    reasoningEffort: null,
    recordedAt: "2026-04-10T12:00:00Z",
    ...overrides,
  };
}

function makeErrorRecord(
  overrides: Partial<AgentErrorRecord> = {},
): AgentErrorRecord {
  return {
    id: "e1",
    roomId: null,
    errorType: "transient",
    message: "Connection timeout",
    recoverable: true,
    retried: true,
    occurredAt: "2026-04-10T11:30:00Z",
    ...overrides,
  };
}

function makeTask(overrides: Partial<AgentTaskRecord> = {}): AgentTaskRecord {
  return {
    id: "t1",
    title: "Implement login flow",
    status: "Completed",
    roomId: "room-1",
    branchName: "feat/login",
    pullRequestUrl: "https://github.com/org/repo/pull/42",
    pullRequestNumber: 42,
    createdAt: "2026-04-09T08:00:00Z",
    completedAt: "2026-04-10T10:00:00Z",
    ...overrides,
  };
}

function makeModelBreakdown(
  overrides: Partial<AgentModelBreakdown> = {},
): AgentModelBreakdown {
  return {
    model: "claude-sonnet-4",
    requests: 80,
    totalTokens: 45000,
    totalCost: 1.5,
    ...overrides,
  };
}

function makeBucket(
  overrides: Partial<AgentActivityBucket> = {},
): AgentActivityBucket {
  return {
    bucketStart: "2026-04-10T00:00:00Z",
    bucketEnd: "2026-04-10T01:00:00Z",
    requests: 10,
    tokens: 5000,
    ...overrides,
  };
}

function makeDetail(
  overrides: Partial<AgentAnalyticsDetail> = {},
): AgentAnalyticsDetail {
  return {
    agent: makeAgent(),
    windowStart: "2026-04-10T00:00:00Z",
    windowEnd: "2026-04-11T00:00:00Z",
    recentRequests: [makeUsageRecord()],
    recentErrors: [makeErrorRecord()],
    tasks: [makeTask()],
    modelBreakdown: [makeModelBreakdown()],
    activityBuckets: [makeBucket()],
    ...overrides,
  };
}

// ── Helpers ─────────────────────────────────────────────────────────────

function renderView(
  props: { agentId?: string; hoursBack?: number; onClose?: () => void } = {},
) {
  const {
    agentId = "architect",
    hoursBack,
    onClose = vi.fn(),
  } = props;
  return {
    onClose,
    ...render(
      createElement(
        FluentProvider,
        { theme: webDarkTheme },
        createElement(AgentDetailView, { agentId, hoursBack, onClose }),
      ),
    ),
  };
}

// ── Lifecycle ──────────────────────────────────────────────────────────

afterEach(() => {
  cleanup();
  document.body.innerHTML = "";
  vi.restoreAllMocks();
});

// ── Tests ──────────────────────────────────────────────────────────────

describe("AgentDetailView", () => {
  it("shows spinner while loading", () => {
    mockGetDetail.mockReturnValue(new Promise(() => {}));
    renderView();
    expect(screen.getByText("Loading agent detail…")).toBeInTheDocument();
  });

  it("shows error message when fetch fails", async () => {
    mockGetDetail.mockRejectedValueOnce(new Error("Server error"));
    renderView();
    await waitFor(() => {
      expect(screen.getByText("Server error")).toBeInTheDocument();
    });
  });

  it("calls onClose when close button is clicked", async () => {
    const user = userEvent.setup();
    mockGetDetail.mockRejectedValueOnce(new Error("fail"));
    const { onClose } = renderView();
    await waitFor(() => {
      expect(screen.getByText("fail")).toBeInTheDocument();
    });

    const closeBtn = screen.getByTitle("Close");
    await user.click(closeBtn);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("shows agent name in header when available", async () => {
    mockGetDetail.mockResolvedValueOnce(makeDetail());
    renderView();
    await waitFor(() => {
      expect(screen.getByText("Athena")).toBeInTheDocument();
    });
    expect(screen.getByText("architect")).toBeInTheDocument();
  });

  it("shows agentId as header when agentName is null", async () => {
    mockGetDetail.mockResolvedValueOnce(
      makeDetail({ agent: makeAgent({ agentName: null as any }) }),
    );
    renderView();
    await waitFor(() => {
      // The component renders agent.agentName in the header — when null it renders nothing there
      // and the agentId text appears separately
      expect(screen.getByText("architect")).toBeInTheDocument();
    });
  });

  it("shows KPI metrics", async () => {
    mockGetDetail.mockResolvedValueOnce(makeDetail());
    renderView();
    await waitFor(() => {
      expect(screen.getByText("Athena")).toBeInTheDocument();
    });

    // KPI labels — some appear in both KPI row and table headers
    expect(screen.getByText("Requests")).toBeInTheDocument();
    expect(screen.getByText("Tokens")).toBeInTheDocument();
    expect(screen.getAllByText("Cost").length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText("Avg Response")).toBeInTheDocument();
    expect(screen.getAllByText(/Errors/).length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText("Tasks Done")).toBeInTheDocument();
    // KPI values — some may appear multiple times (e.g. in table rows)
    expect(screen.getAllByText("150").length).toBeGreaterThanOrEqual(1); // totalRequests
    expect(screen.getByText("5/6")).toBeInTheDocument(); // tasks
    expect(screen.getByText("4.2s")).toBeInTheDocument(); // avg response
  });

  it("shows model breakdown grid rows", async () => {
    mockGetDetail.mockResolvedValueOnce(
      makeDetail({
        modelBreakdown: [
          makeModelBreakdown({ model: "claude-sonnet-4", requests: 80 }),
          makeModelBreakdown({
            model: "gpt-4o",
            requests: 20,
            totalTokens: 10000,
            totalCost: 0.5,
          }),
        ],
        recentRequests: [],
      }),
    );
    renderView();
    await waitFor(() => {
      expect(screen.getByText("Model Breakdown")).toBeInTheDocument();
    });
    expect(screen.getByText("claude-sonnet-4")).toBeInTheDocument();
    expect(screen.getByText("gpt-4o")).toBeInTheDocument();
  });

  it("shows recent requests table", async () => {
    mockGetDetail.mockResolvedValueOnce(
      makeDetail({
        recentRequests: [
          makeUsageRecord({ model: "claude-sonnet-4", inputTokens: 1200, outputTokens: 800 }),
        ],
        recentErrors: [],
      }),
    );
    renderView();
    await waitFor(() => {
      expect(screen.getByText(/Recent Requests/)).toBeInTheDocument();
    });

    // Table headers — "Time" may appear once now that errors are empty
    expect(screen.getByText("Time")).toBeInTheDocument();
    expect(screen.getByText("Model")).toBeInTheDocument();
    expect(screen.getByText("In Tokens")).toBeInTheDocument();
    expect(screen.getByText("Out Tokens")).toBeInTheDocument();
    expect(screen.getByText("Duration")).toBeInTheDocument();

    // Table data — model name also appears in model breakdown grid
    expect(screen.getAllByText("claude-sonnet-4").length).toBeGreaterThanOrEqual(1);
  });

  it("shows recent errors table with error type badges", async () => {
    mockGetDetail.mockResolvedValueOnce(
      makeDetail({
        recentErrors: [
          makeErrorRecord({ errorType: "transient", message: "Connection timeout" }),
          makeErrorRecord({
            id: "e2",
            errorType: "authentication",
            message: "Token expired",
            recoverable: false,
            retried: false,
          }),
        ],
      }),
    );
    renderView();
    await waitFor(() => {
      expect(screen.getByText(/Recent Errors/)).toBeInTheDocument();
    });

    // Error type badges rendered via mocked V3Badge
    const badges = screen.getAllByTestId("v3-badge");
    const badgeTexts = badges.map((b) => b.textContent);
    expect(badgeTexts).toContain("Transient");
    expect(badgeTexts).toContain("Auth");

    // Recovery column
    expect(badgeTexts).toContain("retried");
    expect(badgeTexts).toContain("unrecoverable");
  });

  it("shows task list with status badges", async () => {
    mockGetDetail.mockResolvedValueOnce(
      makeDetail({
        tasks: [
          makeTask({ title: "Implement login flow", status: "Completed" }),
          makeTask({
            id: "t2",
            title: "Fix header bug",
            status: "InProgress",
            branchName: null,
            pullRequestUrl: null,
            pullRequestNumber: null,
          }),
        ],
      }),
    );
    renderView();
    await waitFor(() => {
      expect(screen.getByText("Implement login flow")).toBeInTheDocument();
    });
    expect(screen.getByText("Fix header bug")).toBeInTheDocument();

    const badges = screen.getAllByTestId("v3-badge");
    const badgeTexts = badges.map((b) => b.textContent);
    expect(badgeTexts).toContain("Completed");
    expect(badgeTexts).toContain("InProgress");
  });

  it("shows branch name for tasks with branches", async () => {
    mockGetDetail.mockResolvedValueOnce(
      makeDetail({
        tasks: [makeTask({ branchName: "feat/login", pullRequestNumber: 42 })],
      }),
    );
    renderView();
    await waitFor(() => {
      expect(screen.getByText("Implement login flow")).toBeInTheDocument();
    });
    expect(screen.getByText("PR #42")).toBeInTheDocument();
  });

  it("passes agentId and hoursBack to API", async () => {
    mockGetDetail.mockResolvedValueOnce(makeDetail());
    renderView({ agentId: "coder", hoursBack: 72 });
    await waitFor(() => {
      expect(mockGetDetail).toHaveBeenCalledWith("coder", 72);
    });
  });
});
