import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Spinner,
  Tooltip,
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
import { useErrorsPanelStyles } from "./errors/errorsPanelStyles";
import { circuitBreakerDisplay } from "./errors/circuitBreakerDisplay";

export { circuitBreakerDisplay } from "./errors/circuitBreakerDisplay";

const RECORDS_PAGE = 15;

// ── Component ──

interface ErrorsPanelProps {
  hoursBack?: number;
  circuitBreakerState?: CircuitBreakerState;
}

export default function ErrorsPanel({ hoursBack, circuitBreakerState }: ErrorsPanelProps) {
  const s = useErrorsPanelStyles();
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
