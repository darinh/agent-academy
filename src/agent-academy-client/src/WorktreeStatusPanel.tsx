import { useCallback, useEffect, useRef, useState } from "react";
import {
  Spinner,
  makeStyles,
  shorthands,
  Tooltip,
} from "@fluentui/react-components";
import V3Badge from "./V3Badge";
import type { BadgeColor } from "./V3Badge";
import {
  BranchRegular,
  PersonRegular,
  DocumentRegular,
  ErrorCircleRegular,
} from "@fluentui/react-icons";
import { getWorktreeStatus, type WorktreeStatusSnapshot } from "./api";
import EmptyState from "./EmptyState";

// ── Styles ──

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    gap: "10px",
  },
  card: {
    display: "grid",
    gridTemplateColumns: "1fr auto",
    gap: "8px",
    alignItems: "start",
    ...shorthands.padding("10px", "12px"),
    ...shorthands.borderRadius("6px"),
    border: "1px solid var(--aa-border)",
    backgroundColor: "var(--aa-bg)",
  },
  cardLeft: {
    display: "flex",
    flexDirection: "column",
    gap: "4px",
    minWidth: 0,
  },
  branchRow: {
    display: "flex",
    alignItems: "center",
    gap: "6px",
    fontSize: "13px",
    fontWeight: 600,
    color: "var(--aa-text)",
    fontFamily: "var(--mono)",
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap",
  },
  branchIcon: {
    fontSize: "16px",
    color: "var(--aa-cyan)",
    flexShrink: 0,
  },
  metaRow: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    fontSize: "11px",
    color: "var(--aa-soft)",
    flexWrap: "wrap",
  },
  metaItem: {
    display: "flex",
    alignItems: "center",
    gap: "3px",
  },
  taskTitle: {
    fontSize: "12px",
    color: "var(--aa-muted)",
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap",
    maxWidth: "300px",
  },
  commitMsg: {
    fontSize: "11px",
    color: "var(--aa-soft)",
    fontFamily: "var(--mono)",
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap",
    maxWidth: "250px",
  },
  cardRight: {
    display: "flex",
    flexDirection: "column",
    alignItems: "flex-end",
    gap: "4px",
    flexShrink: 0,
  },
  diffStats: {
    display: "flex",
    gap: "6px",
    fontSize: "11px",
    fontFamily: "var(--mono)",
    fontWeight: 600,
  },
  insertions: { color: "var(--aa-lime, #4caf50)" },
  deletions: { color: "var(--aa-copper, #f44336)" },
  filesLabel: { color: "var(--aa-soft)" },
  errorCard: {
    ...shorthands.padding("10px", "12px"),
    ...shorthands.borderRadius("6px"),
    border: "1px solid var(--aa-border)",
    backgroundColor: "var(--aa-bg)",
    display: "flex",
    alignItems: "center",
    gap: "8px",
    fontSize: "12px",
    color: "var(--aa-soft)",
  },
  dirtyList: {
    fontSize: "11px",
    fontFamily: "var(--mono)",
    color: "var(--aa-soft)",
    ...shorthands.padding("4px", "0", "0", "0"),
    margin: 0,
    listStyleType: "none",
  },
  dirtyItem: {
    ...shorthands.padding("1px", "0"),
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap",
  },
});

// ── Helpers ──

function dirtyIndicator(count: number): BadgeColor {
  if (count === 0) return "ok";
  if (count <= 5) return "warn";
  return "err";
}

function shortSha(sha: string | null): string {
  return sha ? sha.slice(0, 7) : "";
}

// ── Component ──

interface WorktreeStatusPanelProps {
  hoursBack?: number;
}

export default function WorktreeStatusPanel(_props: WorktreeStatusPanelProps) {
  const s = useLocalStyles();
  const [data, setData] = useState<WorktreeStatusSnapshot[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const timerRef = useRef<ReturnType<typeof setInterval>>(undefined);

  const fetchData = useCallback(async () => {
    try {
      const result = await getWorktreeStatus();
      setData(result);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load worktree status");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchData();
    timerRef.current = setInterval(fetchData, 30_000);
    return () => clearInterval(timerRef.current);
  }, [fetchData]);

  if (loading) return <Spinner size="tiny" label="Loading worktrees…" />;
  if (error) return <div className={s.errorCard}><ErrorCircleRegular /> {error}</div>;
  if (!data || data.length === 0) {
    return <EmptyState icon="🌳" title="No active worktrees" detail="Worktrees appear when agents start working on tasks." />;
  }

  return (
    <div className={s.root}>
      {data.map((wt) => (
        <div key={wt.branch} className={s.card}>
          <div className={s.cardLeft}>
            <div className={s.branchRow}>
              <BranchRegular className={s.branchIcon} />
              <span>{wt.branch}</span>
              <V3Badge color={dirtyIndicator(wt.totalDirtyFiles)}>
                {wt.totalDirtyFiles === 0 ? "clean" : `${wt.totalDirtyFiles} dirty`}
              </V3Badge>
              {wt.taskStatus && (
                <V3Badge color="info">{wt.taskStatus}</V3Badge>
              )}
            </div>

            <div className={s.metaRow}>
              {wt.agentName && (
                <Tooltip content={`Agent: ${wt.agentId}`} relationship="label">
                  <span className={s.metaItem}>
                    <PersonRegular style={{ fontSize: 12 }} />
                    {wt.agentName}
                  </span>
                </Tooltip>
              )}
              {wt.taskTitle && (
                <Tooltip content={wt.taskTitle} relationship="label">
                  <span className={s.taskTitle}>{wt.taskTitle}</span>
                </Tooltip>
              )}
            </div>

            {wt.lastCommitMessage && (
              <Tooltip content={`${shortSha(wt.lastCommitSha)} by ${wt.lastCommitAuthor ?? "unknown"}`} relationship="label">
                <span className={s.commitMsg}>
                  {shortSha(wt.lastCommitSha)} {wt.lastCommitMessage}
                </span>
              </Tooltip>
            )}

            {!wt.statusAvailable && wt.error && (
              <span className={s.metaRow}>
                <ErrorCircleRegular style={{ fontSize: 12, color: "var(--aa-copper)" }} />
                {wt.error}
              </span>
            )}

            {wt.dirtyFilesPreview.length > 0 && (
              <ul className={s.dirtyList}>
                {wt.dirtyFilesPreview.map((f) => (
                  <li key={f} className={s.dirtyItem}>
                    <DocumentRegular style={{ fontSize: 11, marginRight: 4 }} />
                    {f}
                  </li>
                ))}
                {wt.totalDirtyFiles > wt.dirtyFilesPreview.length && (
                  <li className={s.dirtyItem} style={{ fontStyle: "italic" }}>
                    …and {wt.totalDirtyFiles - wt.dirtyFilesPreview.length} more
                  </li>
                )}
              </ul>
            )}
          </div>

          <div className={s.cardRight}>
            {wt.statusAvailable && (wt.insertions > 0 || wt.deletions > 0) && (
              <div className={s.diffStats}>
                <span className={s.filesLabel}>{wt.filesChanged}f</span>
                <span className={s.insertions}>+{wt.insertions}</span>
                <span className={s.deletions}>−{wt.deletions}</span>
              </div>
            )}
          </div>
        </div>
      ))}
    </div>
  );
}
