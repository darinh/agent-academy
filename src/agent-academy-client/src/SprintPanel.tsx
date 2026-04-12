import { useState, useEffect, useCallback, useRef } from "react";
import {
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import EmptyState from "./EmptyState";
import ErrorState from "./ErrorState";
import SkeletonLoader from "./SkeletonLoader";
import type {
  SprintSnapshot,
  SprintDetailResponse,
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
import {
  StagePipeline,
  SprintMetricsBar,
  ArtifactList,
  SprintHistory,
  SignOffBanner,
  SprintHeader,
  computeSprintMetrics,
} from "./sprint";

// ── Styles ──────────────────────────────────────────────────────────────

const useLocalStyles = makeStyles({
  root: {
    display: "grid",
    gap: "20px",
    minHeight: 0,
    overflowY: "auto",
    ...shorthands.padding("20px"),
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
  const mountedRef = useRef(true);
  const reconcileTimerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const lastHandledEventRef = useRef<string | null>(null);
  const artifactFetchSeqRef = useRef(0);
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
      if (active && !selectedSprintIdRef.current) {
        selectedSprintIdRef.current = active.sprint.id;
        setSelectedSprintId(active.sprint.id);
        setSelectedDetail(active);
        setSelectedStage(active.sprint.currentStage);
      } else if (active && selectedSprintIdRef.current === active.sprint.id) {
        setSelectedDetail(active);
      } else if (selectedSprintIdRef.current) {
        const sel = selectedSprintIdRef.current;
        const updated = list.sprints.find((s) => s.id === sel);
        if (updated) {
          try {
            const detail = await getSprintDetail(sel);
            if (mountedRef.current && detail) setSelectedDetail(detail);
          } catch { /* ignore */ }
        } else {
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
        const affectedStage = metadata.stage as SprintStage | undefined;
        if (sprintId === selectedSprintIdRef.current) {
          const seq = ++artifactFetchSeqRef.current;
          getSprintArtifacts(sprintId, affectedStage ?? undefined)
            .then((artifacts) => {
              if (!mountedRef.current || artifactFetchSeqRef.current !== seq) return;
              setSelectedDetail((prev) => {
                if (!prev || prev.sprint.id !== sprintId) return prev;
                if (affectedStage) {
                  const otherArtifacts = prev.artifacts.filter((a) => a.stage !== affectedStage);
                  return { ...prev, artifacts: [...otherArtifacts, ...artifacts] };
                }
                return { ...prev, artifacts };
              });
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
        fetchData();
        break;

      case "SprintCompleted":
      case "SprintCancelled": {
        const status: SprintStatus = type === "SprintCancelled"
          ? "Cancelled"
          : (metadata.status as SprintStatus | undefined) ?? "Completed";
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

  const prevVersionRef = useRef(sprintVersion);
  useEffect(() => {
    if (sprintVersion !== prevVersionRef.current) {
      prevVersionRef.current = sprintVersion;
      if (optimisticVersionRef.current !== sprintVersion) {
        fetchData();
      }
    }
  }, [sprintVersion, fetchData]);

  const handleSelectSprint = useCallback(
    async (id: string) => {
      selectedSprintIdRef.current = id;
      setSelectedSprintId(id);
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
  const stageArtifacts = detail && selectedStage
    ? detail.artifacts.filter((a) => a.stage === selectedStage)
    : [];

  const metrics = detail ? computeSprintMetrics(detail) : null;
  const activeStageMetrics = metrics && selectedStage
    ? metrics.stages.find((m) => m.stage === selectedStage) ?? null
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
      <SprintHeader
        detail={detail}
        hasActiveSprint={!!activeSprint}
        actionBusy={actionBusy}
        onRefresh={fetchData}
        onStartSprint={handleStartSprint}
        onAdvanceSprint={handleAdvanceSprint}
        onCompleteSprint={handleCompleteSprint}
        onCancelSprint={handleCancelSprint}
        onApproveAdvance={handleApproveAdvance}
        onRejectAdvance={handleRejectAdvance}
      />

      {detail && <SignOffBanner detail={detail} />}

      {detail && (
        <StagePipeline
          detail={detail}
          selectedStage={selectedStage}
          stageMetrics={metrics?.stages ?? null}
          onSelectStage={setSelectedStage}
        />
      )}

      {metrics && detail && (
        <SprintMetricsBar
          metrics={metrics}
          activeStageMetrics={activeStageMetrics}
          totalArtifacts={detail.artifacts.length}
        />
      )}

      {detail && selectedStage && (
        <ArtifactList
          selectedStage={selectedStage}
          artifacts={stageArtifacts}
        />
      )}

      <SprintHistory
        history={history}
        selectedSprintId={selectedSprintId}
        onSelectSprint={handleSelectSprint}
      />
    </div>
  );
}
