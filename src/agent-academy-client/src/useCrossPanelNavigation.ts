import { useCallback, useEffect, useState } from "react";

/**
 * Cross-panel navigation state for focusing a task (from retrospectives → tasks)
 * and filtering retrospectives by a specific task (from tasks → retrospectives).
 *
 * The retro task filter clears automatically when the user leaves the retrospectives tab
 * so returning to it shows the full list again.
 */
export function useCrossPanelNavigation(
  tab: string,
  setTab: (tab: string) => void,
) {
  const [focusTaskId, setFocusTaskId] = useState<string | null>(null);
  const [retroFilterTaskId, setRetroFilterTaskId] = useState<string | null>(null);

  const handleNavigateToTask = useCallback((taskId: string) => {
    setFocusTaskId(taskId);
    setTab("tasks");
  }, [setTab]);

  const handleFocusTaskHandled = useCallback(() => {
    setFocusTaskId(null);
  }, []);

  const handleNavigateToRetro = useCallback((taskId: string) => {
    setRetroFilterTaskId(taskId);
    setTab("retrospectives");
  }, [setTab]);

  const handleClearRetroTaskFilter = useCallback(() => {
    setRetroFilterTaskId(null);
  }, []);

  useEffect(() => {
    if (tab !== "retrospectives") setRetroFilterTaskId(null);
  }, [tab]);

  return {
    focusTaskId,
    retroFilterTaskId,
    handleNavigateToTask,
    handleFocusTaskHandled,
    handleNavigateToRetro,
    handleClearRetroTaskFilter,
  };
}
