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
  SprintArtifact,
  SprintStage,
  SprintStatus,
  SprintRealtimeEvent,
} from "./api";
import {
  getActiveSprint,
  getSprints,
  getSprintDetail,
  getSprintArtifacts,
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

function wordCount(text: string): number {
  return text.trim().split(/\s+/).filter(Boolean).length;
}

interface StageMetrics {
  stage: SprintStage;
  durationMs: number | null;
  artifactCount: number;
  totalWords: number;
}

function computeSprintMetrics(
  detail: SprintDetailResponse,
): { stages: StageMetrics[]; totalWords: number; totalDurationMs: number } {
  const now = Date.now();
  const sprintStart = new Date(detail.sprint.createdAt).getTime();
  const sprintEnd = detail.sprint.completedAt
    ? new Date(detail.sprint.completedAt).getTime()
    : now;
  const currentStageIdx = ALL_STAGES.indexOf(detail.sprint.currentStage);

  // Group artifacts by stage and find the earliest artifact timestamp per stage
  const stageFirstArtifact = new Map<SprintStage, number>();
  const stageArtifacts = new Map<SprintStage, SprintArtifact[]>();
  for (const a of detail.artifacts) {
    const ts = new Date(a.createdAt).getTime();
    const prev = stageFirstArtifact.get(a.stage);
    if (prev === undefined || ts < prev) stageFirstArtifact.set(a.stage, ts);
    const list = stageArtifacts.get(a.stage) ?? [];
    list.push(a);
    stageArtifacts.set(a.stage, list);
  }

  let totalWords = 0;
  const stages: StageMetrics[] = ALL_STAGES.map((stage, idx) => {
    const arts = stageArtifacts.get(stage) ?? [];
    const words = arts.reduce((sum, a) => sum + wordCount(a.content), 0);
    totalWords += words;

    let durationMs: number | null = null;
    const stageIdx = idx;

    if (detail.sprint.status === "Completed" || stageIdx < currentStageIdx) {
      // Completed stage: estimate duration from artifact boundaries
      const stageStart =
        stageIdx === 0
          ? sprintStart
          : stageFirstArtifact.get(stage) ?? null;
      // Stage end = first artifact of next completed stage, or sprint end
      let stageEnd: number | null = null;
      for (let j = stageIdx + 1; j < ALL_STAGES.length; j++) {
        const nextTs = stageFirstArtifact.get(ALL_STAGES[j]);
        if (nextTs !== undefined) {
          stageEnd = nextTs;
          break;
        }
      }
      if (stageStart !== null) {
        durationMs = (stageEnd ?? sprintEnd) - stageStart;
      }
    } else if (stageIdx === currentStageIdx && detail.sprint.status === "Active") {
      // Current active stage: time since last transition (or sprint start)
      const stageStart =
        stageIdx === 0
          ? sprintStart
          : stageFirstArtifact.get(stage) ??
            // Fallback: latest artifact from previous stage
            (() => {
              for (let j = stageIdx - 1; j >= 0; j--) {
                const arts = stageArtifacts.get(ALL_STAGES[j]);
                if (arts?.length) {
                  return Math.max(...arts.map((a) => new Date(a.createdAt).getTime()));
                }
              }
              return sprintStart;
            })();
      durationMs = now - stageStart;
    }

    return { stage, durationMs, artifactCount: arts.length, totalWords: words };
  });

  return { stages, totalWords, totalDurationMs: sprintEnd - sprintStart };
}

function formatDurationCompact(ms: number): string {
  const seconds = Math.floor(ms / 1000);
  const minutes = Math.floor(seconds / 60);
  const hours = Math.floor(minutes / 60);
  if (minutes < 1) return "<1m";
  if (minutes < 60) return `${minutes}m`;
  if (hours < 24) return `${hours}h ${minutes % 60}m`;
  const days = Math.floor(hours / 24);
  return `${days}d ${hours % 24}h`;
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
  // Metrics bar
  metricsBar: {
    display: "flex",
    gap: "16px",
    flexWrap: "wrap",
    ...shorthands.padding("10px", "14px"),
    background: "var(--aa-surface)",
    ...shorthands.borderRadius("6px"),
    ...shorthands.border("1px", "solid", "var(--aa-border)"),
  },
  metricItem: {
    display: "flex",
    alignItems: "baseline",
    gap: "6px",
  },
  metricValue: {
    fontFamily: "var(--mono)",
    fontSize: "13px",
    fontWeight: 600,
    color: "var(--aa-text)",
  },
  metricLabel: {
    fontSize: "11px",
    color: "var(--aa-muted)",
  },
  metricDivider: {
    width: "1px",
    alignSelf: "stretch",
    background: "var(--aa-border)",
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

export default function SprintPanel({
  sprintVersion = 0,
  lastSprintEvent,
}: {
  sprintVersion?: number;
  lastSprintEvent?: SprintRealtimeEvent | null;
}) {
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
  const reconcileTimerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const lastHandledEventRef = useRef<string | null>(null);
  const artifactFetchSeqRef = useRef(0);
  // Tracks the sprintVersion that was last handled by an optimistic update
  const optimisticVersionRef = useRef(-1);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      if (reconcileTimerRef.current) clearTimeout(reconcileTimerRef.current);
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
  }, []);

  useEffect(() => {
    fetchData();
  }, [fetchData]);

  // Schedule a debounced reconciliation fetch (replaces immediate refetch on every event)
  const scheduleReconcile = useCallback(() => {
    if (reconcileTimerRef.current) clearTimeout(reconcileTimerRef.current);
    reconcileTimerRef.current = setTimeout(() => {
      fetchData();
    }, 1500);
  }, [fetchData]);

  // Handle real-time sprint events with optimistic updates
  useEffect(() => {
    if (!lastSprintEvent || lastSprintEvent.eventId === lastHandledEventRef.current) return;
    lastHandledEventRef.current = lastSprintEvent.eventId;
    optimisticVersionRef.current = sprintVersion;
    const { type, sprintId, metadata } = lastSprintEvent;

    switch (type) {
      case "SprintStageAdvanced": {
        const action = metadata.action as string | undefined;
        const currentStage = metadata.currentStage as SprintStage | undefined;
        const pendingStage = metadata.pendingStage as SprintStage | undefined;

        // Optimistically update the active sprint's stage/sign-off state
        setActiveSprint((prev) => {
          if (!prev || prev.sprint.id !== sprintId) return prev;
          const updated = { ...prev, sprint: { ...prev.sprint } };
          if (action === "signoff_requested") {
            updated.sprint.awaitingSignOff = true;
            updated.sprint.pendingStage = pendingStage ?? null;
          } else if (action === "advanced" || action === "approved") {
            if (currentStage) updated.sprint.currentStage = currentStage;
            updated.sprint.awaitingSignOff = false;
            updated.sprint.pendingStage = null;
          } else if (action === "rejected") {
            updated.sprint.awaitingSignOff = false;
            updated.sprint.pendingStage = null;
          }
          return updated;
        });

        // Update selected detail if viewing this sprint
        setSelectedDetail((prev) => {
          if (!prev || prev.sprint.id !== sprintId) return prev;
          const updated = { ...prev, sprint: { ...prev.sprint } };
          if (action === "signoff_requested") {
            updated.sprint.awaitingSignOff = true;
            updated.sprint.pendingStage = pendingStage ?? null;
          } else if (action === "advanced" || action === "approved") {
            if (currentStage) {
              updated.sprint.currentStage = currentStage;
              setSelectedStage(currentStage);
            }
            updated.sprint.awaitingSignOff = false;
            updated.sprint.pendingStage = null;
          } else if (action === "rejected") {
            updated.sprint.awaitingSignOff = false;
            updated.sprint.pendingStage = null;
          }
          return updated;
        });

        // Update history snapshot
        setHistory((prev) =>
          prev.map((snap) => {
            if (snap.id !== sprintId) return snap;
            const updated = { ...snap };
            if (action === "signoff_requested") {
              updated.awaitingSignOff = true;
              updated.pendingStage = pendingStage ?? null;
            } else if (action === "advanced" || action === "approved") {
              if (currentStage) updated.currentStage = currentStage;
              updated.awaitingSignOff = false;
              updated.pendingStage = null;
            } else if (action === "rejected") {
              updated.awaitingSignOff = false;
              updated.pendingStage = null;
            }
            return updated;
          }),
        );

        scheduleReconcile();
        break;
      }

      case "SprintArtifactStored": {
        // Fetch updated artifacts for the affected sprint
        const affectedStage = metadata.stage as SprintStage | undefined;
        if (sprintId === selectedSprintIdRef.current) {
          const seq = ++artifactFetchSeqRef.current;
          getSprintArtifacts(sprintId, affectedStage ?? undefined)
            .then((artifacts) => {
              // Ignore stale responses from earlier fetches
              if (!mountedRef.current || artifactFetchSeqRef.current !== seq) return;
              setSelectedDetail((prev) => {
                if (!prev || prev.sprint.id !== sprintId) return prev;
                if (affectedStage) {
                  // Merge: replace artifacts for this stage, keep others
                  const otherArtifacts = prev.artifacts.filter((a) => a.stage !== affectedStage);
                  return { ...prev, artifacts: [...otherArtifacts, ...artifacts] };
                }
                return { ...prev, artifacts };
              });
              // Also update activeSprint if it matches
              setActiveSprint((prev) => {
                if (!prev || prev.sprint.id !== sprintId) return prev;
                if (affectedStage) {
                  const otherArtifacts = prev.artifacts.filter((a) => a.stage !== affectedStage);
                  return { ...prev, artifacts: [...otherArtifacts, ...artifacts] };
                }
                return { ...prev, artifacts };
              });
            })
            .catch(() => { /* ignore — reconcile will fix */ });
        }
        scheduleReconcile();
        break;
      }

      case "SprintStarted":
        // New sprint — full refetch to get the snapshot
        fetchData();
        break;

      case "SprintCompleted":
      case "SprintCancelled": {
        const status: SprintStatus = type === "SprintCancelled"
          ? "Cancelled"
          : (metadata.status as SprintStatus | undefined) ?? "Completed";
        // Optimistically mark sprint as completed/cancelled
        setActiveSprint((prev) => {
          if (!prev || prev.sprint.id !== sprintId) return prev;
          return {
            ...prev,
            sprint: {
              ...prev.sprint,
              status,
              completedAt: new Date().toISOString(),
            },
          };
        });
        setSelectedDetail((prev) => {
          if (!prev || prev.sprint.id !== sprintId) return prev;
          return {
            ...prev,
            sprint: {
              ...prev.sprint,
              status,
              completedAt: new Date().toISOString(),
            },
          };
        });
        setHistory((prev) =>
          prev.map((snap) =>
            snap.id === sprintId
              ? { ...snap, status, completedAt: new Date().toISOString() }
              : snap,
          ),
        );
        scheduleReconcile();
        break;
      }
    }
  }, [lastSprintEvent, sprintVersion, fetchData, scheduleReconcile]);

  // Fallback: re-fetch on sprintVersion change if no structured event handled it
  // (e.g., events without metadata from older server versions)
  const prevVersionRef = useRef(sprintVersion);
  useEffect(() => {
    if (sprintVersion !== prevVersionRef.current) {
      prevVersionRef.current = sprintVersion;
      // Only refetch if this version bump was NOT handled by the optimistic path
      if (optimisticVersionRef.current !== sprintVersion) {
        fetchData();
      }
    }
  }, [sprintVersion, fetchData]);

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

  const metrics = detail ? computeSprintMetrics(detail) : null;
  const activeStageMetrics = metrics && selectedStage
    ? metrics.stages.find((m) => m.stage === selectedStage)
    : null;

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
            {detail.sprint.signOffRequestedAt && (() => {
              const elapsed = Date.now() - new Date(detail.sprint.signOffRequestedAt!).getTime();
              const mins = Math.floor(elapsed / 60000);
              const label = mins < 60 ? `${mins}m` : `${Math.floor(mins / 60)}h ${mins % 60}m`;
              return ` Waiting ${label}.`;
            })()}
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
            const stageMetric = metrics?.stages.find((m) => m.stage === stage);
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
                {stageMetric?.durationMs != null && (
                  <span className={s.stageDesc}>
                    ⏱ {formatDurationCompact(stageMetric.durationMs)}
                    {stageMetric.totalWords > 0 &&
                      ` · ${stageMetric.totalWords.toLocaleString()}w`}
                  </span>
                )}
              </div>
            );
          })}
        </div>
      )}

      {/* Metrics bar */}
      {metrics && detail && (
        <div className={s.metricsBar}>
          <div className={s.metricItem}>
            <span className={s.metricValue}>
              {formatDurationCompact(metrics.totalDurationMs)}
            </span>
            <span className={s.metricLabel}>total</span>
          </div>
          <div className={s.metricDivider} />
          {activeStageMetrics?.durationMs != null && (
            <>
              <div className={s.metricItem}>
                <span className={s.metricValue}>
                  {formatDurationCompact(activeStageMetrics.durationMs)}
                </span>
                <span className={s.metricLabel}>
                  {STAGE_META[activeStageMetrics.stage].label.toLowerCase()}
                </span>
              </div>
              <div className={s.metricDivider} />
            </>
          )}
          <div className={s.metricItem}>
            <span className={s.metricValue}>
              {metrics.totalWords.toLocaleString()}
            </span>
            <span className={s.metricLabel}>words</span>
          </div>
          {activeStageMetrics && activeStageMetrics.totalWords > 0 && (
            <>
              <div className={s.metricDivider} />
              <div className={s.metricItem}>
                <span className={s.metricValue}>
                  {activeStageMetrics.totalWords.toLocaleString()}
                </span>
                <span className={s.metricLabel}>
                  in {STAGE_META[activeStageMetrics.stage].label.toLowerCase()}
                </span>
              </div>
            </>
          )}
          <div className={s.metricDivider} />
          <div className={s.metricItem}>
            <span className={s.metricValue}>{detail.artifacts.length}</span>
            <span className={s.metricLabel}>
              artifact{detail.artifacts.length !== 1 ? "s" : ""}
            </span>
          </div>
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
