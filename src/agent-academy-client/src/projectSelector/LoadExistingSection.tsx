import { useEffect, useState } from "react";
import { Button, Spinner } from "@fluentui/react-components";
import { listWorkspaces } from "../api";
import type { WorkspaceMeta } from "../api";
import { useProjectSelectorStyles } from "./projectSelectorStyles";
import { relativeTime } from "./projectSelectorConstants";

interface LoadExistingSectionProps {
  onProjectSelected: (path: string) => void;
}

export default function LoadExistingSection({ onProjectSelected }: LoadExistingSectionProps) {
  const classes = useProjectSelectorStyles();
  const [workspaces, setWorkspaces] = useState<WorkspaceMeta[]>([]);
  const [loading, setLoading] = useState(true);
  const [fetchError, setFetchError] = useState(false);
  const [fetchKey, setFetchKey] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setFetchError(false);
    setLoading(true);
    listWorkspaces()
      .then((ws) => {
        if (!cancelled) setWorkspaces(ws);
      })
      .catch(() => {
        if (!cancelled) { setWorkspaces([]); setFetchError(true); }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [fetchKey]);

  if (loading) {
    return (
      <div className={classes.loadingWrap}>
        <Spinner size="small" label="Loading workspaces…" />
      </div>
    );
  }

  if (fetchError) {
    return (
      <div className={classes.placeholder}>
        <span className={classes.body1}>Failed to load workspaces. Check your connection and try again.</span>
        <Button appearance="subtle" onClick={() => setFetchKey((k) => k + 1)} style={{ marginTop: "12px" }}>
          Retry
        </Button>
      </div>
    );
  }

  if (workspaces.length === 0) {
    return (
      <div className={classes.placeholder}>
        <span className={classes.body1}>No existing projects found yet. Onboard a repository or create a new workspace below.</span>
      </div>
    );
  }

  return (
    <div className={classes.workspaceList}>
      {workspaces.map((ws) => (
        <button
          key={ws.path}
          className={classes.workspaceCard}
          onClick={() => onProjectSelected(ws.path)}
          title={`Open ${ws.projectName ?? ws.path}`}
          type="button"
        >
          <div className={classes.workspaceIcon}>{(ws.projectName ?? ws.path).charAt(0).toUpperCase()}</div>
          <div>
            <div className={classes.workspaceName}>{ws.projectName ?? ws.path.split("/").pop()}</div>
            <div className={classes.workspacePath}>{ws.path}</div>
          </div>
          <span className={classes.workspaceMeta}>
            {ws.lastAccessedAt ? `Active ${relativeTime(ws.lastAccessedAt)}` : "New workspace"}
          </span>
        </button>
      ))}
    </div>
  );
}
