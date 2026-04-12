// @vitest-environment jsdom
import "@testing-library/jest-dom/vitest";
import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import RestartHistoryPanel from "../RestartHistoryPanel";

vi.mock("../api", () => ({
  getRestartHistory: vi.fn(),
  getRestartStats: vi.fn(),
}));

import { getRestartHistory, getRestartStats } from "../api";

const mockGetHistory = vi.mocked(getRestartHistory);
const mockGetStats = vi.mocked(getRestartStats);

function makeInstance(overrides: Partial<{
  id: string; startedAt: string; shutdownAt: string | null;
  exitCode: number | null; crashDetected: boolean; version: string;
  shutdownReason: string;
}> = {}) {
  return {
    id: overrides.id ?? "inst-1",
    startedAt: overrides.startedAt ?? "2026-04-10T12:00:00Z",
    shutdownAt: "shutdownAt" in overrides ? overrides.shutdownAt! : "2026-04-10T13:00:00Z",
    exitCode: "exitCode" in overrides ? overrides.exitCode! : 0,
    crashDetected: overrides.crashDetected ?? false,
    version: overrides.version ?? "2.0.0",
    shutdownReason: overrides.shutdownReason ?? "CleanShutdown",
  };
}

function makeStats(overrides: Partial<{
  totalInstances: number; crashRestarts: number; intentionalRestarts: number;
  cleanShutdowns: number; stillRunning: number; windowHours: number;
  maxRestartsPerWindow: number; restartWindowHours: number;
}> = {}) {
  return {
    totalInstances: overrides.totalInstances ?? 5,
    crashRestarts: overrides.crashRestarts ?? 1,
    intentionalRestarts: overrides.intentionalRestarts ?? 2,
    cleanShutdowns: overrides.cleanShutdowns ?? 1,
    stillRunning: overrides.stillRunning ?? 1,
    windowHours: overrides.windowHours ?? 24,
    maxRestartsPerWindow: overrides.maxRestartsPerWindow ?? 10,
    restartWindowHours: overrides.restartWindowHours ?? 1,
  };
}

function renderPanel(props: { hoursBack?: number } = {}) {
  return render(
    <FluentProvider theme={webDarkTheme}>
      <RestartHistoryPanel {...props} />
    </FluentProvider>,
  );
}

describe("RestartHistoryPanel", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  afterEach(() => {
    cleanup();
    document.body.innerHTML = "";
  });

  describe("loading state", () => {
    it("shows spinner while loading", () => {
      mockGetHistory.mockReturnValue(new Promise(() => {})); // never resolves
      mockGetStats.mockReturnValue(new Promise(() => {}));
      renderPanel();
      expect(screen.getByText("Loading restart history…")).toBeInTheDocument();
    });
  });

  describe("error state", () => {
    it("shows error when history fetch fails and no cached data", async () => {
      mockGetHistory.mockRejectedValue(new Error("Network error"));
      mockGetStats.mockResolvedValue(makeStats());
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Network error")).toBeInTheDocument();
      });
    });

    it("shows inline error with cached data when refresh fails", async () => {
      // First load succeeds
      mockGetHistory.mockResolvedValueOnce({
        instances: [makeInstance()],
        total: 1,
        limit: 10,
        offset: 0,
      });
      mockGetStats.mockResolvedValueOnce(makeStats());
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("1 instance recorded")).toBeInTheDocument();
      });

      // Refresh fails
      mockGetHistory.mockRejectedValueOnce(new Error("Timeout"));
      mockGetStats.mockResolvedValueOnce(makeStats());
      await userEvent.click(screen.getByText("Refresh"));
      await waitFor(() => {
        expect(screen.getByText(/Timeout — showing cached data/)).toBeInTheDocument();
      });
    });
  });

  describe("stats display", () => {
    it("shows stats cards when data loads", async () => {
      mockGetHistory.mockResolvedValue({
        instances: [makeInstance()],
        total: 1,
        limit: 10,
        offset: 0,
      });
      mockGetStats.mockResolvedValue(makeStats({ totalInstances: 5, crashRestarts: 1, intentionalRestarts: 2, cleanShutdowns: 1, stillRunning: 1 }));
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("5")).toBeInTheDocument();
      });
      expect(screen.getByText("Instances (24h)")).toBeInTheDocument();
      expect(screen.getByText("Crashes")).toBeInTheDocument();
      expect(screen.getByText("Restarts")).toBeInTheDocument();
      expect(screen.getByText("Clean Stops")).toBeInTheDocument();
      expect(screen.getByText("Running")).toBeInTheDocument();
    });
  });

  describe("instance table", () => {
    it("shows table with instance data", async () => {
      mockGetHistory.mockResolvedValue({
        instances: [makeInstance({ version: "2.1.0", shutdownReason: "CleanShutdown", exitCode: 0 })],
        total: 1,
        limit: 10,
        offset: 0,
      });
      mockGetStats.mockResolvedValue(makeStats());
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("CleanShutdown")).toBeInTheDocument();
      });
      expect(screen.getByText("2.1.0")).toBeInTheDocument();
      expect(screen.getByText("0")).toBeInTheDocument();
    });

    it("shows dash for null exit code", async () => {
      mockGetHistory.mockResolvedValue({
        instances: [makeInstance({ exitCode: null, shutdownReason: "Running" })],
        total: 1,
        limit: 10,
        offset: 0,
      });
      mockGetStats.mockResolvedValue(makeStats());
      const { container } = renderPanel();
      await waitFor(() => {
        expect(screen.getAllByText("Running").length).toBeGreaterThanOrEqual(1);
      });
      // Null exit code renders an em dash instead of a number
      const exitCodeCells = container.querySelectorAll("td");
      const exitText = Array.from(exitCodeCells).map((td) => td.textContent).join("|");
      expect(exitText).toContain("\u2014");
    });

    it("shows crash recovery badge when crashDetected is true", async () => {
      mockGetHistory.mockResolvedValue({
        instances: [makeInstance({ crashDetected: true, shutdownReason: "Crash" })],
        total: 1,
        limit: 10,
        offset: 0,
      });
      mockGetStats.mockResolvedValue(makeStats());
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("⚡ crash recovery")).toBeInTheDocument();
      });
    });

    it("shows empty state when no instances", async () => {
      mockGetHistory.mockResolvedValue({
        instances: [],
        total: 0,
        limit: 10,
        offset: 0,
      });
      mockGetStats.mockResolvedValue(makeStats());
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("No server instances recorded yet.")).toBeInTheDocument();
      });
    });

    it("shows total count", async () => {
      mockGetHistory.mockResolvedValue({
        instances: [makeInstance(), makeInstance({ id: "inst-2" })],
        total: 2,
        limit: 10,
        offset: 0,
      });
      mockGetStats.mockResolvedValue(makeStats());
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("2 instances recorded")).toBeInTheDocument();
      });
    });

    it("shows singular 'instance' for count of 1", async () => {
      mockGetHistory.mockResolvedValue({
        instances: [makeInstance()],
        total: 1,
        limit: 10,
        offset: 0,
      });
      mockGetStats.mockResolvedValue(makeStats());
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("1 instance recorded")).toBeInTheDocument();
      });
    });
  });

  describe("pagination", () => {
    it("disables Newer button on first page", async () => {
      mockGetHistory.mockResolvedValue({
        instances: Array.from({ length: 10 }, (_, i) => makeInstance({ id: `inst-${i}` })),
        total: 15,
        limit: 10,
        offset: 0,
      });
      mockGetStats.mockResolvedValue(makeStats());
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("1–10 of 15")).toBeInTheDocument();
      });
      expect(screen.getByText("← Newer")).toBeDisabled();
      expect(screen.getByText("Older →")).not.toBeDisabled();
    });

    it("navigates to next page when Older is clicked", async () => {
      mockGetHistory.mockResolvedValueOnce({
        instances: Array.from({ length: 10 }, (_, i) => makeInstance({ id: `inst-${i}` })),
        total: 15,
        limit: 10,
        offset: 0,
      });
      mockGetStats.mockResolvedValue(makeStats());
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("1–10 of 15")).toBeInTheDocument();
      });

      mockGetHistory.mockResolvedValueOnce({
        instances: Array.from({ length: 5 }, (_, i) => makeInstance({ id: `inst-${i + 10}` })),
        total: 15,
        limit: 10,
        offset: 10,
      });
      await userEvent.click(screen.getByText("Older →"));
      await waitFor(() => {
        expect(screen.getByText("11–15 of 15")).toBeInTheDocument();
      });
    });
  });

  describe("refresh", () => {
    it("re-fetches data on Refresh click", async () => {
      mockGetHistory.mockResolvedValue({
        instances: [makeInstance()],
        total: 1,
        limit: 10,
        offset: 0,
      });
      mockGetStats.mockResolvedValue(makeStats());
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("1 instance recorded")).toBeInTheDocument();
      });

      expect(mockGetHistory).toHaveBeenCalledTimes(1);
      mockGetHistory.mockResolvedValueOnce({
        instances: [makeInstance(), makeInstance({ id: "inst-2" })],
        total: 2,
        limit: 10,
        offset: 0,
      });
      await userEvent.click(screen.getByText("Refresh"));
      await waitFor(() => {
        expect(mockGetHistory).toHaveBeenCalledTimes(2);
      });
    });
  });

  describe("API calls", () => {
    it("passes hoursBack to getRestartStats", async () => {
      mockGetHistory.mockResolvedValue({
        instances: [makeInstance()],
        total: 1,
        limit: 10,
        offset: 0,
      });
      mockGetStats.mockResolvedValue(makeStats());
      renderPanel({ hoursBack: 48 });
      await waitFor(() => {
        expect(mockGetStats).toHaveBeenCalledWith(48);
      });
    });

    it("defaults hoursBack to 24", async () => {
      mockGetHistory.mockResolvedValue({
        instances: [makeInstance()],
        total: 1,
        limit: 10,
        offset: 0,
      });
      mockGetStats.mockResolvedValue(makeStats());
      renderPanel();
      await waitFor(() => {
        expect(mockGetStats).toHaveBeenCalledWith(24);
      });
    });
  });
});
