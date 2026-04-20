import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { mergeClasses } from "@fluentui/react-components";
import { ArrowSyncRegular } from "@fluentui/react-icons";
import { Button } from "@fluentui/react-components";
import V3Badge from "./V3Badge";
import type { BadgeColor } from "./V3Badge";
import EmptyState from "./EmptyState";
import ErrorState from "./ErrorState";
import SkeletonLoader from "./SkeletonLoader";
import { formatTimestamp } from "./panelUtils";
import {
  getGoalCards,
  type GoalCard,
  type GoalCardStatus,
  type GoalCardVerdict,
} from "./api";
import { useGoalCardPanelStyles } from "./goalCards";

// ── Badge helpers ───────────────────────────────────────────────────────

function statusBadge(status: GoalCardStatus): { color: BadgeColor; label: string } {
  switch (status) {
    case "Active":     return { color: "active", label: "Active" };
    case "Challenged": return { color: "warn", label: "Challenged" };
    case "Completed":  return { color: "done", label: "Completed" };
    case "Abandoned":  return { color: "cancel", label: "Abandoned" };
    default:           return { color: "muted", label: status };
  }
}

function verdictBadge(verdict: GoalCardVerdict): { color: BadgeColor; label: string } {
  switch (verdict) {
    case "Proceed":           return { color: "ok", label: "Proceed" };
    case "ProceedWithCaveat": return { color: "review", label: "Caveat" };
    case "Challenge":         return { color: "err", label: "Challenge" };
    default:                  return { color: "muted", label: verdict };
  }
}

// ── Filter types ────────────────────────────────────────────────────────

type StatusFilter = GoalCardStatus | "";
type VerdictFilter = GoalCardVerdict | "";

const STATUS_OPTIONS: { value: StatusFilter; label: string }[] = [
  { value: "", label: "All" },
  { value: "Active", label: "Active" },
  { value: "Challenged", label: "Challenged" },
  { value: "Completed", label: "Completed" },
  { value: "Abandoned", label: "Abandoned" },
];

const VERDICT_OPTIONS: { value: VerdictFilter; label: string }[] = [
  { value: "", label: "All Verdicts" },
  { value: "Proceed", label: "Proceed" },
  { value: "ProceedWithCaveat", label: "Caveat" },
  { value: "Challenge", label: "Challenge" },
];

// ── Component ───────────────────────────────────────────────────────────

interface GoalCardPanelProps {
  roomId?: string | null;
  refreshTrigger?: number;
  onNavigateToTask?: (taskId: string) => void;
}

export default function GoalCardPanel({
  roomId,
  refreshTrigger = 0,
  onNavigateToTask,
}: GoalCardPanelProps) {
  const s = useGoalCardPanelStyles();

  const [cards, setCards] = useState<GoalCard[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [statusFilter, setStatusFilter] = useState<StatusFilter>("");
  const [verdictFilter, setVerdictFilter] = useState<VerdictFilter>("");
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const fetchIdRef = useRef(0);
  const prevTriggerRef = useRef(refreshTrigger);

  const fetchCards = useCallback(async () => {
    const id = ++fetchIdRef.current;
    setLoading(true);
    setError(null);
    try {
      const result = await getGoalCards({
        roomId: roomId ?? undefined,
      });
      if (fetchIdRef.current !== id) return;
      setCards(result);
    } catch (err) {
      if (fetchIdRef.current !== id) return;
      setError(err instanceof Error ? err.message : "Failed to load goal cards");
    } finally {
      if (fetchIdRef.current === id) setLoading(false);
    }
  }, [roomId]);

  useEffect(() => { fetchCards(); }, [fetchCards]);
  useEffect(() => {
    if (refreshTrigger !== prevTriggerRef.current) {
      prevTriggerRef.current = refreshTrigger;
      fetchCards();
    }
  }, [refreshTrigger, fetchCards]);

  // Client-side filtering
  const filtered = useMemo(() => {
    let result = cards;
    if (statusFilter) result = result.filter((c) => c.status === statusFilter);
    if (verdictFilter) result = result.filter((c) => c.verdict === verdictFilter);
    return result;
  }, [cards, statusFilter, verdictFilter]);

  // Stats from full (unfiltered) card set
  const stats = useMemo(() => {
    const counts = { active: 0, challenged: 0, completed: 0, abandoned: 0 };
    for (const c of cards) {
      switch (c.status) {
        case "Active":     counts.active++; break;
        case "Challenged": counts.challenged++; break;
        case "Completed":  counts.completed++; break;
        case "Abandoned":  counts.abandoned++; break;
      }
    }
    return counts;
  }, [cards]);

  if (loading && cards.length === 0) return <SkeletonLoader rows={6} />;
  if (error && cards.length === 0) return <ErrorState message={error} onRetry={fetchCards} />;

  return (
    <div className={s.root}>
      {/* Header */}
      <div className={s.header}>
        <div className={s.headerLeft}>
          <span style={{ fontSize: "16px" }}>🎯</span>
          <span style={{ fontFamily: "var(--aa-mono)", fontSize: "13px", fontWeight: 600, color: "var(--aa-text)" }}>
            Goal Cards
          </span>
          <span style={{ fontFamily: "var(--aa-mono)", fontSize: "11px", color: "var(--aa-soft)" }}>
            ({cards.length})
          </span>
        </div>
        <Button
          appearance="subtle"
          icon={<ArrowSyncRegular />}
          size="small"
          onClick={fetchCards}
          disabled={loading}
        />
      </div>

      {/* Stats row */}
      {cards.length > 0 && (
        <div className={s.statsRow}>
          <div className={s.statCard}>
            <span className={s.statValue}>{stats.active}</span>
            <span className={s.statLabel}>Active</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue}>{stats.challenged}</span>
            <span className={s.statLabel}>Challenged</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue}>{stats.completed}</span>
            <span className={s.statLabel}>Completed</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue}>{stats.abandoned}</span>
            <span className={s.statLabel}>Abandoned</span>
          </div>
        </div>
      )}

      {/* Filter bar */}
      {cards.length > 0 && (
        <div className={s.filterBar}>
          {STATUS_OPTIONS.map((opt) => (
            <span
              key={`s-${opt.value}`}
              className={mergeClasses(s.filterChip, statusFilter === opt.value && s.filterChipActive)}
              onClick={() => setStatusFilter(opt.value)}
              role="button"
              tabIndex={0}
              onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") setStatusFilter(opt.value); }}
            >
              {opt.label}
            </span>
          ))}
          <span style={{ width: "1px", backgroundColor: "var(--aa-border)", margin: "0 4px" }} />
          {VERDICT_OPTIONS.map((opt) => (
            <span
              key={`v-${opt.value}`}
              className={mergeClasses(s.filterChip, verdictFilter === opt.value && s.filterChipActive)}
              onClick={() => setVerdictFilter(opt.value)}
              role="button"
              tabIndex={0}
              onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") setVerdictFilter(opt.value); }}
            >
              {opt.label}
            </span>
          ))}
        </div>
      )}

      {/* Card list */}
      {filtered.length === 0 && cards.length === 0 && (
        <EmptyState
          icon="🎯"
          title="No goal cards yet"
          detail="Goal cards appear when agents create structured intent artifacts before starting significant work."
        />
      )}
      {filtered.length === 0 && cards.length > 0 && (
        <EmptyState
          icon="🔍"
          title="No matching cards"
          detail="Try adjusting your filters."
          action={{ label: "Clear filters", onClick: () => { setStatusFilter(""); setVerdictFilter(""); } }}
        />
      )}

      {filtered.length > 0 && (
        <div className={s.list}>
          {filtered.map((card) => {
            const expanded = expandedId === card.id;
            const sb = statusBadge(card.status);
            const vb = verdictBadge(card.verdict);
            return (
              <div
                key={card.id}
                className={mergeClasses(s.card, expanded && s.cardExpanded)}
                onClick={() => setExpandedId(expanded ? null : card.id)}
                role="button"
                tabIndex={0}
                onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") setExpandedId(expanded ? null : card.id); }}
              >
                {/* Card header */}
                <div className={s.cardHeader}>
                  <span className={s.agentName}>{card.agentName}</span>
                  <V3Badge color={sb.color}>{sb.label}</V3Badge>
                  <V3Badge color={vb.color}>{vb.label}</V3Badge>
                  {card.taskId && onNavigateToTask && (
                    <span
                      className={s.taskLink}
                      onClick={(e) => { e.stopPropagation(); onNavigateToTask(card.taskId!); }}
                      role="link"
                      tabIndex={0}
                      onKeyDown={(e) => {
                        if (e.key === "Enter" || e.key === " ") {
                          e.stopPropagation();
                          onNavigateToTask(card.taskId!);
                        }
                      }}
                    >
                      → task
                    </span>
                  )}
                  <span className={s.cardTimestamp}>{formatTimestamp(card.createdAt, false)}</span>
                </div>

                {/* Collapsed preview */}
                {!expanded && (
                  <div className={s.cardPreview}>
                    {card.taskDescription.length > 120
                      ? card.taskDescription.slice(0, 120) + "…"
                      : card.taskDescription}
                  </div>
                )}

                {/* Expanded body */}
                {expanded && (
                  <div className={s.expandedBody}>
                    <div>
                      <div className={s.sectionLabel}>Task Description</div>
                      <div className={s.sectionContent}>{card.taskDescription}</div>
                    </div>
                    <div>
                      <div className={s.sectionLabel}>Intent</div>
                      <div className={s.sectionContent}>{card.intent}</div>
                    </div>
                    <div>
                      <div className={s.sectionLabel}>Divergence</div>
                      <div className={s.sectionContent}>{card.divergence}</div>
                    </div>
                    <div>
                      <div className={s.sectionLabel}>Steelman</div>
                      <div className={s.sectionContent}>{card.steelman}</div>
                    </div>
                    <div>
                      <div className={s.sectionLabel}>Strawman</div>
                      <div className={s.sectionContent}>{card.strawman}</div>
                    </div>
                    <div>
                      <div className={s.sectionLabel}>Fresh Eyes</div>
                      <div className={s.freshEyesGrid}>
                        <div className={s.freshEyesItem}>
                          <div className={s.sectionLabel}>#1</div>
                          <div className={s.sectionContent}>{card.freshEyes1}</div>
                        </div>
                        <div className={s.freshEyesItem}>
                          <div className={s.sectionLabel}>#2</div>
                          <div className={s.sectionContent}>{card.freshEyes2}</div>
                        </div>
                        <div className={s.freshEyesItem}>
                          <div className={s.sectionLabel}>#3</div>
                          <div className={s.sectionContent}>{card.freshEyes3}</div>
                        </div>
                      </div>
                    </div>
                    <div style={{ display: "flex", gap: "16px", flexWrap: "wrap" }}>
                      <span style={{ fontFamily: "var(--aa-mono)", fontSize: "10px", color: "var(--aa-soft)" }}>
                        ID: {card.id}
                      </span>
                      <span style={{ fontFamily: "var(--aa-mono)", fontSize: "10px", color: "var(--aa-soft)" }}>
                        Prompt v{card.promptVersion}
                      </span>
                      <span style={{ fontFamily: "var(--aa-mono)", fontSize: "10px", color: "var(--aa-soft)" }}>
                        Updated: {formatTimestamp(card.updatedAt, false)}
                      </span>
                    </div>
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
