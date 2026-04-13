import { useCallback, useEffect, useState } from "react";
import { getActiveWorkspace, switchWorkspace } from "./api";
import type { OnboardResult, WorkspaceMeta } from "./api";

export interface ProjectSelectionOptions {
  onSwitched: () => void;
  onSwitchedToast: (ws: WorkspaceMeta) => void;
}

export function useProjectSelection({ onSwitched, onSwitchedToast }: ProjectSelectionOptions) {
  const [workspace, setWorkspace] = useState<WorkspaceMeta | null>(null);
  const [loading, setLoading] = useState(true);
  const [showProjectSelector, setShowProjectSelector] = useState(false);
  const [switching, setSwitching] = useState(false);
  const [switchError, setSwitchError] = useState("");

  // On mount, check for active workspace — retry on failure (backend may still be starting)
  useEffect(() => {
    let cancelled = false;
    let retryTimer: ReturnType<typeof setTimeout> | null = null;
    async function checkWorkspace(attempt: number) {
      if (cancelled) return;
      try {
        const data = await getActiveWorkspace();
        if (cancelled) return;
        if (data.active) {
          setWorkspace(data.active);
        } else {
          setShowProjectSelector(true);
        }
        setLoading(false);
      } catch {
        if (cancelled) return;
        if (attempt < 3) {
          retryTimer = setTimeout(() => void checkWorkspace(attempt + 1), 2000);
        } else {
          setShowProjectSelector(true);
          setLoading(false);
        }
      }
    }
    void checkWorkspace(0);
    return () => {
      cancelled = true;
      if (retryTimer) clearTimeout(retryTimer);
    };
  }, []);

  const handleProjectSelected = useCallback(
    async (workspacePath: string) => {
      if (switching) return;
      setSwitching(true);
      setSwitchError("");
      try {
        const ws = await switchWorkspace(workspacePath);
        setWorkspace(ws);
        setShowProjectSelector(false);
        onSwitched();
        onSwitchedToast(ws);
      } catch (e) {
        setSwitchError(e instanceof Error ? e.message : "Failed to switch workspace");
      } finally {
        setSwitching(false);
      }
    },
    [onSwitched, onSwitchedToast, switching],
  );

  const handleProjectOnboarded = useCallback(
    (result: OnboardResult) => {
      setWorkspace(result.workspace);
      setShowProjectSelector(false);
      onSwitched();
    },
    [onSwitched],
  );

  return {
    workspace,
    loading,
    showProjectSelector,
    setShowProjectSelector,
    switching,
    switchError,
    handleProjectSelected,
    handleProjectOnboarded,
  };
}
