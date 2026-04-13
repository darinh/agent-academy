import { useCallback, useEffect, useRef, useState } from "react";
import { Button, mergeClasses, Spinner } from "@fluentui/react-components";
import { ArrowSyncRegular } from "@fluentui/react-icons";
import V3Badge from "./V3Badge";
import type { BadgeColor } from "./V3Badge";
import EmptyState from "./EmptyState";
import { formatTimestamp } from "./panelUtils";
import {
  listDigests,
  getDigest,
  getDigestStats,
  type DigestListItem,
  type DigestDetailResponse,
  type DigestStatsResponse,
} from "./api";
import { useDigestPanelStyles } from "./digest";

const PAGE_SIZE = 20;
const SUMMARY_TRUNCATE = 120;

function statusBadge(status: string): { color: BadgeColor; label: string } {
  switch (status) {
    case "Completed":
      return { color: "done", label: "Completed" };
    case "Failed":
      return { color: "err", label: "Failed" };
    case "Pending":
      return { color: "review", label: "Pending" };
    default:
      return { color: "muted", label: status };
  }
}

export default function DigestPanel() {
  const s = useDigestPanelStyles();

  const [digests, setDigests] = useState<DigestListItem[]>([]);
  const [total, setTotal] = useState(0);
  const [offset, setOffset] = useState(0);
  const [statusFilter, setStatusFilter] = useState<string>("");
  const [stats, setStats] = useState<DigestStatsResponse | null>(null);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [detail, setDetail] = useState<DigestDetailResponse | null>(null);
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
        listDigests({
          status: statusFilter || undefined,
          limit: PAGE_SIZE,
          offset,
        }),
        getDigestStats(),
      ]);
      if (fetchIdRef.current !== id) return;
      setDigests(listRes.digests);
      setTotal(listRes.total);
      setStats(statsRes);
    } catch (err) {
      if (fetchIdRef.current !== id) return;
      setError(err instanceof Error ? err.message : "Failed to load digests");
    } finally {
      if (fetchIdRef.current === id) setLoading(false);
    }
  }, [statusFilter, offset]);

  useEffect(() => { fetchList(); }, [fetchList]);

  const fetchDetail = useCallback(async (id: number) => {
    const reqId = ++detailFetchIdRef.current;
    setDetailLoading(true);
    try {
      const res = await getDigest(id);
      if (detailFetchIdRef.current !== reqId) return;
      setDetail(res);
    } catch {
      if (detailFetchIdRef.current !== reqId) return;
      setDetail(null);
    } finally {
      if (detailFetchIdRef.current === reqId) setDetailLoading(false);
    }
  }, []);

  const handleSelect = useCallback((id: number) => {
    if (selectedId === id) {
      setSelectedId(null);
      setDetail(null);
    } else {
      setSelectedId(id);
      fetchDetail(id);
    }
  }, [selectedId, fetchDetail]);

  const handleFilterChange = useCallback((e: React.ChangeEvent<HTMLSelectElement>) => {
    setStatusFilter(e.target.value);
    setOffset(0);
    setSelectedId(null);
    setDetail(null);
  }, []);

  const totalPages = Math.ceil(total / PAGE_SIZE);
  const currentPage = Math.floor(offset / PAGE_SIZE) + 1;

  return (
    <div className={s.root}>
      {/* Header */}
      <div className={s.header}>
        <div className={s.headerLeft}>
          <span style={{ fontSize: "16px" }}>📚</span>
          <span style={{ fontFamily: "var(--aa-mono)", fontSize: "13px", fontWeight: 600 }}>
            Learning Digests
          </span>
          <V3Badge color="info">{total} total</V3Badge>
        </div>
        <div className={s.controls}>
          <select
            className={s.select}
            value={statusFilter}
            onChange={handleFilterChange}
            aria-label="Filter by status"
          >
            <option value="">All statuses</option>
            <option value="Completed">Completed</option>
            <option value="Pending">Pending</option>
            <option value="Failed">Failed</option>
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
            <span className={s.statValue}>{stats.totalDigests}</span>
            <span className={s.statLabel}>Digests</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue}>{stats.totalMemoriesCreated}</span>
            <span className={s.statLabel}>Memories created</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue}>{stats.totalRetrospectivesProcessed}</span>
            <span className={s.statLabel}>Retros processed</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue} style={stats.undigestedRetrospectives > 0 ? { color: "var(--aa-gold)" } : undefined}>
              {stats.undigestedRetrospectives}
            </span>
            <span className={s.statLabel}>Undigested retros</span>
          </div>
          {stats.lastCompletedAt && (
            <div className={s.statCard}>
              <span className={s.statValue} style={{ fontSize: "13px" }}>
                {formatTimestamp(stats.lastCompletedAt, false)}
              </span>
              <span className={s.statLabel}>Last completed</span>
            </div>
          )}
        </div>
      )}

      {/* Error */}
      {error && (
        <div style={{ color: "var(--aa-copper)", fontFamily: "var(--aa-mono)", fontSize: "12px" }}>
          ⚠ {error}
        </div>
      )}

      {/* Loading */}
      {loading && digests.length === 0 && <Spinner label="Loading digests…" size="small" />}

      {/* Empty */}
      {!loading && !error && digests.length === 0 && (
        <EmptyState
          icon="📚"
          title="No digests yet"
          detail="Learning digests are created automatically when retrospectives accumulate. Use GENERATE_DIGEST to create one manually."
        />
      )}

      {/* Digest list */}
      {digests.length > 0 && (
        <div className={s.list}>
          {digests.map((d) => {
            const badge = statusBadge(d.status);
            const isActive = selectedId === d.id;
            const summary = d.summary.length > SUMMARY_TRUNCATE
              ? d.summary.slice(0, SUMMARY_TRUNCATE) + "…"
              : d.summary;
            return (
              <div
                key={d.id}
                className={mergeClasses(s.row, isActive && s.rowActive)}
                onClick={() => handleSelect(d.id)}
                role="button"
                tabIndex={0}
                onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") handleSelect(d.id); }}
              >
                <V3Badge color={badge.color}>{badge.label}</V3Badge>
                <span className={s.rowSummary} title={d.summary}>{summary}</span>
                <div className={s.rowMeta}>
                  <span className={s.rowMetaText}>{d.memoriesCreated} mem</span>
                  <span className={s.rowMetaText}>{d.retrospectivesProcessed} retro</span>
                  <span className={s.rowMetaText}>{formatTimestamp(d.createdAt, false)}</span>
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
                <span style={{ fontFamily: "var(--aa-mono)", fontSize: "12px", fontWeight: 600 }}>
                  Digest #{detail.id}
                </span>
                <V3Badge color={statusBadge(detail.status).color}>
                  {statusBadge(detail.status).label}
                </V3Badge>
              </div>
              <div className={s.detailMetaRow}>
                <span className={s.detailMeta}>Created: {formatTimestamp(detail.createdAt)}</span>
                <span className={s.detailMeta}>{detail.memoriesCreated} memories</span>
                <span className={s.detailMeta}>{detail.retrospectivesProcessed} retrospectives</span>
              </div>
              <div className={s.detailSummary}>{detail.summary}</div>

              {detail.sources.length > 0 && (
                <>
                  <div className={s.sourcesHeader}>
                    Source retrospectives ({detail.sources.length})
                  </div>
                  {detail.sources.map((src) => (
                    <div key={src.commentId} className={s.sourceCard}>
                      <div className={s.sourceMetaRow}>
                        <span>Agent: {src.agentId}</span>
                        <span>Task: {src.taskId}</span>
                        <span>{formatTimestamp(src.createdAt, false)}</span>
                      </div>
                      <div className={s.sourceContent}>{src.content}</div>
                    </div>
                  ))}
                </>
              )}
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
