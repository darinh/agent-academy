// @vitest-environment jsdom
import "@testing-library/jest-dom/vitest";
import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, waitFor, fireEvent } from "@testing-library/react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import SessionHistoryPanel from "../SessionHistoryPanel";
import type { ConversationSessionSnapshot, SessionStats } from "../api";

vi.mock("../api", async (importOriginal) => {
  const actual = await importOriginal<Record<string, unknown>>();
  return { ...actual, getSessions: vi.fn(), getSessionStats: vi.fn() };
});

import { getSessions, getSessionStats } from "../api";

const mockGetSessions = vi.mocked(getSessions);
const mockGetSessionStats = vi.mocked(getSessionStats);

function makeSession(overrides: Partial<ConversationSessionSnapshot> = {}): ConversationSessionSnapshot {
  return {
    id: "sess-1",
    roomId: "room-1",
    roomType: "Main",
    sequenceNumber: 1,
    status: "Active",
    summary: null,
    messageCount: 10,
    createdAt: "2026-04-10T12:00:00Z",
    archivedAt: null,
    ...overrides,
  };
}

function makeStats(overrides: Partial<SessionStats> = {}): SessionStats {
  return {
    totalSessions: 5,
    activeSessions: 2,
    archivedSessions: 3,
    totalMessages: 120,
    ...overrides,
  };
}

function renderPanel(props: { hoursBack?: number } = {}) {
  return render(
    <FluentProvider theme={webDarkTheme}>
      <SessionHistoryPanel {...props} />
    </FluentProvider>,
  );
}

describe("SessionHistoryPanel", () => {
  beforeEach(() => { vi.resetAllMocks(); });
  afterEach(() => { cleanup(); document.body.innerHTML = ""; });

  // ── Loading ──

  it("shows spinner while loading", () => {
    mockGetSessions.mockReturnValue(new Promise(() => {}));
    mockGetSessionStats.mockReturnValue(new Promise(() => {}));
    renderPanel();
    expect(screen.getByText("Loading session history…")).toBeInTheDocument();
  });

  // ── Error ──

  it("shows error when fetch fails and no cached data", async () => {
    mockGetSessions.mockRejectedValue(new Error("Connection lost"));
    mockGetSessionStats.mockResolvedValue(makeStats());
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Connection lost")).toBeInTheDocument();
    });
  });

  // ── Empty ──

  it("shows empty message when no sessions exist", async () => {
    mockGetSessions.mockResolvedValue({ sessions: [], totalCount: 0 });
    mockGetSessionStats.mockResolvedValue(makeStats({ totalSessions: 0 }));
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("No conversation sessions recorded yet.")).toBeInTheDocument();
    });
  });

  // ── Stats display ──

  it("renders stat cards from stats response", async () => {
    mockGetSessions.mockResolvedValue({ sessions: [makeSession()], totalCount: 1 });
    mockGetSessionStats.mockResolvedValue(makeStats({ totalSessions: 7, activeSessions: 3, archivedSessions: 4, totalMessages: 200 }));
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("7")).toBeInTheDocument();
      expect(screen.getByText("200")).toBeInTheDocument();
    });
    expect(screen.getByText("Total Sessions")).toBeInTheDocument();
    expect(screen.getByText("Total Messages")).toBeInTheDocument();
    // "Active" and "Archived" appear as both stat labels and filter buttons, so just check stat values
    expect(screen.getByText("3")).toBeInTheDocument();
    expect(screen.getByText("4")).toBeInTheDocument();
  });

  // ── Session table ──

  it("renders session rows with status, room, epoch, message count", async () => {
    mockGetSessions.mockResolvedValue({
      sessions: [
        makeSession({ id: "s1", roomId: "room-a", sequenceNumber: 3, messageCount: 42, status: "Active" }),
        makeSession({ id: "s2", roomId: "room-b", sequenceNumber: 1, messageCount: 7, status: "Archived", archivedAt: "2026-04-10T14:00:00Z" }),
      ],
      totalCount: 2,
    });
    mockGetSessionStats.mockResolvedValue(makeStats());
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("room-a")).toBeInTheDocument();
      expect(screen.getByText("room-b")).toBeInTheDocument();
    });
    expect(screen.getByText("#3")).toBeInTheDocument();
    expect(screen.getByText("42")).toBeInTheDocument();
    expect(screen.getByText("#1")).toBeInTheDocument();
    expect(screen.getByText("7")).toBeInTheDocument();
  });

  // ── Summary expand/collapse ──

  it("truncates long summaries and expands on 'Show more' click", async () => {
    const longSummary = "A".repeat(200);
    mockGetSessions.mockResolvedValue({
      sessions: [makeSession({ summary: longSummary })],
      totalCount: 1,
    });
    mockGetSessionStats.mockResolvedValue(makeStats());
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Show more")).toBeInTheDocument();
    });
    // Should show truncated text
    expect(screen.queryByText(longSummary)).not.toBeInTheDocument();
    fireEvent.click(screen.getByText("Show more"));
    expect(screen.getByText(longSummary)).toBeInTheDocument();
    expect(screen.getByText("Show less")).toBeInTheDocument();
  });

  it("does not show expand toggle for short summaries", async () => {
    mockGetSessions.mockResolvedValue({
      sessions: [makeSession({ summary: "Short summary" })],
      totalCount: 1,
    });
    mockGetSessionStats.mockResolvedValue(makeStats());
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Short summary")).toBeInTheDocument();
    });
    expect(screen.queryByText("Show more")).not.toBeInTheDocument();
  });

  // ── Filter buttons ──

  it("calls API with filter when clicking filter buttons", async () => {
    mockGetSessions.mockResolvedValue({ sessions: [makeSession()], totalCount: 1 });
    mockGetSessionStats.mockResolvedValue(makeStats());
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("room-1")).toBeInTheDocument();
    });

    // Initially called with no filter
    expect(mockGetSessions).toHaveBeenCalledWith(undefined, expect.any(Number), 0, undefined);

    // Find all "Archived" buttons/elements — the filter button is the one we want
    mockGetSessions.mockResolvedValue({ sessions: [], totalCount: 0 });
    const archivedBtns = screen.getAllByText("Archived");
    // Click the filter button (the one that is a <button> with the filter class)
    const filterBtn = archivedBtns.find((el) => el.tagName === "BUTTON");
    fireEvent.click(filterBtn!);
    await waitFor(() => {
      expect(mockGetSessions).toHaveBeenCalledWith("Archived", expect.any(Number), 0, undefined);
    });
  });

  // ── Refresh ──

  it("re-fetches data when Refresh button is clicked", async () => {
    mockGetSessions.mockResolvedValue({ sessions: [makeSession()], totalCount: 1 });
    mockGetSessionStats.mockResolvedValue(makeStats());
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("room-1")).toBeInTheDocument();
    });
    expect(mockGetSessions).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByText("Refresh"));
    await waitFor(() => {
      expect(mockGetSessions).toHaveBeenCalledTimes(2);
    });
  });

  // ── Pagination ──

  it("shows pagination and navigates pages", async () => {
    mockGetSessions.mockResolvedValue({
      sessions: Array.from({ length: 10 }, (_, i) => makeSession({ id: `s-${i}` })),
      totalCount: 25,
    });
    mockGetSessionStats.mockResolvedValue(makeStats());
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText(/1–10 of 25/)).toBeInTheDocument();
    });

    const olderBtn = screen.getByText("Older →");
    expect(olderBtn).not.toBeDisabled();
    const newerBtn = screen.getByText("← Newer");
    expect(newerBtn).toBeDisabled();

    fireEvent.click(olderBtn);
    await waitFor(() => {
      expect(mockGetSessions).toHaveBeenCalledWith(undefined, 10, 10, undefined);
    });
  });

  // ── Active row highlight ──

  it("shows 'In progress' for active sessions without summary", async () => {
    mockGetSessions.mockResolvedValue({
      sessions: [makeSession({ status: "Active", summary: null })],
      totalCount: 1,
    });
    mockGetSessionStats.mockResolvedValue(makeStats());
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("In progress")).toBeInTheDocument();
    });
  });
});
