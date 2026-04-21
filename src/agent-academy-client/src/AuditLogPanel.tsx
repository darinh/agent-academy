import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Spinner,
  Tooltip,
} from "@fluentui/react-components";
import V3Badge from "./V3Badge";
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
import { useAuditLogPanelStyles } from "./auditLog/auditLogPanelStyles";
import { statusBadge } from "./auditLog/statusBadge";

const RECORDS_PAGE = 15;

// ── Component ──

interface AuditLogPanelProps {
  hoursBack?: number;
}

export default function AuditLogPanel({ hoursBack }: AuditLogPanelProps) {
  const s = useAuditLogPanelStyles();
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
            <span className={s.statValue} style={{ color: "var(--aa-copper)" }}>
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
                        <V3Badge color="info">{agentId}</V3Badge>
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
              <V3Badge color="info">{total}</V3Badge>
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
                        <V3Badge color={rec.source === "human-ui" ? "warn" : "info"}>
                          {rec.agentId}
                        </V3Badge>
                      </td>
                      <td className={s.td}>
                        <span className={s.mono}>{rec.command}</span>
                      </td>
                      <td className={s.td}>
                        <V3Badge color={badge.color}>{badge.label}</V3Badge>
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
