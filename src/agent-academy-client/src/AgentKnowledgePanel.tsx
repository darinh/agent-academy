import { useCallback, useEffect, useMemo, useState } from "react";
import { makeStyles, shorthands, Spinner } from "@fluentui/react-components";
import { ArrowSyncRegular } from "@fluentui/react-icons";
import V3Badge from "./V3Badge";
import EmptyState from "./EmptyState";
import {
  getAgentKnowledge,
  type AgentDefinition,
  type AgentKnowledgeResponse,
} from "./api";

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    height: "100%",
    overflow: "hidden",
  },
  header: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    ...shorthands.padding("12px", "20px"),
    borderBottom: "1px solid var(--aa-border)",
  },
  headerLeft: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
  },
  controls: {
    ...shorthands.padding("10px", "20px"),
    borderBottom: "1px solid var(--aa-hairline)",
    display: "flex",
    alignItems: "center",
    gap: "10px",
  },
  select: {
    ...shorthands.padding("6px", "10px"),
    ...shorthands.borderRadius("6px"),
    ...shorthands.border("1px", "solid", "var(--aa-border)"),
    background: "var(--aa-surface)",
    color: "var(--aa-text)",
    fontSize: "12px",
    cursor: "pointer",
  },
  list: {
    flex: 1,
    overflow: "auto",
    ...shorthands.padding("8px", "20px"),
    display: "flex",
    flexDirection: "column",
    gap: "6px",
  },
  entry: {
    ...shorthands.padding("10px", "14px"),
    ...shorthands.borderRadius("6px"),
    background: "rgba(0,0,0,0.15)",
    ...shorthands.border("1px", "solid", "var(--aa-hairline)"),
    fontSize: "12px",
    color: "var(--aa-text)",
    lineHeight: "1.5",
    fontFamily: "var(--mono)",
  },
  refreshBtn: {
    background: "none",
    ...shorthands.border("none"),
    color: "var(--aa-soft)",
    cursor: "pointer",
    ...shorthands.padding("4px"),
    ...shorthands.borderRadius("4px"),
    ":hover": { background: "rgba(255,255,255,0.05)" },
  },
});

interface AgentKnowledgePanelProps {
  agents: AgentDefinition[];
}

export default function AgentKnowledgePanel({ agents }: AgentKnowledgePanelProps) {
  const s = useLocalStyles();
  const [selectedAgent, setSelectedAgent] = useState<string>("");
  const [data, setData] = useState<AgentKnowledgeResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Auto-select first agent
  useEffect(() => {
    if (!selectedAgent && agents.length > 0) {
      setSelectedAgent(agents[0].id);
    }
  }, [agents, selectedAgent]);

  const fetchData = useCallback(async () => {
    if (!selectedAgent) return;
    setLoading(true);
    setError(null);
    try {
      const result = await getAgentKnowledge(selectedAgent);
      setData(result);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load knowledge");
      setData(null);
    } finally {
      setLoading(false);
    }
  }, [selectedAgent]);

  useEffect(() => { fetchData(); }, [fetchData]);

  const agentName = useMemo(() =>
    agents.find((a) => a.id === selectedAgent)?.name ?? selectedAgent,
    [agents, selectedAgent],
  );

  if (agents.length === 0) {
    return (
      <div className={s.root}>
        <EmptyState
          icon={<span style={{ fontSize: 48 }}>📖</span>}
          title="No agents configured"
          detail="Agent knowledge will appear here once agents are loaded."
        />
      </div>
    );
  }

  return (
    <div className={s.root}>
      <div className={s.header}>
        <div className={s.headerLeft}>
          <span style={{ fontSize: 14 }}>📖</span>
          <span style={{ fontWeight: 600, fontSize: 13, color: "var(--aa-text)" }}>
            Agent Knowledge
          </span>
          {data && <V3Badge color="info">{data.entries.length} entries</V3Badge>}
        </div>
        <button className={s.refreshBtn} onClick={fetchData} aria-label="Refresh knowledge">
          <ArrowSyncRegular fontSize={14} />
        </button>
      </div>

      <div className={s.controls}>
        <select
          className={s.select}
          value={selectedAgent}
          onChange={(e) => setSelectedAgent(e.target.value)}
          aria-label="Select agent"
        >
          {agents.map((a) => (
            <option key={a.id} value={a.id}>{a.name} ({a.role})</option>
          ))}
        </select>
      </div>

      {loading && <Spinner size="small" label="Loading knowledge…" />}
      {error && <div style={{ color: "var(--aa-copper)", fontSize: 12, padding: "8px 20px" }}>⚠ {error}</div>}

      {!loading && !error && data && data.entries.length === 0 && (
        <EmptyState
          icon={<span style={{ fontSize: 48 }}>📭</span>}
          title="No knowledge entries"
          detail={`${agentName} has no stored knowledge entries yet.`}
        />
      )}

      {!loading && data && data.entries.length > 0 && (
        <div className={s.list}>
          {data.entries.map((entry, i) => (
            <div key={i} className={s.entry}>{entry}</div>
          ))}
        </div>
      )}
    </div>
  );
}
