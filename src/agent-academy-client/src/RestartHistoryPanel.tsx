import { useCallback, useEffect, useRef, useState } from "react";
import {
  Badge,
  Spinner,
  makeStyles,
  shorthands,
  Tooltip,
} from "@fluentui/react-components";
import {
  ArrowSyncRegular,
  CheckmarkCircleRegular,
  ErrorCircleRegular,
  WarningRegular,
  PlayRegular,
} from "@fluentui/react-icons";
import {
  getRestartHistory,
  getRestartStats,
  type ServerInstanceDto,
  type RestartStatsDto,
} from "./api";

// ── Styles ──

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    gap: "20px",
  },
  statsRow: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(140px, 1fr))",
    gap: "12px",
  },
  statCard: {
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    ...shorthands.padding("14px", "8px"),
    ...shorthands.borderRadius("16px"),
    border: "1px solid rgba(214, 188, 149, 0.10)",
    backgroundColor: "rgba(255, 255, 255, 0.025)",
  },
  statValue: {
    fontFamily: "var(--heading)",
    fontSize: "28px",
    fontWeight: 780,
    color: "var(--aa-text-strong)",
    lineHeight: 1,
    letterSpacing: "-0.04em",
  },
  statLabel: {
    color: "var(--aa-muted)",
    fontSize: "11px",
    fontWeight: 600,
    textTransform: "uppercase" as const,
    letterSpacing: "0.08em",
    marginTop: "6px",
    textAlign: "center" as const,
  },
  table: {
    width: "100%",
    borderCollapse: "collapse" as const,
    fontSize: "13px",
  },
  th: {
    textAlign: "left" as const,
    color: "var(--aa-soft)",
    fontSize: "11px",
    fontWeight: 700,
    letterSpacing: "0.10em",
    textTransform: "uppercase" as const,
    ...shorthands.padding("8px", "12px"),
    borderBottom: "1px solid rgba(255, 244, 227, 0.10)",
  },
  td: {
    ...shorthands.padding("10px", "12px"),
    borderBottom: "1px solid rgba(255, 244, 227, 0.05)",
    color: "var(--aa-text)",
    verticalAlign: "middle" as const,
  },
  currentRow: {
    backgroundColor: "rgba(108, 182, 255, 0.06)",
  },
  mono: {
    fontFamily: "var(--mono, monospace)",
    fontSize: "12px",
    color: "var(--aa-muted)",
  },
  emptyNote: {
    color: "var(--aa-muted)",
    fontSize: "13px",
    textAlign: "center" as const,
    ...shorthands.padding("24px"),
  },
  error: {
    color: "#f85149",
    fontSize: "13px",
    ...shorthands.padding("12px"),
    ...shorthands.borderRadius("8px"),
    backgroundColor: "rgba(248, 81, 73, 0.08)",
  },
  pagerRow: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    fontSize: "12px",
    color: "var(--aa-muted)",
  },
  pagerBtn: {
    background: "none",
    ...shorthands.border("1px", "solid", "rgba(155, 176, 210, 0.20)"),
    ...shorthands.borderRadius("8px"),
    ...shorthands.padding("4px", "12px"),
    color: "var(--aa-text)",
    cursor: "pointer",
    fontSize: "12px",
    ":hover": {
      backgroundColor: "rgba(255, 255, 255, 0.06)",
    },
    ":disabled": {
      opacity: 0.4,
      cursor: "default",
    },
  },
  refreshBtn: {
    background: "none",
    ...shorthands.border("none"),
    color: "var(--aa-soft)",
    cursor: "pointer",
    fontSize: "12px",
    display: "flex",
    alignItems: "center",
    gap: "4px",
    ":hover": {
      color: "var(--aa-text)",
    },
  },
  headerRow: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
  },
});

// ── Helpers ──

function reasonBadge(reason: string): { color: "informative" | "success" | "warning" | "danger" | "important"; icon: JSX.Element } {
  switch (reason) {
    case "Running":
      return { color: "informative", icon: <PlayRegular style={{ fontSize: 14 }} /> };
    case "CleanShutdown":
      return { color: "success", icon: <CheckmarkCircleRegular style={{ fontSize: 14 }} /> };
    case "IntentionalRestart":
      return { color: "warning", icon: <ArrowSyncRegular style={{ fontSize: 14 }} /> };
    case "Crash":
      return { color: "danger", icon: <ErrorCircleRegular style={{ fontSize: 14 }} /> };
    default:
      return { color: "important", icon: <WarningRegular style={{ fontSize: 14 }} /> };
  }
}

function formatTimestamp(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleString(undefined, {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });
}

function formatDuration(startIso: string, endIso: string | null): string {
  if (!endIso) {
    const ms = Date.now() - new Date(startIso).getTime();
    return fmtMs(ms) + " (running)";
  }
  const ms = new Date(endIso).getTime() - new Date(startIso).getTime();
  return fmtMs(ms);
}

function fmtMs(ms: number): string {
  const seconds = Math.floor(ms / 1000);
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ${seconds % 60}s`;
  const hours = Math.floor(minutes / 60);
  return `${hours}h ${minutes % 60}m`;
}

const PAGE_SIZE = 10;

// ── Component ──

interface RestartHistoryPanelProps {
  hoursBack?: number;
}

export default function RestartHistoryPanel({ hoursBack }: RestartHistoryPanelProps) {
  const s = useLocalStyles();
  const [instances, setInstances] = useState<ServerInstanceDto[]>([]);
  const [stats, setStats] = useState<RestartStatsDto | null>(null);
  const [total, setTotal] = useState(0);
  const [offset, setOffset] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const fetchIdRef = useRef(0);

  const fetchData = useCallback(async (pageOffset: number) => {
    const id = ++fetchIdRef.current;
    setLoading(true);
    setError(null);

    const [historyResult, statsResult] = await Promise.allSettled([
      getRestartHistory(PAGE_SIZE, pageOffset),
      getRestartStats(hoursBack ?? 24),
    ]);

    // Discard stale responses from superseded requests
    if (id !== fetchIdRef.current) return;

    if (historyResult.status === "fulfilled") {
      const { instances: newInstances, total: newTotal } = historyResult.value;
      setInstances(newInstances);
      setTotal(newTotal);
      // Clamp offset if total shrank below current page
      if (pageOffset > 0 && pageOffset >= newTotal) {
        const clamped = Math.max(0, Math.floor((newTotal - 1) / PAGE_SIZE) * PAGE_SIZE);
        setOffset(clamped);
        // Will trigger a re-fetch via the useEffect dependency
      }
    } else {
      setError(historyResult.reason instanceof Error ? historyResult.reason.message : "Failed to load restart history");
    }

    if (statsResult.status === "fulfilled") {
      setStats(statsResult.value);
    }
    // Stats failure is non-critical — keep stale stats if available

    setLoading(false);
  }, [hoursBack]);

  useEffect(() => {
    fetchData(offset);
  }, [fetchData, offset]);

  if (loading && instances.length === 0) {
    return (
      <div className={s.root}>
        <Spinner size="small" label="Loading restart history…" />
      </div>
    );
  }

  if (error && instances.length === 0) {
    return (
      <div className={s.root}>
        <div className={s.error}>{error}</div>
      </div>
    );
  }

  const hasNext = offset + PAGE_SIZE < total;
  const hasPrev = offset > 0;

  return (
    <div className={s.root}>
      {/* Inline error for failed refresh when stale data is visible */}
      {error && instances.length > 0 && (
        <div className={s.error}>{error} — showing cached data.</div>
      )}

      {/* Stats summary */}
      {stats && (
        <div className={s.statsRow}>
          <div className={s.statCard}>
            <span className={s.statValue}>{stats.totalInstances}</span>
            <span className={s.statLabel}>Instances ({stats.windowHours}h)</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue} style={{ color: "#f85149" }}>{stats.crashRestarts}</span>
            <span className={s.statLabel}>Crashes</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue} style={{ color: "#ffbe70" }}>{stats.intentionalRestarts}</span>
            <span className={s.statLabel}>Restarts</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue} style={{ color: "#48d67a" }}>{stats.cleanShutdowns}</span>
            <span className={s.statLabel}>Clean Stops</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue} style={{ color: "#6cb6ff" }}>{stats.stillRunning}</span>
            <span className={s.statLabel}>Running</span>
          </div>
        </div>
      )}

      {/* Instance table */}
      <div className={s.headerRow}>
        <span style={{ fontSize: "13px", color: "var(--aa-muted)" }}>
          {total} instance{total !== 1 ? "s" : ""} recorded
        </span>
        <button className={s.refreshBtn} onClick={() => fetchData(offset)}>
          <ArrowSyncRegular style={{ fontSize: 14 }} /> Refresh
        </button>
      </div>

      {instances.length === 0 ? (
        <div className={s.emptyNote}>No server instances recorded yet.</div>
      ) : (
        <>
          <table className={s.table}>
            <thead>
              <tr>
                <th className={s.th}>Status</th>
                <th className={s.th}>Started</th>
                <th className={s.th}>Duration</th>
                <th className={s.th}>Version</th>
                <th className={s.th}>Exit</th>
              </tr>
            </thead>
            <tbody>
              {instances.map((inst) => {
                const badge = reasonBadge(inst.shutdownReason);
                const isRunning = inst.shutdownReason === "Running";
                return (
                  <tr key={inst.id} className={isRunning ? s.currentRow : undefined}>
                    <td className={s.td}>
                      <Tooltip content={inst.shutdownReason} relationship="label">
                        <Badge
                          appearance="filled"
                          color={badge.color}
                          icon={badge.icon}
                        >
                          {inst.shutdownReason}
                        </Badge>
                      </Tooltip>
                      {inst.crashDetected && (
                        <Tooltip content="Crash detected on startup" relationship="label">
                          <Badge
                            appearance="outline"
                            color="danger"
                            style={{ marginLeft: 6 }}
                          >
                            ⚡ crash recovery
                          </Badge>
                        </Tooltip>
                      )}
                    </td>
                    <td className={s.td}>{formatTimestamp(inst.startedAt)}</td>
                    <td className={s.td}>{formatDuration(inst.startedAt, inst.shutdownAt)}</td>
                    <td className={s.td}>
                      <span className={s.mono}>{inst.version}</span>
                    </td>
                    <td className={s.td}>
                      <span className={s.mono}>
                        {inst.exitCode != null ? inst.exitCode : "—"}
                      </span>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>

          <div className={s.pagerRow}>
            <button
              className={s.pagerBtn}
              disabled={!hasPrev}
              onClick={() => setOffset(Math.max(0, offset - PAGE_SIZE))}
            >
              ← Newer
            </button>
            <span>
              {offset + 1}–{Math.min(offset + PAGE_SIZE, total)} of {total}
            </span>
            <button
              className={s.pagerBtn}
              disabled={!hasNext}
              onClick={() => setOffset(offset + PAGE_SIZE)}
            >
              Older →
            </button>
          </div>
        </>
      )}
    </div>
  );
}
