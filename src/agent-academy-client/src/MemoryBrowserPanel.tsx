import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Spinner, Tooltip } from "@fluentui/react-components";
import { mergeClasses } from "@fluentui/react-components";
import V3Badge from "./V3Badge";
import { ArrowSyncRegular, DeleteRegular } from "@fluentui/react-icons";
import { formatTimestamp } from "./panelUtils";
import {
  browseMemories,
  getMemoryStats,
  deleteMemory,
  type MemoryDto,
  type MemoryStatsResponse,
  type AgentDefinition,
} from "./api";
import { useMemoryBrowserStyles } from "./memory";

const VALUE_TRUNCATE = 120;
const DEBOUNCE_MS = 300;

interface MemoryBrowserPanelProps {
  agents: AgentDefinition[];
  refreshTrigger?: number;
}

export default function MemoryBrowserPanel({ agents, refreshTrigger = 0 }: MemoryBrowserPanelProps) {
  const s = useMemoryBrowserStyles();
  const [selectedAgent, setSelectedAgent] = useState<string>("");
  const [category, setCategory] = useState<string>("");
  const [search, setSearch] = useState("");
  const [debouncedSearch, setDebouncedSearch] = useState("");
  const [memories, setMemories] = useState<MemoryDto[]>([]);
  const [stats, setStats] = useState<MemoryStatsResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [expandedKeys, setExpandedKeys] = useState<Set<string>>(new Set());
  const [includeExpired, setIncludeExpired] = useState(false);
  const fetchIdRef = useRef(0);
  const selectedAgentRef = useRef(selectedAgent);

  // Auto-select first agent
  useEffect(() => {
    if (!selectedAgent && agents.length > 0) {
      setSelectedAgent(agents[0].id);
    }
  }, [agents, selectedAgent]);

  // Keep ref in sync for stale-response guards
  useEffect(() => { selectedAgentRef.current = selectedAgent; }, [selectedAgent]);

  // Debounce search
  useEffect(() => {
    const timer = setTimeout(() => setDebouncedSearch(search), DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [search]);

  const fetchData = useCallback(async () => {
    if (!selectedAgent) return;
    const id = ++fetchIdRef.current;
    setLoading(true);
    setError(null);

    const [browseResult, statsResult] = await Promise.allSettled([
      browseMemories({
        agentId: selectedAgent,
        category: category || undefined,
        search: debouncedSearch || undefined,
        includeExpired,
      }),
      getMemoryStats(selectedAgent),
    ]);

    if (id !== fetchIdRef.current) return;

    if (browseResult.status === "fulfilled") {
      setMemories(browseResult.value.memories);
    } else {
      setError(browseResult.reason instanceof Error ? browseResult.reason.message : "Failed to load memories");
      setMemories([]);
    }

    if (statsResult.status === "fulfilled") {
      setStats(statsResult.value);
    }

    setLoading(false);
  }, [selectedAgent, category, debouncedSearch, includeExpired]);

  useEffect(() => { fetchData(); }, [fetchData]);

  // Re-fetch when a LearningDigestCompleted event arrives (new shared memories)
  const prevTrigger = useRef(refreshTrigger);
  useEffect(() => {
    if (refreshTrigger !== prevTrigger.current) {
      prevTrigger.current = refreshTrigger;
      fetchData();
    }
  }, [refreshTrigger, fetchData]);

  const handleDelete = useCallback(async (agentId: string, key: string) => {
    try {
      await deleteMemory(agentId, key);
      setMemories((prev) => prev.filter((m) => m.key !== key));
      // Refresh stats — guard against stale response if agent changed
      const agentAtDelete = selectedAgent;
      if (agentAtDelete) {
        getMemoryStats(agentAtDelete).then((s) => {
          if (selectedAgentRef.current === agentAtDelete) setStats(s);
        }).catch(() => {});
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : "Delete failed");
    }
  }, [selectedAgent]);

  const toggleExpand = useCallback((key: string) => {
    setExpandedKeys((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }, []);

  const categories = useMemo(() =>
    stats?.categories.filter((c) => c.active > 0).map((c) => c.category) ?? [],
    [stats],
  );

  const agentName = useMemo(() =>
    agents.find((a) => a.id === selectedAgent)?.name ?? selectedAgent,
    [agents, selectedAgent],
  );

  return (
    <div className={s.root}>
      {/* Header */}
      <div className={s.header}>
        <div className={s.headerLeft}>
          <span style={{ fontSize: "16px" }}>🧠</span>
          <span style={{ fontWeight: 600, fontSize: "13px", color: "var(--aa-text)" }}>
            Agent Memory
          </span>
          {stats && (
            <>
              <V3Badge color="info">{stats.activeMemories} active</V3Badge>
              {stats.expiredMemories > 0 && (
                <V3Badge color="muted">{stats.expiredMemories} expired</V3Badge>
              )}
            </>
          )}
        </div>
        <Tooltip content="Refresh" relationship="label">
          <button
            onClick={fetchData}
            style={{ background: "none", border: "none", color: "var(--aa-soft)", cursor: "pointer" }}
            aria-label="Refresh memories"
          >
            <ArrowSyncRegular fontSize={14} />
          </button>
        </Tooltip>
      </div>

      {/* Controls */}
      <div className={s.controls}>
        <select
          className={s.select}
          value={selectedAgent}
          onChange={(e) => { setSelectedAgent(e.target.value); setCategory(""); }}
          aria-label="Select agent"
        >
          {agents.map((a) => (
            <option key={a.id} value={a.id}>{a.name} ({a.role})</option>
          ))}
        </select>

        <input
          className={s.searchInput}
          type="text"
          placeholder="Search memories…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          aria-label="Search memories"
        />

        <label style={{ display: "flex", alignItems: "center", gap: "4px", fontSize: "11px", color: "var(--aa-soft)", cursor: "pointer" }}>
          <input
            type="checkbox"
            checked={includeExpired}
            onChange={(e) => setIncludeExpired(e.target.checked)}
          />
          Include expired
        </label>
      </div>

      {/* Category chips */}
      {categories.length > 0 && (
        <div className={s.categoryChips}>
          <span
            className={mergeClasses(s.categoryChip, !category && s.categoryChipActive)}
            onClick={() => setCategory("")}
            role="button"
            tabIndex={0}
            onKeyDown={(e) => e.key === "Enter" && setCategory("")}
          >
            all
          </span>
          {categories.map((cat) => (
            <span
              key={cat}
              className={mergeClasses(s.categoryChip, category === cat && s.categoryChipActive)}
              onClick={() => setCategory(category === cat ? "" : cat)}
              role="button"
              tabIndex={0}
              onKeyDown={(e) => e.key === "Enter" && setCategory(category === cat ? "" : cat)}
              data-category={cat}
            >
              <span>{cat}</span>
              {" "}
              <span style={{ opacity: 0.6 }}>
                {stats?.categories.find((c) => c.category === cat)?.active ?? 0}
              </span>
            </span>
          ))}
        </div>
      )}

      {/* Loading / Error */}
      {loading && <Spinner size="small" label="Loading memories…" />}
      {error && <div style={{ color: "var(--aa-copper)", fontSize: "12px" }}>⚠ {error}</div>}

      {/* Memory list */}
      {!loading && !error && memories.length === 0 && selectedAgent && (
        <div className={s.empty}>
          {debouncedSearch
            ? `No memories matching "${debouncedSearch}" for ${agentName}`
            : `${agentName} has no memories${category ? ` in category "${category}"` : ""}`}
        </div>
      )}

      <div className={s.list}>
        {memories.map((m) => {
          const expanded = expandedKeys.has(m.key);
          const truncated = m.value.length > VALUE_TRUNCATE;
          return (
            <div key={m.key}>
              <div className={s.memoryRow}>
                <V3Badge color={categoryColor(m.category)}>{m.category}</V3Badge>
                <span
                  className={s.memoryKey}
                  onClick={() => toggleExpand(m.key)}
                  style={{ cursor: truncated ? "pointer" : "default" }}
                  role={truncated ? "button" : undefined}
                  tabIndex={truncated ? 0 : undefined}
                  onKeyDown={truncated ? (e) => e.key === "Enter" && toggleExpand(m.key) : undefined}
                >
                  {m.key}
                </span>
                <span className={s.memoryValue}>
                  {truncated && !expanded
                    ? m.value.slice(0, VALUE_TRUNCATE) + "…"
                    : m.value}
                </span>
                <div style={{ display: "flex", alignItems: "center", gap: "4px" }}>
                  <Tooltip content={formatMeta(m)} relationship="label">
                    <span className={s.memoryMeta}>
                      {formatTimestamp(m.updatedAt ?? m.createdAt)}
                    </span>
                  </Tooltip>
                  <Tooltip content="Delete memory" relationship="label">
                    <button
                      className={s.deleteBtn}
                      onClick={() => handleDelete(m.agentId, m.key)}
                      aria-label={`Delete memory: ${m.key}`}
                    >
                      <DeleteRegular fontSize={12} />
                    </button>
                  </Tooltip>
                </div>
              </div>
              {expanded && truncated && (
                <div className={s.expandedValue}>{m.value}</div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function categoryColor(cat: string): "info" | "active" | "warn" | "muted" | "ok" | "err" | "feat" | "review" | "tool" {
  switch (cat) {
    case "decision": return "active";
    case "lesson": return "ok";
    case "pattern": return "feat";
    case "preference": return "info";
    case "invariant": return "warn";
    case "risk": return "err";
    case "gotcha": return "warn";
    case "incident": return "err";
    case "constraint": return "review";
    case "finding": return "info";
    case "spec-drift": return "warn";
    case "verification": return "ok";
    case "shared": return "tool";
    default: return "muted";
  }
}

function formatMeta(m: MemoryDto): string {
  const parts: string[] = [];
  parts.push(`Created: ${formatTimestamp(m.createdAt)}`);
  if (m.updatedAt) parts.push(`Updated: ${formatTimestamp(m.updatedAt)}`);
  if (m.lastAccessedAt) parts.push(`Last accessed: ${formatTimestamp(m.lastAccessedAt)}`);
  if (m.expiresAt) parts.push(`Expires: ${formatTimestamp(m.expiresAt)}`);
  return parts.join("\n");
}
