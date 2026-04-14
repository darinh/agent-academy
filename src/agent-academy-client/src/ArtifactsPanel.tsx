import { useEffect, useState, useCallback } from "react";
import {
  Button,
  Spinner,
  makeStyles,
  shorthands,
  Tooltip,
} from "@fluentui/react-components";
import { ArrowSyncRegular } from "@fluentui/react-icons";
import EmptyState from "./EmptyState";
import { getRoomArtifacts, getRoomEvaluations } from "./api";
import type { ArtifactRecord, EvaluationResult, RoomEvaluationResponse } from "./api";

// ── Styles ──

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    height: "100%",
    overflowY: "auto",
    overflowX: "hidden",
    gap: "16px",
    ...shorthands.padding("14px", "20px"),
  },
  header: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    gap: "12px",
  },
  title: {
    fontSize: "15px",
    fontWeight: 600,
    color: "var(--aa-text)",
    fontFamily: "var(--mono)",
    letterSpacing: "0.02em",
  },
  scoreBadge: {
    display: "inline-flex",
    alignItems: "center",
    gap: "6px",
    ...shorthands.padding("4px", "12px"),
    ...shorthands.borderRadius("16px"),
    fontSize: "13px",
    fontWeight: 700,
    fontFamily: "var(--mono)",
  },
  scoreGreen: { backgroundColor: "rgba(16, 185, 129, 0.15)", color: "#10b981" },
  scoreYellow: { backgroundColor: "rgba(245, 158, 11, 0.15)", color: "#f59e0b" },
  scoreRed: { backgroundColor: "rgba(239, 68, 68, 0.15)", color: "#ef4444" },
  section: {
    display: "flex",
    flexDirection: "column",
    gap: "8px",
  },
  sectionHeader: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    color: "var(--aa-soft)",
    fontFamily: "var(--mono)",
    fontSize: "10px",
    fontWeight: 600,
    letterSpacing: "0.04em",
    textTransform: "uppercase",
  },
  evalGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fill, minmax(280px, 1fr))",
    gap: "10px",
  },
  evalCard: {
    border: "1px solid var(--aa-border)",
    background: "var(--aa-panel)",
    ...shorthands.borderRadius("8px"),
    ...shorthands.padding("12px", "14px"),
    display: "flex",
    flexDirection: "column",
    gap: "8px",
  },
  evalFilePath: {
    fontSize: "12px",
    fontFamily: "var(--mono)",
    color: "var(--aa-text)",
    fontWeight: 600,
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap",
  },
  evalScoreRow: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
  },
  scoreBar: {
    flex: 1,
    height: "6px",
    backgroundColor: "rgba(255, 255, 255, 0.08)",
    ...shorthands.borderRadius("3px"),
    overflow: "hidden",
  },
  scoreBarFill: {
    height: "100%",
    ...shorthands.borderRadius("3px"),
    transitionProperty: "width",
    transitionDuration: "0.3s",
  },
  scoreLabel: {
    fontSize: "12px",
    fontWeight: 700,
    fontFamily: "var(--mono)",
    minWidth: "32px",
    textAlign: "right",
  },
  checks: {
    display: "flex",
    gap: "12px",
    flexWrap: "wrap",
    fontSize: "11px",
    fontFamily: "var(--mono)",
  },
  checkPass: { color: "#10b981" },
  checkFail: { color: "#ef4444" },
  issues: {
    fontSize: "11px",
    color: "var(--aa-muted)",
    ...shorthands.margin("4px", "0", "0"),
    listStyleType: "disc",
    ...shorthands.padding("0", "0", "0", "16px"),
  },
  logTable: {
    width: "100%",
    borderCollapse: "collapse",
    fontSize: "12px",
    fontFamily: "var(--mono)",
  },
  logTh: {
    textAlign: "left",
    ...shorthands.padding("6px", "10px"),
    borderBottom: "1px solid var(--aa-border)",
    color: "var(--aa-soft)",
    fontWeight: 600,
    fontSize: "10px",
    letterSpacing: "0.04em",
    textTransform: "uppercase",
  },
  logTd: {
    ...shorthands.padding("5px", "10px"),
    borderBottom: "1px solid rgba(255, 255, 255, 0.04)",
    color: "var(--aa-text)",
  },
  opCreated: { color: "#10b981" },
  opUpdated: { color: "#3b82f6" },
  opCommitted: { color: "#8b5cf6" },
  opDeleted: { color: "#ef4444" },
  loadingRow: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    color: "var(--aa-muted)",
    fontSize: "12px",
  },
  error: {
    color: "#ef4444",
    fontSize: "12px",
  },
  collapsible: {
    cursor: "pointer",
    userSelect: "none",
  },
});

// ── Helpers ──

function scoreClass(s: ReturnType<typeof useLocalStyles>, score: number): string {
  if (score >= 80) return s.scoreGreen;
  if (score >= 50) return s.scoreYellow;
  return s.scoreRed;
}

function scoreColor(score: number): string {
  if (score >= 80) return "#10b981";
  if (score >= 50) return "#f59e0b";
  return "#ef4444";
}

function opClass(s: ReturnType<typeof useLocalStyles>, op: string): string {
  switch (op) {
    case "Created": return s.opCreated;
    case "Updated": return s.opUpdated;
    case "Committed": return s.opCommitted;
    case "Deleted": return s.opDeleted;
    default: return "";
  }
}

function formatTime(iso: string): string {
  try {
    const d = new Date(iso);
    return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
  } catch {
    return iso;
  }
}

// ── Component ──

interface ArtifactsPanelProps {
  roomId: string | null;
  refreshTrigger?: number;
}

export default function ArtifactsPanel({ roomId, refreshTrigger }: ArtifactsPanelProps) {
  const s = useLocalStyles();

  const [artifacts, setArtifacts] = useState<ArtifactRecord[]>([]);
  const [artifactsLoading, setArtifactsLoading] = useState(false);
  const [artifactsError, setArtifactsError] = useState<string | null>(null);

  const [evaluation, setEvaluation] = useState<RoomEvaluationResponse | null>(null);
  const [evalLoading, setEvalLoading] = useState(false);
  const [evalError, setEvalError] = useState<string | null>(null);

  const [logExpanded, setLogExpanded] = useState(true);

  const fetchArtifacts = useCallback(async (rid: string) => {
    setArtifactsLoading(true);
    setArtifactsError(null);
    try {
      const data = await getRoomArtifacts(rid);
      setArtifacts(data);
    } catch (err) {
      setArtifactsError(err instanceof Error ? err.message : "Failed to load artifacts");
    } finally {
      setArtifactsLoading(false);
    }
  }, []);

  const fetchEvaluations = useCallback(async (rid: string) => {
    setEvalLoading(true);
    setEvalError(null);
    try {
      const data = await getRoomEvaluations(rid);
      setEvaluation(data);
    } catch (err) {
      setEvalError(err instanceof Error ? err.message : "Failed to evaluate artifacts");
    } finally {
      setEvalLoading(false);
    }
  }, []);

  useEffect(() => {
    if (!roomId) return;
    let cancelled = false;

    setArtifactsLoading(true);
    setArtifactsError(null);
    getRoomArtifacts(roomId)
      .then((data) => { if (!cancelled) setArtifacts(data); })
      .catch((err) => { if (!cancelled) setArtifactsError(err instanceof Error ? err.message : "Failed to load artifacts"); })
      .finally(() => { if (!cancelled) setArtifactsLoading(false); });

    setEvalLoading(true);
    setEvalError(null);
    getRoomEvaluations(roomId)
      .then((data) => { if (!cancelled) setEvaluation(data); })
      .catch((err) => { if (!cancelled) setEvalError(err instanceof Error ? err.message : "Failed to evaluate artifacts"); })
      .finally(() => { if (!cancelled) setEvalLoading(false); });

    return () => { cancelled = true; };
  }, [roomId, refreshTrigger]);

  const handleRefresh = useCallback(() => {
    if (!roomId) return;
    fetchArtifacts(roomId);
    fetchEvaluations(roomId);
  }, [roomId, fetchArtifacts, fetchEvaluations]);

  if (!roomId) {
    return <EmptyState icon="📦" title="No room selected" detail="Select a room to view its artifacts." />;
  }

  const noData = !artifactsLoading && !evalLoading && artifacts.length === 0
    && (!evaluation || evaluation.artifacts.length === 0);

  if (noData && !artifactsError && !evalError) {
    return (
      <EmptyState
        icon="📦"
        title="No artifacts yet"
        detail="Artifacts will appear here as agents create and modify files in this room."
        action={{ label: "Refresh", onClick: handleRefresh }}
      />
    );
  }

  return (
    <div className={s.root}>
      {/* Header */}
      <div className={s.header}>
        <span className={s.title}>Artifacts</span>
        <div style={{ display: "flex", alignItems: "center", gap: "10px" }}>
          {evaluation && (
            <Tooltip content={`Aggregate quality: ${evaluation.aggregateScore.toFixed(0)}%`} relationship="description">
              <span className={`${s.scoreBadge} ${scoreClass(s, evaluation.aggregateScore)}`}>
                {evaluation.aggregateScore.toFixed(0)}%
              </span>
            </Tooltip>
          )}
          <Button
            appearance="subtle"
            icon={<ArrowSyncRegular />}
            size="small"
            onClick={handleRefresh}
            disabled={artifactsLoading && evalLoading}
          >
            Refresh
          </Button>
        </div>
      </div>

      {/* Evaluations Section */}
      <div className={s.section}>
        <div className={s.sectionHeader}>
          <span>Quality Evaluations</span>
          {evaluation && <span>{evaluation.artifacts.length} files</span>}
        </div>

        {evalLoading && (
          <div className={s.loadingRow}>
            <Spinner size="tiny" />
            <span>Evaluating files on disk…</span>
          </div>
        )}

        {evalError && <div className={s.error}>{evalError}</div>}

        {evaluation && evaluation.artifacts.length === 0 && !evalLoading && (
          <div style={{ color: "var(--aa-muted)", fontSize: "12px" }}>
            No evaluable files found. Deleted files are excluded.
          </div>
        )}

        {evaluation && evaluation.artifacts.length > 0 && (
          <div className={s.evalGrid}>
            {evaluation.artifacts.map((ev) => (
              <EvalCard key={ev.filePath} ev={ev} s={s} />
            ))}
          </div>
        )}
      </div>

      {/* File Operations Log */}
      <div className={s.section}>
        <div
          className={`${s.sectionHeader} ${s.collapsible}`}
          onClick={() => setLogExpanded((v) => !v)}
          role="button"
          tabIndex={0}
          onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") setLogExpanded((v) => !v); }}
        >
          <span>{logExpanded ? "▾" : "▸"} Recent File Operations</span>
          <span>{artifacts.length} events</span>
        </div>

        {artifactsLoading && (
          <div className={s.loadingRow}>
            <Spinner size="tiny" />
            <span>Loading…</span>
          </div>
        )}

        {artifactsError && <div className={s.error}>{artifactsError}</div>}

        {logExpanded && !artifactsLoading && artifacts.length > 0 && (
          <table className={s.logTable}>
            <thead>
              <tr>
                <th className={s.logTh}>Time</th>
                <th className={s.logTh}>Agent</th>
                <th className={s.logTh}>Operation</th>
                <th className={s.logTh}>File</th>
              </tr>
            </thead>
            <tbody>
              {artifacts.map((a, i) => (
                <tr key={`${a.filePath}-${a.timestamp}-${i}`}>
                  <td className={s.logTd}>{formatTime(a.timestamp)}</td>
                  <td className={s.logTd}>{a.agentId}</td>
                  <td className={`${s.logTd} ${opClass(s, a.operation)}`}>{a.operation}</td>
                  <td className={s.logTd} title={a.filePath}>
                    {a.filePath.length > 50 ? `…${a.filePath.slice(-47)}` : a.filePath}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}

// ── Sub-components ──

function EvalCard({ ev, s }: { ev: EvaluationResult; s: ReturnType<typeof useLocalStyles> }) {
  const check = (label: string, passed: boolean) => (
    <span className={passed ? s.checkPass : s.checkFail}>
      {passed ? "✓" : "✗"} {label}
    </span>
  );

  return (
    <div className={s.evalCard}>
      <div className={s.evalFilePath} title={ev.filePath}>{ev.filePath}</div>
      <div className={s.evalScoreRow}>
        <div className={s.scoreBar}>
          <div
            className={s.scoreBarFill}
            style={{ width: `${ev.score}%`, backgroundColor: scoreColor(ev.score) }}
          />
        </div>
        <span className={s.scoreLabel} style={{ color: scoreColor(ev.score) }}>
          {ev.score.toFixed(0)}
        </span>
      </div>
      <div className={s.checks}>
        {check("Exists", ev.exists)}
        {check("Content", ev.nonEmpty)}
        {check("Syntax", ev.syntaxValid)}
        {check("Complete", ev.complete)}
      </div>
      {ev.issues.length > 0 && (
        <ul className={s.issues}>
          {ev.issues.map((issue, i) => <li key={i}>{issue}</li>)}
        </ul>
      )}
    </div>
  );
}
