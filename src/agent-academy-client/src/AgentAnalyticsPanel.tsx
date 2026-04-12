import { useCallback, useEffect, useRef, useState } from "react";
import {
  Spinner,
  Tooltip,
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import {
  ArrowSyncRegular,
  ArrowDownloadRegular,
  PeopleRegular,
  ErrorCircleRegular,
  CheckmarkCircleRegular,
  MoneyRegular,
} from "@fluentui/react-icons";
import V3Badge from "./V3Badge";
import Sparkline from "./Sparkline";
import { formatCost, formatTokenCount } from "./panelUtils";
import { getAgentAnalytics, exportAgentAnalytics, type AgentAnalyticsSummary, type AgentPerformanceMetrics } from "./api";
import AgentDetailView from "./AgentDetailView";

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
  grid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fill, minmax(280px, 1fr))",
    gap: "10px",
  },
  card: {
    display: "flex",
    flexDirection: "column",
    gap: "8px",
    ...shorthands.padding("12px"),
    ...shorthands.borderRadius("8px"),
    border: "1px solid var(--aa-border)",
    backgroundColor: "var(--aa-bg)",
    cursor: "pointer",
    transitionProperty: "border-color",
    transitionDuration: "0.15s",
    ":hover": { ...shorthands.borderColor("var(--aa-cyan, #5b8def)") },
  },
  cardSelected: {
    display: "flex",
    flexDirection: "column",
    gap: "8px",
    ...shorthands.padding("12px"),
    ...shorthands.borderRadius("8px"),
    border: "2px solid var(--aa-cyan, #5b8def)",
    backgroundColor: "var(--aa-bg)",
    cursor: "pointer",
  },
  cardHeader: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    gap: "8px",
  },
  agentName: {
    fontSize: "13px",
    fontWeight: 600,
    color: "var(--aa-text)",
  },
  agentId: {
    fontFamily: "var(--mono)",
    fontSize: "10px",
    color: "var(--aa-soft)",
  },
  metricsRow: {
    display: "grid",
    gridTemplateColumns: "1fr 1fr",
    gap: "6px",
  },
  metric: {
    display: "flex",
    flexDirection: "column",
    gap: "1px",
  },
  metricValue: {
    fontSize: "14px",
    fontWeight: 600,
    color: "var(--aa-text)",
    fontFamily: "var(--mono)",
  },
  metricLabel: {
    fontSize: "9px",
    fontFamily: "var(--mono)",
    color: "var(--aa-soft)",
    textTransform: "uppercase" as const,
  },
  trendRow: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
  },
  empty: {
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    gap: "8px",
    ...shorthands.padding("24px"),
    color: "var(--aa-soft)",
    fontFamily: "var(--mono)",
    fontSize: "12px",
  },
  error: {
    color: "var(--aa-red, #f44)",
    fontFamily: "var(--mono)",
    fontSize: "11px",
  },
});

// ── Sort Options ──

type SortKey = "requests" | "tokens" | "cost" | "errors" | "tasks";

const SORT_OPTIONS: { key: SortKey; label: string }[] = [
  { key: "requests", label: "Requests" },
  { key: "tokens", label: "Tokens" },
  { key: "cost", label: "Cost" },
  { key: "errors", label: "Errors" },
  { key: "tasks", label: "Tasks" },
];

function sortAgents(agents: AgentPerformanceMetrics[], key: SortKey): AgentPerformanceMetrics[] {
  const sorted = [...agents];
  switch (key) {
    case "requests": return sorted.sort((a, b) => b.totalRequests - a.totalRequests);
    case "tokens": return sorted.sort((a, b) =>
      (b.totalInputTokens + b.totalOutputTokens) - (a.totalInputTokens + a.totalOutputTokens));
    case "cost": return sorted.sort((a, b) => b.totalCost - a.totalCost);
    case "errors": return sorted.sort((a, b) => b.totalErrors - a.totalErrors);
    case "tasks": return sorted.sort((a, b) => b.tasksAssigned - a.tasksAssigned);
  }
}

function errorRateColor(errors: number, requests: number): string {
  if (requests === 0 || errors === 0) return "var(--aa-green, #4a4)";
  const rate = errors / requests;
  if (rate > 0.2) return "var(--aa-red, #f44)";
  if (rate > 0.05) return "var(--aa-yellow, #fa0)";
  return "var(--aa-green, #4a4)";
}

// ── Component ──

interface AgentAnalyticsPanelProps {
  hoursBack?: number;
}

export default function AgentAnalyticsPanel({ hoursBack }: AgentAnalyticsPanelProps) {
  const s = useLocalStyles();
  const [data, setData] = useState<AgentAnalyticsSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [sortBy, setSortBy] = useState<SortKey>("requests");
  const [selectedAgentId, setSelectedAgentId] = useState<string | null>(null);
  const fetchIdRef = useRef(0);

  const fetchData = useCallback(async () => {
    const id = ++fetchIdRef.current;
    setLoading(true);
    setError(null);
    try {
      const result = await getAgentAnalytics(hoursBack);
      if (id !== fetchIdRef.current) return;
      setData(result);
    } catch (e) {
      if (id !== fetchIdRef.current) return;
      setError(e instanceof Error ? e.message : "Failed to load analytics");
    } finally {
      if (id === fetchIdRef.current) setLoading(false);
    }
  }, [hoursBack]);

  useEffect(() => {
    fetchData();
  }, [fetchData]);

  // Auto-refresh every 60s
  useEffect(() => {
    const timer = setInterval(fetchData, 60_000);
    return () => clearInterval(timer);
  }, [fetchData]);

  if (loading && !data) {
    return <div className={s.root}><Spinner size="small" label="Loading analytics…" /></div>;
  }

  if (error && !data) {
    return <div className={s.root}><span className={s.error}>{error}</span></div>;
  }

  if (!data || data.agents.length === 0) {
    return (
      <div className={s.empty}>
        <PeopleRegular style={{ fontSize: 28 }} />
        No agent activity recorded{hoursBack ? ` in the last ${hoursBack}h` : ""}.
      </div>
    );
  }

  const sorted = sortAgents(data.agents, sortBy);

  return (
    <div className={s.root}>
      {/* Summary row */}
      <div className={s.summaryRow}>
        <div className={s.summaryCard}>
          <span className={s.summaryValue}>{data.agents.length}</span>
          <span className={s.summaryLabel}>Agents</span>
        </div>
        <div className={s.summaryCard}>
          <span className={s.summaryValue}>{data.totalRequests.toLocaleString()}</span>
          <span className={s.summaryLabel}>Requests</span>
        </div>
        <div className={s.summaryCard}>
          <span className={s.summaryValue}>{formatCost(data.totalCost)}</span>
          <span className={s.summaryLabel}>Total Cost</span>
        </div>
        <div className={s.summaryCard}>
          <span className={s.summaryValue}>{data.totalErrors.toLocaleString()}</span>
          <span className={s.summaryLabel}>Errors</span>
        </div>
      </div>

      {/* Toolbar */}
      <div className={s.toolbar}>
        <label>
          <span style={{ fontFamily: "var(--mono)", fontSize: "10px", color: "var(--aa-soft)", marginRight: 6 }}>
            Sort by
          </span>
          <select
            className={s.sortSelect}
            value={sortBy}
            onChange={(e) => setSortBy(e.target.value as SortKey)}
          >
            {SORT_OPTIONS.map((opt) => (
              <option key={opt.key} value={opt.key}>{opt.label}</option>
            ))}
          </select>
        </label>
        <button className={s.refreshBtn} onClick={fetchData} disabled={loading}>
          <ArrowSyncRegular style={{ fontSize: 12 }} />
          {loading ? "Loading…" : "Refresh"}
        </button>
        <button
          className={s.refreshBtn}
          onClick={() => exportAgentAnalytics(hoursBack, "csv").catch((e) => {
            setError(e instanceof Error ? e.message : "Export failed");
          })}
          disabled={loading || !data}
          title="Export agent analytics as CSV"
        >
          <ArrowDownloadRegular style={{ fontSize: 12 }} />
          Export CSV
        </button>
      </div>

      {/* Agent cards */}
      <div className={s.grid}>
        {sorted.map((agent) => (
          <AgentCard
            key={agent.agentId}
            agent={agent}
            styles={s}
            selected={agent.agentId === selectedAgentId}
            onClick={() => setSelectedAgentId(
              agent.agentId === selectedAgentId ? null : agent.agentId
            )}
          />
        ))}
      </div>

      {/* Detail view */}
      {selectedAgentId && (
        <AgentDetailView
          agentId={selectedAgentId}
          hoursBack={hoursBack}
          onClose={() => setSelectedAgentId(null)}
        />
      )}
    </div>
  );
}

// ── Agent Card ──

function AgentCard({ agent, styles: s, selected, onClick }: {
  agent: AgentPerformanceMetrics;
  styles: ReturnType<typeof useLocalStyles>;
  selected: boolean;
  onClick: () => void;
}) {
  const totalTokens = agent.totalInputTokens + agent.totalOutputTokens;
  const errColor = errorRateColor(agent.totalErrors, agent.totalRequests);
  const taskRate = agent.tasksAssigned > 0
    ? Math.round((agent.tasksCompleted / agent.tasksAssigned) * 100)
    : null;

  return (
    <div className={selected ? s.cardSelected : s.card} onClick={onClick}>
      <div className={s.cardHeader}>
        <div>
          <div className={s.agentName}>{agent.agentName}</div>
          <div className={s.agentId}>{agent.agentId}</div>
        </div>
        <div style={{ display: "flex", gap: 4 }}>
          {agent.totalErrors > 0 && (
            <Tooltip content={`${agent.totalErrors} errors (${agent.recoverableErrors} recoverable)`} relationship="label">
              <span><V3Badge color="err">{agent.totalErrors} err</V3Badge></span>
            </Tooltip>
          )}
          {taskRate !== null && (
            <Tooltip content={`${agent.tasksCompleted}/${agent.tasksAssigned} tasks completed`} relationship="label">
              <span><V3Badge color={taskRate >= 80 ? "ok" : taskRate >= 50 ? "warn" : "err"}>{taskRate}%</V3Badge></span>
            </Tooltip>
          )}
        </div>
      </div>

      <div className={s.metricsRow}>
        <div className={s.metric}>
          <span className={s.metricValue}>{agent.totalRequests.toLocaleString()}</span>
          <span className={s.metricLabel}>requests</span>
        </div>
        <div className={s.metric}>
          <span className={s.metricValue}>{formatTokenCount(totalTokens)}</span>
          <span className={s.metricLabel}>tokens</span>
        </div>
        <div className={s.metric}>
          <span className={s.metricValue}>{formatCost(agent.totalCost)}</span>
          <span className={s.metricLabel}>cost</span>
        </div>
        <div className={s.metric}>
          <span className={s.metricValue}>
            {agent.averageResponseTimeMs != null
              ? `${(agent.averageResponseTimeMs / 1000).toFixed(1)}s`
              : "—"}
          </span>
          <span className={s.metricLabel}>avg response</span>
        </div>
      </div>

      {/* Error/Task row */}
      <div className={s.metricsRow}>
        <div className={s.metric}>
          <span className={s.metricValue} style={{ color: errColor }}>
            <ErrorCircleRegular style={{ fontSize: 12, marginRight: 2, verticalAlign: "middle" }} />
            {agent.totalErrors}
          </span>
          <span className={s.metricLabel}>errors</span>
        </div>
        <div className={s.metric}>
          <span className={s.metricValue}>
            <CheckmarkCircleRegular style={{ fontSize: 12, marginRight: 2, verticalAlign: "middle" }} />
            {agent.tasksCompleted}/{agent.tasksAssigned}
          </span>
          <span className={s.metricLabel}>tasks done</span>
        </div>
      </div>

      {/* Token trend sparkline */}
      <div className={s.trendRow}>
        <MoneyRegular style={{ fontSize: 12, color: "var(--aa-soft)" }} />
        <Sparkline data={agent.tokenTrend} width={200} height={28} />
      </div>
    </div>
  );
}
