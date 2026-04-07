import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Badge,
  Spinner,
  Tooltip,
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import {
  ArrowSyncRegular,
  CheckmarkCircleRegular,
  ClipboardTaskListLtrRegular,
} from "@fluentui/react-icons";
import { formatTimestamp } from "./panelUtils";
import {
  getAuditLog,
  getAuditStats,
  type AuditLogEntry,
  type AuditStatsResponse,
} from "./api";
import Sparkline from "./Sparkline";
import { bucketByTime } from "./sparklineUtils";

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
  thRight: {
    textAlign: "right" as const,
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
    verticalAlign: "middle" as const,
  },
  tdRight: {
    ...shorthands.padding("5px", "10px"),
    borderBottom: "1px solid var(--aa-border)",
    color: "var(--aa-muted)",
    fontFamily: "var(--mono)",
    fontSize: "11px",
    verticalAlign: "middle" as const,
    textAlign: "right" as const,
  },
  mono: {
    fontFamily: "var(--mono, monospace)",
    fontSize: "12px",
    color: "var(--aa-muted)",
  },
  msgCell: {
    maxWidth: "260px",
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap" as const,
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
  headerRow: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
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
  sectionTitle: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    fontSize: "13px",
    fontWeight: 600,
    color: "var(--aa-text)",
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
  breakdownRow: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(200px, 1fr))",
    gap: "16px",
  },
  sparklineRow: {
    display: "flex",
    alignItems: "center",
    gap: "12px",
    ...shorthands.padding("8px", "12px"),
    ...shorthands.borderRadius("6px"),
    backgroundColor: "rgba(255, 255, 255, 0.02)",
    border: "1px solid var(--aa-border)",
  },
  sparklineLabel: {
    color: "var(--aa-muted)",
    fontSize: "11px",
    fontWeight: 600,
    letterSpacing: "0.06em",
    textTransform: "uppercase" as const,
    whiteSpace: "nowrap" as const,
  },
});

// ── Helpers ──

function statusBadge(
  status: string,
): { color: "success" | "danger" | "warning" | "important" | "informative"; label: string } {
  switch (status) {
    case "Success":
      return { color: "success", label: "Success" };
    case "Error":
      return { color: "danger", label: "Error" };
    case "Denied":
      return { color: "warning", label: "Denied" };
    case "Pending":
      return { color: "informative", label: "Pending" };
    default:
      return { color: "important", label: status };
  }
}

const RECORDS_PAGE = 15;

// ── Component ──

interface AuditLogPanelProps {
  hoursBack?: number;
}

export default function AuditLogPanel({ hoursBack }: AuditLogPanelProps) {
  const s = useLocalStyles();
  const [stats, setStats] = useState<AuditStatsResponse | null>(null);
  const [records, setRecords] = useState<AuditLogEntry[]>([]);
  const [allRecordsForTrend, setAllRecordsForTrend] = useState<AuditLogEntry[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [statsError, setStatsError] = useState<string | null>(null);
  const [recordsError, setRecordsError] = useState<string | null>(null);
  const [page, setPage] = useState(0);
  const fetchIdRef = useRef(0);

  const fetchRecords = useCallback(async (pageNum: number) => {
    const id = ++fetchIdRef.current;
    setLoading(true);
    setRecordsError(null);

    try {
      const result = await getAuditLog({
        hoursBack,
        limit: RECORDS_PAGE,
        offset: pageNum * RECORDS_PAGE,
      });
      if (id !== fetchIdRef.current) return;
      setRecords(result.records);
      setTotal(result.total);
    } catch (err) {
      if (id !== fetchIdRef.current) return;
      setRecordsError(
        err instanceof Error ? err.message : "Failed to load audit records",
      );
    } finally {
      setLoading(false);
    }
  }, [hoursBack]);

  const fetchAll = useCallback(async () => {
    const id = ++fetchIdRef.current;
    setLoading(true);
    setStatsError(null);
    setRecordsError(null);
    setPage(0);

    const [statsResult, recordsResult, trendResult] = await Promise.allSettled([
      getAuditStats(hoursBack),
      getAuditLog({ hoursBack, limit: RECORDS_PAGE, offset: 0 }),
      getAuditLog({ hoursBack, limit: 200, offset: 0 }),
    ]);

    if (id !== fetchIdRef.current) return;

    if (statsResult.status === "fulfilled") {
      setStats(statsResult.value);
    } else {
      setStatsError(
        statsResult.reason instanceof Error
          ? statsResult.reason.message
          : "Failed to load audit stats",
      );
    }

    if (recordsResult.status === "fulfilled") {
      setRecords(recordsResult.value.records);
      setTotal(recordsResult.value.total);
      setPage(0);
    } else {
      setRecordsError(
        recordsResult.reason instanceof Error
          ? recordsResult.reason.message
          : "Failed to load audit records",
      );
    }

    if (trendResult.status === "fulfilled") {
      setAllRecordsForTrend(trendResult.value.records);
    }

    setLoading(false);
  }, [hoursBack]);

  useEffect(() => {
    fetchAll();
  }, [fetchAll]);

  const handlePageChange = (newPage: number) => {
    setPage(newPage);
    fetchRecords(newPage);
  };

  const totalPages = Math.ceil(total / RECORDS_PAGE);

  const SPARKLINE_BUCKETS = 24;
  const commandTrend = useMemo(
    () => bucketByTime(allRecordsForTrend, (r) => r.timestamp, SPARKLINE_BUCKETS, hoursBack),
    [allRecordsForTrend, hoursBack],
  );

  if (loading && !stats && records.length === 0) {
    return (
      <div className={s.root}>
        <Spinner size="small" label="Loading audit data…" />
      </div>
    );
  }

  const bothFailed = statsError && recordsError;
  if (bothFailed && !stats && records.length === 0) {
    return (
      <div className={s.root}>
        <div className={s.error}>{statsError}</div>
      </div>
    );
  }

  if (stats && stats.totalCommands === 0 && records.length === 0) {
    return (
      <div className={s.root}>
        <div className={s.emptyNote}>
          <CheckmarkCircleRegular style={{ fontSize: 20, color: "var(--aa-lime)", marginRight: 6 }} />
          No commands recorded yet.
        </div>
      </div>
    );
  }

  const successCount = stats?.byStatus["Success"] ?? 0;
  const errorCount = stats?.byStatus["Error"] ?? 0;
  const deniedCount = stats?.byStatus["Denied"] ?? 0;

  return (
    <div className={s.root}>
      {statsError && <div className={s.error}>{statsError}</div>}
      {recordsError && <div className={s.error}>{recordsError}</div>}

      {/* Summary stat cards */}
      {stats && stats.totalCommands > 0 && (
        <div className={s.statsRow}>
          <div className={s.statCard}>
            <span className={s.statValue}>{stats.totalCommands}</span>
            <span className={s.statLabel}>Total</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue} style={{ color: "var(--aa-lime)" }}>
              {successCount}
            </span>
            <span className={s.statLabel}>Success</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue} style={{ color: "#f85149" }}>
              {errorCount}
            </span>
            <span className={s.statLabel}>Errors</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue} style={{ color: "var(--aa-gold)" }}>
              {deniedCount}
            </span>
            <span className={s.statLabel}>Denied</span>
          </div>
        </div>
      )}

      {/* Command trend sparkline */}
      {allRecordsForTrend.length >= 2 && (
        <div className={s.sparklineRow} data-testid="audit-sparkline">
          <span className={s.sparklineLabel}>Commands</span>
          <Sparkline data={commandTrend} color="var(--aa-plum)" width={180} height={28} />
        </div>
      )}

      {/* Breakdowns */}
      {stats && (Object.keys(stats.byAgent).length > 0 || Object.keys(stats.byCommand).length > 0) && (
        <div className={s.breakdownRow}>
          {Object.keys(stats.byAgent).length > 0 && (
            <div>
              <div className={s.sectionTitle} style={{ marginBottom: "8px" }}>By Agent</div>
              <div className={s.tableWrap}>
              <table className={s.table}>
                <thead>
                  <tr>
                    <th className={s.th}>Agent</th>
                    <th className={s.thRight}>Count</th>
                  </tr>
                </thead>
                <tbody>
                  {Object.entries(stats.byAgent).map(([agentId, count]) => (
                    <tr key={agentId}>
                      <td className={s.td}>
                        <Badge appearance="outline" color="informative">{agentId}</Badge>
                      </td>
                      <td className={s.tdRight}>{count}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              </div>
            </div>
          )}
          {Object.keys(stats.byCommand).length > 0 && (
            <div>
              <div className={s.sectionTitle} style={{ marginBottom: "8px" }}>Top Commands</div>
              <div className={s.tableWrap}>
              <table className={s.table}>
                <thead>
                  <tr>
                    <th className={s.th}>Command</th>
                    <th className={s.thRight}>Count</th>
                  </tr>
                </thead>
                <tbody>
                  {Object.entries(stats.byCommand).map(([cmd, count]) => (
                    <tr key={cmd}>
                      <td className={s.td}>
                        <span className={s.mono}>{cmd}</span>
                      </td>
                      <td className={s.tdRight}>{count}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              </div>
            </div>
          )}
        </div>
      )}

      {/* Recent audit records */}
      <div>
        <div className={s.headerRow}>
          <div className={s.sectionTitle}>
            <ClipboardTaskListLtrRegular style={{ fontSize: 18 }} />
            Recent Commands
            {total > 0 && (
              <Badge appearance="outline" color="informative" size="small">{total}</Badge>
            )}
          </div>
          <button className={s.refreshBtn} onClick={fetchAll}>
            <ArrowSyncRegular style={{ fontSize: 14 }} /> Refresh
          </button>
        </div>

        {records.length === 0 ? (
          <div className={s.emptyNote}>No command records found.</div>
        ) : (
          <>
            <div className={s.tableWrap}>
            <table className={s.table}>
              <thead>
                <tr>
                  <th className={s.th}>Agent</th>
                  <th className={s.th}>Command</th>
                  <th className={s.th}>Status</th>
                  <th className={s.th}>Error</th>
                  <th className={s.th}>Time</th>
                </tr>
              </thead>
              <tbody>
                {records.map((rec) => {
                  const badge = statusBadge(rec.status);
                  return (
                    <tr key={rec.id}>
                      <td className={s.td}>
                        <Badge
                          appearance="outline"
                          color={rec.source === "human-ui" ? "warning" : "informative"}
                        >
                          {rec.agentId}
                        </Badge>
                      </td>
                      <td className={s.td}>
                        <span className={s.mono}>{rec.command}</span>
                      </td>
                      <td className={s.td}>
                        <Badge appearance="filled" color={badge.color}>{badge.label}</Badge>
                      </td>
                      <td className={s.td}>
                        {rec.errorMessage ? (
                          <Tooltip content={rec.errorMessage} relationship="label">
                            <span className={s.msgCell}>
                              {rec.errorCode ? `[${rec.errorCode}] ` : ""}
                              {rec.errorMessage}
                            </span>
                          </Tooltip>
                        ) : (
                          <span className={s.mono}>—</span>
                        )}
                      </td>
                      <td className={s.td}>
                        <span className={s.mono}>{formatTimestamp(rec.timestamp)}</span>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
            </div>

            {totalPages > 1 && (
              <div className={s.pagerRow}>
                <button
                  className={s.pagerBtn}
                  disabled={page === 0}
                  onClick={() => handlePageChange(Math.max(0, page - 1))}
                >
                  ← Newer
                </button>
                <span>
                  {page * RECORDS_PAGE + 1}–
                  {Math.min((page + 1) * RECORDS_PAGE, total)} of{" "}
                  {total}
                </span>
                <button
                  className={s.pagerBtn}
                  disabled={page >= totalPages - 1}
                  onClick={() => handlePageChange(page + 1)}
                >
                  Older →
                </button>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}
