// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import { createElement } from "react";
import { render, screen, fireEvent, waitFor, cleanup, act } from "@testing-library/react";
import "@testing-library/jest-dom/vitest";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../api", () => ({
  browseMemories: vi.fn(),
  getMemoryStats: vi.fn(),
  deleteMemory: vi.fn(),
}));

import MemoryBrowserPanel from "../MemoryBrowserPanel";
import type { BrowseMemoriesResponse, MemoryStatsResponse, AgentDefinition } from "../api";
import { browseMemories, getMemoryStats, deleteMemory } from "../api";

const mockBrowse = vi.mocked(browseMemories);
const mockStats = vi.mocked(getMemoryStats);
const mockDelete = vi.mocked(deleteMemory);

function makeAgents(count = 2): AgentDefinition[] {
  return Array.from({ length: count }, (_, i) => ({
    id: `agent-${i + 1}`,
    name: `Agent ${i + 1}`,
    role: "Coder",
    persona: "",
    summary: "",
    startupPrompt: "",
    enabledTools: [],
    allowedCommands: [],
    capabilityTags: [],
    models: [],
    isCustom: false,
    autoJoinDefaultRoom: true,
  })) as AgentDefinition[];
}

function makeBrowseResponse(count = 3): BrowseMemoriesResponse {
  return {
    total: count,
    memories: Array.from({ length: count }, (_, i) => ({
      agentId: "agent-1",
      category: i % 2 === 0 ? "decision" : "lesson",
      key: `key-${i}`,
      value: `Value for memory ${i}`,
      createdAt: "2026-04-10T12:00:00Z",
      updatedAt: null,
      lastAccessedAt: null,
      expiresAt: null,
    })),
  };
}

function makeStatsResponse(): MemoryStatsResponse {
  return {
    agentId: "agent-1",
    totalMemories: 5,
    activeMemories: 4,
    expiredMemories: 1,
    categories: [
      { category: "decision", total: 3, active: 3, expired: 0, lastUpdated: "2026-04-10T12:00:00Z" },
      { category: "lesson", total: 2, active: 1, expired: 1, lastUpdated: "2026-04-09T12:00:00Z" },
    ],
  };
}

function wrap(el: React.ReactElement) {
  return createElement(FluentProvider, { theme: webDarkTheme }, el);
}

/** Render and wait for the initial data fetch to complete. */
async function renderAndWaitForData(agents = makeAgents()) {
  render(wrap(createElement(MemoryBrowserPanel, { agents })));
  await waitFor(() => expect(screen.queryByText("Loading memories…")).not.toBeInTheDocument());
}

beforeEach(() => {
  mockBrowse.mockImplementation(() => Promise.resolve(makeBrowseResponse()));
  mockStats.mockImplementation(() => Promise.resolve(makeStatsResponse()));
  mockDelete.mockImplementation(() => Promise.resolve({ status: "deleted", agentId: "agent-1", key: "key-0" }));
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

// ── Tests ──────────────────────────────────────────────────────────────

describe("MemoryBrowserPanel", () => {
  it("renders and fetches data for first agent", async () => {
    await renderAndWaitForData();
    expect(mockBrowse).toHaveBeenCalledWith(
      expect.objectContaining({ agentId: "agent-1" }),
    );
    expect(mockStats).toHaveBeenCalledWith("agent-1");
  });

  it("shows loading spinner while fetching", () => {
    mockBrowse.mockReturnValue(new Promise(() => {}));
    mockStats.mockReturnValue(new Promise(() => {}));
    render(wrap(createElement(MemoryBrowserPanel, { agents: makeAgents() })));
    expect(screen.getByText("Loading memories…")).toBeInTheDocument();
  });

  it("displays memory entries after load", async () => {
    await renderAndWaitForData();
    expect(screen.getByText("key-0")).toBeInTheDocument();
    expect(screen.getByText("key-1")).toBeInTheDocument();
    expect(screen.getByText("key-2")).toBeInTheDocument();
  });

  it("displays stats badges", async () => {
    await renderAndWaitForData();
    expect(screen.getByText("4 active")).toBeInTheDocument();
    expect(screen.getByText("1 expired")).toBeInTheDocument();
  });

  it("renders category chips from stats", async () => {
    await renderAndWaitForData();
    expect(screen.getByText("all")).toBeInTheDocument();
    // Chips use data-category attribute for reliable selection
    expect(document.querySelector("[data-category='decision']")).toBeInTheDocument();
    expect(document.querySelector("[data-category='lesson']")).toBeInTheDocument();
  });

  it("filters by category when chip is clicked", async () => {
    await renderAndWaitForData();

    const chip = document.querySelector("[data-category='decision']") as HTMLElement;
    expect(chip).toBeTruthy();

    await act(async () => { fireEvent.click(chip); });
    await waitFor(() => {
      expect(mockBrowse).toHaveBeenCalledWith(
        expect.objectContaining({ category: "decision" }),
      );
    });
  });

  it("switches agent on dropdown change", async () => {
    await renderAndWaitForData();

    const select = screen.getByLabelText("Select agent");
    await act(async () => {
      fireEvent.change(select, { target: { value: "agent-2" } });
    });

    await waitFor(() => {
      expect(mockBrowse).toHaveBeenCalledWith(
        expect.objectContaining({ agentId: "agent-2" }),
      );
    });
  });

  it("shows empty state when no memories", async () => {
    mockBrowse.mockImplementation(() => Promise.resolve({ total: 0, memories: [] }));
    await renderAndWaitForData();
    expect(screen.getByText(/has no memories/)).toBeInTheDocument();
  });

  it("shows error message on fetch failure", async () => {
    mockBrowse.mockImplementation(() => Promise.reject(new Error("Network error")));
    await renderAndWaitForData();
    expect(screen.getByText(/Network error/)).toBeInTheDocument();
  });

  it("deletes memory and removes from list", async () => {
    await renderAndWaitForData();
    expect(screen.getByText("key-0")).toBeInTheDocument();

    const deleteButtons = screen.getAllByLabelText(/Delete memory/);
    await act(async () => { fireEvent.click(deleteButtons[0]); });

    await waitFor(() => {
      expect(mockDelete).toHaveBeenCalledWith("agent-1", "key-0");
    });
    expect(screen.queryByText("key-0")).not.toBeInTheDocument();
  });

  it("toggles include expired checkbox", async () => {
    await renderAndWaitForData();

    const checkbox = screen.getByRole("checkbox");
    await act(async () => { fireEvent.click(checkbox); });

    await waitFor(() => {
      expect(mockBrowse).toHaveBeenCalledWith(
        expect.objectContaining({ includeExpired: true }),
      );
    });
  });

  it("refresh button re-fetches data", async () => {
    await renderAndWaitForData();
    const callsBefore = mockBrowse.mock.calls.length;

    const refreshBtn = screen.getByLabelText("Refresh memories");
    await act(async () => { fireEvent.click(refreshBtn); });

    await waitFor(() => {
      expect(mockBrowse.mock.calls.length).toBeGreaterThan(callsBefore);
    });
  });

  it("does not fetch when no agents available and shows empty state", () => {
    render(wrap(createElement(MemoryBrowserPanel, { agents: [] })));
    expect(mockBrowse).not.toHaveBeenCalled();
    expect(mockStats).not.toHaveBeenCalled();
    expect(screen.getByText("No agents configured")).toBeInTheDocument();
    expect(screen.getByText(/Agent memories will appear here/)).toBeInTheDocument();
  });

  it("debounces search input", async () => {
    vi.useFakeTimers();
    render(wrap(createElement(MemoryBrowserPanel, { agents: makeAgents() })));
    // Flush initial effects + debounce timer
    await act(async () => { vi.advanceTimersByTime(500); });

    const input = screen.getByPlaceholderText("Search memories…");

    await act(async () => {
      fireEvent.change(input, { target: { value: "test query" } });
    });

    // Should NOT have fired with the search yet (debounce pending)
    const hasSearchCallEarly = mockBrowse.mock.calls.some(
      (c) => (c[0] as { search?: string }).search === "test query",
    );
    expect(hasSearchCallEarly).toBe(false);

    // Advance past debounce (300ms) and flush promises
    await act(async () => { vi.advanceTimersByTime(400); });

    // Now the search should have fired
    const hasSearchCall = mockBrowse.mock.calls.some(
      (c) => (c[0] as { search?: string }).search === "test query",
    );
    expect(hasSearchCall).toBe(true);

    vi.useRealTimers();
  });

  describe("real-time refresh via refreshTrigger", () => {
    it("re-fetches when refreshTrigger prop changes", async () => {
      const { rerender } = render(
        wrap(createElement(MemoryBrowserPanel, { agents: makeAgents(), refreshTrigger: 0 })),
      );
      await waitFor(() => expect(mockBrowse).toHaveBeenCalledTimes(1));

      mockBrowse.mockClear();
      mockStats.mockClear();
      rerender(
        wrap(createElement(MemoryBrowserPanel, { agents: makeAgents(), refreshTrigger: 1 })),
      );
      await waitFor(() => expect(mockBrowse).toHaveBeenCalledTimes(1));
    });

    it("does not re-fetch when refreshTrigger stays the same", async () => {
      const { rerender } = render(
        wrap(createElement(MemoryBrowserPanel, { agents: makeAgents(), refreshTrigger: 5 })),
      );
      await waitFor(() => expect(mockBrowse).toHaveBeenCalledTimes(1));

      mockBrowse.mockClear();
      rerender(
        wrap(createElement(MemoryBrowserPanel, { agents: makeAgents(), refreshTrigger: 5 })),
      );
      expect(mockBrowse).not.toHaveBeenCalled();
    });

    it("works without refreshTrigger prop (defaults to 0)", async () => {
      await renderAndWaitForData();
      expect(mockBrowse).toHaveBeenCalledTimes(1);
    });

    it("re-fetches multiple times for successive refreshTrigger increments", async () => {
      const { rerender } = render(
        wrap(createElement(MemoryBrowserPanel, { agents: makeAgents(), refreshTrigger: 0 })),
      );
      await waitFor(() => expect(mockBrowse).toHaveBeenCalledTimes(1));

      mockBrowse.mockClear();
      rerender(
        wrap(createElement(MemoryBrowserPanel, { agents: makeAgents(), refreshTrigger: 1 })),
      );
      await waitFor(() => expect(mockBrowse).toHaveBeenCalledTimes(1));

      mockBrowse.mockClear();
      rerender(
        wrap(createElement(MemoryBrowserPanel, { agents: makeAgents(), refreshTrigger: 2 })),
      );
      await waitFor(() => expect(mockBrowse).toHaveBeenCalledTimes(1));
    });

    it("also re-fetches stats when refreshTrigger changes", async () => {
      const { rerender } = render(
        wrap(createElement(MemoryBrowserPanel, { agents: makeAgents(), refreshTrigger: 0 })),
      );
      await waitFor(() => expect(mockStats).toHaveBeenCalledTimes(1));

      mockStats.mockClear();
      rerender(
        wrap(createElement(MemoryBrowserPanel, { agents: makeAgents(), refreshTrigger: 1 })),
      );
      await waitFor(() => expect(mockStats).toHaveBeenCalledTimes(1));
    });

    it("handles browse failure gracefully during refresh (shows error, keeps stats)", async () => {
      const { rerender } = render(
        wrap(createElement(MemoryBrowserPanel, { agents: makeAgents(), refreshTrigger: 0 })),
      );
      await waitFor(() => expect(mockBrowse).toHaveBeenCalledTimes(1));

      // Stats should be visible after initial load
      expect(screen.getByText("4 active")).toBeInTheDocument();

      // Next refresh: browse fails, stats succeeds (Promise.allSettled)
      mockBrowse.mockRejectedValue(new Error("Network timeout"));
      mockStats.mockResolvedValue(makeStatsResponse());
      rerender(
        wrap(createElement(MemoryBrowserPanel, { agents: makeAgents(), refreshTrigger: 1 })),
      );

      await waitFor(() => {
        expect(screen.getByText(/Network timeout/)).toBeInTheDocument();
      });
      // Stats should still be visible despite browse failure
      expect(screen.getByText("4 active")).toBeInTheDocument();
    });

    it("discards stale refresh response when a newer fetch overtakes it", async () => {
      let resolveFirst: (v: ReturnType<typeof makeBrowseResponse>) => void;
      const firstPromise = new Promise<ReturnType<typeof makeBrowseResponse>>((r) => { resolveFirst = r; });

      const { rerender } = render(
        wrap(createElement(MemoryBrowserPanel, { agents: makeAgents(), refreshTrigger: 0 })),
      );
      await waitFor(() => expect(mockBrowse).toHaveBeenCalledTimes(1));

      // Trigger refresh with a slow response
      mockBrowse.mockReturnValueOnce(firstPromise);
      rerender(
        wrap(createElement(MemoryBrowserPanel, { agents: makeAgents(), refreshTrigger: 1 })),
      );

      // Trigger another refresh that resolves immediately with uniquely identifiable data
      const freshResponse: ReturnType<typeof makeBrowseResponse> = {
        total: 1,
        memories: [{
          agentId: "agent-1",
          category: "decision",
          key: "fresh-unique-key",
          value: "Fresh value",
          createdAt: "2026-04-10T12:00:00Z",
          updatedAt: null,
          lastAccessedAt: null,
          expiresAt: null,
        }],
      };
      mockBrowse.mockResolvedValue(freshResponse);
      rerender(
        wrap(createElement(MemoryBrowserPanel, { agents: makeAgents(), refreshTrigger: 2 })),
      );

      // Wait for the fresh response to render
      await waitFor(() => {
        expect(screen.getByText("fresh-unique-key")).toBeInTheDocument();
      });

      // Now resolve the stale first response — it should be ignored
      resolveFirst!(makeBrowseResponse(5));
      await waitFor(() => {
        // Should still show the fresh data, not the stale 5-item response
        expect(screen.getByText("fresh-unique-key")).toBeInTheDocument();
        expect(screen.queryByText("key-4")).not.toBeInTheDocument();
      });
    });
  });
});
