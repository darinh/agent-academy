// @vitest-environment jsdom
/**
 * DOM tests for ActivityFeedPanel.
 *
 * Covers: loading state, error state, empty state, event list with severity
 * badges and time, refresh button, event count badge.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../api", () => ({
  getRecentActivity: vi.fn(),
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

vi.mock("../EmptyState", () => ({
  default: ({ title, detail }: { title: string; detail?: string }) =>
    createElement(
      "div",
      { "data-testid": "empty-state" },
      createElement("span", null, title),
      detail && createElement("span", null, detail),
    ),
}));

vi.mock("../timelinePanelUtils", () => ({
  relativeTime: (iso: string) => iso.slice(11, 16),
  eventCategory: (type: string) => type.replace(/([A-Z])/g, " $1").trim(),
}));

import ActivityFeedPanel from "../ActivityFeedPanel";
import type { ActivityEvent } from "../api";
import { getRecentActivity } from "../api";

const mockGetActivity = vi.mocked(getRecentActivity);

// ── Helpers ────────────────────────────────────────────────────────────

function wrap(ui: React.ReactNode) {
  return createElement(FluentProvider, { theme: webDarkTheme }, ui);
}

function makeEvent(overrides: Partial<ActivityEvent> = {}): ActivityEvent {
  return {
    id: "evt-1",
    type: "TaskCreated",
    severity: "Info",
    message: "Task completed successfully",
    occurredAt: "2026-04-10T14:30:00Z",
    ...overrides,
  };
}

// ── Tests ──────────────────────────────────────────────────────────────

describe("ActivityFeedPanel", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(cleanup);

  it("shows loading spinner while fetching", () => {
    mockGetActivity.mockReturnValue(new Promise(() => {}));
    render(wrap(createElement(ActivityFeedPanel)));
    expect(screen.getByText("Loading activity…")).toBeInTheDocument();
  });

  it("shows error state on fetch failure", async () => {
    mockGetActivity.mockRejectedValue(new Error("Connection refused"));
    render(wrap(createElement(ActivityFeedPanel)));
    await waitFor(() => {
      expect(screen.getByTestId("empty-state")).toBeInTheDocument();
    });
    expect(screen.getByText("Failed to load activity")).toBeInTheDocument();
    expect(screen.getByText("Connection refused")).toBeInTheDocument();
  });

  it("shows generic error for non-Error rejection", async () => {
    mockGetActivity.mockRejectedValue("boom");
    render(wrap(createElement(ActivityFeedPanel)));
    await waitFor(() => {
      // Title and detail are both "Failed to load activity" — the mock renders both
      const matches = screen.getAllByText("Failed to load activity");
      expect(matches.length).toBeGreaterThanOrEqual(1);
    });
    expect(screen.getByTestId("empty-state")).toBeInTheDocument();
  });

  it("shows empty state when no events exist", async () => {
    mockGetActivity.mockResolvedValue([]);
    render(wrap(createElement(ActivityFeedPanel)));
    await waitFor(() => {
      expect(screen.getByText("No activity yet")).toBeInTheDocument();
    });
  });

  it("renders event list with messages", async () => {
    mockGetActivity.mockResolvedValue([
      makeEvent({ id: "e1", message: "Task A done" }),
      makeEvent({ id: "e2", message: "Agent joined room" }),
    ]);
    render(wrap(createElement(ActivityFeedPanel)));
    await waitFor(() => {
      expect(screen.getByText("Task A done")).toBeInTheDocument();
    });
    expect(screen.getByText("Agent joined room")).toBeInTheDocument();
  });

  it("shows event count badge", async () => {
    mockGetActivity.mockResolvedValue([
      makeEvent({ id: "e1" }),
      makeEvent({ id: "e2" }),
      makeEvent({ id: "e3" }),
    ]);
    render(wrap(createElement(ActivityFeedPanel)));
    await waitFor(() => {
      expect(screen.getByText("3")).toBeInTheDocument();
    });
  });

  it("renders header with Recent Activity title", async () => {
    mockGetActivity.mockResolvedValue([]);
    render(wrap(createElement(ActivityFeedPanel)));
    await waitFor(() => {
      expect(screen.getByText("Recent Activity")).toBeInTheDocument();
    });
  });

  it("refresh button re-fetches data", async () => {
    mockGetActivity.mockResolvedValue([]);
    render(wrap(createElement(ActivityFeedPanel)));
    await waitFor(() => {
      expect(screen.getByText("No activity yet")).toBeInTheDocument();
    });
    expect(mockGetActivity).toHaveBeenCalledTimes(1);

    mockGetActivity.mockResolvedValue([
      makeEvent({ message: "New event after refresh" }),
    ]);
    const user = userEvent.setup();
    await user.click(screen.getByLabelText("Refresh activity"));

    await waitFor(() => {
      expect(mockGetActivity).toHaveBeenCalledTimes(2);
    });
    await waitFor(() => {
      expect(screen.getByText("New event after refresh")).toBeInTheDocument();
    });
  });

  it("uses severity-based badge colors", async () => {
    mockGetActivity.mockResolvedValue([
      makeEvent({ id: "e1", severity: "Error" }),
      makeEvent({ id: "e2", severity: "Warning" }),
      makeEvent({ id: "e3", severity: "Info" }),
    ]);
    render(wrap(createElement(ActivityFeedPanel)));
    await waitFor(() => {
      expect(screen.getByTestId("badge-err")).toBeInTheDocument();
    });
    expect(screen.getByTestId("badge-warn")).toBeInTheDocument();
    // Info severity maps to "muted" — count badge is also muted
    const mutedBadges = screen.getAllByTestId("badge-muted");
    expect(mutedBadges.length).toBeGreaterThanOrEqual(2);
  });
});
