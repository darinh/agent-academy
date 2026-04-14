// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { renderHook, waitFor, act } from "@testing-library/react";
import type { WorkspaceMeta, OnboardResult, ProjectScanResult } from "../api";

vi.mock("../api", () => ({
  getActiveWorkspace: vi.fn(),
  switchWorkspace: vi.fn(),
}));

import { getActiveWorkspace, switchWorkspace } from "../api";
import { useProjectSelection } from "../useProjectSelection";

const mockGetActiveWorkspace = vi.mocked(getActiveWorkspace);
const mockSwitchWorkspace = vi.mocked(switchWorkspace);

function makeWorkspace(overrides: Partial<WorkspaceMeta> = {}): WorkspaceMeta {
  return {
    path: "/home/user/project",
    projectName: "test-project",
    lastAccessedAt: "2026-04-01T00:00:00Z",
    ...overrides,
  };
}

function makeOnboardResult(overrides: Partial<OnboardResult> = {}): OnboardResult {
  return {
    scan: {} as ProjectScanResult,
    workspace: makeWorkspace(),
    ...overrides,
  };
}

const noop = () => {};
const noopToast = () => {};

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(() => {
  vi.useRealTimers();
});

describe("useProjectSelection", () => {
  describe("initial workspace check", () => {
    it("loads active workspace on mount", async () => {
      const ws = makeWorkspace();
      mockGetActiveWorkspace.mockResolvedValue({ active: ws, dataDir: "/data" });

      const { result } = renderHook(() =>
        useProjectSelection({ onSwitched: noop, onSwitchedToast: noopToast }),
      );

      expect(result.current.loading).toBe(true);

      await waitFor(() => {
        expect(result.current.loading).toBe(false);
      });

      expect(result.current.workspace).toEqual(ws);
      expect(result.current.showProjectSelector).toBe(false);
    });

    it("shows project selector when no active workspace", async () => {
      mockGetActiveWorkspace.mockResolvedValue({ active: null, dataDir: null });

      const { result } = renderHook(() =>
        useProjectSelection({ onSwitched: noop, onSwitchedToast: noopToast }),
      );

      await waitFor(() => {
        expect(result.current.loading).toBe(false);
      });

      expect(result.current.workspace).toBeNull();
      expect(result.current.showProjectSelector).toBe(true);
    });

    it("shows project selector after max retries on failure", async () => {
      vi.useFakeTimers();
      mockGetActiveWorkspace.mockRejectedValue(new Error("Server starting"));

      const { result } = renderHook(() =>
        useProjectSelection({ onSwitched: noop, onSwitchedToast: noopToast }),
      );

      // Flush initial attempt (0) + 3 retry timeouts (attempts 1, 2, 3)
      for (let i = 0; i < 4; i++) {
        await act(async () => {
          await vi.advanceTimersByTimeAsync(2000);
        });
      }

      expect(result.current.loading).toBe(false);
      expect(result.current.showProjectSelector).toBe(true);
      // 4 attempts total: initial + 3 retries
      expect(mockGetActiveWorkspace).toHaveBeenCalledTimes(4);
    });
  });

  describe("handleProjectSelected", () => {
    it("switches workspace and notifies callbacks", async () => {
      mockGetActiveWorkspace.mockResolvedValue({ active: null, dataDir: null });
      const ws = makeWorkspace({ path: "/new/project" });
      mockSwitchWorkspace.mockResolvedValue(ws);
      const onSwitched = vi.fn();
      const onSwitchedToast = vi.fn();

      const { result } = renderHook(() =>
        useProjectSelection({ onSwitched, onSwitchedToast }),
      );

      await waitFor(() => {
        expect(result.current.loading).toBe(false);
      });

      await act(async () => {
        await result.current.handleProjectSelected("/new/project");
      });

      expect(mockSwitchWorkspace).toHaveBeenCalledWith("/new/project");
      expect(result.current.workspace).toEqual(ws);
      expect(result.current.showProjectSelector).toBe(false);
      expect(onSwitched).toHaveBeenCalled();
      expect(onSwitchedToast).toHaveBeenCalledWith(ws);
    });

    it("sets switchError on failure", async () => {
      mockGetActiveWorkspace.mockResolvedValue({ active: null, dataDir: null });
      mockSwitchWorkspace.mockRejectedValue(new Error("Invalid path"));

      const { result } = renderHook(() =>
        useProjectSelection({ onSwitched: noop, onSwitchedToast: noopToast }),
      );

      await waitFor(() => {
        expect(result.current.loading).toBe(false);
      });

      await act(async () => {
        await result.current.handleProjectSelected("/bad/path");
      });

      expect(result.current.switchError).toBe("Invalid path");
      expect(result.current.switching).toBe(false);
    });

    it("sets generic error for non-Error throws", async () => {
      mockGetActiveWorkspace.mockResolvedValue({ active: null, dataDir: null });
      mockSwitchWorkspace.mockRejectedValue("string error");

      const { result } = renderHook(() =>
        useProjectSelection({ onSwitched: noop, onSwitchedToast: noopToast }),
      );

      await waitFor(() => {
        expect(result.current.loading).toBe(false);
      });

      await act(async () => {
        await result.current.handleProjectSelected("/path");
      });

      expect(result.current.switchError).toBe("Failed to switch workspace");
    });

    it("ignores duplicate calls while switching", async () => {
      mockGetActiveWorkspace.mockResolvedValue({ active: null, dataDir: null });
      let resolveSwitch: (ws: WorkspaceMeta) => void;
      mockSwitchWorkspace.mockImplementation(
        () => new Promise((r) => { resolveSwitch = r; }),
      );

      const { result } = renderHook(() =>
        useProjectSelection({ onSwitched: noop, onSwitchedToast: noopToast }),
      );

      await waitFor(() => {
        expect(result.current.loading).toBe(false);
      });

      // Start first switch
      act(() => {
        void result.current.handleProjectSelected("/path1");
      });

      expect(result.current.switching).toBe(true);

      // Second call while switching should be ignored
      act(() => {
        void result.current.handleProjectSelected("/path2");
      });

      expect(mockSwitchWorkspace).toHaveBeenCalledTimes(1);

      // Resolve the pending switch
      await act(async () => {
        resolveSwitch!(makeWorkspace());
      });
    });
  });

  describe("handleProjectOnboarded", () => {
    it("sets workspace from onboard result", async () => {
      mockGetActiveWorkspace.mockResolvedValue({ active: null, dataDir: null });
      const onSwitched = vi.fn();
      const ws = makeWorkspace({ path: "/onboarded" });

      const { result } = renderHook(() =>
        useProjectSelection({ onSwitched, onSwitchedToast: noopToast }),
      );

      await waitFor(() => {
        expect(result.current.loading).toBe(false);
      });

      act(() => {
        result.current.handleProjectOnboarded(makeOnboardResult({ workspace: ws }));
      });

      expect(result.current.workspace).toEqual(ws);
      expect(result.current.showProjectSelector).toBe(false);
      expect(onSwitched).toHaveBeenCalled();
    });
  });
});
