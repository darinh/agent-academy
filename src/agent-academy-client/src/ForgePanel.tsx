import { useCallback, useEffect, useRef, useState } from "react";
import { Button, mergeClasses, Spinner } from "@fluentui/react-components";
import { ArrowSyncRegular, ChevronLeftRegular, ChevronDownRegular, ChevronRightRegular, PlayRegular } from "@fluentui/react-icons";
import V3Badge from "./V3Badge";
import type { BadgeColor } from "./V3Badge";
import EmptyState from "./EmptyState";
import { formatTimestamp, formatCost, formatTokenCount } from "./panelUtils";
import {
  getForgeStatus,
  listForgeJobs,
  listForgeRuns,
  getForgeRun,
  getForgeRunPhases,
  getForgeArtifact,
  startForgeRun,
  listMethodologies,
  getMethodology,
  saveMethodology,
  type ForgeStatus,
  type ForgeJobSummary,
  type ForgeRunSummary,
  type ForgeRunTrace,
  type ForgePhaseRunTrace,
  type ForgeArtifactResponse,
  type MethodologySummary,
} from "./api";
import { useForgePanelStyles } from "./forge";
import { DEFAULT_METHODOLOGY_JSON } from "./forge/defaultMethodology";

const POLL_INTERVAL_MS = 5000;

function jobStatusBadge(status: string): { color: BadgeColor; label: string } {
  const s = status.toLowerCase();
  switch (s) {
    case "completed":
    case "succeeded":
      return { color: "done", label: status };
    case "running":
      return { color: "review", label: "Running" };
    case "queued":
    case "pending":
      return { color: "info", label: status };
    case "failed":
      return { color: "err", label: "Failed" };
    case "aborted":
      return { color: "warn", label: "Aborted" };
    case "skipped":
      return { color: "muted", label: "Skipped" };
    default:
      return { color: "muted", label: status };
  }
}

function attemptStatusBadge(status: string): { color: BadgeColor; label: string } {
  switch (status) {
    case "Accepted":
      return { color: "done", label: "Accepted" };
    case "Rejected":
      return { color: "warn", label: "Rejected" };
    case "Errored":
      return { color: "err", label: "Errored" };
    case "Generating":
    case "Validating":
    case "Prompting":
      return { color: "review", label: status };
    default:
      return { color: "muted", label: status };
  }
}

function validatorSeverityColor(severity: string): string {
  switch (severity) {
    case "error": return "var(--aa-copper, #e06c75)";
    case "warning": return "var(--aa-gold, #e5c07b)";
    default: return "var(--aa-soft)";
  }
}

function fidelityBadge(outcome?: string | null): { color: BadgeColor; label: string } | null {
  if (!outcome) return null;
  switch (outcome) {
    case "pass": return { color: "done", label: "Fidelity: Pass" };
    case "fail": return { color: "err", label: "Fidelity: Fail" };
    case "partial": return { color: "warn", label: "Fidelity: Partial" };
    default: return { color: "muted", label: `Fidelity: ${outcome}` };
  }
}

function formatLatencyMs(ms: number): string {
  if (ms < 1000) return `${ms}ms`;
  return `${(ms / 1000).toFixed(1)}s`;
}

// ── Main Panel ───────────────────────────────────────────────

type ForgeView = "list" | "run-detail" | "new-run";

export default function ForgePanel({ refreshTrigger }: { refreshTrigger?: number }) {
  const s = useForgePanelStyles();

  // List view state
  const [status, setStatus] = useState<ForgeStatus | null>(null);
  const [jobs, setJobs] = useState<ForgeJobSummary[]>([]);
  const [runs, setRuns] = useState<ForgeRunSummary[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const fetchIdRef = useRef(0);

  // Detail view state
  const [view, setView] = useState<ForgeView>("list");
  const [selectedRunId, setSelectedRunId] = useState<string | null>(null);
  const [runTrace, setRunTrace] = useState<ForgeRunTrace | null>(null);
  const [phases, setPhases] = useState<ForgePhaseRunTrace[]>([]);
  const [detailLoading, setDetailLoading] = useState(false);
  const detailFetchIdRef = useRef(0);

  // Phase expansion state
  const [expandedPhases, setExpandedPhases] = useState<Set<string>>(new Set());

  // Artifact viewer state
  const [viewingArtifact, setViewingArtifact] = useState<{ hash: string; data: ForgeArtifactResponse } | null>(null);
  const [artifactLoading, setArtifactLoading] = useState(false);
  const artifactCache = useRef<Map<string, ForgeArtifactResponse>>(new Map());
  const artifactFetchIdRef = useRef(0);

  // New run form state
  const [newRunTitle, setNewRunTitle] = useState("");
  const [newRunDescription, setNewRunDescription] = useState("");
  const [newRunMethodology, setNewRunMethodology] = useState(DEFAULT_METHODOLOGY_JSON);
  const [newRunSubmitting, setNewRunSubmitting] = useState(false);
  const [newRunError, setNewRunError] = useState<string | null>(null);

  // Methodology catalog state
  const [methodologyCatalog, setMethodologyCatalog] = useState<MethodologySummary[]>([]);
  const [selectedMethodologyId, setSelectedMethodologyId] = useState<string>("");
  const [savingMethodology, setSavingMethodology] = useState(false);
  const methodologyFetchIdRef = useRef(0);

  // Polling ref
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null);

  // ── Data fetching ──

  const fetchList = useCallback(async () => {
    const id = ++fetchIdRef.current;
    setLoading(true);
    setError(null);
    try {
      const [statusRes, jobsRes, runsRes] = await Promise.all([
        getForgeStatus(),
        listForgeJobs(),
        listForgeRuns(),
      ]);
      if (fetchIdRef.current !== id) return;
      setStatus(statusRes);
      setJobs(jobsRes);
      setRuns(runsRes);
    } catch (err) {
      if (fetchIdRef.current !== id) return;
      setError(err instanceof Error ? err.message : "Failed to load forge data");
    } finally {
      if (fetchIdRef.current === id) setLoading(false);
    }
  }, []);

  const fetchRunDetail = useCallback(async (runId: string) => {
    const reqId = ++detailFetchIdRef.current;
    setDetailLoading(true);
    try {
      const [traceRes, phasesRes] = await Promise.all([
        getForgeRun(runId),
        getForgeRunPhases(runId),
      ]);
      if (detailFetchIdRef.current !== reqId) return;
      setRunTrace(traceRes);
      setPhases(phasesRes);
    } catch {
      if (detailFetchIdRef.current !== reqId) return;
      setRunTrace(null);
      setPhases([]);
    } finally {
      if (detailFetchIdRef.current === reqId) setDetailLoading(false);
    }
  }, []);

  const fetchArtifact = useCallback(async (hash: string) => {
    const cached = artifactCache.current.get(hash);
    if (cached) {
      setViewingArtifact({ hash, data: cached });
      return;
    }
    const reqId = ++artifactFetchIdRef.current;
    setArtifactLoading(true);
    try {
      const res = await getForgeArtifact(hash);
      if (artifactFetchIdRef.current !== reqId) return;
      artifactCache.current.set(hash, res);
      setViewingArtifact({ hash, data: res });
    } catch {
      if (artifactFetchIdRef.current !== reqId) return;
      setViewingArtifact(null);
    } finally {
      if (artifactFetchIdRef.current === reqId) setArtifactLoading(false);
    }
  }, []);

  // ── Initial fetch ──
  useEffect(() => { fetchList(); }, [fetchList]);

  // ── SignalR-driven refresh: re-fetch when forge events arrive ──
  const prevTrigger = useRef(refreshTrigger);
  useEffect(() => {
    if (refreshTrigger !== undefined && refreshTrigger !== prevTrigger.current) {
      prevTrigger.current = refreshTrigger;
      fetchList();
      if (selectedRunId) fetchRunDetail(selectedRunId);
    }
  }, [refreshTrigger, fetchList, fetchRunDetail, selectedRunId]);

  // ── Conditional polling: poll when active jobs exist (fallback for no SignalR) ──
  useEffect(() => {
    const hasActive = status != null && status.activeJobs > 0;
    const isRunning = runTrace != null && (runTrace.outcome === "Running" || runTrace.outcome === "Pending");

    if (hasActive || isRunning) {
      pollRef.current = setInterval(() => {
        fetchList();
        if (selectedRunId && isRunning) fetchRunDetail(selectedRunId);
      }, POLL_INTERVAL_MS);
    }

    return () => {
      if (pollRef.current) {
        clearInterval(pollRef.current);
        pollRef.current = null;
      }
    };
  }, [status?.activeJobs, runTrace?.outcome, selectedRunId, fetchList, fetchRunDetail]);

  // ── Navigation ──

  const handleSelectRun = useCallback((runId: string) => {
    setSelectedRunId(runId);
    setView("run-detail");
    setExpandedPhases(new Set());
    setViewingArtifact(null);
    fetchRunDetail(runId);
  }, [fetchRunDetail]);

  const handleSelectJob = useCallback((job: ForgeJobSummary) => {
    if (job.runId) {
      handleSelectRun(job.runId);
    }
  }, [handleSelectRun]);

  const handleBack = useCallback(() => {
    setView("list");
    setSelectedRunId(null);
    setRunTrace(null);
    setPhases([]);
    setViewingArtifact(null);
    fetchList();
  }, [fetchList]);

  const togglePhase = useCallback((phaseId: string) => {
    setExpandedPhases((prev) => {
      const next = new Set(prev);
      if (next.has(phaseId)) next.delete(phaseId);
      else next.add(phaseId);
      return next;
    });
  }, []);

  // ── New run handlers ──

  const handleOpenNewRun = useCallback(async () => {
    setNewRunTitle("");
    setNewRunDescription("");
    setNewRunMethodology(DEFAULT_METHODOLOGY_JSON);
    setNewRunError(null);
    setSelectedMethodologyId("");
    setView("new-run");
    try {
      const catalog = await listMethodologies();
      setMethodologyCatalog(catalog);
    } catch {
      // Catalog is optional — form still works with manual JSON editing
      setMethodologyCatalog([]);
    }
  }, []);

  const handleCancelNewRun = useCallback(() => {
    setView("list");
    setNewRunError(null);
  }, []);

  const handleSubmitNewRun = useCallback(async () => {
    setNewRunError(null);

    if (!newRunTitle.trim()) {
      setNewRunError("Title is required");
      return;
    }
    if (!newRunDescription.trim()) {
      setNewRunError("Description is required");
      return;
    }

    let methodology;
    try {
      methodology = JSON.parse(newRunMethodology);
    } catch {
      setNewRunError("Methodology JSON is invalid");
      return;
    }

    setNewRunSubmitting(true);
    try {
      await startForgeRun({
        title: newRunTitle.trim(),
        description: newRunDescription.trim(),
        methodology,
      });
      setView("list");
      fetchList();
    } catch (err) {
      setNewRunError(err instanceof Error ? err.message : "Failed to start run");
    } finally {
      setNewRunSubmitting(false);
    }
  }, [newRunTitle, newRunDescription, newRunMethodology, fetchList]);

  const handleSelectMethodology = useCallback(async (id: string) => {
    setSelectedMethodologyId(id);
    if (!id) return; // "Custom" selected — keep current JSON
    const fetchId = ++methodologyFetchIdRef.current;
    try {
      const methodology = await getMethodology(id);
      if (fetchId !== methodologyFetchIdRef.current) return; // Stale response
      setNewRunMethodology(JSON.stringify(methodology, null, 2));
      setNewRunError(null);
    } catch {
      if (fetchId !== methodologyFetchIdRef.current) return;
      setNewRunError(`Failed to load methodology "${id}"`);
    }
  }, []);

  const handleSaveAsTemplate = useCallback(async () => {
    let methodology;
    try {
      methodology = JSON.parse(newRunMethodology);
    } catch {
      setNewRunError("Cannot save: methodology JSON is invalid");
      return;
    }
    if (!methodology.id) {
      setNewRunError("Cannot save: methodology must have an 'id' field");
      return;
    }
    setSavingMethodology(true);
    setNewRunError(null);
    try {
      await saveMethodology(methodology.id, methodology);
      const catalog = await listMethodologies();
      setMethodologyCatalog(catalog);
      setSelectedMethodologyId(methodology.id);
      setNewRunError(null);
    } catch (err) {
      setNewRunError(err instanceof Error ? err.message : "Failed to save methodology");
    } finally {
      setSavingMethodology(false);
    }
  }, [newRunMethodology]);

  // ── Render: New Run Form ──

  if (view === "new-run") {
    return (
      <div className={s.root}>
        <Button
          className={s.backButton}
          appearance="subtle"
          size="small"
          icon={<ChevronLeftRegular />}
          onClick={handleCancelNewRun}
        >
          Cancel
        </Button>

        <div className={s.detail}>
          <div className={s.detailHeader}>
            <span style={{ fontFamily: "var(--aa-mono)", fontSize: "14px", fontWeight: 600 }}>
              🚀 New Pipeline Run
            </span>
          </div>

          <div className={s.formGroup}>
            <label className={s.formLabel} htmlFor="forge-title">Title</label>
            <input
              id="forge-title"
              className={s.formInput}
              type="text"
              placeholder="e.g. Build auth module"
              value={newRunTitle}
              onChange={(e) => setNewRunTitle(e.target.value)}
              disabled={newRunSubmitting}
            />
          </div>

          <div className={s.formGroup}>
            <label className={s.formLabel} htmlFor="forge-description">Description</label>
            <textarea
              id="forge-description"
              className={s.formTextarea}
              placeholder="Describe the task for the pipeline…"
              value={newRunDescription}
              onChange={(e) => setNewRunDescription(e.target.value)}
              disabled={newRunSubmitting}
              rows={4}
            />
          </div>

          <div className={s.formGroup}>
            <label className={s.formLabel} htmlFor="forge-methodology-select">Methodology</label>
            <select
              id="forge-methodology-select"
              className={s.formInput}
              value={selectedMethodologyId}
              onChange={(e) => handleSelectMethodology(e.target.value)}
              disabled={newRunSubmitting}
              style={{ cursor: "pointer" }}
            >
              <option value="">Custom (edit JSON below)</option>
              {methodologyCatalog.map((m) => (
                <option key={m.id} value={m.id}>
                  {m.id} — {m.phaseCount} phases{m.description ? ` · ${m.description}` : ""}
                </option>
              ))}
            </select>
          </div>

          <div className={s.formGroup}>
            <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
              <label className={s.formLabel} htmlFor="forge-methodology" style={{ marginBottom: 0 }}>
                Methodology JSON
              </label>
              <Button
                appearance="subtle"
                size="small"
                onClick={handleSaveAsTemplate}
                disabled={newRunSubmitting || savingMethodology}
                style={{ fontSize: "11px" }}
              >
                {savingMethodology ? "Saving…" : "💾 Save as Template"}
              </Button>
            </div>
            <textarea
              id="forge-methodology"
              className={s.formTextarea}
              value={newRunMethodology}
              onChange={(e) => {
                setNewRunMethodology(e.target.value);
                setSelectedMethodologyId(""); // Mark as custom when editing
              }}
              disabled={newRunSubmitting}
              rows={12}
              style={{ fontSize: "11px" }}
            />
          </div>

          {newRunError && (
            <div style={{ color: "var(--aa-copper, #e06c75)", fontFamily: "var(--aa-mono)", fontSize: "12px" }}>
              ⚠ {newRunError}
            </div>
          )}

          <Button
            appearance="primary"
            size="small"
            icon={newRunSubmitting ? <Spinner size="tiny" /> : <PlayRegular />}
            onClick={handleSubmitNewRun}
            disabled={newRunSubmitting}
          >
            {newRunSubmitting ? "Starting…" : "Start Run"}
          </Button>
        </div>
      </div>
    );
  }

  // ── Render: Run Detail ──

  if (view === "run-detail") {
    return (
      <div className={s.root}>
        <Button
          className={s.backButton}
          appearance="subtle"
          size="small"
          icon={<ChevronLeftRegular />}
          onClick={handleBack}
        >
          Back to list
        </Button>

        {detailLoading && !runTrace ? (
          <Spinner label="Loading run detail…" size="small" />
        ) : runTrace ? (
          <>
            {/* Run header */}
            <div className={s.detailHeader}>
              <span style={{ fontFamily: "var(--aa-mono)", fontSize: "12px", fontWeight: 600 }}>
                Run {runTrace.runId}
              </span>
              <div style={{ display: "flex", gap: "6px", alignItems: "center" }}>
                <V3Badge color={jobStatusBadge(runTrace.outcome).color}>
                  {jobStatusBadge(runTrace.outcome).label}
                </V3Badge>
                {fidelityBadge(runTrace.fidelityOutcome) && (
                  <V3Badge color={fidelityBadge(runTrace.fidelityOutcome)!.color}>
                    {fidelityBadge(runTrace.fidelityOutcome)!.label}
                  </V3Badge>
                )}
              </div>
            </div>

            {/* Run metadata */}
            <div className={s.detailMetaRow}>
              <span className={s.detailMeta}>Task: {runTrace.taskId}</span>
              <span className={s.detailMeta}>Methodology: {runTrace.methodologyVersion}</span>
              <span className={s.detailMeta}>Started: {formatTimestamp(runTrace.startedAt)}</span>
              {runTrace.endedAt && (
                <span className={s.detailMeta}>Ended: {formatTimestamp(runTrace.endedAt)}</span>
              )}
            </div>

            {/* Cost/token summary */}
            <div className={s.statsRow}>
              <div className={s.statCard}>
                <span className={s.statValue}>{formatTokenCount(runTrace.pipelineTokens.in + runTrace.pipelineTokens.out)}</span>
                <span className={s.statLabel}>Total tokens</span>
              </div>
              <div className={s.statCard}>
                <span className={s.statValue}>{runTrace.pipelineCost != null ? formatCost(runTrace.pipelineCost) : "—"}</span>
                <span className={s.statLabel}>Pipeline cost</span>
              </div>
              {runTrace.controlCost != null && (
                <div className={s.statCard}>
                  <span className={s.statValue}>{formatCost(runTrace.controlCost)}</span>
                  <span className={s.statLabel}>Control cost</span>
                </div>
              )}
              {runTrace.costRatio != null && (
                <div className={s.statCard}>
                  <span className={s.statValue}>{runTrace.costRatio.toFixed(2)}x</span>
                  <span className={s.statLabel}>Cost ratio</span>
                </div>
              )}
              <div className={s.statCard}>
                <span className={s.statValue}>{phases.length}</span>
                <span className={s.statLabel}>Phases</span>
              </div>
            </div>

            {runTrace.abortReason && (
              <div style={{ color: "var(--aa-copper)", fontFamily: "var(--aa-mono)", fontSize: "12px" }}>
                ⚠ Abort reason: {runTrace.abortReason}
              </div>
            )}

            {/* Phases */}
            <div className={s.sectionHeader}>Phases</div>
            <div className={s.list}>
              {phases.map((phase) => {
                const isExpanded = expandedPhases.has(phase.phaseId);
                const lastState = phase.stateTransitions.length > 0
                  ? phase.stateTransitions[phase.stateTransitions.length - 1].to
                  : "Pending";
                const badge = jobStatusBadge(lastState);

                return (
                  <div key={phase.phaseId} className={s.phaseCard}>
                    <div
                      className={s.phaseHeader}
                      onClick={() => togglePhase(phase.phaseId)}
                      role="button"
                      tabIndex={0}
                      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") togglePhase(phase.phaseId); }}
                    >
                      <div className={s.phaseHeaderLeft}>
                        {isExpanded ? <ChevronDownRegular fontSize={12} /> : <ChevronRightRegular fontSize={12} />}
                        <span style={{ fontFamily: "var(--aa-mono)", fontSize: "12px", fontWeight: 600 }}>
                          {phase.phaseId}
                        </span>
                        <V3Badge color={badge.color}>{badge.label}</V3Badge>
                        <span style={{ fontSize: "10px", fontFamily: "var(--aa-mono)", color: "var(--aa-soft)" }}>
                          {phase.artifactType}
                        </span>
                      </div>
                      <span style={{ fontSize: "10px", fontFamily: "var(--aa-mono)", color: "var(--aa-soft)" }}>
                        {phase.attempts.length} attempt{phase.attempts.length !== 1 ? "s" : ""}
                      </span>
                    </div>

                    {isExpanded && (
                      <>
                        {/* Attempts */}
                        {phase.attempts.map((attempt) => {
                          const aBadge = attemptStatusBadge(attempt.status);
                          return (
                            <div key={attempt.attemptNumber}>
                              <div className={s.attemptRow}>
                                <span>#{attempt.attemptNumber}</span>
                                <V3Badge color={aBadge.color}>{aBadge.label}</V3Badge>
                                <span>{attempt.model}</span>
                                <span>{formatLatencyMs(attempt.latencyMs)}</span>
                                <span>{formatTokenCount(attempt.tokens.in + attempt.tokens.out)} tok</span>
                                {attempt.cost != null && <span>{formatCost(attempt.cost)}</span>}
                                {attempt.artifactHash && (
                                  <span
                                    className={s.artifactLink}
                                    onClick={() => fetchArtifact(attempt.artifactHash!)}
                                    role="link"
                                    tabIndex={0}
                                    onKeyDown={(e) => { if (e.key === "Enter") fetchArtifact(attempt.artifactHash!); }}
                                  >
                                    {attempt.artifactHash.slice(0, 12)}…
                                  </span>
                                )}
                              </div>

                              {/* Validator results */}
                              {attempt.validatorResults.length > 0 && (
                                <div className={s.validatorList}>
                                  {attempt.validatorResults.map((v, i) => (
                                    <div
                                      key={i}
                                      className={s.validatorItem}
                                      style={{ color: validatorSeverityColor(v.severity) }}
                                    >
                                      [{v.phase}] {v.code}{v.blocking ? " ⛔" : ""}{v.path ? ` @ ${v.path}` : ""}
                                      {v.evidence && <span style={{ color: "var(--aa-soft)" }}> — {v.evidence}</span>}
                                    </div>
                                  ))}
                                </div>
                              )}
                            </div>
                          );
                        })}

                        {/* Output artifacts */}
                        {phase.outputArtifactHashes.length > 0 && (
                          <div className={s.artifactSection}>
                            <span className={s.sectionHeader}>Output artifacts</span>
                            {phase.outputArtifactHashes.map((hash) => (
                              <span
                                key={hash}
                                className={s.artifactLink}
                                onClick={() => fetchArtifact(hash)}
                                role="link"
                                tabIndex={0}
                                onKeyDown={(e) => { if (e.key === "Enter") fetchArtifact(hash); }}
                              >
                                📦 {hash.slice(0, 16)}…
                              </span>
                            ))}
                          </div>
                        )}
                      </>
                    )}
                  </div>
                );
              })}
            </div>

            {/* Final artifacts */}
            {Object.keys(runTrace.finalArtifactHashes).length > 0 && (
              <>
                <div className={s.sectionHeader}>Final artifacts</div>
                <div className={s.artifactSection}>
                  {Object.entries(runTrace.finalArtifactHashes).map(([phase, hash]) => (
                    <div key={phase} style={{ display: "flex", gap: "8px", alignItems: "center" }}>
                      <span style={{ fontSize: "11px", fontFamily: "var(--aa-mono)", color: "var(--aa-soft)" }}>
                        {phase}:
                      </span>
                      <span
                        className={s.artifactLink}
                        onClick={() => fetchArtifact(hash)}
                        role="link"
                        tabIndex={0}
                        onKeyDown={(e) => { if (e.key === "Enter") fetchArtifact(hash); }}
                      >
                        {hash.slice(0, 16)}…
                      </span>
                    </div>
                  ))}
                </div>
              </>
            )}

            {/* Inline artifact viewer */}
            {artifactLoading && <Spinner label="Loading artifact…" size="small" />}
            {viewingArtifact && (
              <div className={s.detail}>
                <div className={s.detailHeader}>
                  <span style={{ fontFamily: "var(--aa-mono)", fontSize: "12px", fontWeight: 600 }}>
                    Artifact: {viewingArtifact.data.artifact.artifactType}/{viewingArtifact.data.artifact.schemaVersion}
                  </span>
                  <Button
                    appearance="subtle"
                    size="small"
                    onClick={() => setViewingArtifact(null)}
                  >
                    Close
                  </Button>
                </div>
                <div className={s.detailMetaRow}>
                  <span className={s.detailMeta}>Phase: {viewingArtifact.data.artifact.producedByPhase}</span>
                  <span className={s.detailMeta}>Hash: {viewingArtifact.hash.slice(0, 20)}…</span>
                  <span className={s.detailMeta}>Attempt: #{viewingArtifact.data.meta.attemptNumber}</span>
                  <span className={s.detailMeta}>Produced: {formatTimestamp(viewingArtifact.data.meta.producedAt)}</span>
                </div>
                <div className={s.artifactViewer}>
                  <pre className={s.artifactJson}>
                    {JSON.stringify(viewingArtifact.data.artifact.payload, null, 2)}
                  </pre>
                </div>
              </div>
            )}
          </>
        ) : (
          <span style={{ color: "var(--aa-soft)", fontFamily: "var(--aa-mono)", fontSize: "12px" }}>
            Failed to load run detail
          </span>
        )}
      </div>
    );
  }

  // ── Render: List View ──

  const activeJobs = jobs.filter((j) => j.status === "queued" || j.status === "running");

  return (
    <div className={s.root}>
      {/* Header */}
      <div className={s.header}>
        <div className={s.headerLeft}>
          <span style={{ fontSize: "16px" }}>🔥</span>
          <span style={{ fontFamily: "var(--aa-mono)", fontSize: "13px", fontWeight: 600 }}>
            Forge
          </span>
          {status && (
            <V3Badge
              color={status.enabled ? (status.executionAvailable ? "done" : "warn") : "muted"}
            >
              {status.enabled ? (status.executionAvailable ? "Ready" : "Execution disabled") : "Disabled"}
            </V3Badge>
          )}
        </div>
        <div className={s.controls}>
          {status?.executionAvailable && (
            <Button
              appearance="subtle"
              size="small"
              icon={<PlayRegular />}
              onClick={handleOpenNewRun}
              aria-label="New run"
            >
              New Run
            </Button>
          )}
          <Button
            appearance="subtle"
            size="small"
            icon={<ArrowSyncRegular />}
            onClick={fetchList}
            aria-label="Refresh"
          />
        </div>
      </div>

      {/* Disabled state */}
      {status && !status.enabled && (
        <div className={s.disabledBanner}>
          <span style={{ fontSize: "28px" }}>🔒</span>
          <span style={{ fontFamily: "var(--aa-mono)", fontSize: "13px", color: "var(--aa-text)" }}>
            Forge engine is disabled
          </span>
          <span style={{ fontFamily: "var(--aa-mono)", fontSize: "11px", color: "var(--aa-soft)" }}>
            Enable it in server configuration to start pipeline runs.
          </span>
        </div>
      )}

      {/* Execution disabled (kill switch) — engine is enabled but execution is gated off */}
      {status && status.enabled && !status.executionAvailable && (
        <div className={s.disabledBanner}>
          <span style={{ fontSize: "28px" }}>⏸️</span>
          <span style={{ fontFamily: "var(--aa-mono)", fontSize: "13px", color: "var(--aa-text)" }}>
            Forge execution is disabled
          </span>
          <span style={{ fontFamily: "var(--aa-mono)", fontSize: "11px", color: "var(--aa-soft)" }}>
            You can browse existing runs but cannot start new ones. Set <code>Forge:ExecutionEnabled</code> to <code>true</code> in server configuration to re-enable.
          </span>
        </div>
      )}

      {/* Status cards */}
      {status && status.enabled && (
        <div className={s.statsRow}>
          <div className={s.statCard}>
            <span className={s.statValue} style={status.activeJobs > 0 ? { color: "var(--aa-cyan, #5b8def)" } : undefined}>
              {status.activeJobs}
            </span>
            <span className={s.statLabel}>Active</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue}>{status.completedJobs}</span>
            <span className={s.statLabel}>Completed</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue} style={status.failedJobs > 0 ? { color: "var(--aa-copper, #e06c75)" } : undefined}>
              {status.failedJobs}
            </span>
            <span className={s.statLabel}>Failed</span>
          </div>
          <div className={s.statCard}>
            <span className={s.statValue}>{status.totalJobs}</span>
            <span className={s.statLabel}>Total jobs</span>
          </div>
        </div>
      )}

      {/* Error */}
      {error && (
        <div style={{ color: "var(--aa-copper)", fontFamily: "var(--aa-mono)", fontSize: "12px" }}>
          ⚠ {error}
        </div>
      )}

      {/* Loading */}
      {loading && jobs.length === 0 && runs.length === 0 && (
        <Spinner label="Loading forge data…" size="small" />
      )}

      {/* Active jobs */}
      {activeJobs.length > 0 && (
        <>
          <div className={s.sectionHeader}>Active Jobs ({activeJobs.length})</div>
          <div className={s.list}>
            {activeJobs.map((job) => {
              const badge = jobStatusBadge(job.status);
              return (
                <div
                  key={job.jobId}
                  className={s.row}
                  onClick={() => handleSelectJob(job)}
                  role="button"
                  tabIndex={0}
                  onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") handleSelectJob(job); }}
                  style={!job.runId ? { cursor: "default", opacity: 0.7 } : undefined}
                >
                  <V3Badge color={badge.color}>{badge.label}</V3Badge>
                  <span className={s.rowTitle}>{job.taskTitle || job.taskId}</span>
                  <div className={s.rowMeta}>
                    {job.startedAt && (
                      <span className={s.rowMetaText}>Started {formatTimestamp(job.startedAt, false)}</span>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        </>
      )}

      {/* Completed runs */}
      {status?.enabled && (
        <>
          <div className={s.sectionHeader}>Runs ({runs.length})</div>
          {runs.length === 0 && !loading && (
            <EmptyState
              icon="🔥"
              title="No forge runs yet"
              detail="Start a run from the New Run button above. Completed runs and their artifacts appear here."
            />
          )}
          {runs.length > 0 && (
            <div className={s.list}>
              {runs.map((run) => {
                const badge = jobStatusBadge(run.outcome);
                const fidBadge = fidelityBadge(run.fidelityOutcome);
                return (
                  <div
                    key={run.runId}
                    className={mergeClasses(s.row, selectedRunId === run.runId && s.rowActive)}
                    onClick={() => handleSelectRun(run.runId)}
                    role="button"
                    tabIndex={0}
                    onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") handleSelectRun(run.runId); }}
                  >
                    <V3Badge color={badge.color}>{badge.label}</V3Badge>
                    {fidBadge && <V3Badge color={fidBadge.color}>{fidBadge.label}</V3Badge>}
                    <span className={s.rowTitle}>
                      {run.taskId} · {run.methodologyVersion}
                    </span>
                    <div className={s.rowMeta}>
                      <span className={s.rowMetaText}>{run.phaseCount} phases</span>
                      {run.pipelineCost != null && (
                        <span className={s.rowMetaText}>{formatCost(run.pipelineCost)}</span>
                      )}
                      <span className={s.rowMetaText}>{formatTimestamp(run.startedAt, false)}</span>
                    </div>
                  </div>
                );
              })}
            </div>
          )}
        </>
      )}
    </div>
  );
}
