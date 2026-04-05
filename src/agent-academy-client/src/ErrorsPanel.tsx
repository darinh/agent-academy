import { useCallback, useEffect, useRef, useState } from "react";
import {
  Badge,
  Spinner,
  Tooltip,
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import {
  ArrowSyncRegular,
  ErrorCircleRegular,
  CheckmarkCircleRegular,
} from "@fluentui/react-icons";
import {
  getGlobalErrorSummary,
  getGlobalErrorRecords,
  type ErrorSummary,
  type ErrorRecord,
} from "./api";
import type { CircuitBreakerState } from "./useCircuitBreakerPolling";

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
  thRight: {
    textAlign: "right" as const,
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
  tdRight: {
    ...shorthands.padding("10px", "12px"),
    borderBottom: "1px solid rgba(255, 244, 227, 0.05)",
    color: "var(--aa-text)",
    verticalAlign: "middle" as const,
    textAlign: "right" as const,
    fontFamily: "var(--mono, monospace)",
    fontSize: "12px",
  },
  mono: {
    fontFamily: "var(--mono, monospace)",
    fontSize: "12px",
    color: "var(--aa-muted)",
  },
  msgCell: {
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
    fontSize: "14px",
    fontWeight: 680,
    color: "var(--aa-text-strong)",
    letterSpacing: "-0.02em",
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
    ...shorthands.borderRadius("14px"),
    border: "1px solid rgba(214, 188, 149, 0.10)",
    backgroundColor: "rgba(255, 255, 255, 0.025)",
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
});

// ── Helpers ──

function errorTypeBadge(
  errorType: string,
): { color: "danger" | "warning" | "important" | "informative"; label: string } {
  switch (errorType) {
    case "authentication":
      return { color: "danger", label: "Auth" };
    case "authorization":
      return { color: "danger", label: "Authz" };
    case "quota":
      return { color: "warning", label: "Quota" };
    case "transient":
      return { color: "important", label: "Transient" };
    default:
      return { color: "informative", label: errorType };
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

export function circuitBreakerDisplay(state: CircuitBreakerState): {
  color: string;
  label: string;
  detail: string;
} {
  switch (state) {
    case "Open":
      return {
        color: "#f85149",
        label: "Circuit Open",
        detail: "Agent requests are blocked. Waiting for cooldown before probing.",
      };
    case "HalfOpen":
      return {
        color: "#ffbe70",
        label: "Circuit Half-Open",
        detail: "Probing with a single request to test if the backend has recovered.",
      };
    case "Closed":
      return {
        color: "#48d67a",
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
          <CheckmarkCircleRegular style={{ fontSize: 20, color: "#48d67a", marginRight: 6 }} />
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
            <span className={s.statValue} style={{ color: "#f85149" }}>
              {summary.totalErrors}
            </span>
            <span className={s.statLabel}>Total Errors</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue} style={{ color: "#ffbe70" }}>
              {summary.recoverableErrors}
            </span>
            <span className={s.statLabel}>Recoverable</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue} style={{ color: "#f85149" }}>
              {summary.unrecoverableErrors}
            </span>
            <span className={s.statLabel}>Unrecoverable</span>
          </div>
        </div>
      )}

      {/* Breakdowns */}
      {summary && (summary.byType.length > 0 || summary.byAgent.length > 0) && (
        <div className={s.breakdownRow}>
          {summary.byType.length > 0 && (
            <div>
              <div className={s.sectionTitle} style={{ marginBottom: "8px" }}>By Type</div>
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
                          <Badge appearance="filled" color={badge.color}>{badge.label}</Badge>
                        </td>
                        <td className={s.tdRight}>{t.count}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
          {summary.byAgent.length > 0 && (
            <div>
              <div className={s.sectionTitle} style={{ marginBottom: "8px" }}>By Agent</div>
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
                        <Badge appearance="outline" color="informative">{a.agentId}</Badge>
                      </td>
                      <td className={s.tdRight}>{a.count}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
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
                        <Badge appearance="filled" color={badge.color}>{badge.label}</Badge>
                      </td>
                      <td className={s.td}>
                        <Tooltip content={rec.message} relationship="label">
                          <span className={s.msgCell}>{rec.message}</span>
                        </Tooltip>
                      </td>
                      <td className={s.td}>
                        <Badge
                          appearance="outline"
                          color={rec.recoverable ? "success" : "danger"}
                        >
                          {rec.recoverable ? "Yes" : "No"}
                        </Badge>
                      </td>
                      <td className={s.td}>
                        <span className={s.mono}>{formatTimestamp(rec.timestamp)}</span>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>

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
