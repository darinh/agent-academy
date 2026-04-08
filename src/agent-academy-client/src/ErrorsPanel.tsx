import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Spinner,
  Tooltip,
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import V3Badge from "./V3Badge";
import {
  ArrowSyncRegular,
  ErrorCircleRegular,
  CheckmarkCircleRegular,
} from "@fluentui/react-icons";
import { errorTypeBadge, formatTimestamp } from "./panelUtils";
import {
  getGlobalErrorSummary,
  getGlobalErrorRecords,
  type ErrorSummary,
  type ErrorRecord,
} from "./api";
import type { CircuitBreakerState } from "./useCircuitBreakerPolling";
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
    display: "block",
    maxWidth: "300px",
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
    color: "var(--aa-copper)",
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
  cbRow: {
    display: "flex",
    alignItems: "center",
    gap: "12px",
    ...shorthands.padding("12px", "16px"),
    ...shorthands.borderRadius("6px"),
    border: "1px solid var(--aa-border)",
    backgroundColor: "var(--aa-bg)",
  },
  cbDot: {
    width: "10px",
    height: "10px",
    ...shorthands.borderRadius("50%"),
    flexShrink: 0,
  },
  cbLabel: {
    fontSize: "13px",
    fontWeight: 600,
    color: "var(--aa-text)",
  },
  cbDetail: {
    fontSize: "12px",
    color: "var(--aa-muted)",
    marginLeft: "auto",
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

export function circuitBreakerDisplay(state: CircuitBreakerState): {
  color: string;
  label: string;
  detail: string;
} {
  switch (state) {
    case "Open":
      return {
        color: "var(--aa-copper)",
        label: "Circuit Open",
        detail: "Agent requests are blocked. Waiting for cooldown before probing.",
      };
    case "HalfOpen":
      return {
        color: "var(--aa-gold)",
        label: "Circuit Half-Open",
        detail: "Probing with a single request to test if the backend has recovered.",
      };
    case "Closed":
      return {
        color: "var(--aa-lime)",
        label: "Circuit Closed",
        detail: "All systems normal.",
      };
    default:
      return {
        color: "var(--aa-muted)",
        label: "Unknown",
        detail: "Circuit breaker state is unavailable.",
      };
  }
}

const RECORDS_PAGE = 15;

// ── Component ──

interface ErrorsPanelProps {
  hoursBack?: number;
  circuitBreakerState?: CircuitBreakerState;
}

export default function ErrorsPanel({ hoursBack, circuitBreakerState }: ErrorsPanelProps) {
  const s = useLocalStyles();
  const [summary, setSummary] = useState<ErrorSummary | null>(null);
  const [records, setRecords] = useState<ErrorRecord[]>([]);
  const [loading, setLoading] = useState(true);
  const [summaryError, setSummaryError] = useState<string | null>(null);
  const [recordsError, setRecordsError] = useState<string | null>(null);
  const [page, setPage] = useState(0);
  const fetchIdRef = useRef(0);

  const fetchData = useCallback(async () => {
    const id = ++fetchIdRef.current;
    setLoading(true);
    setSummaryError(null);
    setRecordsError(null);

    const [summaryResult, recordsResult] = await Promise.allSettled([
      getGlobalErrorSummary(hoursBack),
      getGlobalErrorRecords(undefined, hoursBack, 100),
    ]);

    if (id !== fetchIdRef.current) return;

    if (summaryResult.status === "fulfilled") {
      setSummary(summaryResult.value);
    } else {
      setSummaryError(
        summaryResult.reason instanceof Error
          ? summaryResult.reason.message
          : "Failed to load error summary",
      );
    }

    if (recordsResult.status === "fulfilled") {
      setRecords(recordsResult.value);
      setPage(0);
    } else {
      setRecordsError(
        recordsResult.reason instanceof Error
          ? recordsResult.reason.message
          : "Failed to load error records",
      );
    }

    setLoading(false);
  }, [hoursBack]);

  useEffect(() => {
    fetchData();
  }, [fetchData]);

  const pagedRecords = records.slice(page * RECORDS_PAGE, (page + 1) * RECORDS_PAGE);
  const totalPages = Math.ceil(records.length / RECORDS_PAGE);

  const SPARKLINE_BUCKETS = 24;
  const errorTrend = useMemo(
    () => bucketByTime(records, (r) => r.timestamp, SPARKLINE_BUCKETS, hoursBack),
    [records, hoursBack],
  );

  if (loading && !summary && records.length === 0) {
    return (
      <div className={s.root}>
        <Spinner size="small" label="Loading error data…" />
      </div>
    );
  }

  const bothFailed = summaryError && recordsError;
  if (bothFailed && !summary && records.length === 0) {
    return (
      <div className={s.root}>
        <div className={s.error}>{summaryError}</div>
      </div>
    );
  }

  // Zero errors — show clean state
  if (summary && summary.totalErrors === 0 && records.length === 0) {
    const cbInfo = circuitBreakerState ? circuitBreakerDisplay(circuitBreakerState) : null;
    return (
      <div className={s.root}>
        {cbInfo && circuitBreakerState !== "Closed" && (
          <div className={s.cbRow} data-testid="circuit-breaker-status">
            <span className={s.cbDot} style={{ backgroundColor: cbInfo.color }} />
            <span className={s.cbLabel}>{cbInfo.label}</span>
            <span className={s.cbDetail}>{cbInfo.detail}</span>
          </div>
        )}
        <div className={s.emptyNote}>
          <CheckmarkCircleRegular style={{ fontSize: 20, color: "var(--aa-lime)", marginRight: 6 }} />
          No errors recorded. All agents operating normally.
        </div>
      </div>
    );
  }

  const cbInfo = circuitBreakerState ? circuitBreakerDisplay(circuitBreakerState) : null;

  return (
    <div className={s.root}>
      {cbInfo && (
        <div className={s.cbRow} data-testid="circuit-breaker-status">
          <span className={s.cbDot} style={{ backgroundColor: cbInfo.color }} />
          <span className={s.cbLabel}>{cbInfo.label}</span>
          <span className={s.cbDetail}>{cbInfo.detail}</span>
        </div>
      )}

      {summaryError && (
        <div className={s.error}>{summaryError}</div>
      )}
      {recordsError && (
        <div className={s.error}>{recordsError}</div>
      )}

      {/* Summary stat cards */}
      {summary && summary.totalErrors > 0 && (
        <div className={s.statsRow}>
          <div className={s.statCard}>
            <span className={s.statValue} style={{ color: "var(--aa-copper)" }}>
              {summary.totalErrors}
            </span>
            <span className={s.statLabel}>Total Errors</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue} style={{ color: "var(--aa-gold)" }}>
              {summary.recoverableErrors}
            </span>
            <span className={s.statLabel}>Recoverable</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue} style={{ color: "var(--aa-copper)" }}>
              {summary.unrecoverableErrors}
            </span>
            <span className={s.statLabel}>Unrecoverable</span>
          </div>
        </div>
      )}

      {/* Error trend sparkline */}
      {records.length >= 2 && (
        <div className={s.sparklineRow} data-testid="errors-sparkline">
          <span className={s.sparklineLabel}>Error Rate</span>
          <Sparkline data={errorTrend} color="var(--aa-copper)" width={180} height={28} />
        </div>
      )}

      {/* Breakdowns */}
      {summary && (summary.byType.length > 0 || summary.byAgent.length > 0) && (
        <div className={s.breakdownRow}>
          {summary.byType.length > 0 && (
            <div>
              <div className={s.sectionTitle} style={{ marginBottom: "8px" }}>By Type</div>
              <div className={s.tableWrap}>
              <table className={s.table}>
                <thead>
                  <tr>
                    <th className={s.th}>Type</th>
                    <th className={s.thRight}>Count</th>
                  </tr>
                </thead>
                <tbody>
                  {summary.byType.map((t) => {
                    const badge = errorTypeBadge(t.errorType);
                    return (
                      <tr key={t.errorType}>
                        <td className={s.td}>
                          <V3Badge color={badge.color}>{badge.label}</V3Badge>
                        </td>
                        <td className={s.tdRight}>{t.count}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
              </div>
            </div>
          )}
          {summary.byAgent.length > 0 && (
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
                  {summary.byAgent.map((a) => (
                    <tr key={a.agentId}>
                      <td className={s.td}>
                        <V3Badge color="info">{a.agentId}</V3Badge>
                      </td>
                      <td className={s.tdRight}>{a.count}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              </div>
            </div>
          )}
        </div>
      )}

      {/* Recent error records */}
      <div>
        <div className={s.headerRow}>
          <div className={s.sectionTitle}>
            <ErrorCircleRegular style={{ fontSize: 18 }} />
            Recent Errors
          </div>
          <button className={s.refreshBtn} onClick={fetchData}>
            <ArrowSyncRegular style={{ fontSize: 14 }} /> Refresh
          </button>
        </div>

        {records.length === 0 ? (
          <div className={s.emptyNote}>No error records found.</div>
        ) : (
          <>
            <div className={s.tableWrap}>
            <table className={s.table}>
              <thead>
                <tr>
                  <th className={s.th}>Agent</th>
                  <th className={s.th}>Type</th>
                  <th className={s.th}>Message</th>
                  <th className={s.th}>Recovery</th>
                  <th className={s.th}>Time</th>
                </tr>
              </thead>
              <tbody>
                {pagedRecords.map((rec, i) => {
                  const badge = errorTypeBadge(rec.errorType);
                  return (
                    <tr key={`${rec.timestamp}-${i}`}>
                      <td className={s.td}>
                        <span className={s.mono}>{rec.agentId}</span>
                      </td>
                      <td className={s.td}>
                        <V3Badge color={badge.color}>{badge.label}</V3Badge>
                      </td>
                      <td className={s.td}>
                        <Tooltip content={rec.message} relationship="label">
                          <span className={s.msgCell}>{rec.message}</span>
                        </Tooltip>
                      </td>
                      <td className={s.td}>
                        <V3Badge color={rec.recoverable ? "ok" : "err"}>
                          {rec.recoverable ? "Yes" : "No"}
                        </V3Badge>
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
                  onClick={() => setPage((p) => Math.max(0, p - 1))}
                >
                  ← Newer
                </button>
                <span>
                  {page * RECORDS_PAGE + 1}–
                  {Math.min((page + 1) * RECORDS_PAGE, records.length)} of{" "}
                  {records.length}
                </span>
                <button
                  className={s.pagerBtn}
                  disabled={page >= totalPages - 1}
                  onClick={() => setPage((p) => p + 1)}
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
