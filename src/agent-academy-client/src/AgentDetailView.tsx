import { useCallback, useEffect, useRef, useState } from "react";
import {
  Spinner,
  Tooltip,
} from "@fluentui/react-components";
import {
  DismissRegular,
  ArrowSyncRegular,
  ErrorCircleRegular,
  CheckmarkCircleRegular,
  BranchRegular,
} from "@fluentui/react-icons";
import V3Badge from "./V3Badge";
import Sparkline from "./Sparkline";
import { formatTimestamp, formatTokenCount, formatCost, errorTypeBadge } from "./panelUtils";
import {
  getAgentAnalyticsDetail,
  type AgentAnalyticsDetail,
  type AgentUsageRecord,
  type AgentErrorRecord,
  type AgentTaskRecord,
  type AgentModelBreakdown,
} from "./api";
import { useAgentDetailStyles } from "./agentDetail/agentDetailStyles";
import { taskStatusColor } from "./agentDetail/taskStatusColor";

// ── Component ──

interface AgentDetailViewProps {
  agentId: string;
  hoursBack?: number;
  onClose: () => void;
}

export default function AgentDetailView({ agentId, hoursBack, onClose }: AgentDetailViewProps) {
  const s = useAgentDetailStyles();
  const [data, setData] = useState<AgentAnalyticsDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const fetchIdRef = useRef(0);

  const fetchData = useCallback(async () => {
    const id = ++fetchIdRef.current;
    setLoading(true);
    setError(null);
    try {
      const result = await getAgentAnalyticsDetail(agentId, hoursBack);
      if (id !== fetchIdRef.current) return;
      setData(result);
    } catch (e) {
      if (id !== fetchIdRef.current) return;
      setError(e instanceof Error ? e.message : "Failed to load agent detail");
    } finally {
      if (id === fetchIdRef.current) setLoading(false);
    }
  }, [agentId, hoursBack]);

  useEffect(() => {
    fetchData();
  }, [fetchData]);

  if (loading && !data) {
    return <div className={s.root}><Spinner size="small" label="Loading agent detail…" /></div>;
  }

  if (error && !data) {
    return (
      <div className={s.root}>
        <div className={s.header}>
          <span className={s.error}>{error}</span>
          <button className={s.iconBtn} onClick={onClose} title="Close"><DismissRegular /></button>
        </div>
      </div>
    );
  }

  if (!data) return null;

  const { agent } = data;
  const totalTokens = agent.totalInputTokens + agent.totalOutputTokens;
  const activityTokens = data.activityBuckets.map(b => b.tokens);

  return (
    <div className={s.root}>
      {/* Header */}
      <div className={s.header}>
        <div className={s.headerLeft}>
          <span className={s.agentName}>{agent.agentName}</span>
          <span className={s.agentId}>{agent.agentId}</span>
        </div>
        <div className={s.headerActions}>
          <button className={s.iconBtn} onClick={fetchData} disabled={loading} title="Refresh">
            <ArrowSyncRegular />
          </button>
          <button className={s.iconBtn} onClick={onClose} title="Close">
            <DismissRegular />
          </button>
        </div>
      </div>

      {/* KPI row */}
      <div className={s.kpiRow}>
        <div className={s.kpiCard}>
          <span className={s.kpiValue}>{agent.totalRequests.toLocaleString()}</span>
          <span className={s.kpiLabel}>Requests</span>
        </div>
        <div className={s.kpiCard}>
          <span className={s.kpiValue}>{formatTokenCount(totalTokens)}</span>
          <span className={s.kpiLabel}>Tokens</span>
        </div>
        <div className={s.kpiCard}>
          <span className={s.kpiValue}>{formatCost(agent.totalCost)}</span>
          <span className={s.kpiLabel}>Cost</span>
        </div>
        <div className={s.kpiCard}>
          <span className={s.kpiValue}>
            {agent.averageResponseTimeMs != null
              ? `${(agent.averageResponseTimeMs / 1000).toFixed(1)}s`
              : "—"}
          </span>
          <span className={s.kpiLabel}>Avg Response</span>
        </div>
        <div className={s.kpiCard}>
          <span className={s.kpiValue} style={{ color: agent.totalErrors > 0 ? "var(--aa-red, #f44)" : undefined }}>
            {agent.totalErrors}
          </span>
          <span className={s.kpiLabel}>Errors</span>
        </div>
        <div className={s.kpiCard}>
          <span className={s.kpiValue}>{agent.tasksCompleted}/{agent.tasksAssigned}</span>
          <span className={s.kpiLabel}>Tasks Done</span>
        </div>
      </div>

      {/* Activity trend */}
      <div className={s.section}>
        <span className={s.sectionTitle}>Activity Trend</span>
        <div className={s.trendSection}>
          <Sparkline data={activityTokens} width={400} height={40} />
        </div>
      </div>

      {/* Model breakdown */}
      {data.modelBreakdown.length > 0 && (
        <div className={s.section}>
          <span className={s.sectionTitle}>Model Breakdown</span>
          <ModelBreakdownGrid models={data.modelBreakdown} styles={s} />
        </div>
      )}

      {/* Recent requests */}
      <div className={s.section}>
        <span className={s.sectionTitle}>Recent Requests ({data.recentRequests.length})</span>
        {data.recentRequests.length > 0 ? (
          <RequestsTable records={data.recentRequests} styles={s} />
        ) : (
          <div className={s.empty}>No requests in this window.</div>
        )}
      </div>

      {/* Recent errors */}
      <div className={s.section}>
        <span className={s.sectionTitle}>
          <ErrorCircleRegular style={{ fontSize: 12, marginRight: 4, verticalAlign: "middle" }} />
          Recent Errors ({data.recentErrors.length})
        </span>
        {data.recentErrors.length > 0 ? (
          <ErrorsTable records={data.recentErrors} styles={s} />
        ) : (
          <div className={s.empty}>No errors in this window.</div>
        )}
      </div>

      {/* Tasks */}
      <div className={s.section}>
        <span className={s.sectionTitle}>
          <CheckmarkCircleRegular style={{ fontSize: 12, marginRight: 4, verticalAlign: "middle" }} />
          Tasks ({data.tasks.length})
        </span>
        {data.tasks.length > 0 ? (
          <TasksList tasks={data.tasks} styles={s} />
        ) : (
          <div className={s.empty}>No tasks in this window.</div>
        )}
      </div>
    </div>
  );
}

// ── Sub-components ──

function ModelBreakdownGrid({ models, styles: s }: { models: AgentModelBreakdown[]; styles: ReturnType<typeof useAgentDetailStyles> }) {
  const maxRequests = Math.max(...models.map(m => m.requests), 1);

  return (
    <div className={s.modelGrid}>
      {models.map((m) => (
        <div key={m.model} className={s.modelCard}>
          <span className={s.modelName}>{m.model}</span>
          <span className={s.modelStat}>
            {m.requests.toLocaleString()} req · {formatTokenCount(m.totalTokens)} tok · {formatCost(m.totalCost)}
          </span>
          <div
            className={s.modelBar}
            style={{ width: `${Math.round((m.requests / maxRequests) * 100)}%` }}
          />
        </div>
      ))}
    </div>
  );
}

function RequestsTable({ records, styles: s }: { records: AgentUsageRecord[]; styles: ReturnType<typeof useAgentDetailStyles> }) {
  return (
    <div style={{ overflowX: "auto" }}>
      <table className={s.table}>
        <thead>
          <tr>
            <th className={s.th}>Time</th>
            <th className={s.th}>Model</th>
            <th className={s.th}>In Tokens</th>
            <th className={s.th}>Out Tokens</th>
            <th className={s.th}>Cost</th>
            <th className={s.th}>Duration</th>
          </tr>
        </thead>
        <tbody>
          {records.map((r) => (
            <tr key={r.id}>
              <td className={s.td}>{formatTimestamp(r.recordedAt)}</td>
              <td className={s.td}>{r.model ?? "unknown"}</td>
              <td className={s.td}>{r.inputTokens.toLocaleString()}</td>
              <td className={s.td}>{r.outputTokens.toLocaleString()}</td>
              <td className={s.td}>{r.cost != null ? formatCost(r.cost) : "—"}</td>
              <td className={s.td}>
                {r.durationMs != null ? `${(r.durationMs / 1000).toFixed(1)}s` : "—"}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function ErrorsTable({ records, styles: s }: { records: AgentErrorRecord[]; styles: ReturnType<typeof useAgentDetailStyles> }) {
  return (
    <div style={{ overflowX: "auto" }}>
      <table className={s.table}>
        <thead>
          <tr>
            <th className={s.th}>Time</th>
            <th className={s.th}>Type</th>
            <th className={s.th}>Message</th>
            <th className={s.th}>Recovery</th>
          </tr>
        </thead>
        <tbody>
          {records.map((r) => {
            const badge = errorTypeBadge(r.errorType);
            return (
              <tr key={r.id}>
                <td className={s.td}>{formatTimestamp(r.occurredAt)}</td>
                <td className={s.td}><V3Badge color={badge.color}>{badge.label}</V3Badge></td>
                <td className={s.td}>
                  <Tooltip content={r.message} relationship="description">
                    <span className={s.errorMsg}>{r.message}</span>
                  </Tooltip>
                </td>
                <td className={s.td}>
                  {r.recoverable ? (
                    <V3Badge color="ok">{r.retried ? "retried" : "recoverable"}</V3Badge>
                  ) : (
                    <V3Badge color="err">unrecoverable</V3Badge>
                  )}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function TasksList({ tasks, styles: s }: { tasks: AgentTaskRecord[]; styles: ReturnType<typeof useAgentDetailStyles> }) {
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "4px" }}>
      {tasks.map((t) => (
        <div key={t.id} className={s.taskRow}>
          <span className={s.taskTitle}>{t.title}</span>
          <div className={s.taskMeta}>
            {t.branchName && (
              <Tooltip content={t.branchName} relationship="label">
                <span style={{ display: "inline-flex", alignItems: "center" }}>
                  <BranchRegular style={{ fontSize: 11, color: "var(--aa-soft)" }} />
                </span>
              </Tooltip>
            )}
            {t.pullRequestNumber && (
              <span className={s.taskMetaText}>PR #{t.pullRequestNumber}</span>
            )}
            <V3Badge color={taskStatusColor(t.status)}>{t.status}</V3Badge>
            <span className={s.taskMetaText}>{formatTimestamp(t.createdAt, false)}</span>
          </div>
        </div>
      ))}
    </div>
  );
}
