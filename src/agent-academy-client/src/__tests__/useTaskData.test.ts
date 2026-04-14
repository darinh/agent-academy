// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { renderHook, waitFor, act } from "@testing-library/react";
import type { TaskSnapshot, SprintDetailResponse, SprintSnapshot } from "../api";

vi.mock("../api", () => ({
  getTasks: vi.fn(),
  getActiveSprint: vi.fn(),
}));

import { getTasks, getActiveSprint } from "../api";
import { useTaskData } from "../useTaskData";

const mockGetTasks = vi.mocked(getTasks);
const mockGetActiveSprint = vi.mocked(getActiveSprint);

function makeTask(overrides: Partial<TaskSnapshot> = {}): TaskSnapshot {
  return {
    id: "task-1",
    title: "Test task",
    description: "",
    successCriteria: "",
    status: "Active",
    currentPhase: "Implementation",
    currentPlan: "",
    validationStatus: "",
    validationSummary: "",
    implementationStatus: "",
    implementationSummary: "",
    preferredRoles: [],
    createdAt: "2026-04-01T00:00:00Z",
    updatedAt: "2026-04-01T00:00:00Z",
    ...overrides,
  };
}

function makeSprintDetail(id = "sprint-1"): SprintDetailResponse {
  return {
    sprint: { id, name: "Sprint 1" } as SprintSnapshot,
    artifacts: [],
    stages: [],
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  mockGetTasks.mockResolvedValue([]);
  mockGetActiveSprint.mockResolvedValue(null);
});

describe("useTaskData", () => {
  describe("when enabled", () => {
    it("fetches tasks on mount", async () => {
      const tasks = [makeTask({ id: "t1" }), makeTask({ id: "t2" })];
      mockGetTasks.mockResolvedValue(tasks);

      const { result } = renderHook(() => useTaskData({ enabled: true }));

      expect(result.current.tasksLoading).toBe(true);

      await waitFor(() => {
        expect(result.current.tasksLoading).toBe(false);
      });

      expect(mockGetTasks).toHaveBeenCalled();
      expect(result.current.allTasks).toEqual(tasks);
      expect(result.current.tasksError).toBe(false);
    });

    it("fetches active sprint on mount", async () => {
      mockGetActiveSprint.mockResolvedValue(makeSprintDetail("sprint-42"));

      const { result } = renderHook(() => useTaskData({ enabled: true }));

      await waitFor(() => {
        expect(result.current.activeSprintId).toBe("sprint-42");
      });
    });

    it("sets activeSprintId to null when no active sprint", async () => {
      mockGetActiveSprint.mockResolvedValue(null);

      const { result } = renderHook(() => useTaskData({ enabled: true }));

      await waitFor(() => {
        expect(result.current.tasksLoading).toBe(false);
      });

      expect(result.current.activeSprintId).toBeNull();
    });

    it("sets tasksError on fetch failure", async () => {
      mockGetTasks.mockRejectedValue(new Error("Network error"));

      const { result } = renderHook(() => useTaskData({ enabled: true }));

      await waitFor(() => {
        expect(result.current.tasksLoading).toBe(false);
      });

      expect(result.current.tasksError).toBe(true);
      expect(result.current.allTasks).toEqual([]);
    });

    it("sets activeSprintId to null on sprint fetch failure", async () => {
      mockGetActiveSprint.mockRejectedValue(new Error("fail"));

      const { result } = renderHook(() => useTaskData({ enabled: true }));

      await waitFor(() => {
        expect(result.current.tasksLoading).toBe(false);
      });

      expect(result.current.activeSprintId).toBeNull();
    });
  });

  describe("when disabled", () => {
    it("does not fetch tasks", () => {
      renderHook(() => useTaskData({ enabled: false }));

      expect(mockGetTasks).not.toHaveBeenCalled();
      expect(mockGetActiveSprint).not.toHaveBeenCalled();
    });

    it("returns empty initial state", () => {
      const { result } = renderHook(() => useTaskData({ enabled: false }));

      expect(result.current.allTasks).toEqual([]);
      expect(result.current.tasksLoading).toBe(false);
      expect(result.current.tasksError).toBe(false);
      expect(result.current.activeSprintId).toBeNull();
    });
  });

  describe("refreshTasks", () => {
    it("re-fetches tasks when called", async () => {
      const initial = [makeTask({ id: "t1" })];
      const refreshed = [makeTask({ id: "t1" }), makeTask({ id: "t2" })];
      mockGetTasks.mockResolvedValueOnce(initial).mockResolvedValueOnce(refreshed);

      const { result } = renderHook(() => useTaskData({ enabled: true }));

      await waitFor(() => {
        expect(result.current.tasksLoading).toBe(false);
      });
      expect(result.current.allTasks).toEqual(initial);

      act(() => {
        result.current.refreshTasks();
      });

      await waitFor(() => {
        expect(result.current.allTasks).toEqual(refreshed);
      });

      expect(mockGetTasks).toHaveBeenCalledTimes(2);
    });
  });

  describe("enabled transitions", () => {
    it("fetches when enabled transitions from false to true", async () => {
      const tasks = [makeTask({ id: "t1" })];
      mockGetTasks.mockResolvedValue(tasks);

      const { result, rerender } = renderHook(
        ({ enabled }) => useTaskData({ enabled }),
        { initialProps: { enabled: false } },
      );

      expect(mockGetTasks).not.toHaveBeenCalled();

      rerender({ enabled: true });

      await waitFor(() => {
        expect(result.current.tasksLoading).toBe(false);
      });

      expect(mockGetTasks).toHaveBeenCalledTimes(1);
      expect(result.current.allTasks).toEqual(tasks);
    });
  });
});
