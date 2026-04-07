import { useCallback, useEffect, useRef, useState } from "react";
import {
  Badge,
  Spinner,
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import {
  ArrowSyncRegular,
  CheckmarkCircleRegular,
  ErrorCircleRegular,
} from "@fluentui/react-icons";
import { errorTypeBadge, formatCost, formatTimestamp, formatTokenCount } from "./panelUtils";
import {
  getRoomUsage,
  getRoomUsageByAgent,
  getRoomErrors,
  type UsageSummary,
  type AgentUsageSummary,
  type ErrorRecord,
} from "./api";

// ── Styles ──

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    gap: "16px",
  },
  statsRow: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(110px, 1fr))",
    gap: "10px",
  },
  statCard: {
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    ...shorthands.padding("12px", "6px"),
    ...shorthands.borderRadius("6px"),
    border: "1px solid var(--aa-border)",
    backgroundColor: "rgba(255, 255, 255, 0.025)",
  },
  statValue: {
    fontFamily: "var(--mono)",
    fontSize: "14px",
    fontWeight: 780,
    color: "var(--aa-text-strong)",
    lineHeight: 1,
    letterSpacing: "-0.04em",
  },
  statLabel: {
    color: "var(--aa-muted)",
    fontSize: "10px",
    fontWeight: 600,
    textTransform: "uppercase" as const,
    letterSpacing: "0.08em",
    marginTop: "5px",
    textAlign: "center" as const,
  },
  table: {
    width: "100%",
    borderCollapse: "collapse" as const,
    fontSize: "12px",
  },
  th: {
    textAlign: "left" as const,
    color: "var(--aa-soft)",
    fontSize: "10px",
    fontWeight: 700,
    letterSpacing: "0.10em",
    textTransform: "uppercase" as const,
    ...shorthands.padding("6px", "10px"),
    borderBottom: "1px solid rgba(255, 244, 227, 0.10)",
  },
  thRight: {
    textAlign: "right" as const,
    color: "var(--aa-soft)",
    fontSize: "10px",
    fontWeight: 700,
    letterSpacing: "0.10em",
    textTransform: "uppercase" as const,
    ...shorthands.padding("6px", "10px"),
    borderBottom: "1px solid rgba(255, 244, 227, 0.10)",
  },
  td: {
    ...shorthands.padding("8px", "10px"),
    borderBottom: "1px solid rgba(110, 118, 129, 0.1)",
    color: "var(--aa-text)",
    verticalAlign: "middle" as const,
  },
  tdRight: {
    ...shorthands.padding("8px", "10px"),
    borderBottom: "1px solid rgba(110, 118, 129, 0.1)",
    color: "var(--aa-text)",
    verticalAlign: "middle" as const,
    textAlign: "right" as const,
    fontFamily: "var(--mono, monospace)",
    fontSize: "11px",
  },
  mono: {
    fontFamily: "var(--mono, monospace)",
    fontSize: "11px",
    color: "var(--aa-muted)",
  },
  emptyNote: {
    color: "var(--aa-muted)",
    fontSize: "12px",
    textAlign: "center" as const,
    ...shorthands.padding("16px"),
  },
  error: {
    color: "#f85149",
    fontSize: "12px",
    ...shorthands.padding("10px"),
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
    fontSize: "11px",
    display: "flex",
    alignItems: "center",
    gap: "4px",
    ":hover": {
      color: "var(--aa-text)",
    },
  },
  sectionLabel: {
    display: "flex",
    alignItems: "center",
    gap: "6px",
    fontSize: "13px",
    fontWeight: 680,
    color: "var(--aa-text-strong)",
    letterSpacing: "-0.02em",
  },
  columns: {
    display: "grid",
    gridTemplateColumns: "1fr 1fr",
    gap: "16px",
  },
  msgCell: {
    maxWidth: "200px",
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap" as const,
    fontFamily: "var(--mono, monospace)",
    fontSize: "11px",
    color: "var(--aa-muted)",
  },
});

// ── Helpers ──

// ── Component ──

interface RoomStatsPanelProps {
  roomId: string;
}

export default function RoomStatsPanel({ roomId }: RoomStatsPanelProps) {
  const s = useLocalStyles();
  const [usage, setUsage] = useState<UsageSummary | null>(null);
  const [agents, setAgents] = useState<AgentUsageSummary[]>([]);
  const [errors, setErrors] = useState<ErrorRecord[]>([]);
  const [loading, setLoading] = useState(true);
  const [usageError, setUsageError] = useState<string | null>(null);
  const [agentsError, setAgentsError] = useState<string | null>(null);
  const [errorsError, setErrorsError] = useState<string | null>(null);
  const fetchIdRef = useRef(0);
  const prevRoomRef = useRef<string>(roomId);

  const fetchData = useCallback(async (rid: string) => {
    const id = ++fetchIdRef.current;

    // Clear stale data when switching rooms
    if (rid !== prevRoomRef.current) {
      setUsage(null);
      setAgents([]);
      setErrors([]);
      prevRoomRef.current = rid;
    }

    setLoading(true);
    setUsageError(null);
    setAgentsError(null);
    setErrorsError(null);

    const [usageResult, agentsResult, errorsResult] = await Promise.allSettled([
      getRoomUsage(rid),
      getRoomUsageByAgent(rid),
      getRoomErrors(rid, 20),
    ]);

    if (id !== fetchIdRef.current) return;

    if (usageResult.status === "fulfilled") {
      setUsage(usageResult.value);
    } else {
      setUsageError("Failed to load room usage data");
    }

    if (agentsResult.status === "fulfilled") {
      setAgents(agentsResult.value);
    } else {
      setAgentsError("Failed to load agent breakdown");
    }

    if (errorsResult.status === "fulfilled") {
      setErrors(errorsResult.value);
    } else {
      setErrorsError("Failed to load error data");
    }

    setLoading(false);
  }, []);

  useEffect(() => {
    fetchData(roomId);
  }, [roomId, fetchData]);

  if (loading && !usage && agents.length === 0 && errors.length === 0) {
    return (
      <div className={s.root}>
        <Spinner size="small" label="Loading room stats…" />
      </div>
    );
  }

  const allFailed = usageError && agentsError && errorsError;
  if (allFailed && !usage && agents.length === 0 && errors.length === 0) {
    return (
      <div className={s.root}>
        <div className={s.error}>Failed to load room stats</div>
      </div>
    );
  }

  const hasUsage = usage && usage.requestCount > 0;
  const hasErrors = errors.length > 0;
  const hasAgents = agents.length > 0;

  if (!hasUsage && !hasErrors && !hasAgents && !usageError && !agentsError && !errorsError) {
    return (
      <div className={s.root}>
        <div className={s.emptyNote}>
          <CheckmarkCircleRegular style={{ fontSize: 16, color: "var(--aa-lime)", marginRight: 4 }} />
          No activity recorded for this room yet.
        </div>
      </div>
    );
  }

  return (
    <div className={s.root}>
      {/* Usage summary cards */}
      {usageError && (
        <div className={s.error}>{usageError}</div>
      )}
      {usage && hasUsage && (
        <>
          <div className={s.headerRow}>
            <div className={s.sectionLabel}>Usage</div>
            <button className={s.refreshBtn} onClick={() => fetchData(roomId)}>
              <ArrowSyncRegular style={{ fontSize: 12 }} /> Refresh
            </button>
          </div>
          <div className={s.statsRow}>
            <div className={s.statCard}>
              <span className={s.statValue}>{formatTokenCount(usage.totalInputTokens)}</span>
              <span className={s.statLabel}>Input</span>
            </div>
            <div className={s.statCard}>
              <span className={s.statValue}>{formatTokenCount(usage.totalOutputTokens)}</span>
              <span className={s.statLabel}>Output</span>
            </div>
            <div className={s.statCard}>
              <span className={s.statValue} style={{ color: "var(--aa-lime)" }}>
                {formatCost(usage.totalCost)}
              </span>
              <span className={s.statLabel}>Cost</span>
            </div>
            <div className={s.statCard}>
              <span className={s.statValue} style={{ color: "var(--aa-gold)" }}>
                {usage.requestCount}
              </span>
              <span className={s.statLabel}>Calls</span>
            </div>
          </div>
        </>
      )}

      {/* Per-agent breakdown */}
      {agentsError && (
        <div className={s.error}>{agentsError}</div>
      )}
      {agents.length > 0 && (
        <div>
          <div className={s.sectionLabel} style={{ marginBottom: "6px" }}>Per-Agent</div>
          <table className={s.table}>
            <thead>
              <tr>
                <th className={s.th}>Agent</th>
                <th className={s.thRight}>In</th>
                <th className={s.thRight}>Out</th>
                <th className={s.thRight}>Cost</th>
                <th className={s.thRight}>Calls</th>
              </tr>
            </thead>
            <tbody>
              {agents.map((a) => (
                <tr key={a.agentId}>
                  <td className={s.td}>
                    <Badge appearance="outline" color="informative">
                      {a.agentId}
                    </Badge>
                  </td>
                  <td className={s.tdRight}>{formatTokenCount(a.totalInputTokens)}</td>
                  <td className={s.tdRight}>{formatTokenCount(a.totalOutputTokens)}</td>
                  <td className={s.tdRight}>{formatCost(a.totalCost)}</td>
                  <td className={s.tdRight}>{a.requestCount}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* Error summary */}
      {errorsError && (
        <div className={s.error}>{errorsError}</div>
      )}
      {hasErrors && (
        <div>
          <div className={s.sectionLabel} style={{ marginBottom: "6px" }}>
            <ErrorCircleRegular style={{ fontSize: 14 }} />
            Errors ({errors.length})
          </div>
          <table className={s.table}>
            <thead>
              <tr>
                <th className={s.th}>Agent</th>
                <th className={s.th}>Type</th>
                <th className={s.th}>Message</th>
                <th className={s.th}>Time</th>
              </tr>
            </thead>
            <tbody>
              {errors.slice(0, 5).map((rec, i) => {
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
                      <span className={s.msgCell}>{rec.message}</span>
                    </td>
                    <td className={s.td}>
                      <span className={s.mono}>{formatTimestamp(rec.timestamp, false)}</span>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
          {errors.length > 5 && (
            <div className={s.emptyNote}>
              Showing 5 of {errors.length} errors. See Dashboard for full details.
            </div>
          )}
        </div>
      )}

      {!hasErrors && !errorsError && hasUsage && (
        <div className={s.emptyNote}>
          <CheckmarkCircleRegular style={{ fontSize: 14, color: "var(--aa-lime)", marginRight: 4 }} />
          No errors in this room.
        </div>
      )}
    </div>
  );
}
