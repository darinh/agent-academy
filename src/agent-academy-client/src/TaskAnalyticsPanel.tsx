import { useCallback, useEffect, useRef, useState } from "react";
import {
  Spinner,
  Tooltip,
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import {
  ArrowSyncRegular,
  CheckmarkCircleRegular,
  ClockRegular,
  ArrowRepeatAllRegular,
  BranchRegular,
} from "@fluentui/react-icons";
import V3Badge from "./V3Badge";
import type { BadgeColor } from "./V3Badge";
import Sparkline from "./Sparkline";
import { getTaskCycleAnalytics, type TaskCycleAnalytics, type AgentTaskEffectiveness } from "./api";

// ── Styles ──

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    gap: "12px",
  },
  toolbar: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    gap: "8px",
  },
  sortSelect: {
    background: "var(--aa-bg)",
    color: "var(--aa-text)",
    border: "1px solid var(--aa-border)",
    ...shorthands.borderRadius("4px"),
    ...shorthands.padding("4px", "8px"),
    fontSize: "11px",
    fontFamily: "var(--mono)",
    cursor: "pointer",
  },
  refreshBtn: {
    display: "inline-flex",
    alignItems: "center",
    gap: "4px",
    cursor: "pointer",
    background: "none",
    border: "none",
    color: "var(--aa-soft)",
    fontSize: "11px",
    fontFamily: "var(--mono)",
    ":hover": { color: "var(--aa-text)" },
  },
  summaryRow: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(100px, 1fr))",
    gap: "8px",
  },
  summaryCard: {
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    ...shorthands.padding("8px", "10px"),
    ...shorthands.borderRadius("6px"),
    border: "1px solid var(--aa-border)",
    backgroundColor: "var(--aa-bg)",
  },
  summaryValue: {
    fontSize: "18px",
    fontWeight: 700,
    color: "var(--aa-text)",
    lineHeight: 1,
  },
  summaryLabel: {
    fontFamily: "var(--mono)",
    color: "var(--aa-soft)",
    fontSize: "10px",
    textAlign: "center" as const,
  },
  throughputChart: {
    ...shorthands.padding("8px"),
    ...shorthands.borderRadius("6px"),
    border: "1px solid var(--aa-border)",
    backgroundColor: "var(--aa-bg)",
  },
  chartLabel: {
    fontFamily: "var(--mono)",
    color: "var(--aa-soft)",
    fontSize: "10px",
    marginBottom: "4px",
  },
  statusRow: {
    display: "flex",
    flexWrap: "wrap" as const,
    gap: "6px",
  },
  agentTable: {
    width: "100%",
    borderCollapse: "collapse" as const,
    fontSize: "12px",
    fontFamily: "var(--mono)",
  },
  th: {
    textAlign: "left" as const,
    color: "var(--aa-soft)",
    fontWeight: 500,
    ...shorthands.padding("6px", "8px"),
    borderBottom: "1px solid var(--aa-border)",
    cursor: "pointer",
    userSelect: "none" as const,
    whiteSpace: "nowrap" as const,
    ":hover": { color: "var(--aa-text)" },
  },
  td: {
    ...shorthands.padding("6px", "8px"),
    borderBottom: "1px solid var(--aa-border)",
    color: "var(--aa-text)",
    whiteSpace: "nowrap" as const,
  },
  nameCell: {
    fontWeight: 600,
  },
  typeRow: {
    display: "flex",
    gap: "10px",
  },
  typeChip: {
    display: "flex",
    alignItems: "center",
    gap: "4px",
    ...shorthands.padding("4px", "8px"),
    ...shorthands.borderRadius("4px"),
    border: "1px solid var(--aa-border)",
    backgroundColor: "var(--aa-bg)",
    fontSize: "11px",
    fontFamily: "var(--mono)",
    color: "var(--aa-text)",
  },
  typeCount: {
    fontWeight: 700,
  },
  empty: {
    color: "var(--aa-soft)",
    fontFamily: "var(--mono)",
    fontSize: "12px",
    textAlign: "center" as const,
    ...shorthands.padding("20px"),
  },
  error: {
    color: "var(--aa-copper, #e85d5d)",
    fontFamily: "var(--mono)",
    fontSize: "12px",
    textAlign: "center" as const,
    ...shorthands.padding("12px"),
  },
});

// ── Helpers ──

function fmtPct(v: number): string {
  return `${(v * 100).toFixed(0)}%`;
}

function fmtHours(v: number | null): string {
  if (v == null) return "—";
  if (v < 1) return `${(v * 60).toFixed(0)}m`;
  if (v < 24) return `${v.toFixed(1)}h`;
  return `${(v / 24).toFixed(1)}d`;
}

function fmtNum(v: number | null): string {
  return v != null ? v.toFixed(1) : "—";
}

type SortField = "completed" | "completionRate" | "avgCycleTimeHours" | "reworkRate" | "firstPassApprovalRate";

function sortAgents(agents: AgentTaskEffectiveness[], field: SortField, desc: boolean): AgentTaskEffectiveness[] {
  return [...agents].sort((a, b) => {
    const av = a[field] ?? -Infinity;
    const bv = b[field] ?? -Infinity;
    return desc ? (bv as number) - (av as number) : (av as number) - (bv as number);
  });
}

const STATUS_BADGES: Array<{ key: string; label: string; color: BadgeColor }> = [
  { key: "active", label: "Active", color: "active" },
  { key: "inReview", label: "In Review", color: "review" },
  { key: "completed", label: "Completed", color: "done" },
  { key: "blocked", label: "Blocked", color: "warn" },
  { key: "cancelled", label: "Cancelled", color: "cancel" },
  { key: "queued", label: "Queued", color: "muted" },
  { key: "changesRequested", label: "Changes Req", color: "bug" },
  { key: "approved", label: "Approved", color: "ok" },
  { key: "awaitingValidation", label: "Awaiting Val", color: "info" },
  { key: "merging", label: "Merging", color: "tool" },
];

// ── Component ──

interface TaskAnalyticsPanelProps {
  hoursBack: number | undefined;
}

export default function TaskAnalyticsPanel({ hoursBack }: TaskAnalyticsPanelProps) {
  const s = useLocalStyles();
  const [data, setData] = useState<TaskCycleAnalytics | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [sortField, setSortField] = useState<SortField>("completed");
  const [sortDesc, setSortDesc] = useState(true);
  const seqRef = useRef(0);

  const load = useCallback(async () => {
    const seq = ++seqRef.current;
    setLoading(true);
    setError(null);
    try {
      const result = await getTaskCycleAnalytics(hoursBack);
      if (seq === seqRef.current) setData(result);
    } catch (err) {
      if (seq === seqRef.current) setError(err instanceof Error ? err.message : "Failed to load");
    } finally {
      if (seq === seqRef.current) setLoading(false);
    }
  }, [hoursBack]);

  useEffect(() => { load(); }, [load]);

  // Auto-refresh every 60s
  useEffect(() => {
    const t = setInterval(load, 60_000);
    return () => clearInterval(t);
  }, [load]);

  if (loading && !data) return <Spinner size="tiny" label="Loading task analytics…" />;
  if (error) return <div className={s.error}>{error}</div>;
  if (!data) return <div className={s.empty}>No data available</div>;

  const { overview, agentEffectiveness, throughputBuckets, typeBreakdown } = data;
  const sorted = sortAgents(agentEffectiveness, sortField, sortDesc);
  const throughputData = throughputBuckets.map((b) => b.completed);

  const handleSort = (field: SortField) => {
    if (sortField === field) setSortDesc(!sortDesc);
    else { setSortField(field); setSortDesc(true); }
  };

  const sortArrow = (field: SortField) => sortField === field ? (sortDesc ? " ▾" : " ▴") : "";

  return (
    <div className={s.root}>
      {/* Toolbar */}
      <div className={s.toolbar}>
        <span style={{ fontFamily: "var(--mono)", fontSize: "10px", color: "var(--aa-soft)" }}>
          {overview.totalTasks} tasks total
        </span>
        <button className={s.refreshBtn} onClick={load} title="Refresh">
          <ArrowSyncRegular style={{ fontSize: 14 }} /> Refresh
        </button>
      </div>

      {/* Summary KPIs */}
      <div className={s.summaryRow}>
        <Tooltip content="Tasks completed / total tasks" relationship="description">
          <div className={s.summaryCard}>
            <CheckmarkCircleRegular style={{ fontSize: 16, color: "var(--aa-lime)" }} />
            <span className={s.summaryValue}>{fmtPct(overview.completionRate)}</span>
            <span className={s.summaryLabel}>Completion</span>
          </div>
        </Tooltip>
        <Tooltip content="Average time from task creation to completion" relationship="description">
          <div className={s.summaryCard}>
            <ClockRegular style={{ fontSize: 16, color: "var(--aa-cyan)" }} />
            <span className={s.summaryValue}>{fmtHours(overview.avgCycleTimeHours)}</span>
            <span className={s.summaryLabel}>Avg Cycle</span>
          </div>
        </Tooltip>
        <Tooltip content="Average time waiting before work starts" relationship="description">
          <div className={s.summaryCard}>
            <ClockRegular style={{ fontSize: 16, color: "var(--aa-gold)" }} />
            <span className={s.summaryValue}>{fmtHours(overview.avgQueueTimeHours)}</span>
            <span className={s.summaryLabel}>Avg Queue</span>
          </div>
        </Tooltip>
        <Tooltip content="Average review rounds per completed task" relationship="description">
          <div className={s.summaryCard}>
            <ArrowRepeatAllRegular style={{ fontSize: 16, color: "var(--aa-plum)" }} />
            <span className={s.summaryValue}>{fmtNum(overview.avgReviewRounds)}</span>
            <span className={s.summaryLabel}>Avg Reviews</span>
          </div>
        </Tooltip>
        <Tooltip content="Tasks requiring more than 1 review round" relationship="description">
          <div className={s.summaryCard}>
            <ArrowRepeatAllRegular style={{ fontSize: 16, color: "var(--aa-copper)" }} />
            <span className={s.summaryValue}>{fmtPct(overview.reworkRate)}</span>
            <span className={s.summaryLabel}>Rework Rate</span>
          </div>
        </Tooltip>
        <Tooltip content="Total commits across completed tasks" relationship="description">
          <div className={s.summaryCard}>
            <BranchRegular style={{ fontSize: 16, color: "var(--aa-text)" }} />
            <span className={s.summaryValue}>{overview.totalCommits}</span>
            <span className={s.summaryLabel}>Commits</span>
          </div>
        </Tooltip>
      </div>

      {/* Status distribution */}
      <div className={s.statusRow}>
        {STATUS_BADGES.map(({ key, label, color }) => {
          const count = overview.statusCounts[key as keyof typeof overview.statusCounts];
          if (count === 0) return null;
          return <V3Badge key={key} color={color}>{label}: {count}</V3Badge>;
        })}
      </div>

      {/* Throughput sparkline */}
      {throughputData.some((v) => v > 0) && (
        <div className={s.throughputChart}>
          <div className={s.chartLabel}>Completed tasks over time</div>
          <Sparkline data={throughputData} width={320} height={40} color="var(--aa-lime)" />
        </div>
      )}

      {/* Type breakdown */}
      <div className={s.typeRow}>
        {typeBreakdown.feature > 0 && (
          <div className={s.typeChip}><span className={s.typeCount}>{typeBreakdown.feature}</span> Feature</div>
        )}
        {typeBreakdown.bug > 0 && (
          <div className={s.typeChip}><span className={s.typeCount}>{typeBreakdown.bug}</span> Bug</div>
        )}
        {typeBreakdown.chore > 0 && (
          <div className={s.typeChip}><span className={s.typeCount}>{typeBreakdown.chore}</span> Chore</div>
        )}
        {typeBreakdown.spike > 0 && (
          <div className={s.typeChip}><span className={s.typeCount}>{typeBreakdown.spike}</span> Spike</div>
        )}
      </div>

      {/* Per-agent effectiveness table */}
      {sorted.length > 0 && (
        <div style={{ overflowX: "auto" }}>
          <table className={s.agentTable}>
            <thead>
              <tr>
                <th className={s.th}>Agent</th>
                <th className={s.th} onClick={() => handleSort("completed")}>
                  Done{sortArrow("completed")}
                </th>
                <th className={s.th} onClick={() => handleSort("completionRate")}>
                  Rate{sortArrow("completionRate")}
                </th>
                <th className={s.th} onClick={() => handleSort("avgCycleTimeHours")}>
                  Cycle{sortArrow("avgCycleTimeHours")}
                </th>
                <th className={s.th} onClick={() => handleSort("firstPassApprovalRate")}>
                  1st Pass{sortArrow("firstPassApprovalRate")}
                </th>
                <th className={s.th} onClick={() => handleSort("reworkRate")}>
                  Rework{sortArrow("reworkRate")}
                </th>
              </tr>
            </thead>
            <tbody>
              {sorted.map((a) => (
                <tr key={a.agentId}>
                  <td className={`${s.td} ${s.nameCell}`}>
                    {a.agentName}
                    <span style={{ color: "var(--aa-soft)", fontWeight: 400, marginLeft: 4 }}>
                      ({a.assigned})
                    </span>
                  </td>
                  <td className={s.td}>{a.completed}</td>
                  <td className={s.td}>{fmtPct(a.completionRate)}</td>
                  <td className={s.td}>{fmtHours(a.avgCycleTimeHours)}</td>
                  <td className={s.td}>{fmtPct(a.firstPassApprovalRate)}</td>
                  <td className={s.td}>{fmtPct(a.reworkRate)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
