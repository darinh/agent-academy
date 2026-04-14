import { useCallback, useEffect, useMemo, useRef, useState, type MouseEvent } from "react";
import { Button, mergeClasses, Spinner } from "@fluentui/react-components";
import { ArrowSyncRegular, OpenRegular } from "@fluentui/react-icons";
import V3Badge from "./V3Badge";
import type { BadgeColor } from "./V3Badge";
import EmptyState from "./EmptyState";
import { formatTimestamp } from "./panelUtils";
import {
  listRetrospectives,
  getRetrospective,
  getRetrospectiveStats,
  type RetrospectiveListItem,
  type RetrospectiveDetailResponse,
  type RetrospectiveStatsResponse,
} from "./api";
import { useRetrospectivePanelStyles } from "./retrospective";

const PAGE_SIZE = 20;
const PREVIEW_TRUNCATE = 100;

function taskStatusBadge(status: string): { color: BadgeColor; label: string } {
  switch (status) {
    case "Completed":
      return { color: "done", label: "Completed" };
    case "InProgress":
      return { color: "active", label: "In progress" };
    case "Failed":
      return { color: "err", label: "Failed" };
    case "Blocked":
      return { color: "warn", label: "Blocked" };
    default:
      return { color: "muted", label: status };
  }
}

interface RetrospectivePanelProps {
  refreshTrigger?: number;
  onNavigateToTask?: (taskId: string) => void;
}

export default function RetrospectivePanel({ refreshTrigger = 0, onNavigateToTask }: RetrospectivePanelProps) {
  const s = useRetrospectivePanelStyles();

  const [retros, setRetros] = useState<RetrospectiveListItem[]>([]);
  const [total, setTotal] = useState(0);
  const [offset, setOffset] = useState(0);
  const [agentFilter, setAgentFilter] = useState<string>("");
  const [stats, setStats] = useState<RetrospectiveStatsResponse | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [detail, setDetail] = useState<RetrospectiveDetailResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [detailLoading, setDetailLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const fetchIdRef = useRef(0);
  const detailFetchIdRef = useRef(0);

  const fetchList = useCallback(async () => {
    const id = ++fetchIdRef.current;
    setLoading(true);
    setError(null);
    try {
      const [listRes, statsRes] = await Promise.all([
        listRetrospectives({
          agentId: agentFilter || undefined,
          limit: PAGE_SIZE,
          offset,
        }),
        getRetrospectiveStats().catch(() => null),
      ]);
      if (fetchIdRef.current !== id) return;
      setRetros(listRes.retrospectives);
      setTotal(listRes.total);
      if (statsRes) setStats(statsRes);
    } catch (err) {
      if (fetchIdRef.current !== id) return;
      setError(err instanceof Error ? err.message : "Failed to load retrospectives");
    } finally {
      if (fetchIdRef.current === id) setLoading(false);
    }
  }, [agentFilter, offset]);

  useEffect(() => { fetchList(); }, [fetchList]);

  // Re-fetch when a TaskRetrospectiveCompleted event arrives
  const prevTrigger = useRef(refreshTrigger);
  useEffect(() => {
    if (refreshTrigger !== prevTrigger.current) {
      prevTrigger.current = refreshTrigger;
      fetchList();
    }
  }, [refreshTrigger, fetchList]);

  const fetchDetail = useCallback(async (commentId: string) => {
    const reqId = ++detailFetchIdRef.current;
    setDetailLoading(true);
    try {
      const res = await getRetrospective(commentId);
      if (detailFetchIdRef.current !== reqId) return;
      setDetail(res);
    } catch {
      if (detailFetchIdRef.current !== reqId) return;
      setDetail(null);
    } finally {
      if (detailFetchIdRef.current === reqId) setDetailLoading(false);
    }
  }, []);

  const handleSelect = useCallback((id: string) => {
    if (selectedId === id) {
      setSelectedId(null);
      setDetail(null);
    } else {
      setSelectedId(id);
      fetchDetail(id);
    }
  }, [selectedId, fetchDetail]);

  const handleFilterChange = useCallback((e: React.ChangeEvent<HTMLSelectElement>) => {
    setAgentFilter(e.target.value);
    setOffset(0);
    setSelectedId(null);
    setDetail(null);
  }, []);

  const handleTaskClick = useCallback((e: MouseEvent, taskId: string) => {
    e.stopPropagation();
    onNavigateToTask?.(taskId);
  }, [onNavigateToTask]);

  const agentOptions = useMemo(() => {
    if (!stats) return [];
    return stats.byAgent
      .slice()
      .sort((a, b) => b.count - a.count);
  }, [stats]);

  const totalPages = Math.ceil(total / PAGE_SIZE);
  const currentPage = Math.floor(offset / PAGE_SIZE) + 1;

  return (
    <div className={s.root}>
      {/* Header */}
      <div className={s.header}>
        <div className={s.headerLeft}>
          <span style={{ fontSize: "16px" }}>🔬</span>
          <span style={{ fontFamily: "var(--aa-mono)", fontSize: "13px", fontWeight: 600 }}>
            Retrospectives
          </span>
          <V3Badge color="info">{total} total</V3Badge>
        </div>
        <div className={s.controls}>
          <select
            className={s.select}
            value={agentFilter}
            onChange={handleFilterChange}
            aria-label="Filter by agent"
          >
            <option value="">All agents</option>
            {agentOptions.map((a) => (
              <option key={a.agentId} value={a.agentId}>
                {a.agentName} ({a.count})
              </option>
            ))}
          </select>
          <Button
            appearance="subtle"
            size="small"
            icon={<ArrowSyncRegular />}
            onClick={fetchList}
            aria-label="Refresh"
          />
        </div>
      </div>

      {/* Stats */}
      {stats && (
        <div className={s.statsRow}>
          <div className={s.statCard}>
            <span className={s.statValue}>{stats.totalRetrospectives}</span>
            <span className={s.statLabel}>Total</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue}>{stats.byAgent.length}</span>
            <span className={s.statLabel}>Agents</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue}>{Math.round(stats.averageContentLength)}</span>
            <span className={s.statLabel}>Avg length</span>
          </div>
          {stats.latestRetrospectiveAt && (
            <div className={s.statCard}>
              <span className={s.statValue} style={{ fontSize: "13px" }}>
                {formatTimestamp(stats.latestRetrospectiveAt, false)}
              </span>
              <span className={s.statLabel}>Latest</span>
            </div>
          )}
        </div>
      )}

      {/* Agent breakdown chips */}
      {stats && stats.byAgent.length > 0 && (
        <div style={{ display: "flex", gap: "12px", flexWrap: "wrap" }}>
          {stats.byAgent.map((a) => (
            <span key={a.agentId} className={s.agentChip}>
              {a.agentName}: <span className={s.agentChipCount}>{a.count}</span>
            </span>
          ))}
        </div>
      )}

      {/* Error */}
      {error && (
        <div style={{ color: "var(--aa-copper)", fontFamily: "var(--aa-mono)", fontSize: "12px" }}>
          ⚠ {error}
        </div>
      )}

      {/* Loading */}
      {loading && retros.length === 0 && <Spinner label="Loading retrospectives…" size="small" />}

      {/* Empty */}
      {!loading && !error && retros.length === 0 && (
        <EmptyState
          icon="🔬"
          title="No retrospectives yet"
          detail="Retrospectives are created automatically after agents complete tasks. They capture lessons learned, patterns discovered, and improvement opportunities."
        />
      )}

      {/* Retrospective list */}
      {retros.length > 0 && (
        <div className={s.list}>
          {retros.map((r) => {
            const isActive = selectedId === r.id;
            const preview = r.contentPreview.length > PREVIEW_TRUNCATE
              ? r.contentPreview.slice(0, PREVIEW_TRUNCATE) + "…"
              : r.contentPreview;
            return (
              <div
                key={r.id}
                className={mergeClasses(s.row, isActive && s.rowActive)}
                onClick={() => handleSelect(r.id)}
                role="button"
                tabIndex={0}
                onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") handleSelect(r.id); }}
              >
                <V3Badge color="info">{r.agentName}</V3Badge>
                <div className={s.rowContent}>
                  <span
                    className={mergeClasses(s.rowTitle, onNavigateToTask && s.rowTitleLink)}
                    title={r.taskTitle}
                    role={onNavigateToTask ? "link" : undefined}
                    onClick={onNavigateToTask ? (e) => handleTaskClick(e, r.taskId) : undefined}
                  >
                    {r.taskTitle}
                    {onNavigateToTask && <OpenRegular fontSize={10} style={{ marginLeft: 4, verticalAlign: "middle" }} />}
                  </span>
                  <span className={s.rowPreview} title={r.contentPreview}>{preview}</span>
                </div>
                <div className={s.rowMeta}>
                  <span className={s.rowMetaText}>{formatTimestamp(r.createdAt, false)}</span>
                </div>
              </div>
            );
          })}
        </div>
      )}

      {/* Pagination */}
      {totalPages > 1 && (
        <div className={s.pagination}>
          <Button
            appearance="subtle"
            size="small"
            disabled={currentPage <= 1}
            onClick={() => setOffset(Math.max(0, offset - PAGE_SIZE))}
          >
            ← Prev
          </Button>
          <span className={s.paginationText}>
            Page {currentPage} of {totalPages}
          </span>
          <Button
            appearance="subtle"
            size="small"
            disabled={currentPage >= totalPages}
            onClick={() => setOffset(offset + PAGE_SIZE)}
          >
            Next →
          </Button>
        </div>
      )}

      {/* Detail panel */}
      {selectedId != null && (
        <div className={s.detail}>
          {detailLoading ? (
            <Spinner label="Loading detail…" size="small" />
          ) : detail ? (
            <>
              <div className={s.detailHeader}>
                <span
                  style={{ fontFamily: "var(--aa-mono)", fontSize: "12px", fontWeight: 600 }}
                  className={onNavigateToTask ? s.detailTitleLink : undefined}
                  role={onNavigateToTask ? "link" : undefined}
                  onClick={onNavigateToTask ? (e) => handleTaskClick(e, detail.taskId) : undefined}
                >
                  {detail.taskTitle}
                  {onNavigateToTask && <OpenRegular fontSize={10} style={{ marginLeft: 4, verticalAlign: "middle" }} />}
                </span>
                <V3Badge color={taskStatusBadge(detail.taskStatus).color}>
                  {taskStatusBadge(detail.taskStatus).label}
                </V3Badge>
              </div>
              <div className={s.detailMetaRow}>
                <span className={s.detailMeta}>Agent: {detail.agentName}</span>
                <span className={s.detailMeta}>Task: {detail.taskId}</span>
                <span className={s.detailMeta}>Created: {formatTimestamp(detail.createdAt)}</span>
                {detail.taskCompletedAt && (
                  <span className={s.detailMeta}>
                    Task completed: {formatTimestamp(detail.taskCompletedAt)}
                  </span>
                )}
              </div>
              <div className={s.detailContent}>{detail.content}</div>
            </>
          ) : (
            <span style={{ color: "var(--aa-soft)", fontFamily: "var(--aa-mono)", fontSize: "12px" }}>
              Failed to load detail
            </span>
          )}
        </div>
      )}
    </div>
  );
}
