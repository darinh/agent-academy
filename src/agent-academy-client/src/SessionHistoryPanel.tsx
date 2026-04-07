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
  ChatRegular,
  ArchiveRegular,
  PlayRegular,
} from "@fluentui/react-icons";
import {
  getSessions,
  getSessionStats,
  type ConversationSessionSnapshot,
  type SessionStats,
} from "./api";
import {
  formatRelativeTime,
  formatTimestamp,
  truncateSummary,
  PAGE_SIZE,
} from "./sessionHistoryPanelUtils";

// ── Styles ──

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    gap: "20px",
  },
  statsRow: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(100px, 1fr))",
    gap: "8px",
    marginBottom: "12px",
  },
  statCard: {
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    ...shorthands.padding("8px", "10px"),
    ...shorthands.borderRadius("6px"),
    border: "1px solid var(--aa-border)",
    backgroundColor: "var(--aa-bg)",
  },
  statValue: {
    fontSize: "18px",
    fontWeight: 700,
    color: "var(--aa-text)",
    lineHeight: 1,
  },
  statLabel: {
    fontFamily: "var(--mono)",
    color: "var(--aa-soft)",
    fontSize: "10px",
    textAlign: "center" as const,
  },
  tableWrap: {
    overflowX: "auto" as const,
    maxHeight: "240px",
    overflowY: "auto" as const,
    border: "1px solid var(--aa-border)",
    ...shorthands.borderRadius("6px"),
  },
  table: {
    width: "100%",
    borderCollapse: "collapse" as const,
  },
  th: {
    textAlign: "left" as const,
    color: "var(--aa-soft)",
    fontSize: "10px",
    fontWeight: 600,
    fontFamily: "var(--mono)",
    letterSpacing: "0.04em",
    textTransform: "uppercase" as const,
    ...shorthands.padding("5px", "10px"),
    borderBottom: "1px solid var(--aa-border)",
    position: "sticky" as const,
    top: 0,
    background: "var(--aa-panel)",
    zIndex: 1,
  },
  td: {
    ...shorthands.padding("5px", "10px"),
    borderBottom: "1px solid var(--aa-border)",
    color: "var(--aa-muted)",
    fontFamily: "var(--mono)",
    fontSize: "11px",
    verticalAlign: "top" as const,
  },
  activeRow: {
    backgroundColor: "rgba(72, 214, 122, 0.06)",
  },
  mono: {
    fontFamily: "var(--mono, monospace)",
    fontSize: "12px",
    color: "var(--aa-muted)",
  },
  summaryCell: {
    fontSize: "12px",
    color: "var(--aa-soft)",
    lineHeight: 1.5,
    maxWidth: "400px",
    whiteSpace: "pre-wrap" as const,
  },
  summaryToggle: {
    background: "none",
    ...shorthands.border("none"),
    color: "var(--aa-accent)",
    cursor: "pointer",
    fontSize: "12px",
    ...shorthands.padding("0"),
    ":hover": {
      textDecoration: "underline",
    },
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
    ...shorthands.border("1px", "solid", "var(--aa-border)"),
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
  filterRow: {
    display: "flex",
    gap: "8px",
    alignItems: "center",
  },
  filterBtn: {
    background: "none",
    ...shorthands.border("1px", "solid", "var(--aa-border)"),
    ...shorthands.borderRadius("8px"),
    ...shorthands.padding("4px", "12px"),
    color: "var(--aa-soft)",
    cursor: "pointer",
    fontSize: "12px",
    ":hover": {
      backgroundColor: "rgba(255, 255, 255, 0.06)",
    },
  },
  filterBtnActive: {
    background: "rgba(108, 182, 255, 0.12)",
    ...shorthands.border("1px", "solid", "rgba(108, 182, 255, 0.30)"),
    ...shorthands.borderRadius("8px"),
    ...shorthands.padding("4px", "12px"),
    color: "var(--aa-text)",
    cursor: "pointer",
    fontSize: "12px",
  },
});

// ── Component ──

export interface SessionHistoryPanelProps {
  hoursBack?: number;
}

export default function SessionHistoryPanel({
  hoursBack,
}: SessionHistoryPanelProps) {
  const s = useLocalStyles();
  const [sessions, setSessions] = useState<ConversationSessionSnapshot[]>([]);
  const [stats, setStats] = useState<SessionStats | null>(null);
  const [total, setTotal] = useState(0);
  const [offset, setOffset] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState<string | undefined>(undefined);
  const [expandedIds, setExpandedIds] = useState<Set<string>>(new Set());
  const fetchIdRef = useRef(0);

  const toggleExpanded = useCallback((id: string) => {
    setExpandedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);

  const fetchData = useCallback(
    async (pageOffset: number) => {
      const id = ++fetchIdRef.current;
      setLoading(true);
      setError(null);

      const [sessionsResult, statsResult] = await Promise.allSettled([
        getSessions(filter, PAGE_SIZE, pageOffset, hoursBack),
        getSessionStats(hoursBack),
      ]);

      if (id !== fetchIdRef.current) return;

      if (sessionsResult.status === "fulfilled") {
        setSessions(sessionsResult.value.sessions);
        setTotal(sessionsResult.value.totalCount);
        if (pageOffset > 0 && pageOffset >= sessionsResult.value.totalCount) {
          const clamped = Math.max(
            0,
            Math.floor((sessionsResult.value.totalCount - 1) / PAGE_SIZE) *
              PAGE_SIZE,
          );
          setOffset(clamped);
        }
      } else {
        setError(
          sessionsResult.reason instanceof Error
            ? sessionsResult.reason.message
            : "Failed to load session history",
        );
      }

      if (statsResult.status === "fulfilled") {
        setStats(statsResult.value);
      }

      setLoading(false);
    },
    [filter, hoursBack],
  );

  useEffect(() => {
    setOffset(0);
  }, [filter]);

  useEffect(() => {
    fetchData(offset);
  }, [fetchData, offset]);

  if (loading && sessions.length === 0) {
    return (
      <div className={s.root}>
        <Spinner size="small" label="Loading session history…" />
      </div>
    );
  }

  if (error && sessions.length === 0) {
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
      {error && sessions.length > 0 && (
        <div className={s.error}>{error} — showing cached data.</div>
      )}

      {stats && (
        <div className={s.statsRow}>
          <div className={s.statCard}>
            <span className={s.statValue}>{stats.totalSessions}</span>
            <span className={s.statLabel}>Total Sessions</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue} style={{ color: "var(--aa-lime)" }}>
              {stats.activeSessions}
            </span>
            <span className={s.statLabel}>Active</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue} style={{ color: "var(--aa-plum)" }}>
              {stats.archivedSessions}
            </span>
            <span className={s.statLabel}>Archived</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue} style={{ color: "var(--aa-cyan)" }}>
              {stats.totalMessages}
            </span>
            <span className={s.statLabel}>Total Messages</span>
          </div>
        </div>
      )}

      <div className={s.headerRow}>
        <div className={s.filterRow}>
          <button
            className={filter === undefined ? s.filterBtnActive : s.filterBtn}
            onClick={() => setFilter(undefined)}
          >
            All
          </button>
          <button
            className={filter === "Active" ? s.filterBtnActive : s.filterBtn}
            onClick={() => setFilter("Active")}
          >
            Active
          </button>
          <button
            className={filter === "Archived" ? s.filterBtnActive : s.filterBtn}
            onClick={() => setFilter("Archived")}
          >
            Archived
          </button>
        </div>
        <button className={s.refreshBtn} onClick={() => fetchData(offset)}>
          <ArrowSyncRegular style={{ fontSize: 14 }} /> Refresh
        </button>
      </div>

      {sessions.length === 0 ? (
        <div className={s.emptyNote}>No conversation sessions recorded yet.</div>
      ) : (
        <>
          <div className={s.tableWrap}>
          <table className={s.table}>
            <thead>
              <tr>
                <th className={s.th}>Status</th>
                <th className={s.th}>Room</th>
                <th className={s.th}>Epoch</th>
                <th className={s.th}>Messages</th>
                <th className={s.th}>Created</th>
                <th className={s.th}>Summary</th>
              </tr>
            </thead>
            <tbody>
              {sessions.map((session) => {
                const isActive = session.status === "Active";
                const hasSummary =
                  session.summary != null && session.summary.length > 0;
                const isExpanded = expandedIds.has(session.id);

                return (
                  <tr
                    key={session.id}
                    className={isActive ? s.activeRow : undefined}
                  >
                    <td className={s.td}>
                      <Badge
                        appearance="filled"
                        color={isActive ? "success" : "informative"}
                        icon={
                          isActive ? (
                            <PlayRegular style={{ fontSize: 14 }} />
                          ) : (
                            <ArchiveRegular style={{ fontSize: 14 }} />
                          )
                        }
                      >
                        {session.status}
                      </Badge>
                    </td>
                    <td className={s.td}>
                      <Tooltip
                        content={`${session.roomType} room`}
                        relationship="label"
                      >
                        <span>
                          <ChatRegular
                            style={{ fontSize: 14, marginRight: 4 }}
                          />
                          <span className={s.mono}>{session.roomId}</span>
                        </span>
                      </Tooltip>
                    </td>
                    <td className={s.td}>
                      <span className={s.mono}>#{session.sequenceNumber}</span>
                    </td>
                    <td className={s.td}>{session.messageCount}</td>
                    <td className={s.td}>
                      <Tooltip
                        content={formatTimestamp(session.createdAt, false)}
                        relationship="label"
                      >
                        <span>{formatRelativeTime(session.createdAt)}</span>
                      </Tooltip>
                      {session.archivedAt && (
                        <div
                          style={{
                            fontSize: "11px",
                            color: "var(--aa-muted)",
                          }}
                        >
                          Archived{" "}
                          {formatRelativeTime(session.archivedAt)}
                        </div>
                      )}
                    </td>
                    <td className={s.td}>
                      {hasSummary ? (
                        <div className={s.summaryCell}>
                          {isExpanded
                            ? session.summary
                            : truncateSummary(session.summary!)}
                          {session.summary!.length > 120 && (
                            <>
                              {" "}
                              <button
                                className={s.summaryToggle}
                                onClick={() => toggleExpanded(session.id)}
                              >
                                {isExpanded ? "Show less" : "Show more"}
                              </button>
                            </>
                          )}
                        </div>
                      ) : (
                        <span className={s.mono}>
                          {isActive ? "In progress" : "—"}
                        </span>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
          </div>

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
