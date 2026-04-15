// @vitest-environment jsdom
import "@testing-library/jest-dom/vitest";
import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, waitFor, fireEvent } from "@testing-library/react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import UsagePanel from "../UsagePanel";
import type { UsageSummary, LlmUsageRecord } from "../api";

vi.mock("../api", async (importOriginal) => {
  const actual = await importOriginal<Record<string, unknown>>();
  return {
    ...actual,
    getGlobalUsage: vi.fn(),
    getGlobalUsageRecords: vi.fn(),
  };
});

import { getGlobalUsage, getGlobalUsageRecords } from "../api";

const mockGetUsage = vi.mocked(getGlobalUsage);
const mockGetRecords = vi.mocked(getGlobalUsageRecords);

function makeSummary(overrides: Partial<UsageSummary> = {}): UsageSummary {
  return {
    totalInputTokens: 100000,
    totalOutputTokens: 50000,
    totalCost: 3.50,
    requestCount: 75,
    models: ["gpt-4", "claude-3"],
    ...overrides,
  };
}

function makeRecord(overrides: Partial<LlmUsageRecord> = {}): LlmUsageRecord {
  return {
    id: "rec-1",
    agentId: "planner",
    roomId: "room-1",
    model: "gpt-4",
    inputTokens: 1000,
    outputTokens: 500,
    cacheReadTokens: 0,
    cacheWriteTokens: 0,
    cost: 0.05,
    durationMs: 2500,
    reasoningEffort: null,
    recordedAt: "2026-04-10T12:00:00Z",
    ...overrides,
  };
}

function renderPanel(props: { hoursBack?: number } = {}) {
  return render(
    <FluentProvider theme={webDarkTheme}>
      <UsagePanel {...props} />
    </FluentProvider>,
  );
}

describe("UsagePanel", () => {
  beforeEach(() => { vi.resetAllMocks(); });
  afterEach(() => { cleanup(); document.body.innerHTML = ""; });

  // ── Loading ──

  it("shows spinner while loading", () => {
    mockGetUsage.mockReturnValue(new Promise(() => {}));
    mockGetRecords.mockReturnValue(new Promise(() => {}));
    renderPanel();
    expect(screen.getByText("Loading usage data…")).toBeInTheDocument();
  });

  // ── Error ──

  it("shows error when both fetches fail", async () => {
    mockGetUsage.mockRejectedValue(new Error("Network error"));
    mockGetRecords.mockRejectedValue(new Error("Network error"));
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Network error")).toBeInTheDocument();
    });
  });

  // ── Summary stat cards ──

  it("renders summary stat cards", async () => {
    mockGetUsage.mockResolvedValue(makeSummary({ requestCount: 75 }));
    mockGetRecords.mockResolvedValue([]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("75")).toBeInTheDocument();
      expect(screen.getByText("LLM Calls")).toBeInTheDocument();
    });
    expect(screen.getByText("Input Tokens")).toBeInTheDocument();
    expect(screen.getByText("Output Tokens")).toBeInTheDocument();
    expect(screen.getByText("Total Cost")).toBeInTheDocument();
  });

  // ── Models ──

  it("renders model tags", async () => {
    mockGetUsage.mockResolvedValue(makeSummary({ models: ["gpt-4", "claude-3"] }));
    mockGetRecords.mockResolvedValue([]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("gpt-4")).toBeInTheDocument();
      expect(screen.getByText("claude-3")).toBeInTheDocument();
    });
    expect(screen.getByText("Models")).toBeInTheDocument();
  });

  // ── Per-agent breakdown ──

  it("derives per-agent breakdown from records", async () => {
    mockGetUsage.mockResolvedValue(makeSummary());
    mockGetRecords.mockResolvedValue([
      makeRecord({ agentId: "planner", cost: 0.10 }),
      makeRecord({ id: "rec-2", agentId: "coder", cost: 0.20 }),
      makeRecord({ id: "rec-3", agentId: "planner", cost: 0.05 }),
    ]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Per-Agent Breakdown")).toBeInTheDocument();
    });
    // Both agents appear (may be multiple elements since they're in breakdown and records table)
    expect(screen.getAllByText("planner").length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText("coder").length).toBeGreaterThanOrEqual(1);
  });

  // ── Empty records ──

  it("shows empty message when no records exist", async () => {
    mockGetUsage.mockResolvedValue(makeSummary({ requestCount: 0, totalInputTokens: 0, totalOutputTokens: 0, totalCost: 0, models: [] }));
    mockGetRecords.mockResolvedValue([]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("No LLM usage recorded yet.")).toBeInTheDocument();
    });
  });

  // ── Records table ──

  it("renders records table with agent, model, and duration columns", async () => {
    mockGetUsage.mockResolvedValue(makeSummary({ models: [] })); // no model tags
    mockGetRecords.mockResolvedValue([
      makeRecord({ agentId: "unique-rec-agent", model: "test-model-x", durationMs: 1234 }),
    ]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Recent LLM Calls")).toBeInTheDocument();
    });
    // unique-rec-agent only appears once (in per-agent breakdown it also shows, use getAllBy)
    expect(screen.getAllByText("unique-rec-agent").length).toBeGreaterThanOrEqual(1);
    // test-model-x is unique to the records table (no model tags because models=[])
    expect(screen.getByText("test-model-x")).toBeInTheDocument();
    // Duration column renders formatted latency
    expect(screen.getByText("1.2s")).toBeInTheDocument();
  });

  // ── Pagination ──

  it("shows pagination for many records and navigates pages", async () => {
    mockGetUsage.mockResolvedValue(makeSummary());
    mockGetRecords.mockResolvedValue(
      Array.from({ length: 20 }, (_, i) =>
        makeRecord({ id: `rec-${i}`, agentId: `agent-${i}`, recordedAt: `2026-04-10T${String(i).padStart(2, "0")}:00:00Z` }),
      ),
    );
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText(/1–15 of 20/)).toBeInTheDocument();
    });

    const olderBtn = screen.getByText("Older →");
    expect(olderBtn).not.toBeDisabled();
    fireEvent.click(olderBtn);
    await waitFor(() => {
      expect(screen.getByText(/16–20 of 20/)).toBeInTheDocument();
    });
  });

  // ── Refresh ──

  it("re-fetches data on refresh click", async () => {
    mockGetUsage.mockResolvedValue(makeSummary());
    mockGetRecords.mockResolvedValue([makeRecord()]);
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

  // ── Sparklines ──

  it("renders sparklines when 2+ records exist", async () => {
    mockGetUsage.mockResolvedValue(makeSummary());
    mockGetRecords.mockResolvedValue([
      makeRecord({ id: "r1", recordedAt: "2026-04-10T12:00:00Z" }),
      makeRecord({ id: "r2", recordedAt: "2026-04-10T13:00:00Z" }),
    ]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByTestId("usage-sparkline-requests")).toBeInTheDocument();
      expect(screen.getByTestId("usage-sparkline-tokens")).toBeInTheDocument();
    });
  });

  it("does not render sparklines with fewer than 2 records", async () => {
    mockGetUsage.mockResolvedValue(makeSummary());
    mockGetRecords.mockResolvedValue([makeRecord()]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Recent LLM Calls")).toBeInTheDocument();
    });
    expect(screen.queryByTestId("usage-sparkline-requests")).not.toBeInTheDocument();
  });

  // ── Partial failure ──

  it("shows records when summary fails", async () => {
    mockGetUsage.mockRejectedValue(new Error("summary fail"));
    mockGetRecords.mockResolvedValue([makeRecord({ agentId: "test-bot-y" })]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("summary fail")).toBeInTheDocument();
    });
    // Agent appears in per-agent breakdown and records table
    expect(screen.getAllByText("test-bot-y").length).toBeGreaterThanOrEqual(1);
  });
});
