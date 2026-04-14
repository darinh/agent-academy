// @vitest-environment jsdom
import "@testing-library/jest-dom/vitest";
import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import DigestPanel from "../DigestPanel";
import type {
  DigestListItem,
  DigestListResponse,
  DigestDetailResponse,
  DigestStatsResponse,
  DigestSourceItem,
} from "../api";

vi.mock("../api", () => ({
  listDigests: vi.fn(),
  getDigest: vi.fn(),
  getDigestStats: vi.fn(),
}));

import { listDigests, getDigest, getDigestStats } from "../api";

const mockListDigests = vi.mocked(listDigests);
const mockGetDigest = vi.mocked(getDigest);
const mockGetDigestStats = vi.mocked(getDigestStats);

// ── Factories ──

function makeDigestItem(overrides: Partial<DigestListItem> = {}): DigestListItem {
  return {
    id: 1,
    createdAt: "2026-04-10T12:00:00Z",
    summary: "Learned patterns about error handling and retry logic across agent tasks.",
    memoriesCreated: 3,
    retrospectivesProcessed: 5,
    status: "Completed",
    ...overrides,
  };
}

function makeListResponse(
  digests: DigestListItem[] = [makeDigestItem()],
  overrides: Partial<DigestListResponse> = {},
): DigestListResponse {
  return {
    digests,
    total: digests.length,
    limit: 20,
    offset: 0,
    ...overrides,
  };
}

function makeStats(overrides: Partial<DigestStatsResponse> = {}): DigestStatsResponse {
  return {
    totalDigests: 10,
    byStatus: { Completed: 8, Failed: 1, Pending: 1 },
    totalMemoriesCreated: 25,
    totalRetrospectivesProcessed: 40,
    undigestedRetrospectives: 3,
    lastCompletedAt: "2026-04-10T14:00:00Z",
    ...overrides,
  };
}

function makeDetail(overrides: Partial<DigestDetailResponse> = {}): DigestDetailResponse {
  return {
    id: 1,
    createdAt: "2026-04-10T12:00:00Z",
    summary: "Learned patterns about error handling and retry logic across agent tasks.",
    memoriesCreated: 3,
    retrospectivesProcessed: 5,
    status: "Completed",
    sources: [],
    ...overrides,
  };
}

function makeSource(overrides: Partial<DigestSourceItem> = {}): DigestSourceItem {
  return {
    commentId: "comment-1",
    taskId: "task-42",
    agentId: "coder-1",
    content: "Discovered that retry with exponential backoff works well for API calls.",
    createdAt: "2026-04-10T11:00:00Z",
    ...overrides,
  };
}

function renderPanel(props: { refreshTrigger?: number; onNavigateToTask?: (taskId: string) => void } = {}) {
  return render(
    <FluentProvider theme={webDarkTheme}>
      <DigestPanel {...props} />
    </FluentProvider>,
  );
}

// ── Helpers ──

function setupSuccess(
  listOverrides?: Partial<DigestListResponse>,
  statsOverrides?: Partial<DigestStatsResponse>,
  digests?: DigestListItem[],
) {
  mockListDigests.mockResolvedValue(makeListResponse(digests, listOverrides));
  mockGetDigestStats.mockResolvedValue(makeStats(statsOverrides));
}

/** Get digest row elements (div[role=button][tabindex]) — excludes real <button> elements */
function getDigestRows(container: HTMLElement): HTMLElement[] {
  return Array.from(container.querySelectorAll("div[role='button'][tabindex='0']"));
}

describe("DigestPanel", () => {
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
      mockListDigests.mockReturnValue(new Promise(() => {}));
      mockGetDigestStats.mockReturnValue(new Promise(() => {}));
      renderPanel();
      expect(screen.getByText("Loading digests…")).toBeInTheDocument();
    });
  });

  // ── Error state ──

  describe("error state", () => {
    it("shows error message when list fetch fails", async () => {
      mockListDigests.mockRejectedValue(new Error("Network error"));
      mockGetDigestStats.mockResolvedValue(makeStats());
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/Network error/)).toBeInTheDocument();
      });
    });

    it("shows generic error for non-Error rejections", async () => {
      mockListDigests.mockRejectedValue("something broke");
      mockGetDigestStats.mockResolvedValue(makeStats());
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/Failed to load digests/)).toBeInTheDocument();
      });
    });
  });

  // ── Empty state ──

  describe("empty state", () => {
    it("shows empty state when no digests", async () => {
      setupSuccess({}, {}, []);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("No digests yet")).toBeInTheDocument();
      });
      expect(screen.getByText(/Learning digests are created automatically/)).toBeInTheDocument();
    });

    it("does not show empty state when loading", () => {
      mockListDigests.mockReturnValue(new Promise(() => {}));
      mockGetDigestStats.mockReturnValue(new Promise(() => {}));
      renderPanel();
      expect(screen.queryByText("No digests yet")).not.toBeInTheDocument();
    });
  });

  // ── Stats display ──

  describe("stats display", () => {
    it("shows stats cards when data loads", async () => {
      setupSuccess({}, {
        totalDigests: 10,
        totalMemoriesCreated: 25,
        totalRetrospectivesProcessed: 40,
        undigestedRetrospectives: 3,
      });
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("10")).toBeInTheDocument();
      });
      expect(screen.getByText("Digests")).toBeInTheDocument();
      expect(screen.getByText("25")).toBeInTheDocument();
      expect(screen.getByText("Memories created")).toBeInTheDocument();
      expect(screen.getByText("40")).toBeInTheDocument();
      expect(screen.getByText("Retros processed")).toBeInTheDocument();
      expect(screen.getByText("3")).toBeInTheDocument();
      expect(screen.getByText("Undigested retros")).toBeInTheDocument();
    });

    it("shows last completed timestamp when available", async () => {
      setupSuccess({}, { lastCompletedAt: "2026-04-10T14:00:00Z" });
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Last completed")).toBeInTheDocument();
      });
    });

    it("hides last completed card when null", async () => {
      setupSuccess({}, { lastCompletedAt: null });
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Digests")).toBeInTheDocument();
      });
      expect(screen.queryByText("Last completed")).not.toBeInTheDocument();
    });

    it("highlights undigested count when > 0", async () => {
      setupSuccess({}, { undigestedRetrospectives: 5 });
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("5")).toBeInTheDocument();
      });
      // The value should have gold color styling
      const valueEl = screen.getByText("5");
      expect(valueEl.style.color).toBe("var(--aa-gold)");
    });

    it("does not highlight undigested count when 0", async () => {
      setupSuccess({}, { undigestedRetrospectives: 0 });
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Digests")).toBeInTheDocument();
      });
      const zeroEls = screen.getAllByText("0");
      zeroEls.forEach((el) => {
        // None of the "0" elements should have gold color
        expect(el.style.color).not.toBe("var(--aa-gold)");
      });
    });
  });

  // ── Header ──

  describe("header", () => {
    it("shows total badge", async () => {
      const items = [makeDigestItem({ id: 1 }), makeDigestItem({ id: 2 })];
      setupSuccess({ total: 42 }, {}, items);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("42 total")).toBeInTheDocument();
      });
    });

    it("shows refresh button", async () => {
      setupSuccess();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByLabelText("Refresh")).toBeInTheDocument();
      });
    });

    it("shows status filter dropdown", async () => {
      setupSuccess();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByLabelText("Filter by status")).toBeInTheDocument();
      });
    });
  });

  // ── Digest list ──

  describe("digest list", () => {
    it("renders digest items with status badges", async () => {
      const items = [
        makeDigestItem({ id: 1, status: "Completed" }),
        makeDigestItem({ id: 2, status: "Failed" }),
        makeDigestItem({ id: 3, status: "Pending" }),
      ];
      setupSuccess({}, {}, items);
      const { container } = renderPanel();
      await waitFor(() => {
        const rows = getDigestRows(container);
        expect(rows.length).toBe(3);
      });
      // Status text appears in both badges and <option> elements,
      // so check that we have 3 rows with different statuses
      const rows = getDigestRows(container);
      expect(rows[0].textContent).toContain("Completed");
      expect(rows[1].textContent).toContain("Failed");
      expect(rows[2].textContent).toContain("Pending");
    });

    it("shows memory and retro counts for each item", async () => {
      setupSuccess({}, {}, [makeDigestItem({ memoriesCreated: 7, retrospectivesProcessed: 12 })]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("7 mem")).toBeInTheDocument();
      });
      expect(screen.getByText("12 retro")).toBeInTheDocument();
    });

    it("truncates long summaries at 120 characters", async () => {
      const longSummary = "A".repeat(150);
      setupSuccess({}, {}, [makeDigestItem({ summary: longSummary })]);
      renderPanel();
      await waitFor(() => {
        const truncated = "A".repeat(120) + "…";
        expect(screen.getByText(truncated)).toBeInTheDocument();
      });
    });

    it("does not truncate short summaries", async () => {
      const shortSummary = "Short summary";
      setupSuccess({}, {}, [makeDigestItem({ summary: shortSummary })]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Short summary")).toBeInTheDocument();
      });
    });

    it("renders items with role=button for accessibility", async () => {
      setupSuccess();
      const { container } = renderPanel();
      await waitFor(() => {
        const rows = getDigestRows(container);
        expect(rows.length).toBeGreaterThanOrEqual(1);
      });
    });

    it("renders unknown status with muted badge", async () => {
      setupSuccess({}, {}, [makeDigestItem({ status: "SomethingElse" })]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("SomethingElse")).toBeInTheDocument();
      });
    });
  });

  // ── Status filtering ──

  describe("status filtering", () => {
    it("passes status filter to API", async () => {
      setupSuccess();
      renderPanel();
      await waitFor(() => {
        expect(mockListDigests).toHaveBeenCalledTimes(1);
      });

      // Change filter to "Completed"
      mockListDigests.mockResolvedValue(makeListResponse());
      const select = screen.getByLabelText("Filter by status");
      await userEvent.selectOptions(select, "Completed");

      await waitFor(() => {
        expect(mockListDigests).toHaveBeenCalledWith(
          expect.objectContaining({ status: "Completed" }),
        );
      });
    });

    it("resets offset and selection when filter changes", async () => {
      setupSuccess({}, {}, [makeDigestItem({ id: 1 })]);
      const { container } = renderPanel();
      await waitFor(() => {
        expect(getDigestRows(container).length).toBe(1);
      });

      // Select a digest to open detail
      mockGetDigest.mockResolvedValue(makeDetail({ id: 1 }));
      await userEvent.click(getDigestRows(container)[0]);
      await waitFor(() => {
        expect(screen.getByText("Digest #1")).toBeInTheDocument();
      });

      // Change filter — should reset offset and close detail
      mockListDigests.mockResolvedValue(makeListResponse());
      mockGetDigestStats.mockResolvedValue(makeStats());
      const select = screen.getByLabelText("Filter by status");
      await userEvent.selectOptions(select, "Failed");

      await waitFor(() => {
        expect(mockListDigests).toHaveBeenCalledWith(
          expect.objectContaining({ offset: 0 }),
        );
      });
      // Detail panel should be closed
      expect(screen.queryByText("Digest #1")).not.toBeInTheDocument();
    });

    it("sends undefined status for 'All statuses'", async () => {
      setupSuccess();
      renderPanel();
      await waitFor(() => {
        expect(mockListDigests).toHaveBeenCalledTimes(1);
      });

      // First select a filter, then clear it
      mockListDigests.mockResolvedValue(makeListResponse());
      const select = screen.getByLabelText("Filter by status");
      await userEvent.selectOptions(select, "Completed");
      await waitFor(() => expect(mockListDigests).toHaveBeenCalledTimes(2));

      mockListDigests.mockResolvedValue(makeListResponse());
      await userEvent.selectOptions(select, "");
      await waitFor(() => {
        expect(mockListDigests).toHaveBeenLastCalledWith(
          expect.objectContaining({ status: undefined }),
        );
      });
    });
  });

  // ── Pagination ──

  describe("pagination", () => {
    it("shows pagination when total exceeds page size", async () => {
      const items = Array.from({ length: 20 }, (_, i) => makeDigestItem({ id: i + 1 }));
      setupSuccess({ total: 45 }, {}, items);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Page 1 of 3")).toBeInTheDocument();
      });
    });

    it("hides pagination when total fits in one page", async () => {
      setupSuccess({ total: 5 }, {}, [makeDigestItem()]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Digests")).toBeInTheDocument();
      });
      expect(screen.queryByText(/Page \d+ of \d+/)).not.toBeInTheDocument();
    });

    it("disables Prev on first page", async () => {
      const items = Array.from({ length: 20 }, (_, i) => makeDigestItem({ id: i + 1 }));
      setupSuccess({ total: 45 }, {}, items);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Page 1 of 3")).toBeInTheDocument();
      });
      expect(screen.getByText("← Prev")).toBeDisabled();
      expect(screen.getByText("Next →")).not.toBeDisabled();
    });

    it("navigates to next page", async () => {
      const items = Array.from({ length: 20 }, (_, i) => makeDigestItem({ id: i + 1 }));
      setupSuccess({ total: 45 }, {}, items);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Page 1 of 3")).toBeInTheDocument();
      });

      mockListDigests.mockResolvedValue(
        makeListResponse(items, { total: 45, offset: 20 }),
      );
      mockGetDigestStats.mockResolvedValue(makeStats());
      await userEvent.click(screen.getByText("Next →"));

      await waitFor(() => {
        expect(mockListDigests).toHaveBeenCalledWith(
          expect.objectContaining({ offset: 20 }),
        );
      });
    });

    it("navigates to previous page", async () => {
      // Start on page 2 by clicking Next first
      const items = Array.from({ length: 20 }, (_, i) => makeDigestItem({ id: i + 1 }));
      setupSuccess({ total: 45 }, {}, items);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Page 1 of 3")).toBeInTheDocument();
      });

      // Go to page 2
      mockListDigests.mockResolvedValue(makeListResponse(items, { total: 45, offset: 20 }));
      mockGetDigestStats.mockResolvedValue(makeStats());
      await userEvent.click(screen.getByText("Next →"));
      await waitFor(() => {
        expect(screen.getByText("Page 2 of 3")).toBeInTheDocument();
      });

      // Go back to page 1
      mockListDigests.mockResolvedValue(makeListResponse(items, { total: 45, offset: 0 }));
      mockGetDigestStats.mockResolvedValue(makeStats());
      await userEvent.click(screen.getByText("← Prev"));
      await waitFor(() => {
        expect(mockListDigests).toHaveBeenCalledWith(
          expect.objectContaining({ offset: 0 }),
        );
      });
    });

    it("disables Next on last page", async () => {
      const items = Array.from({ length: 20 }, (_, i) => makeDigestItem({ id: i + 1 }));
      setupSuccess({ total: 40 }, {}, items);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Page 1 of 2")).toBeInTheDocument();
      });

      // Navigate to page 2 (last page)
      const lastPageItems = Array.from({ length: 20 }, (_, i) => makeDigestItem({ id: i + 21 }));
      mockListDigests.mockResolvedValue(makeListResponse(lastPageItems, { total: 40, offset: 20 }));
      mockGetDigestStats.mockResolvedValue(makeStats());
      await userEvent.click(screen.getByText("Next →"));
      await waitFor(() => {
        expect(screen.getByText("Page 2 of 2")).toBeInTheDocument();
      });
      expect(screen.getByText("Next →")).toBeDisabled();
      expect(screen.getByText("← Prev")).not.toBeDisabled();
    });
  });

  // ── Refresh ──

  describe("refresh", () => {
    it("re-fetches data on Refresh click", async () => {
      setupSuccess();
      renderPanel();
      await waitFor(() => {
        expect(mockListDigests).toHaveBeenCalledTimes(1);
      });

      mockListDigests.mockResolvedValue(makeListResponse());
      mockGetDigestStats.mockResolvedValue(makeStats());
      await userEvent.click(screen.getByLabelText("Refresh"));

      await waitFor(() => {
        expect(mockListDigests).toHaveBeenCalledTimes(2);
      });
    });
  });

  // ── Detail panel ──

  describe("detail panel", () => {
    it("shows detail when a digest is clicked", async () => {
      setupSuccess({}, {}, [makeDigestItem({ id: 42 })]);
      mockGetDigest.mockResolvedValue(
        makeDetail({
          id: 42,
          summary: "Detailed summary of digest 42",
          memoriesCreated: 5,
          retrospectivesProcessed: 10,
        }),
      );
      const { container } = renderPanel();
      await waitFor(() => {
        expect(getDigestRows(container).length).toBe(1);
      });

      await userEvent.click(getDigestRows(container)[0]);

      await waitFor(() => {
        expect(screen.getByText("Digest #42")).toBeInTheDocument();
      });
      expect(screen.getByText("Detailed summary of digest 42")).toBeInTheDocument();
      expect(screen.getByText("5 memories")).toBeInTheDocument();
      expect(screen.getByText("10 retrospectives")).toBeInTheDocument();
    });

    it("shows loading spinner while detail loads", async () => {
      setupSuccess({}, {}, [makeDigestItem({ id: 1 })]);
      mockGetDigest.mockReturnValue(new Promise(() => {}));
      const { container } = renderPanel();
      await waitFor(() => {
        expect(getDigestRows(container).length).toBe(1);
      });

      await userEvent.click(getDigestRows(container)[0]);
      expect(screen.getByText("Loading detail…")).toBeInTheDocument();
    });

    it("collapses detail when same row is clicked again", async () => {
      setupSuccess({}, {}, [makeDigestItem({ id: 1 })]);
      mockGetDigest.mockResolvedValue(makeDetail({ id: 1 }));
      const { container } = renderPanel();
      await waitFor(() => {
        expect(getDigestRows(container).length).toBe(1);
      });

      const row = getDigestRows(container)[0];
      await userEvent.click(row);
      await waitFor(() => {
        expect(screen.getByText("Digest #1")).toBeInTheDocument();
      });

      await userEvent.click(row);
      expect(screen.queryByText("Digest #1")).not.toBeInTheDocument();
    });

    it("shows failure message when detail fetch fails", async () => {
      setupSuccess({}, {}, [makeDigestItem({ id: 1 })]);
      mockGetDigest.mockRejectedValue(new Error("Not found"));
      const { container } = renderPanel();
      await waitFor(() => {
        expect(getDigestRows(container).length).toBe(1);
      });

      await userEvent.click(getDigestRows(container)[0]);
      await waitFor(() => {
        expect(screen.getByText("Failed to load detail")).toBeInTheDocument();
      });
    });

    it("shows source retrospectives in detail", async () => {
      setupSuccess({}, {}, [makeDigestItem({ id: 1 })]);
      mockGetDigest.mockResolvedValue(
        makeDetail({
          id: 1,
          sources: [
            makeSource({ agentId: "coder-1", taskId: "task-42", content: "Retry logic works well" }),
            makeSource({ commentId: "c2", agentId: "reviewer-1", taskId: "task-99", content: "Use structured logging" }),
          ],
        }),
      );
      const { container } = renderPanel();
      await waitFor(() => {
        expect(getDigestRows(container).length).toBe(1);
      });

      await userEvent.click(getDigestRows(container)[0]);
      await waitFor(() => {
        expect(screen.getByText("Source retrospectives (2)")).toBeInTheDocument();
      });
      expect(screen.getByText("Retry logic works well")).toBeInTheDocument();
      expect(screen.getByText("Use structured logging")).toBeInTheDocument();
      expect(screen.getByText(/coder-1/)).toBeInTheDocument();
      expect(screen.getByText(/reviewer-1/)).toBeInTheDocument();
      expect(screen.getByText(/task-42/)).toBeInTheDocument();
      expect(screen.getByText(/task-99/)).toBeInTheDocument();
    });

    it("hides sources section when no sources", async () => {
      setupSuccess({}, {}, [makeDigestItem({ id: 1 })]);
      mockGetDigest.mockResolvedValue(makeDetail({ id: 1, sources: [] }));
      const { container } = renderPanel();
      await waitFor(() => {
        expect(getDigestRows(container).length).toBe(1);
      });

      await userEvent.click(getDigestRows(container)[0]);
      await waitFor(() => {
        expect(screen.getByText("Digest #1")).toBeInTheDocument();
      });
      expect(screen.queryByText(/Source retrospectives/)).not.toBeInTheDocument();
    });

    it("switches detail when different row is clicked", async () => {
      const items = [
        makeDigestItem({ id: 1, summary: "First digest" }),
        makeDigestItem({ id: 2, summary: "Second digest" }),
      ];
      setupSuccess({}, {}, items);
      mockGetDigest.mockImplementation(async (id: number) =>
        makeDetail({ id, summary: id === 1 ? "Detail for first" : "Detail for second" }),
      );
      const { container } = renderPanel();
      await waitFor(() => {
        expect(getDigestRows(container).length).toBe(2);
      });

      const rows = getDigestRows(container);
      await userEvent.click(rows[0]);
      await waitFor(() => {
        expect(screen.getByText("Detail for first")).toBeInTheDocument();
      });

      await userEvent.click(rows[1]);
      await waitFor(() => {
        expect(screen.getByText("Detail for second")).toBeInTheDocument();
      });
      expect(screen.queryByText("Detail for first")).not.toBeInTheDocument();
    });

    it("supports keyboard activation via Enter", async () => {
      setupSuccess({}, {}, [makeDigestItem({ id: 1 })]);
      mockGetDigest.mockResolvedValue(makeDetail({ id: 1 }));
      const { container } = renderPanel();
      await waitFor(() => {
        expect(getDigestRows(container).length).toBe(1);
      });

      const row = getDigestRows(container)[0];
      row.focus();
      await userEvent.keyboard("{Enter}");
      await waitFor(() => {
        expect(screen.getByText("Digest #1")).toBeInTheDocument();
      });
    });

    it("supports keyboard activation via Space", async () => {
      setupSuccess({}, {}, [makeDigestItem({ id: 1 })]);
      mockGetDigest.mockResolvedValue(makeDetail({ id: 1 }));
      const { container } = renderPanel();
      await waitFor(() => {
        expect(getDigestRows(container).length).toBe(1);
      });

      const row = getDigestRows(container)[0];
      row.focus();
      await userEvent.keyboard(" ");
      await waitFor(() => {
        expect(screen.getByText("Digest #1")).toBeInTheDocument();
      });
    });
  });

  // ── Race condition guard ──

  describe("race condition guard", () => {
    it("ignores stale list responses", async () => {
      // Set up initial load
      setupSuccess({}, {}, [makeDigestItem({ id: 1, summary: "Initial load" })]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Initial load")).toBeInTheDocument();
      });

      // Set up a slow response for the refresh
      let resolveStale: (v: DigestListResponse) => void;
      const stalePromise = new Promise<DigestListResponse>((r) => { resolveStale = r; });
      mockListDigests.mockReturnValueOnce(stalePromise);
      mockGetDigestStats.mockResolvedValue(makeStats());

      // Trigger refresh (first re-fetch — will be slow)
      await userEvent.click(screen.getByLabelText("Refresh"));

      // Trigger a filter change (second re-fetch — fast, overtakes the first)
      const freshResponse = makeListResponse(
        [makeDigestItem({ id: 99, summary: "Fresh filtered result" })],
        { total: 1 },
      );
      mockListDigests.mockResolvedValueOnce(freshResponse);
      mockGetDigestStats.mockResolvedValue(makeStats());
      const select = screen.getByLabelText("Filter by status");
      await userEvent.selectOptions(select, "Completed");

      await waitFor(() => {
        expect(screen.getByText("Fresh filtered result")).toBeInTheDocument();
      });

      // Now resolve the stale refresh response
      resolveStale!(makeListResponse(
        [makeDigestItem({ id: 1, summary: "Stale refresh result" })],
        { total: 1 },
      ));

      // Wait a tick — stale response should be ignored
      await new Promise((r) => setTimeout(r, 50));
      expect(screen.queryByText("Stale refresh result")).not.toBeInTheDocument();
      expect(screen.getByText("Fresh filtered result")).toBeInTheDocument();
    });

    it("ignores stale detail responses", async () => {
      const items = [
        makeDigestItem({ id: 1, summary: "Digest one" }),
        makeDigestItem({ id: 2, summary: "Digest two" }),
      ];
      setupSuccess({}, {}, items);

      let resolveFirst: (v: DigestDetailResponse) => void;
      const firstDetailPromise = new Promise<DigestDetailResponse>((r) => { resolveFirst = r; });

      const { container } = renderPanel();
      await waitFor(() => {
        expect(getDigestRows(container).length).toBe(2);
      });

      // Click first row — mock returns a slow promise
      mockGetDigest.mockReturnValueOnce(firstDetailPromise);
      await userEvent.click(getDigestRows(container)[0]);

      // Immediately click second row — mock returns fast
      mockGetDigest.mockResolvedValueOnce(makeDetail({ id: 2, summary: "Detail for second" }));
      await userEvent.click(getDigestRows(container)[1]);

      // Second detail should show
      await waitFor(() => {
        expect(screen.getByText("Detail for second")).toBeInTheDocument();
      });

      // Now resolve the first (stale) detail
      resolveFirst!(makeDetail({ id: 1, summary: "Stale detail for first" }));

      // Wait a tick — stale response should be ignored
      await new Promise((r) => setTimeout(r, 50));
      expect(screen.queryByText("Stale detail for first")).not.toBeInTheDocument();
      expect(screen.getByText("Detail for second")).toBeInTheDocument();
    });
  });

  // ── API integration ──

  describe("API integration", () => {
    it("passes correct params on initial load", async () => {
      setupSuccess();
      renderPanel();
      await waitFor(() => {
        expect(mockListDigests).toHaveBeenCalledWith({
          status: undefined,
          limit: 20,
          offset: 0,
        });
      });
      expect(mockGetDigestStats).toHaveBeenCalledTimes(1);
    });

    it("fetches detail by ID when row is clicked", async () => {
      setupSuccess({}, {}, [makeDigestItem({ id: 77 })]);
      mockGetDigest.mockResolvedValue(makeDetail({ id: 77 }));
      const { container } = renderPanel();
      await waitFor(() => {
        expect(getDigestRows(container).length).toBe(1);
      });

      await userEvent.click(getDigestRows(container)[0]);
      await waitFor(() => {
        expect(mockGetDigest).toHaveBeenCalledWith(77);
      });
    });
  });

  // ── Task navigation from source retrospectives ──

  describe("source task navigation", () => {
    function setupWithSources(onNavigateToTask = vi.fn()) {
      setupSuccess();
      mockGetDigest.mockResolvedValue(makeDetail({
        id: 1,
        sources: [
          makeSource({ commentId: "c-1", taskId: "task-10", agentId: "coder-1" }),
          makeSource({ commentId: "c-2", taskId: "task-20", agentId: "reviewer-1" }),
        ],
      }));
      return { onNavigateToTask };
    }

    it("renders task IDs as links when onNavigateToTask is provided", async () => {
      const { onNavigateToTask } = setupWithSources();
      const { container } = renderPanel({ onNavigateToTask });

      await waitFor(() => expect(getDigestRows(container).length).toBeGreaterThan(0));
      await userEvent.click(getDigestRows(container)[0]);

      await waitFor(() => {
        expect(screen.getByText(/Task: task-10/)).toBeInTheDocument();
      });

      const taskLink = screen.getByText(/Task: task-10/);
      expect(taskLink).toHaveAttribute("role", "link");
    });

    it("does not render task IDs as links when onNavigateToTask is absent", async () => {
      setupSuccess();
      mockGetDigest.mockResolvedValue(makeDetail({
        id: 1,
        sources: [makeSource({ commentId: "c-1", taskId: "task-10" })],
      }));
      const { container } = renderPanel();

      await waitFor(() => expect(getDigestRows(container).length).toBeGreaterThan(0));
      await userEvent.click(getDigestRows(container)[0]);

      await waitFor(() => {
        expect(screen.getByText(/Task: task-10/)).toBeInTheDocument();
      });

      const taskEl = screen.getByText(/Task: task-10/);
      expect(taskEl).not.toHaveAttribute("role", "link");
    });

    it("calls onNavigateToTask with correct taskId when clicking source task link", async () => {
      const { onNavigateToTask } = setupWithSources();
      const { container } = renderPanel({ onNavigateToTask });

      await waitFor(() => expect(getDigestRows(container).length).toBeGreaterThan(0));
      await userEvent.click(getDigestRows(container)[0]);

      await waitFor(() => {
        expect(screen.getByText(/Task: task-10/)).toBeInTheDocument();
      });

      await userEvent.click(screen.getByText(/Task: task-10/));

      expect(onNavigateToTask).toHaveBeenCalledTimes(1);
      expect(onNavigateToTask).toHaveBeenCalledWith("task-10");
    });

    it("navigates to different tasks from different source retros", async () => {
      const { onNavigateToTask } = setupWithSources();
      const { container } = renderPanel({ onNavigateToTask });

      await waitFor(() => expect(getDigestRows(container).length).toBeGreaterThan(0));
      await userEvent.click(getDigestRows(container)[0]);

      await waitFor(() => {
        expect(screen.getByText(/Task: task-10/)).toBeInTheDocument();
        expect(screen.getByText(/Task: task-20/)).toBeInTheDocument();
      });

      await userEvent.click(screen.getByText(/Task: task-10/));
      await userEvent.click(screen.getByText(/Task: task-20/));

      expect(onNavigateToTask).toHaveBeenCalledTimes(2);
      expect(onNavigateToTask).toHaveBeenNthCalledWith(1, "task-10");
      expect(onNavigateToTask).toHaveBeenNthCalledWith(2, "task-20");
    });

    it("renders open icon on task link when onNavigateToTask is provided", async () => {
      const { onNavigateToTask } = setupWithSources();
      const { container } = renderPanel({ onNavigateToTask });

      await waitFor(() => expect(getDigestRows(container).length).toBeGreaterThan(0));
      await userEvent.click(getDigestRows(container)[0]);

      await waitFor(() => {
        expect(screen.getByText(/Task: task-10/)).toBeInTheDocument();
      });

      const taskLink = screen.getByText(/Task: task-10/);
      const svg = taskLink.querySelector("svg");
      expect(svg).not.toBeNull();
    });

    it("task link is keyboard-activatable via Enter", async () => {
      const { onNavigateToTask } = setupWithSources();
      const { container } = renderPanel({ onNavigateToTask });

      await waitFor(() => expect(getDigestRows(container).length).toBeGreaterThan(0));
      await userEvent.click(getDigestRows(container)[0]);

      await waitFor(() => {
        expect(screen.getByText(/Task: task-10/)).toBeInTheDocument();
      });

      const taskLink = screen.getByText(/Task: task-10/);
      expect(taskLink).toHaveAttribute("tabindex", "0");
      taskLink.focus();
      await userEvent.keyboard("{Enter}");

      expect(onNavigateToTask).toHaveBeenCalledTimes(1);
      expect(onNavigateToTask).toHaveBeenCalledWith("task-10");
    });
  });

  describe("real-time refresh via refreshTrigger", () => {
    it("re-fetches when refreshTrigger prop changes", async () => {
      const { rerender } = render(
        <FluentProvider theme={webDarkTheme}>
          <DigestPanel refreshTrigger={0} />
        </FluentProvider>,
      );
      await waitFor(() => expect(mockListDigests).toHaveBeenCalledTimes(1));

      mockListDigests.mockClear();
      rerender(
        <FluentProvider theme={webDarkTheme}>
          <DigestPanel refreshTrigger={1} />
        </FluentProvider>,
      );
      await waitFor(() => expect(mockListDigests).toHaveBeenCalledTimes(1));
    });

    it("does not re-fetch when refreshTrigger stays the same", async () => {
      const { rerender } = render(
        <FluentProvider theme={webDarkTheme}>
          <DigestPanel refreshTrigger={5} />
        </FluentProvider>,
      );
      await waitFor(() => expect(mockListDigests).toHaveBeenCalledTimes(1));

      mockListDigests.mockClear();
      rerender(
        <FluentProvider theme={webDarkTheme}>
          <DigestPanel refreshTrigger={5} />
        </FluentProvider>,
      );
      // Should not have been called again
      expect(mockListDigests).not.toHaveBeenCalled();
    });

    it("re-fetches multiple times for successive refreshTrigger increments", async () => {
      setupSuccess();
      const { rerender } = render(
        <FluentProvider theme={webDarkTheme}>
          <DigestPanel refreshTrigger={0} />
        </FluentProvider>,
      );
      await waitFor(() => expect(mockListDigests).toHaveBeenCalledTimes(1));

      mockListDigests.mockClear();
      setupSuccess();
      rerender(
        <FluentProvider theme={webDarkTheme}>
          <DigestPanel refreshTrigger={1} />
        </FluentProvider>,
      );
      await waitFor(() => expect(mockListDigests).toHaveBeenCalledTimes(1));

      mockListDigests.mockClear();
      setupSuccess();
      rerender(
        <FluentProvider theme={webDarkTheme}>
          <DigestPanel refreshTrigger={2} />
        </FluentProvider>,
      );
      await waitFor(() => expect(mockListDigests).toHaveBeenCalledTimes(1));
    });

    it("also re-fetches stats when refreshTrigger changes", async () => {
      setupSuccess();
      const { rerender } = render(
        <FluentProvider theme={webDarkTheme}>
          <DigestPanel refreshTrigger={0} />
        </FluentProvider>,
      );
      await waitFor(() => expect(mockGetDigestStats).toHaveBeenCalledTimes(1));

      mockGetDigestStats.mockClear();
      setupSuccess();
      rerender(
        <FluentProvider theme={webDarkTheme}>
          <DigestPanel refreshTrigger={1} />
        </FluentProvider>,
      );
      await waitFor(() => expect(mockGetDigestStats).toHaveBeenCalledTimes(1));
    });

    it("preserves selected detail across refresh trigger", async () => {
      setupSuccess(undefined, undefined, [
        makeDigestItem({ id: 1 }),
        makeDigestItem({ id: 2 }),
      ]);
      mockGetDigest.mockResolvedValue(makeDetail({ id: 1, summary: "Selected digest detail" }));

      const { container, rerender } = render(
        <FluentProvider theme={webDarkTheme}>
          <DigestPanel refreshTrigger={0} />
        </FluentProvider>,
      );

      await waitFor(() => {
        expect(getDigestRows(container).length).toBeGreaterThan(0);
      });

      // Select a digest to show detail
      await userEvent.click(getDigestRows(container)[0]);
      await waitFor(() => {
        expect(screen.getByText("Selected digest detail")).toBeInTheDocument();
      });

      // Trigger refresh — selectedId state is preserved across re-fetch
      setupSuccess(undefined, undefined, [
        makeDigestItem({ id: 1 }),
        makeDigestItem({ id: 2 }),
      ]);
      rerender(
        <FluentProvider theme={webDarkTheme}>
          <DigestPanel refreshTrigger={1} />
        </FluentProvider>,
      );

      // Wait for the refresh fetch to complete
      await waitFor(() => {
        expect(mockListDigests).toHaveBeenCalled();
      });

      // Detail should still be visible (selectedId not cleared by list refresh)
      expect(screen.getByText("Selected digest detail")).toBeInTheDocument();
    });

    it("handles list fetch error during refresh gracefully", async () => {
      setupSuccess();
      const { rerender } = render(
        <FluentProvider theme={webDarkTheme}>
          <DigestPanel refreshTrigger={0} />
        </FluentProvider>,
      );
      await waitFor(() => expect(mockListDigests).toHaveBeenCalledTimes(1));

      // Next refresh fails
      mockListDigests.mockRejectedValue(new Error("Server error"));
      mockGetDigestStats.mockRejectedValue(new Error("Server error"));
      rerender(
        <FluentProvider theme={webDarkTheme}>
          <DigestPanel refreshTrigger={1} />
        </FluentProvider>,
      );

      await waitFor(() => {
        expect(screen.getByText(/Server error/)).toBeInTheDocument();
      });
    });

    it("discards stale refresh response when a newer fetch overtakes it", async () => {
      let resolveFirst: (v: ReturnType<typeof makeListResponse>) => void;
      const firstPromise = new Promise<ReturnType<typeof makeListResponse>>((r) => { resolveFirst = r; });

      setupSuccess();
      const { rerender } = render(
        <FluentProvider theme={webDarkTheme}>
          <DigestPanel refreshTrigger={0} />
        </FluentProvider>,
      );
      await waitFor(() => expect(mockListDigests).toHaveBeenCalledTimes(1));

      // Trigger refresh with a slow list response
      mockListDigests.mockReturnValueOnce(firstPromise);
      mockGetDigestStats.mockResolvedValue(makeStats());
      rerender(
        <FluentProvider theme={webDarkTheme}>
          <DigestPanel refreshTrigger={1} />
        </FluentProvider>,
      );

      // Trigger another refresh that resolves immediately
      const freshList = [makeDigestItem({ id: 99, summary: "Fresh digest" })];
      setupSuccess(undefined, undefined, freshList);
      rerender(
        <FluentProvider theme={webDarkTheme}>
          <DigestPanel refreshTrigger={2} />
        </FluentProvider>,
      );

      await waitFor(() => {
        expect(screen.getByText("Fresh digest")).toBeInTheDocument();
      });

      // Now resolve the stale first response — it should be ignored (fetchIdRef guard)
      resolveFirst!(makeListResponse([
        makeDigestItem({ id: 1, summary: "Stale digest" }),
        makeDigestItem({ id: 2, summary: "Also stale" }),
        makeDigestItem({ id: 3, summary: "Still stale" }),
      ]));

      // Wait a tick and verify the stale data didn't overwrite
      await waitFor(() => {
        expect(screen.getByText("Fresh digest")).toBeInTheDocument();
        expect(screen.queryByText("Stale digest")).not.toBeInTheDocument();
      });
    });
  });
});
