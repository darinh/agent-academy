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
  MoneyRegular,
  TextBulletListSquareRegular,
} from "@fluentui/react-icons";
import {
  getGlobalUsage,
  getGlobalUsageRecords,
  type UsageSummary,
  type LlmUsageRecord,
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
  modelTag: {
    display: "inline-block",
    ...shorthands.padding("2px", "8px"),
    ...shorthands.borderRadius("12px"),
    fontSize: "11px",
    fontWeight: 600,
    letterSpacing: "0.02em",
    backgroundColor: "rgba(108, 182, 255, 0.10)",
    color: "#6cb6ff",
    marginRight: "6px",
    marginBottom: "4px",
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
});

// ── Helpers ──

function formatTokenCount(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
  return String(n);
}

function formatCost(cost: number): string {
  if (cost === 0) return "$0.00";
  if (cost < 0.01) return `$${cost.toFixed(4)}`;
  return `$${cost.toFixed(2)}`;
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

function formatDuration(ms: number | null): string {
  if (ms == null) return "—";
  if (ms < 1000) return `${ms}ms`;
  return `${(ms / 1000).toFixed(1)}s`;
}

const RECORDS_PAGE = 15;

// ── Component ──

export default function UsagePanel() {
  const s = useLocalStyles();
  const [summary, setSummary] = useState<UsageSummary | null>(null);
  const [records, setRecords] = useState<LlmUsageRecord[]>([]);
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
      getGlobalUsage(),
      getGlobalUsageRecords(undefined, 100),
    ]);

    if (id !== fetchIdRef.current) return;

    if (summaryResult.status === "fulfilled") {
      setSummary(summaryResult.value);
    } else {
      setSummaryError(
        summaryResult.reason instanceof Error
          ? summaryResult.reason.message
          : "Failed to load usage summary",
      );
    }

    if (recordsResult.status === "fulfilled") {
      setRecords(recordsResult.value);
      setPage(0);
    } else {
      setRecordsError(
        recordsResult.reason instanceof Error
          ? recordsResult.reason.message
          : "Failed to load usage records",
      );
    }

    setLoading(false);
  }, []);

  useEffect(() => {
    fetchData();
  }, [fetchData]);

  // Derive per-agent breakdown from records
  const agentBreakdown = (() => {
    const map = new Map<string, { input: number; output: number; cost: number; count: number }>();
    for (const r of records) {
      const entry = map.get(r.agentId) ?? { input: 0, output: 0, cost: 0, count: 0 };
      entry.input += r.inputTokens;
      entry.output += r.outputTokens;
      entry.cost += r.cost ?? 0;
      entry.count += 1;
      map.set(r.agentId, entry);
    }
    return [...map.entries()]
      .map(([agentId, stats]) => ({ agentId, ...stats }))
      .sort((a, b) => b.cost - a.cost || b.count - a.count);
  })();

  // Pagination for records
  const pagedRecords = records.slice(page * RECORDS_PAGE, (page + 1) * RECORDS_PAGE);
  const totalPages = Math.ceil(records.length / RECORDS_PAGE);

  if (loading && !summary && records.length === 0) {
    return (
      <div className={s.root}>
        <Spinner size="small" label="Loading usage data…" />
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

  return (
    <div className={s.root}>
      {summaryError && (
        <div className={s.error}>{summaryError}</div>
      )}
      {recordsError && (
        <div className={s.error}>{recordsError}</div>
      )}

      {/* Summary stat cards */}
      {summary && (
        <div className={s.statsRow}>
          <div className={s.statCard}>
            <span className={s.statValue}>{formatTokenCount(summary.totalInputTokens)}</span>
            <span className={s.statLabel}>Input Tokens</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue}>{formatTokenCount(summary.totalOutputTokens)}</span>
            <span className={s.statLabel}>Output Tokens</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue} style={{ color: "#48d67a" }}>
              {formatCost(summary.totalCost)}
            </span>
            <span className={s.statLabel}>Total Cost</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue} style={{ color: "#ffbe70" }}>
              {summary.requestCount}
            </span>
            <span className={s.statLabel}>LLM Calls</span>
          </div>
        </div>
      )}

      {/* Models used */}
      {summary && summary.models.length > 0 && (
        <div>
          <div className={s.sectionTitle} style={{ marginBottom: "8px" }}>Models</div>
          <div>
            {summary.models.map((model) => (
              <span key={model} className={s.modelTag}>{model}</span>
            ))}
          </div>
        </div>
      )}

      {/* Per-agent breakdown */}
      {agentBreakdown.length > 0 && (
        <div>
          <div className={s.headerRow}>
            <div className={s.sectionTitle}>
              <TextBulletListSquareRegular style={{ fontSize: 18 }} />
              Per-Agent Breakdown
              <span style={{ fontSize: "11px", color: "var(--aa-muted)", fontWeight: 400 }}>
                (last {records.length} calls)
              </span>
            </div>
          </div>
          <table className={s.table}>
            <thead>
              <tr>
                <th className={s.th}>Agent</th>
                <th className={s.thRight}>Input</th>
                <th className={s.thRight}>Output</th>
                <th className={s.thRight}>Cost</th>
                <th className={s.thRight}>Calls</th>
              </tr>
            </thead>
            <tbody>
              {agentBreakdown.map((agent) => (
                <tr key={agent.agentId}>
                  <td className={s.td}>
                    <Badge appearance="outline" color="informative">
                      {agent.agentId}
                    </Badge>
                  </td>
                  <td className={s.tdRight}>{formatTokenCount(agent.input)}</td>
                  <td className={s.tdRight}>{formatTokenCount(agent.output)}</td>
                  <td className={s.tdRight}>{formatCost(agent.cost)}</td>
                  <td className={s.tdRight}>{agent.count}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* Recent records */}
      <div>
        <div className={s.headerRow}>
          <div className={s.sectionTitle}>
            <MoneyRegular style={{ fontSize: 18 }} />
            Recent LLM Calls
          </div>
          <button className={s.refreshBtn} onClick={fetchData}>
            <ArrowSyncRegular style={{ fontSize: 14 }} /> Refresh
          </button>
        </div>

        {records.length === 0 ? (
          <div className={s.emptyNote}>No LLM usage recorded yet.</div>
        ) : (
          <>
            <table className={s.table}>
              <thead>
                <tr>
                  <th className={s.th}>Agent</th>
                  <th className={s.th}>Model</th>
                  <th className={s.thRight}>In</th>
                  <th className={s.thRight}>Out</th>
                  <th className={s.thRight}>Cost</th>
                  <th className={s.thRight}>Duration</th>
                  <th className={s.th}>Time</th>
                </tr>
              </thead>
              <tbody>
                {pagedRecords.map((rec) => (
                  <tr key={rec.id}>
                    <td className={s.td}>
                      <Tooltip content={rec.agentId} relationship="label">
                        <span className={s.mono}>
                          {rec.agentId.length > 16
                            ? `${rec.agentId.slice(0, 14)}…`
                            : rec.agentId}
                        </span>
                      </Tooltip>
                    </td>
                    <td className={s.td}>
                      <span className={s.mono}>{rec.model ?? "—"}</span>
                    </td>
                    <td className={s.tdRight}>{formatTokenCount(rec.inputTokens)}</td>
                    <td className={s.tdRight}>{formatTokenCount(rec.outputTokens)}</td>
                    <td className={s.tdRight}>
                      {rec.cost != null ? formatCost(rec.cost) : "—"}
                    </td>
                    <td className={s.tdRight}>{formatDuration(rec.durationMs)}</td>
                    <td className={s.td}>
                      <span className={s.mono}>{formatTimestamp(rec.recordedAt)}</span>
                    </td>
                  </tr>
                ))}
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
