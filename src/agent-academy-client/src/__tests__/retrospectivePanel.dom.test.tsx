// @vitest-environment jsdom
import "@testing-library/jest-dom/vitest";
import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import RetrospectivePanel from "../RetrospectivePanel";
import type {
  RetrospectiveListItem,
  RetrospectiveListResponse,
  RetrospectiveDetailResponse,
  RetrospectiveStatsResponse,
  RetrospectiveAgentStat,
} from "../api";

vi.mock("../api", () => ({
  listRetrospectives: vi.fn(),
  getRetrospective: vi.fn(),
  getRetrospectiveStats: vi.fn(),
}));

import { listRetrospectives, getRetrospective, getRetrospectiveStats } from "../api";

const mockListRetrospectives = vi.mocked(listRetrospectives);
const mockGetRetrospective = vi.mocked(getRetrospective);
const mockGetRetrospectiveStats = vi.mocked(getRetrospectiveStats);

// ── Factories ──

function makeRetroItem(overrides: Partial<RetrospectiveListItem> = {}): RetrospectiveListItem {
  return {
    id: "retro-1",
    taskId: "task-42",
    taskTitle: "Implement user authentication",
    agentId: "coder-1",
    agentName: "Coder",
    contentPreview: "Learned that JWT rotation tokens need careful handling for security compliance.",
    createdAt: "2026-04-10T12:00:00Z",
    ...overrides,
  };
}

function makeListResponse(
  retrospectives: RetrospectiveListItem[] = [makeRetroItem()],
  overrides: Partial<RetrospectiveListResponse> = {},
): RetrospectiveListResponse {
  return {
    retrospectives,
    total: retrospectives.length,
    limit: 20,
    offset: 0,
    ...overrides,
  };
}

function makeAgentStat(overrides: Partial<RetrospectiveAgentStat> = {}): RetrospectiveAgentStat {
  return {
    agentId: "coder-1",
    agentName: "Coder",
    count: 5,
    ...overrides,
  };
}

function makeStats(overrides: Partial<RetrospectiveStatsResponse> = {}): RetrospectiveStatsResponse {
  return {
    totalRetrospectives: 12,
    byAgent: [
      makeAgentStat(),
      makeAgentStat({ agentId: "reviewer-1", agentName: "Reviewer", count: 7 }),
    ],
    averageContentLength: 450,
    latestRetrospectiveAt: "2026-04-10T14:00:00Z",
    ...overrides,
  };
}

function makeDetail(overrides: Partial<RetrospectiveDetailResponse> = {}): RetrospectiveDetailResponse {
  return {
    id: "retro-1",
    taskId: "task-42",
    taskTitle: "Implement user authentication",
    taskStatus: "Completed",
    agentId: "coder-1",
    agentName: "Coder",
    content: "Learned that JWT rotation tokens need careful handling for security compliance. Also discovered that bcrypt cost factor 12 provides a good balance of security and speed.",
    createdAt: "2026-04-10T12:00:00Z",
    taskCompletedAt: "2026-04-10T11:55:00Z",
    ...overrides,
  };
}

function renderPanel() {
  return render(
    <FluentProvider theme={webDarkTheme}>
      <RetrospectivePanel />
    </FluentProvider>,
  );
}

// ── Helpers ──

function setupSuccess(
  listOverrides?: Partial<RetrospectiveListResponse>,
  statsOverrides?: Partial<RetrospectiveStatsResponse>,
  retrospectives?: RetrospectiveListItem[],
) {
  mockListRetrospectives.mockResolvedValue(makeListResponse(retrospectives, listOverrides));
  mockGetRetrospectiveStats.mockResolvedValue(makeStats(statsOverrides));
}

function getRetroRows(container: HTMLElement): HTMLElement[] {
  return Array.from(container.querySelectorAll("div[role='button'][tabindex='0']"));
}

describe("RetrospectivePanel", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  afterEach(() => {
    cleanup();
    document.body.innerHTML = "";
  });

  // ── Loading state ──

  describe("loading state", () => {
    it("shows spinner while loading", () => {
      mockListRetrospectives.mockReturnValue(new Promise(() => {}));
      mockGetRetrospectiveStats.mockReturnValue(new Promise(() => {}));
      renderPanel();
      expect(screen.getByText("Loading retrospectives…")).toBeInTheDocument();
    });

    it("does not show spinner if data already present", async () => {
      setupSuccess();
      renderPanel();
      await waitFor(() => {
        expect(screen.queryByText("Loading retrospectives…")).not.toBeInTheDocument();
      });
    });
  });

  // ── Error state ──

  describe("error state", () => {
    it("shows error message when list fetch fails", async () => {
      mockListRetrospectives.mockRejectedValue(new Error("Network error"));
      mockGetRetrospectiveStats.mockResolvedValue(makeStats());
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/Network error/)).toBeInTheDocument();
      });
    });

    it("shows generic error for non-Error rejections", async () => {
      mockListRetrospectives.mockRejectedValue("something broke");
      mockGetRetrospectiveStats.mockResolvedValue(makeStats());
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/Failed to load retrospectives/)).toBeInTheDocument();
      });
    });

    it("still renders list when stats fetch fails", async () => {
      mockListRetrospectives.mockResolvedValue(makeListResponse([makeRetroItem()]));
      mockGetRetrospectiveStats.mockRejectedValue(new Error("Stats failed"));
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Implement user authentication")).toBeInTheDocument();
      });
      expect(screen.queryByText(/Stats failed/)).not.toBeInTheDocument();
    });
  });

  // ── Empty state ──

  describe("empty state", () => {
    it("shows empty state when no retrospectives", async () => {
      setupSuccess({}, {}, []);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("No retrospectives yet")).toBeInTheDocument();
      });
      expect(screen.getByText(/Retrospectives are created automatically/)).toBeInTheDocument();
    });

    it("does not show empty state when loading", () => {
      mockListRetrospectives.mockReturnValue(new Promise(() => {}));
      mockGetRetrospectiveStats.mockReturnValue(new Promise(() => {}));
      renderPanel();
      expect(screen.queryByText("No retrospectives yet")).not.toBeInTheDocument();
    });
  });

  // ── Stats display ──

  describe("stats display", () => {
    it("shows stats cards when data loads", async () => {
      setupSuccess({}, {
        totalRetrospectives: 12,
        byAgent: [makeAgentStat(), makeAgentStat({ agentId: "r1", agentName: "Reviewer", count: 7 })],
        averageContentLength: 450,
      });
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("12")).toBeInTheDocument();
      });
      expect(screen.getByText("Total")).toBeInTheDocument();
      expect(screen.getByText("2")).toBeInTheDocument();
      expect(screen.getByText("Agents")).toBeInTheDocument();
      expect(screen.getByText("450")).toBeInTheDocument();
      expect(screen.getByText("Avg length")).toBeInTheDocument();
    });

    it("shows latest retrospective timestamp", async () => {
      setupSuccess({}, { latestRetrospectiveAt: "2026-04-10T14:00:00Z" });
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Latest")).toBeInTheDocument();
      });
    });

    it("does not show latest card when null", async () => {
      setupSuccess({}, { latestRetrospectiveAt: null });
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Total")).toBeInTheDocument();
      });
      expect(screen.queryByText("Latest")).not.toBeInTheDocument();
    });

    it("shows agent breakdown chips", async () => {
      setupSuccess({}, {
        byAgent: [
          makeAgentStat({ agentId: "c1", agentName: "Coder", count: 5 }),
          makeAgentStat({ agentId: "r1", agentName: "Reviewer", count: 7 }),
        ],
      });
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/Coder:/)).toBeInTheDocument();
      });
      expect(screen.getByText(/Reviewer:/)).toBeInTheDocument();
    });
  });

  // ── List rendering ──

  describe("list rendering", () => {
    it("renders retrospective rows with agent badge and task title", async () => {
      const items = [
        makeRetroItem({ id: "r1", agentName: "Coder", taskTitle: "Auth feature" }),
        makeRetroItem({ id: "r2", agentName: "Tester", taskTitle: "Write tests" }),
      ];
      setupSuccess({}, {}, items);
      const { container } = renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Auth feature")).toBeInTheDocument();
      });
      expect(screen.getByText("Write tests")).toBeInTheDocument();
      const rows = getRetroRows(container);
      expect(rows).toHaveLength(2);
    });

    it("truncates long preview text", async () => {
      const longPreview = "A".repeat(150);
      const items = [makeRetroItem({ contentPreview: longPreview })];
      setupSuccess({}, {}, items);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/A{50,}…/)).toBeInTheDocument();
      });
    });

    it("shows total count badge in header", async () => {
      setupSuccess({ total: 42 }, {}, [makeRetroItem()]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("42 total")).toBeInTheDocument();
      });
    });
  });

  // ── Detail view ──

  describe("detail view", () => {
    it("loads and shows detail when row is clicked", async () => {
      setupSuccess();
      mockGetRetrospective.mockResolvedValue(makeDetail());
      const { container } = renderPanel();
      await waitFor(() => {
        expect(getRetroRows(container)).toHaveLength(1);
      });

      const user = userEvent.setup();
      await user.click(getRetroRows(container)[0]);

      // Detail content includes "bcrypt cost factor" which is NOT in the preview
      await waitFor(() => {
        expect(screen.getByText(/bcrypt cost factor/)).toBeInTheDocument();
      });
      expect(mockGetRetrospective).toHaveBeenCalledWith("retro-1");
    });

    it("shows task status badge in detail", async () => {
      setupSuccess();
      mockGetRetrospective.mockResolvedValue(makeDetail({ taskStatus: "Completed" }));
      const { container } = renderPanel();
      await waitFor(() => {
        expect(getRetroRows(container)).toHaveLength(1);
      });

      const user = userEvent.setup();
      await user.click(getRetroRows(container)[0]);

      await waitFor(() => {
        expect(screen.getByText("Completed")).toBeInTheDocument();
      });
    });

    it("shows task completed timestamp when available", async () => {
      setupSuccess();
      mockGetRetrospective.mockResolvedValue(makeDetail({ taskCompletedAt: "2026-04-10T11:55:00Z" }));
      const { container } = renderPanel();
      await waitFor(() => {
        expect(getRetroRows(container)).toHaveLength(1);
      });

      const user = userEvent.setup();
      await user.click(getRetroRows(container)[0]);

      await waitFor(() => {
        expect(screen.getByText(/Task completed:/)).toBeInTheDocument();
      });
    });

    it("hides detail when same row is clicked again", async () => {
      setupSuccess();
      mockGetRetrospective.mockResolvedValue(makeDetail());
      const { container } = renderPanel();
      await waitFor(() => {
        expect(getRetroRows(container)).toHaveLength(1);
      });

      const user = userEvent.setup();
      await user.click(getRetroRows(container)[0]);
      await waitFor(() => {
        expect(screen.getByText(/bcrypt cost factor/)).toBeInTheDocument();
      });

      await user.click(getRetroRows(container)[0]);
      await waitFor(() => {
        expect(screen.queryByText(/bcrypt cost factor/)).not.toBeInTheDocument();
      });
    });

    it("shows failure message when detail fetch fails", async () => {
      setupSuccess();
      mockGetRetrospective.mockRejectedValue(new Error("Not found"));
      const { container } = renderPanel();
      await waitFor(() => {
        expect(getRetroRows(container)).toHaveLength(1);
      });

      const user = userEvent.setup();
      await user.click(getRetroRows(container)[0]);

      await waitFor(() => {
        expect(screen.getByText("Failed to load detail")).toBeInTheDocument();
      });
    });

    it("shows loading spinner while detail loads", async () => {
      setupSuccess();
      mockGetRetrospective.mockReturnValue(new Promise(() => {}));
      const { container } = renderPanel();
      await waitFor(() => {
        expect(getRetroRows(container)).toHaveLength(1);
      });

      const user = userEvent.setup();
      await user.click(getRetroRows(container)[0]);

      expect(screen.getByText("Loading detail…")).toBeInTheDocument();
    });

    it("supports keyboard navigation (Enter to open)", async () => {
      setupSuccess();
      mockGetRetrospective.mockResolvedValue(makeDetail());
      const { container } = renderPanel();
      await waitFor(() => {
        expect(getRetroRows(container)).toHaveLength(1);
      });

      const user = userEvent.setup();
      getRetroRows(container)[0].focus();
      await user.keyboard("{Enter}");

      await waitFor(() => {
        expect(mockGetRetrospective).toHaveBeenCalledWith("retro-1");
      });
    });
  });

  // ── Agent filter ──

  describe("agent filter", () => {
    it("populates agent filter from stats", async () => {
      setupSuccess({}, {
        byAgent: [
          makeAgentStat({ agentId: "c1", agentName: "Coder", count: 5 }),
          makeAgentStat({ agentId: "r1", agentName: "Reviewer", count: 7 }),
        ],
      });
      renderPanel();
      await waitFor(() => {
        expect(screen.getByRole("combobox", { name: /filter by agent/i })).toBeInTheDocument();
      });

      const select = screen.getByRole("combobox", { name: /filter by agent/i });
      const options = Array.from(select.querySelectorAll("option"));
      expect(options).toHaveLength(3);
      expect(options[0]).toHaveTextContent("All agents");
      // Sorted by count descending: Reviewer (7) then Coder (5)
      expect(options[1]).toHaveTextContent("Reviewer (7)");
      expect(options[2]).toHaveTextContent("Coder (5)");
    });

    it("filters by agent when selection changes", async () => {
      setupSuccess({}, {
        byAgent: [
          makeAgentStat({ agentId: "c1", agentName: "Coder", count: 5 }),
        ],
      });
      renderPanel();
      await waitFor(() => {
        expect(screen.getByRole("combobox", { name: /filter by agent/i })).toBeInTheDocument();
      });

      const user = userEvent.setup();
      const select = screen.getByRole("combobox", { name: /filter by agent/i });
      await user.selectOptions(select, "c1");

      await waitFor(() => {
        expect(mockListRetrospectives).toHaveBeenCalledWith(
          expect.objectContaining({ agentId: "c1" }),
        );
      });
    });

    it("resets pagination and clears detail on filter change", async () => {
      setupSuccess(
        { total: 25 },
        { byAgent: [makeAgentStat({ agentId: "c1", agentName: "Coder" })] },
        [makeRetroItem()],
      );
      mockGetRetrospective.mockResolvedValue(makeDetail());
      const { container } = renderPanel();
      await waitFor(() => {
        expect(getRetroRows(container)).toHaveLength(1);
      });

      // Open detail
      const user = userEvent.setup();
      await user.click(getRetroRows(container)[0]);
      await waitFor(() => {
        expect(screen.getByText(/bcrypt cost factor/)).toBeInTheDocument();
      });

      // Change filter
      const select = screen.getByRole("combobox", { name: /filter by agent/i });
      await user.selectOptions(select, "c1");

      // Detail should be gone
      await waitFor(() => {
        expect(screen.queryByText(/bcrypt cost factor/)).not.toBeInTheDocument();
      });
    });
  });

  // ── Pagination ──

  describe("pagination", () => {
    it("does not show pagination when total fits in one page", async () => {
      setupSuccess({ total: 5 }, {}, [makeRetroItem()]);
      renderPanel();
      await waitFor(() => {
        expect(screen.queryByText(/Page /)).not.toBeInTheDocument();
      });
    });

    it("shows pagination when multiple pages", async () => {
      setupSuccess({ total: 45 }, {}, Array(20).fill(null).map((_, i) => makeRetroItem({ id: `r-${i}` })));
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Page 1 of 3")).toBeInTheDocument();
      });
    });

    it("disables prev on first page", async () => {
      setupSuccess({ total: 45 }, {}, Array(20).fill(null).map((_, i) => makeRetroItem({ id: `r-${i}` })));
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("← Prev")).toBeDisabled();
      });
      expect(screen.getByText("Next →")).not.toBeDisabled();
    });

    it("navigates to next page", async () => {
      setupSuccess({ total: 45 }, {}, Array(20).fill(null).map((_, i) => makeRetroItem({ id: `r-${i}` })));
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Page 1 of 3")).toBeInTheDocument();
      });

      const user = userEvent.setup();
      await user.click(screen.getByText("Next →"));

      await waitFor(() => {
        expect(mockListRetrospectives).toHaveBeenCalledWith(
          expect.objectContaining({ offset: 20 }),
        );
      });
    });
  });

  // ── Refresh ──

  describe("refresh", () => {
    it("re-fetches when refresh button is clicked", async () => {
      setupSuccess();
      renderPanel();
      await waitFor(() => {
        expect(mockListRetrospectives).toHaveBeenCalledTimes(1);
      });

      const user = userEvent.setup();
      await user.click(screen.getByRole("button", { name: /refresh/i }));

      await waitFor(() => {
        expect(mockListRetrospectives).toHaveBeenCalledTimes(2);
      });
    });
  });

  // ── Race condition handling ──

  describe("race conditions", () => {
    it("ignores stale list responses", async () => {
      let resolveFirst: (v: RetrospectiveListResponse) => void;
      const firstPromise = new Promise<RetrospectiveListResponse>((r) => { resolveFirst = r; });
      const secondResponse = makeListResponse(
        [makeRetroItem({ id: "r2", taskTitle: "Second result" })],
        { total: 1 },
      );

      mockListRetrospectives.mockReturnValueOnce(firstPromise);
      mockGetRetrospectiveStats.mockResolvedValue(makeStats());
      renderPanel();

      // Trigger a second fetch before the first resolves
      mockListRetrospectives.mockResolvedValueOnce(secondResponse);
      const user = userEvent.setup();

      // Click refresh to trigger second fetch
      await user.click(screen.getByRole("button", { name: /refresh/i }));

      await waitFor(() => {
        expect(screen.getByText("Second result")).toBeInTheDocument();
      });

      // Now resolve the stale first response
      resolveFirst!(makeListResponse(
        [makeRetroItem({ id: "r1", taskTitle: "Stale result" })],
      ));

      // Wait a tick — stale response should be ignored
      await new Promise((r) => setTimeout(r, 50));
      expect(screen.queryByText("Stale result")).not.toBeInTheDocument();
      expect(screen.getByText("Second result")).toBeInTheDocument();
    });

    it("ignores stale detail responses", async () => {
      const items = [
        makeRetroItem({ id: "r1", taskTitle: "First task" }),
        makeRetroItem({ id: "r2", taskTitle: "Second task" }),
      ];
      setupSuccess({}, {}, items);

      let resolveFirst: (v: RetrospectiveDetailResponse) => void;
      const firstDetailPromise = new Promise<RetrospectiveDetailResponse>((r) => { resolveFirst = r; });

      mockGetRetrospective.mockReturnValueOnce(firstDetailPromise);
      mockGetRetrospective.mockResolvedValueOnce(makeDetail({ id: "r2", taskTitle: "Second task detail", content: "Content from second" }));

      const { container } = renderPanel();
      await waitFor(() => {
        expect(getRetroRows(container)).toHaveLength(2);
      });

      const user = userEvent.setup();
      // Click first row
      await user.click(getRetroRows(container)[0]);
      // Immediately click second row (before first resolves)
      await user.click(getRetroRows(container)[1]);

      await waitFor(() => {
        expect(screen.getByText("Content from second")).toBeInTheDocument();
      });

      // Resolve stale first
      resolveFirst!(makeDetail({ id: "r1", content: "Stale content from first" }));
      await new Promise((r) => setTimeout(r, 50));

      expect(screen.queryByText("Stale content from first")).not.toBeInTheDocument();
      expect(screen.getByText("Content from second")).toBeInTheDocument();
    });
  });

  // ── Task status badge variants ──

  describe("task status badges", () => {
    const statusCases: Array<{ status: string; label: string }> = [
      { status: "Completed", label: "Completed" },
      { status: "InProgress", label: "In progress" },
      { status: "Failed", label: "Failed" },
      { status: "Blocked", label: "Blocked" },
      { status: "Unknown", label: "Unknown" },
    ];

    for (const { status, label } of statusCases) {
      it(`shows "${label}" badge for status "${status}"`, async () => {
        setupSuccess();
        mockGetRetrospective.mockResolvedValue(makeDetail({ taskStatus: status }));
        const { container } = renderPanel();
        await waitFor(() => {
          expect(getRetroRows(container)).toHaveLength(1);
        });

        const user = userEvent.setup();
        await user.click(getRetroRows(container)[0]);

        await waitFor(() => {
          expect(screen.getByText(label)).toBeInTheDocument();
        });
      });
    }
  });
});
