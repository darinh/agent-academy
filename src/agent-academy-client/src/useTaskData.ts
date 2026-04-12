import { useCallback, useEffect, useState } from "react";
import { getTasks, getActiveSprint } from "./api";
import type { TaskSnapshot } from "./api";

export interface UseTaskDataOptions {
  /** When false, fetching is paused (e.g. project selector is visible or tab !== "tasks"). */
  enabled: boolean;
}

export interface UseTaskDataResult {
  allTasks: TaskSnapshot[];
  tasksLoading: boolean;
  tasksError: boolean;
  activeSprintId: string | null;
  refreshTasks: () => void;
}

/**
 * Fetches the task list and active sprint when enabled.
 * Re-fetches whenever `enabled` transitions to true or `refreshTasks` is called.
 */
export function useTaskData({ enabled }: UseTaskDataOptions): UseTaskDataResult {
  const [allTasks, setAllTasks] = useState<TaskSnapshot[]>([]);
  const [tasksError, setTasksError] = useState(false);
  const [tasksLoading, setTasksLoading] = useState(false);
  const [tasksFetchKey, setTasksFetchKey] = useState(0);
  const [activeSprintId, setActiveSprintId] = useState<string | null>(null);

  useEffect(() => {
    if (!enabled) return;
    let cancelled = false;
    setTasksError(false);
    setTasksLoading(true);
    getTasks()
      .then((tasks) => { if (!cancelled) setAllTasks(tasks); })
      .catch(() => { if (!cancelled) { setAllTasks([]); setTasksError(true); } })
      .finally(() => { if (!cancelled) setTasksLoading(false); });
    getActiveSprint()
      .then((detail) => { if (!cancelled) setActiveSprintId(detail?.sprint.id ?? null); })
      .catch(() => { if (!cancelled) setActiveSprintId(null); });
    return () => { cancelled = true; };
  }, [enabled, tasksFetchKey]);

  const refreshTasks = useCallback(() => {
    setTasksFetchKey((k) => k + 1);
  }, []);

  return { allTasks, tasksLoading, tasksError, activeSprintId, refreshTasks };
}
