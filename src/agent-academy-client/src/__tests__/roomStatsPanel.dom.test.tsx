// @vitest-environment jsdom
import "@testing-library/jest-dom/vitest";
import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, waitFor, fireEvent } from "@testing-library/react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import RoomStatsPanel from "../RoomStatsPanel";
import type { UsageSummary, AgentUsageSummary, ErrorRecord } from "../api";

vi.mock("../api", async (importOriginal) => {
  const actual = await importOriginal<Record<string, unknown>>();
  return {
    ...actual,
    getRoomUsage: vi.fn(),
    getRoomUsageByAgent: vi.fn(),
    getRoomErrors: vi.fn(),
  };
});

import { getRoomUsage, getRoomUsageByAgent, getRoomErrors } from "../api";

const mockGetUsage = vi.mocked(getRoomUsage);
const mockGetAgents = vi.mocked(getRoomUsageByAgent);
const mockGetErrors = vi.mocked(getRoomErrors);

function makeUsage(overrides: Partial<UsageSummary> = {}): UsageSummary {
  return {
    totalInputTokens: 50000,
    totalOutputTokens: 30000,
    totalCost: 1.25,
    requestCount: 42,
    models: ["gpt-4"],
    ...overrides,
  };
}

function makeAgentUsage(overrides: Partial<AgentUsageSummary> = {}): AgentUsageSummary {
  return {
    agentId: "planner",
    totalInputTokens: 20000,
    totalOutputTokens: 10000,
    totalCost: 0.50,
    requestCount: 15,
    ...overrides,
  };
}

function makeError(overrides: Partial<ErrorRecord> = {}): ErrorRecord {
  return {
    agentId: "coder",
    roomId: "room-1",
    errorType: "ToolCallFailed",
    message: "Something went wrong",
    recoverable: false,
    timestamp: "2026-04-10T12:00:00Z",
    ...overrides,
  };
}

function renderPanel(roomId = "room-1") {
  return render(
    <FluentProvider theme={webDarkTheme}>
      <RoomStatsPanel roomId={roomId} />
    </FluentProvider>,
  );
}

describe("RoomStatsPanel", () => {
  beforeEach(() => { vi.resetAllMocks(); });
  afterEach(() => { cleanup(); document.body.innerHTML = ""; });

  // ── Loading ──

  it("shows spinner while loading", () => {
    mockGetUsage.mockReturnValue(new Promise(() => {}));
    mockGetAgents.mockReturnValue(new Promise(() => {}));
    mockGetErrors.mockReturnValue(new Promise(() => {}));
    renderPanel();
    expect(screen.getByText("Loading room stats…")).toBeInTheDocument();
  });

  // ── All failed ──

  it("shows error when all three fetches fail", async () => {
    mockGetUsage.mockRejectedValue(new Error("fail"));
    mockGetAgents.mockRejectedValue(new Error("fail"));
    mockGetErrors.mockRejectedValue(new Error("fail"));
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Failed to load room stats")).toBeInTheDocument();
    });
  });

  // ── Empty state ──

  it("shows empty message when no activity recorded", async () => {
    mockGetUsage.mockResolvedValue(makeUsage({ requestCount: 0, totalInputTokens: 0, totalOutputTokens: 0, totalCost: 0 }));
    mockGetAgents.mockResolvedValue([]);
    mockGetErrors.mockResolvedValue([]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("No activity recorded for this room yet.")).toBeInTheDocument();
    });
  });

  // ── Usage summary ──

  it("renders usage stat cards", async () => {
    mockGetUsage.mockResolvedValue(makeUsage({ requestCount: 42 }));
    mockGetAgents.mockResolvedValue([]);
    mockGetErrors.mockResolvedValue([]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("42")).toBeInTheDocument();
      expect(screen.getByText("Calls")).toBeInTheDocument();
    });
    expect(screen.getByText("Input")).toBeInTheDocument();
    expect(screen.getByText("Output")).toBeInTheDocument();
    expect(screen.getByText("Cost")).toBeInTheDocument();
  });

  // ── Per-agent breakdown ──

  it("renders per-agent table", async () => {
    mockGetUsage.mockResolvedValue(makeUsage());
    mockGetAgents.mockResolvedValue([
      makeAgentUsage({ agentId: "planner", requestCount: 15 }),
      makeAgentUsage({ agentId: "coder", requestCount: 27 }),
    ]);
    mockGetErrors.mockResolvedValue([]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("planner")).toBeInTheDocument();
      expect(screen.getByText("coder")).toBeInTheDocument();
    });
    expect(screen.getByText("Per-Agent")).toBeInTheDocument();
  });

  // ── Error table ──

  it("renders error records", async () => {
    mockGetUsage.mockResolvedValue(makeUsage());
    mockGetAgents.mockResolvedValue([]);
    mockGetErrors.mockResolvedValue([
      makeError({ agentId: "coder", message: "Tool timeout" }),
    ]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("coder")).toBeInTheDocument();
      expect(screen.getByText("Tool timeout")).toBeInTheDocument();
    });
  });

  it("shows 'Showing 5 of N errors' when more than 5 errors", async () => {
    mockGetUsage.mockResolvedValue(makeUsage());
    mockGetAgents.mockResolvedValue([]);
    mockGetErrors.mockResolvedValue(
      Array.from({ length: 8 }, (_, i) => makeError({ agentId: `agent-${i}`, message: `Error ${i}` })),
    );
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText(/Showing 5 of 8 errors/)).toBeInTheDocument();
    });
  });

  // ── No errors banner ──

  it("shows 'No errors' message when usage exists but no errors", async () => {
    mockGetUsage.mockResolvedValue(makeUsage());
    mockGetAgents.mockResolvedValue([]);
    mockGetErrors.mockResolvedValue([]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("No errors in this room.")).toBeInTheDocument();
    });
  });

  // ── Refresh ──

  it("re-fetches data on refresh click", async () => {
    mockGetUsage.mockResolvedValue(makeUsage());
    mockGetAgents.mockResolvedValue([]);
    mockGetErrors.mockResolvedValue([]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Refresh")).toBeInTheDocument();
    });
    expect(mockGetUsage).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByText("Refresh"));
    await waitFor(() => {
      expect(mockGetUsage).toHaveBeenCalledTimes(2);
    });
  });

  // ── Partial failure ──

  it("shows partial data when only usage fails", async () => {
    mockGetUsage.mockRejectedValue(new Error("usage fail"));
    mockGetAgents.mockResolvedValue([makeAgentUsage({ agentId: "bot" })]);
    mockGetErrors.mockResolvedValue([]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Failed to load room usage data")).toBeInTheDocument();
      expect(screen.getByText("bot")).toBeInTheDocument();
    });
  });
});
