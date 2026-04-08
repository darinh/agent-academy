import { useState, useEffect, useCallback, useRef } from "react";
import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import {
  Button,
  makeStyles,
  mergeClasses,
  shorthands,
} from "@fluentui/react-components";
import { ArrowSyncRegular } from "@fluentui/react-icons";
import EmptyState from "./EmptyState";
import ErrorState from "./ErrorState";
import SkeletonLoader from "./SkeletonLoader";
import V3Badge from "./V3Badge";
import type { BadgeColor } from "./V3Badge";
import type {
  SprintSnapshot,
  SprintDetailResponse,
  SprintStage,
  SprintStatus,
} from "./api";
import {
  getActiveSprint,
  getSprints,
  getSprintDetail,
  startSprint,
  advanceSprint,
  completeSprint,
  cancelSprint,
  approveSprintAdvance,
  rejectSprintAdvance,
} from "./api";
import { formatElapsed } from "./panelUtils";

// ── Constants ──────────────────────────────────────────────────────────

const STAGE_META: Record<
  SprintStage,
  { label: string; icon: string; description: string }
> = {
  Intake: {
    label: "Intake",
    icon: "📥",
    description: "Requirements gathering and scope definition",
  },
  Planning: {
    label: "Planning",
    icon: "📋",
    description: "Sprint plan creation and phase breakdown",
  },
  Discussion: {
    label: "Discussion",
    icon: "💬",
    description: "Team discussion and design decisions",
  },
  Validation: {
    label: "Validation",
    icon: "✅",
    description: "Plan validation and readiness check",
  },
  Implementation: {
    label: "Implementation",
    icon: "🔨",
    description: "Active development and task execution",
  },
  FinalSynthesis: {
    label: "Final Synthesis",
    icon: "📊",
    description: "Sprint report and deliverable summary",
  },
};

const ALL_STAGES: SprintStage[] = [
  "Intake",
  "Planning",
  "Discussion",
  "Validation",
  "Implementation",
  "FinalSynthesis",
];

function statusBadgeColor(status: SprintStatus): BadgeColor {
  switch (status) {
    case "Active":
      return "active";
    case "Completed":
      return "done";
    case "Cancelled":
      return "cancel";
  }
}

function artifactTypeLabel(type: string): string {
  return type.replace(/([A-Z])/g, " $1").trim();
}

// ── Styles ──────────────────────────────────────────────────────────────

const useLocalStyles = makeStyles({
  root: {
    display: "grid",
    gap: "20px",
    minHeight: 0,
    overflowY: "auto",
    ...shorthands.padding("20px"),
  },
  header: {
    display: "flex",
    alignItems: "center",
    gap: "12px",
    flexWrap: "wrap",
  },
  headerTitle: {
    fontFamily: "var(--heading)",
    fontSize: "18px",
    fontWeight: 600,
    color: "var(--aa-text)",
    margin: 0,
  },
  headerMeta: {
    color: "var(--aa-muted)",
    fontSize: "12px",
    fontFamily: "var(--mono)",
  },
  // Stage pipeline
  pipeline: {
    display: "grid",
    gridTemplateColumns: "repeat(6, 1fr)",
    gap: "2px",
    ...shorthands.padding("0"),
    "@media (max-width: 900px)": {
      gridTemplateColumns: "repeat(3, 1fr)",
    },
    "@media (max-width: 600px)": {
      gridTemplateColumns: "1fr",
    },
  },
  stageCard: {
    display: "grid",
    gap: "4px",
    alignContent: "start",
    ...shorthands.padding("12px"),
    background: "var(--aa-surface)",
    ...shorthands.borderRadius("6px"),
    ...shorthands.border("1px", "solid", "var(--aa-border)"),
    cursor: "pointer",
    transitionProperty: "border-color, background",
    transitionDuration: "0.15s",
    "&:hover": {
      ...shorthands.borderColor("var(--aa-border-hover, rgba(139,148,158,0.3))"),
    },
  },
  stageCardActive: {
    ...shorthands.borderColor("var(--aa-cyan, #5b8def)"),
    background:
      "linear-gradient(135deg, rgba(91,141,239,0.06), transparent 60%)",
  },
  stageCardCompleted: {
    ...shorthands.borderColor("var(--aa-lime, #4caf50)"),
    background:
      "linear-gradient(135deg, rgba(76,175,80,0.04), transparent 60%)",
  },
  stageCardSelected: {
    boxShadow: "inset 0 0 0 1px var(--aa-cyan, #5b8def)",
  },
  stageIcon: {
    fontSize: "16px",
    lineHeight: 1,
  },
  stageLabel: {
    fontSize: "11px",
    fontWeight: 600,
    textTransform: "uppercase",
    letterSpacing: "0.06em",
    color: "var(--aa-text)",
    fontFamily: "var(--mono)",
  },
  stageLabelMuted: {
    color: "var(--aa-muted)",
  },
  stageDesc: {
    fontSize: "11px",
    color: "var(--aa-soft)",
    lineHeight: 1.4,
  },
  // Detail section
  detailSection: {
    display: "grid",
    gap: "12px",
  },
  sectionTitle: {
    fontFamily: "var(--mono)",
    fontSize: "11px",
    fontWeight: 600,
    textTransform: "uppercase",
    letterSpacing: "0.08em",
    color: "var(--aa-muted)",
  },
  artifactCard: {
    display: "grid",
    gap: "8px",
    ...shorthands.padding("14px"),
    background: "var(--aa-surface)",
    ...shorthands.borderRadius("6px"),
    ...shorthands.border("1px", "solid", "var(--aa-border)"),
  },
  artifactHeader: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    flexWrap: "wrap",
  },
  artifactTitle: {
    fontSize: "13px",
    fontWeight: 600,
    color: "var(--aa-text)",
  },
  artifactMeta: {
    fontSize: "11px",
    color: "var(--aa-muted)",
    fontFamily: "var(--mono)",
  },
  artifactContent: {
    fontSize: "12px",
    lineHeight: 1.6,
    color: "var(--aa-soft)",
    whiteSpace: "pre-wrap",
    fontFamily: "var(--mono)",
    maxHeight: "300px",
    overflowY: "auto",
    background: "rgba(0,0,0,0.15)",
    ...shorthands.padding("10px"),
    ...shorthands.borderRadius("4px"),
  },
  // History section
  historyList: {
    display: "grid",
    gap: "6px",
  },
  historyItem: {
    display: "flex",
    alignItems: "center",
    gap: "10px",
    ...shorthands.padding("8px", "12px"),
    background: "var(--aa-surface)",
    ...shorthands.borderRadius("6px"),
    ...shorthands.border("1px", "solid", "var(--aa-border)"),
    cursor: "pointer",
    fontSize: "13px",
    transitionProperty: "border-color",
    transitionDuration: "0.15s",
    "&:hover": {
      ...shorthands.borderColor("var(--aa-border-hover, rgba(139,148,158,0.3))"),
    },
  },
  historyItemActive: {
    ...shorthands.borderColor("var(--aa-cyan, #5b8def)"),
  },
  sprintNumber: {
    fontFamily: "var(--mono)",
    fontWeight: 600,
    color: "var(--aa-text)",
    minWidth: "24px",
  },
  sprintMeta: {
    flex: 1,
    color: "var(--aa-soft)",
    fontSize: "12px",
  },
  empty: {
    display: "grid",
    placeItems: "center",
    minHeight: "200px",
    color: "var(--aa-muted)",
    fontSize: "13px",
    textAlign: "center" as const,
    gap: "8px",
  },
  expandToggle: {
    fontSize: "11px",
    color: "var(--aa-cyan, #5b8def)",
    cursor: "pointer",
    fontFamily: "var(--mono)",
    textDecoration: "underline",
    background: "transparent",
    ...shorthands.border("0"),
    ...shorthands.padding("0"),
  },
});

// ── Component ───────────────────────────────────────────────────────────

export default function SprintPanel({ sprintVersion = 0 }: { sprintVersion?: number }) {
  const s = useLocalStyles();

  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [activeSprint, setActiveSprint] =
    useState<SprintDetailResponse | null>(null);
  const [history, setHistory] = useState<SprintSnapshot[]>([]);
  const [selectedSprintId, setSelectedSprintId] = useState<string | null>(null);
  const selectedSprintIdRef = useRef<string | null>(null);
  const [selectedDetail, setSelectedDetail] =
    useState<SprintDetailResponse | null>(null);
  const [selectedStage, setSelectedStage] = useState<SprintStage | null>(null);
  const [expandedArtifacts, setExpandedArtifacts] = useState<Set<number>>(
    new Set(),
  );
  const mountedRef = useRef(true);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  const fetchData = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [active, list] = await Promise.all([
        getActiveSprint(),
        getSprints(50),
      ]);
      if (!mountedRef.current) return;
      setActiveSprint(active);
      setHistory(list.sprints);
      // Default to active sprint only on first load (no selection yet)
      if (active && !selectedSprintIdRef.current) {
        selectedSprintIdRef.current = active.sprint.id;
        setSelectedSprintId(active.sprint.id);
        setSelectedDetail(active);
        setSelectedStage(active.sprint.currentStage);
      } else if (active && selectedSprintIdRef.current === active.sprint.id) {
        // Refresh the active sprint detail in-place
        setSelectedDetail(active);
      } else if (selectedSprintIdRef.current) {
        // Selected sprint is not the active one (or active is null after complete/cancel).
        // Re-fetch its detail so UI reflects current server state.
        const sel = selectedSprintIdRef.current;
        const updated = list.sprints.find((s) => s.id === sel);
        if (updated) {
          try {
            const detail = await getSprintDetail(sel);
            if (mountedRef.current && detail) setSelectedDetail(detail);
          } catch { /* ignore */ }
        } else {
          // Selected sprint no longer in history — reset to active or first
          const fallback = active ?? (list.sprints.length > 0 ? null : null);
          if (fallback) {
            selectedSprintIdRef.current = fallback.sprint.id;
            setSelectedSprintId(fallback.sprint.id);
            setSelectedDetail(fallback);
          } else if (list.sprints.length > 0) {
            const first = list.sprints[0];
            selectedSprintIdRef.current = first.id;
            setSelectedSprintId(first.id);
            try {
              const detail = await getSprintDetail(first.id);
              if (mountedRef.current && detail) setSelectedDetail(detail);
            } catch { /* ignore */ }
          }
        }
      }
    } catch (err) {
      if (!mountedRef.current) return;
      setError(err instanceof Error ? err.message : "Unknown error");
    } finally {
      if (mountedRef.current) setLoading(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    fetchData();
  }, [fetchData]);

  // Re-fetch when SignalR sprint events arrive
  const prevVersionRef = useRef(sprintVersion);
  useEffect(() => {
    if (sprintVersion !== prevVersionRef.current) {
      prevVersionRef.current = sprintVersion;
      fetchData();
      // Also refresh detail for a non-active selected sprint
      const sel = selectedSprintIdRef.current;
      if (sel && (!activeSprint || activeSprint.sprint.id !== sel)) {
        getSprintDetail(sel).then((detail) => {
          if (mountedRef.current && detail) setSelectedDetail(detail);
        }).catch(() => { /* ignore */ });
      }
    }
  }, [sprintVersion, fetchData, activeSprint]);

  const handleSelectSprint = useCallback(
    async (id: string) => {
      selectedSprintIdRef.current = id;
      setSelectedSprintId(id);
      setExpandedArtifacts(new Set());
      // If it's the active sprint, reuse cached data
      if (activeSprint && activeSprint.sprint.id === id) {
        setSelectedDetail(activeSprint);
        setSelectedStage(activeSprint.sprint.currentStage);
        return;
      }
      try {
        const detail = await getSprintDetail(id);
        if (!mountedRef.current) return;
        setSelectedDetail(detail);
        setSelectedStage(detail?.sprint.currentStage ?? null);
      } catch {
        // Ignore - detail stays null
      }
    },
    [activeSprint],
  );

  const toggleArtifact = useCallback((id: number) => {
    setExpandedArtifacts((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);

  const detail = selectedDetail;
  const [actionBusy, setActionBusy] = useState(false);

  const handleStartSprint = useCallback(async () => {
    setActionBusy(true);
    try {
      await startSprint();
      await fetchData();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to start sprint");
    } finally {
      setActionBusy(false);
    }
  }, [fetchData]);

  const handleAdvanceSprint = useCallback(async () => {
    if (!detail) return;
    setActionBusy(true);
    try {
      await advanceSprint(detail.sprint.id);
      await fetchData();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to advance sprint");
    } finally {
      setActionBusy(false);
    }
  }, [detail, fetchData]);

  const handleCompleteSprint = useCallback(async () => {
    if (!detail) return;
    setActionBusy(true);
    try {
      await completeSprint(detail.sprint.id);
      await fetchData();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to complete sprint");
    } finally {
      setActionBusy(false);
    }
  }, [detail, fetchData]);

  const handleCancelSprint = useCallback(async () => {
    if (!detail) return;
    setActionBusy(true);
    try {
      await cancelSprint(detail.sprint.id);
      await fetchData();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to cancel sprint");
    } finally {
      setActionBusy(false);
    }
  }, [detail, fetchData]);

  const handleApproveAdvance = useCallback(async () => {
    if (!detail) return;
    setActionBusy(true);
    try {
      await approveSprintAdvance(detail.sprint.id);
      await fetchData();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to approve advance");
    } finally {
      setActionBusy(false);
    }
  }, [detail, fetchData]);

  const handleRejectAdvance = useCallback(async () => {
    if (!detail) return;
    setActionBusy(true);
    try {
      await rejectSprintAdvance(detail.sprint.id);
      await fetchData();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to reject advance");
    } finally {
      setActionBusy(false);
    }
  }, [detail, fetchData]);

  // Derive current view data
  const currentStageIndex = detail
    ? ALL_STAGES.indexOf(detail.sprint.currentStage)
    : -1;
  const isActive = detail?.sprint.status === "Active";
  const isFinalStage = detail?.sprint.currentStage === "FinalSynthesis";
  const stageArtifacts = detail
    ? detail.artifacts.filter(
        (a) => selectedStage && a.stage === selectedStage,
      )
    : [];

  if (loading) {
    return (
      <div className={s.root}>
        <SkeletonLoader rows={6} />
      </div>
    );
  }

  if (error) {
    return (
      <div className={s.root}>
        <ErrorState
          message={error}
        />
      </div>
    );
  }

  if (!activeSprint && history.length === 0) {
    return (
      <div className={s.root}>
        <EmptyState
          icon={<span style={{ fontSize: "32px" }}>🏃</span>}
          title="No sprints yet"
          detail="Start a sprint to begin the intake process."
          action={{ label: "Start Sprint", onClick: handleStartSprint }}
        />
      </div>
    );
  }

  return (
    <div className={s.root}>
      {/* Header */}
      <div className={s.header}>
        <h2 className={s.headerTitle}>
          {detail
            ? `Sprint #${detail.sprint.number}`
            : "Sprints"}
        </h2>
        {detail && (
          <V3Badge color={statusBadgeColor(detail.sprint.status)}>
            {detail.sprint.status}
          </V3Badge>
        )}
        {detail && (
          <span className={s.headerMeta}>
            started {formatElapsed(detail.sprint.createdAt)}
            {detail.sprint.completedAt &&
              ` · finished ${formatElapsed(detail.sprint.completedAt)}`}
          </span>
        )}
        <Button
          appearance="subtle"
          size="small"
          icon={<ArrowSyncRegular />}
          onClick={fetchData}
          style={{ marginLeft: "auto" }}
        />
        {/* Sprint lifecycle controls */}
        {!activeSprint && (
          <Button appearance="primary" size="small" onClick={handleStartSprint} disabled={actionBusy}>
            Start Sprint
          </Button>
        )}
        {isActive && !isFinalStage && !detail?.sprint.awaitingSignOff && (
          <Button appearance="primary" size="small" onClick={handleAdvanceSprint} disabled={actionBusy}>
            Advance Stage
          </Button>
        )}
        {isActive && detail?.sprint.awaitingSignOff && (
          <>
            <Button appearance="primary" size="small" onClick={handleApproveAdvance} disabled={actionBusy}>
              ✅ Approve → {detail.sprint.pendingStage}
            </Button>
            <Button appearance="subtle" size="small" onClick={handleRejectAdvance} disabled={actionBusy}>
              ✗ Reject
            </Button>
          </>
        )}
        {isActive && isFinalStage && (
          <Button appearance="primary" size="small" onClick={handleCompleteSprint} disabled={actionBusy}>
            Complete Sprint
          </Button>
        )}
        {isActive && (
          <Button appearance="subtle" size="small" onClick={handleCancelSprint} disabled={actionBusy}>
            Cancel
          </Button>
        )}
      </div>

      {/* Sign-off gate banner */}
      {detail?.sprint.awaitingSignOff && (
        <div style={{
          padding: "8px 16px",
          background: "rgba(255, 193, 7, 0.12)",
          borderBottom: "1px solid rgba(255, 193, 7, 0.3)",
          fontSize: "13px",
          display: "flex",
          alignItems: "center",
          gap: "8px",
        }}>
          <span>⏳</span>
          <span>
            <strong>User sign-off required</strong> — agents want to advance from{" "}
            <strong>{detail.sprint.currentStage}</strong> to{" "}
            <strong>{detail.sprint.pendingStage}</strong>.
            Review the {detail.sprint.currentStage} artifacts and approve or reject.
          </span>
        </div>
      )}

      {/* Stage Pipeline */}
      {detail && (
        <div className={s.pipeline}>
          {ALL_STAGES.map((stage, idx) => {
            const meta = STAGE_META[stage];
            const isCurrent = stage === detail.sprint.currentStage;
            const isCompleted =
              detail.sprint.status === "Completed" ||
              idx < currentStageIndex;
            const isSelected = stage === selectedStage;
            const artifactCount = detail.artifacts.filter(
              (a) => a.stage === stage,
            ).length;
            return (
              <div
                key={stage}
                className={mergeClasses(
                  s.stageCard,
                  isCurrent && s.stageCardActive,
                  isCompleted && !isCurrent && s.stageCardCompleted,
                  isSelected && s.stageCardSelected,
                )}
                onClick={() => setSelectedStage(stage)}
                role="button"
                tabIndex={0}
                onKeyDown={(e) => {
                  if (e.key === "Enter" || e.key === " ")
                    setSelectedStage(stage);
                }}
              >
                <span className={s.stageIcon}>{meta.icon}</span>
                <span
                  className={mergeClasses(
                    s.stageLabel,
                    !isCurrent && !isCompleted && s.stageLabelMuted,
                  )}
                >
                  {meta.label}
                  {isCurrent && " ●"}
                </span>
                <span className={s.stageDesc}>
                  {artifactCount > 0
                    ? `${artifactCount} artifact${artifactCount !== 1 ? "s" : ""}`
                    : meta.description}
                </span>
              </div>
            );
          })}
        </div>
      )}

      {/* Artifacts for selected stage */}
      {detail && selectedStage && (
        <div className={s.detailSection}>
          <span className={s.sectionTitle}>
            {STAGE_META[selectedStage].icon}{" "}
            {STAGE_META[selectedStage].label} artifacts
          </span>
          {stageArtifacts.length === 0 ? (
            <div className={s.empty}>
              <span>No artifacts for this stage yet</span>
            </div>
          ) : (
            stageArtifacts.map((artifact) => {
              const expanded = expandedArtifacts.has(artifact.id);
              return (
                <div key={artifact.id} className={s.artifactCard}>
                  <div className={s.artifactHeader}>
                    <span className={s.artifactTitle}>
                      {artifactTypeLabel(artifact.type)}
                    </span>
                    <V3Badge color="info">{artifact.stage}</V3Badge>
                    {artifact.createdByAgentId && (
                      <V3Badge color="tool">
                        {artifact.createdByAgentId}
                      </V3Badge>
                    )}
                    <span className={s.artifactMeta}>
                      {formatElapsed(artifact.createdAt)}
                    </span>
                  </div>
                  {artifact.content.length > 200 && !expanded ? (
                    <>
                      <div className={s.artifactContent}>
                        <Markdown remarkPlugins={[remarkGfm]}>
                          {artifact.content.slice(0, 200) + "…"}
                        </Markdown>
                      </div>
                      <button
                        className={s.expandToggle}
                        onClick={() => toggleArtifact(artifact.id)}
                      >
                        Show full content
                      </button>
                    </>
                  ) : (
                    <>
                      <div className={s.artifactContent}>
                        <Markdown remarkPlugins={[remarkGfm]}>
                          {artifact.content}
                        </Markdown>
                      </div>
                      {artifact.content.length > 200 && (
                        <button
                          className={s.expandToggle}
                          onClick={() => toggleArtifact(artifact.id)}
                        >
                          Collapse
                        </button>
                      )}
                    </>
                  )}
                </div>
              );
            })
          )}
        </div>
      )}

      {/* Sprint History */}
      {history.length > 1 && (
        <div className={s.detailSection}>
          <span className={s.sectionTitle}>Sprint History</span>
          <div className={s.historyList}>
            {history.map((sp) => (
              <div
                key={sp.id}
                className={mergeClasses(
                  s.historyItem,
                  sp.id === selectedSprintId && s.historyItemActive,
                )}
                onClick={() => handleSelectSprint(sp.id)}
                role="button"
                tabIndex={0}
                onKeyDown={(e) => {
                  if (e.key === "Enter" || e.key === " ")
                    handleSelectSprint(sp.id);
                }}
              >
                <span className={s.sprintNumber}>#{sp.number}</span>
                <V3Badge color={statusBadgeColor(sp.status)}>
                  {sp.status}
                </V3Badge>
                <span className={s.sprintMeta}>
                  {sp.currentStage} · {formatElapsed(sp.createdAt)}
                </span>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
