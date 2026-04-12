import { useState, useEffect, useCallback, useRef } from "react";
import {
  Button,
  makeStyles,
  mergeClasses,
  shorthands,
  Spinner,
  Textarea,
} from "@fluentui/react-components";
import {
  CheckmarkCircleRegular,
  ErrorCircleRegular,
  ShieldCheckmarkRegular,
  CommentRegular,
  LinkRegular,
  PersonAddRegular,
} from "@fluentui/react-icons";
import type {
  TaskSnapshot,
  TaskComment,
  CommandExecutionResponse,
  SpecTaskLink,
  EvidenceRow,
  GateCheckResult,
  AgentDefinition,
} from "../api";
import { executeCommand, getTaskComments, getTaskSpecLinks, assignTask } from "../api";
import V3Badge from "../V3Badge";
import {
  type TaskAction,
  ACTION_META,
  getAvailableActions,
  commentTypeBadge,
  specLinkBadge,
  evidencePhaseBadge,
  formatTime,
  getCached,
} from "./taskListHelpers";

const useStyles = makeStyles({
  expandedSection: {
    marginTop: "14px",
    ...shorthands.padding("12px", "0", "0"),
    borderTop: "1px solid var(--aa-border)",
  },
  descriptionText: {
    fontSize: "13px",
    color: "var(--aa-soft)",
    lineHeight: 1.6,
    whiteSpace: "pre-wrap",
    marginBottom: "12px",
  },
  sectionLabel: {
    fontSize: "11px",
    fontWeight: 600,
    color: "var(--aa-muted)",
    textTransform: "uppercase",
    letterSpacing: "0.04em",
    marginBottom: "6px",
    marginTop: "12px",
  },
  actionBar: {
    display: "flex",
    gap: "8px",
    flexWrap: "wrap",
    marginTop: "14px",
    ...shorthands.padding("10px", "0", "0"),
    borderTop: "1px solid var(--aa-border)",
  },
  actionFeedback: {
    display: "flex",
    alignItems: "center",
    gap: "6px",
    fontSize: "12px",
    color: "var(--aa-soft)",
    marginTop: "8px",
  },
  actionError: {
    color: "var(--error)",
  },
  actionSuccess: {
    color: "var(--aa-lime)",
  },
  reasonArea: {
    marginTop: "10px",
    display: "flex",
    flexDirection: "column",
    gap: "8px",
  },
  reasonActions: {
    display: "flex",
    gap: "6px",
    justifyContent: "flex-end",
  },
  commentsSection: {
    marginTop: "14px",
    ...shorthands.padding("10px", "0", "0"),
    borderTop: "1px solid var(--aa-border)",
  },
  commentCard: {
    ...shorthands.padding("8px", "10px"),
    marginBottom: "6px",
    ...shorthands.borderRadius("6px"),
    background: "rgba(255, 255, 255, 0.02)",
    border: "1px solid var(--aa-border)",
  },
  commentHeader: {
    display: "flex",
    alignItems: "center",
    gap: "6px",
    marginBottom: "4px",
    fontSize: "12px",
  },
  commentAuthor: {
    fontWeight: 600,
    color: "var(--aa-text-strong)",
  },
  commentTime: {
    color: "var(--aa-muted)",
    fontFamily: "var(--mono)",
    fontSize: "10px",
  },
  commentContent: {
    fontSize: "13px",
    color: "var(--aa-soft)",
    lineHeight: 1.5,
    whiteSpace: "pre-wrap",
  },
  reviewMeta: {
    display: "flex",
    gap: "12px",
    flexWrap: "wrap",
    marginTop: "8px",
    fontFamily: "var(--mono)",
    fontSize: "10px",
    color: "var(--aa-muted)",
  },
  specLinkRow: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    ...shorthands.padding("6px", "8px"),
    ...shorthands.borderRadius("4px"),
    background: "rgba(255, 255, 255, 0.02)",
    marginBottom: "4px",
    fontSize: "12px",
  },
  specLinkSection: {
    fontFamily: "var(--mono)",
    fontWeight: 600,
    color: "var(--aa-text-strong)",
    fontSize: "12px",
  },
  specLinkNote: {
    fontSize: "11px",
    color: "var(--aa-muted)",
    marginLeft: "auto",
  },
  evidenceTable: {
    width: "100%",
    borderCollapse: "collapse",
    fontSize: "11px",
    fontFamily: "var(--mono)",
    marginTop: "6px",
  },
  evidenceTh: {
    textAlign: "left",
    ...shorthands.padding("4px", "8px"),
    borderBottom: "1px solid var(--aa-border)",
    color: "var(--aa-muted)",
    fontWeight: 600,
    fontSize: "10px",
    textTransform: "uppercase",
    letterSpacing: "0.04em",
  },
  evidenceTd: {
    ...shorthands.padding("4px", "8px"),
    borderBottom: "1px solid var(--aa-hairline)",
    color: "var(--aa-soft)",
    fontSize: "11px",
  },
  gateBox: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    ...shorthands.padding("8px", "10px"),
    ...shorthands.borderRadius("6px"),
    border: "1px solid var(--aa-border)",
    background: "rgba(255, 255, 255, 0.02)",
    marginTop: "6px",
    fontSize: "12px",
  },
  gateMetBorder: {
    ...shorthands.borderColor("rgba(76, 175, 80, 0.3)"),
  },
  gateNotMetBorder: {
    ...shorthands.borderColor("rgba(255, 152, 0, 0.3)"),
  },
  assignPicker: {
    display: "flex",
    flexWrap: "wrap",
    gap: "6px",
    marginTop: "8px",
  },
  assignPickerBtn: {
    cursor: "pointer",
    ...shorthands.padding("4px", "10px"),
    ...shorthands.borderRadius("4px"),
    fontSize: "12px",
    border: "1px solid var(--aa-border)",
    background: "transparent",
    color: "var(--aa-text)",
    transitionProperty: "background, border-color",
    transitionDuration: "0.15s",
    ":hover": {
      background: "var(--aa-border)",
    },
  },
});

interface TaskDetailProps {
  task: TaskSnapshot;
  agents: AgentDefinition[];
  onRefresh: () => void;
}

export default function TaskDetail({ task, agents, onRefresh }: TaskDetailProps) {
  const s = useStyles();
  const cached = getCached(task.id, task.updatedAt);

  const [comments, setComments] = useState<TaskComment[]>(cached.comments ?? []);
  const [commentsLoading, setCommentsLoading] = useState(!cached.comments);
  const [commentsError, setCommentsError] = useState(false);

  const [specLinks, setSpecLinks] = useState<SpecTaskLink[]>(cached.specLinks ?? []);
  const [specLinksLoading, setSpecLinksLoading] = useState(!cached.specLinks);

  const [evidence, setEvidence] = useState<EvidenceRow[]>(cached.evidence ?? []);
  const [evidenceLoading, setEvidenceLoading] = useState(false);
  const [evidenceLoaded, setEvidenceLoaded] = useState(!!cached.evidence);

  const [gate, setGate] = useState<GateCheckResult | null>(cached.gate ?? null);
  const [gateLoading, setGateLoading] = useState(false);

  const [actionPending, setActionPending] = useState<TaskAction | null>(null);
  const [actionResult, setActionResult] = useState<{ ok: boolean; message: string } | null>(null);
  const [reasonAction, setReasonAction] = useState<TaskAction | null>(null);
  const [reasonText, setReasonText] = useState("");
  const [showAssignPicker, setShowAssignPicker] = useState(false);
  const [assignPending, setAssignPending] = useState(false);
  const mountedRef = useRef(true);
  const fetchVersionRef = useRef(0);

  useEffect(() => {
    mountedRef.current = true;
    return () => { mountedRef.current = false; };
  }, []);

  useEffect(() => {
    fetchVersionRef.current += 1;
  }, [task.id, task.updatedAt]);

  const fetchComments = useCallback(() => {
    const version = fetchVersionRef.current;
    setCommentsLoading(true);
    setCommentsError(false);
    getTaskComments(task.id)
      .then((c) => {
        if (!mountedRef.current || fetchVersionRef.current !== version) return;
        setComments(c);
        const cache = getCached(task.id, task.updatedAt);
        cache.comments = c;
      })
      .catch(() => { if (mountedRef.current && fetchVersionRef.current === version) setCommentsError(true); })
      .finally(() => { if (mountedRef.current && fetchVersionRef.current === version) setCommentsLoading(false); });
  }, [task.id, task.updatedAt]);

  const fetchSpecLinks = useCallback(() => {
    const version = fetchVersionRef.current;
    setSpecLinksLoading(true);
    getTaskSpecLinks(task.id)
      .then((links) => {
        if (!mountedRef.current || fetchVersionRef.current !== version) return;
        setSpecLinks(links);
        const cache = getCached(task.id, task.updatedAt);
        cache.specLinks = links;
      })
      .catch(() => { if (mountedRef.current && fetchVersionRef.current === version) setSpecLinks([]); })
      .finally(() => { if (mountedRef.current && fetchVersionRef.current === version) setSpecLinksLoading(false); });
  }, [task.id, task.updatedAt]);

  useEffect(() => {
    if (!cached.comments) fetchComments();
    if (!cached.specLinks) fetchSpecLinks();
  }, [task.id, task.updatedAt]);

  const fetchEvidence = useCallback(() => {
    const version = fetchVersionRef.current;
    setEvidenceLoading(true);
    executeCommand({ command: "QUERY_EVIDENCE", args: { taskId: task.id } })
      .then((resp) => {
        if (!mountedRef.current || fetchVersionRef.current !== version) return;
        if (resp.status === "completed" && resp.result) {
          const result = resp.result as Record<string, unknown>;
          const items = (Array.isArray(result.evidence) ? result.evidence : []) as EvidenceRow[];
          setEvidence(items);
          const cache = getCached(task.id, task.updatedAt);
          cache.evidence = items;
        }
        setEvidenceLoaded(true);
      })
      .catch(() => { if (mountedRef.current && fetchVersionRef.current === version) setEvidenceLoaded(true); })
      .finally(() => { if (mountedRef.current && fetchVersionRef.current === version) setEvidenceLoading(false); });
  }, [task.id, task.updatedAt]);

  const checkGates = useCallback(() => {
    const version = fetchVersionRef.current;
    setGateLoading(true);
    executeCommand({ command: "CHECK_GATES", args: { taskId: task.id } })
      .then((resp) => {
        if (!mountedRef.current || fetchVersionRef.current !== version) return;
        if (resp.status === "completed" && resp.result) {
          const result = resp.result as Record<string, unknown>;
          const gate: GateCheckResult = {
            taskId: (result.taskId as string) ?? task.id,
            currentPhase: (result.currentPhase as string) ?? "",
            targetPhase: (result.targetPhase as string) ?? "",
            met: (result.met as boolean) ?? false,
            requiredChecks: (result.requiredChecks as number) ?? 0,
            passedChecks: (result.passedChecks as number) ?? 0,
            missingChecks: (Array.isArray(result.missingChecks) ? result.missingChecks : []) as string[],
            evidence: (Array.isArray(result.evidence) ? result.evidence : []) as GateCheckResult["evidence"],
            message: (result.message as string) ?? "",
          };
          setGate(gate);
          const cache = getCached(task.id, task.updatedAt);
          cache.gate = gate;
        }
      })
      .catch(() => {})
      .finally(() => { if (mountedRef.current && fetchVersionRef.current === version) setGateLoading(false); });
  }, [task.id, task.updatedAt]);

  const actions = getAvailableActions(task.status);

  const handleAction = useCallback(async (action: TaskAction) => {
    const meta = ACTION_META[action];

    if (meta.needsReason && (reasonAction !== action || !reasonText.trim())) {
      setReasonAction(action);
      if (reasonAction !== action) setReasonText("");
      return;
    }

    const args: Record<string, string> = { taskId: task.id };
    if (meta.needsReason && reasonText.trim()) {
      if (action === "requestChanges") args.findings = reasonText.trim();
      else args.reason = reasonText.trim();
    }

    setActionPending(action);
    setActionResult(null);
    try {
      const resp: CommandExecutionResponse = await executeCommand({ command: meta.command, args });
      if (!mountedRef.current) return;
      if (resp.status === "completed") {
        setActionResult({ ok: true, message: `${meta.label} successful` });
        setReasonAction(null);
        setReasonText("");
        onRefresh();
      } else if (resp.status === "denied") {
        setActionResult({ ok: false, message: resp.error ?? "Permission denied" });
      } else {
        setActionResult({ ok: false, message: resp.error ?? "Command failed" });
      }
    } catch (err) {
      if (!mountedRef.current) return;
      setActionResult({ ok: false, message: err instanceof Error ? err.message : "Request failed" });
    } finally {
      if (mountedRef.current) setActionPending(null);
    }
  }, [task.id, reasonAction, reasonText, onRefresh]);

  const cancelReason = useCallback(() => {
    setReasonAction(null);
    setReasonText("");
  }, []);

  const handleAssign = useCallback(async (agent: AgentDefinition) => {
    setAssignPending(true);
    setActionResult(null);
    try {
      await assignTask(task.id, agent.id, agent.name);
      if (!mountedRef.current) return;
      setActionResult({ ok: true, message: `Assigned to ${agent.name}` });
      setShowAssignPicker(false);
      onRefresh();
    } catch (err) {
      if (!mountedRef.current) return;
      setActionResult({ ok: false, message: err instanceof Error ? err.message : "Assignment failed" });
    } finally {
      if (mountedRef.current) setAssignPending(false);
    }
  }, [task.id, onRefresh]);

  const canCheckGates = ["Active", "AwaitingValidation", "InReview"].includes(task.status);
  const canAssign = task.status === "Queued" && !task.assignedAgentId;

  return (
    <div className={s.expandedSection}>
      {task.description && (
        <>
          <div className={s.sectionLabel}>Description</div>
          <div className={s.descriptionText}>{task.description}</div>
        </>
      )}

      {task.successCriteria && (
        <>
          <div className={s.sectionLabel}>Success Criteria</div>
          <div className={s.descriptionText}>{task.successCriteria}</div>
        </>
      )}

      {(task.reviewRounds != null && task.reviewRounds > 0) && (
        <div className={s.reviewMeta}>
          <span>Review round {task.reviewRounds}</span>
          {task.reviewerAgentId && <span>Reviewer: {task.reviewerAgentId}</span>}
          {task.mergeCommitSha && <span>Merge: {task.mergeCommitSha.slice(0, 8)}</span>}
        </div>
      )}

      {task.implementationSummary && (
        <>
          <div className={s.sectionLabel}>Implementation</div>
          <div className={s.descriptionText}>{task.implementationSummary}</div>
        </>
      )}
      {task.validationSummary && (
        <>
          <div className={s.sectionLabel}>Validation</div>
          <div className={s.descriptionText}>{task.validationSummary}</div>
        </>
      )}

      {task.testsCreated && task.testsCreated.length > 0 && (
        <>
          <div className={s.sectionLabel}>Tests Created</div>
          <div className={s.descriptionText}>{task.testsCreated.join("\n")}</div>
        </>
      )}

      {/* Spec links */}
      <div className={s.commentsSection}>
        <div className={s.sectionLabel}>
          <LinkRegular fontSize={13} style={{ marginRight: 4 }} />
          Spec Links {specLinks.length > 0 ? `(${specLinks.length})` : ""}
        </div>
        {specLinksLoading && <Spinner size="tiny" label="Loading spec links…" />}
        {!specLinksLoading && specLinks.length === 0 && (
          <div style={{ fontSize: "12px", color: "var(--aa-muted)", marginTop: "4px" }}>No spec links</div>
        )}
        {specLinks.map((link) => (
          <div key={link.id} className={s.specLinkRow}>
            <V3Badge color={specLinkBadge(link.linkType)}>{link.linkType}</V3Badge>
            <span className={s.specLinkSection}>{link.specSectionId}</span>
            <span style={{ fontSize: "11px", color: "var(--aa-muted)" }}>
              by {link.linkedByAgentName}
            </span>
            {link.note && <span className={s.specLinkNote} title={link.note}>{link.note}</span>}
          </div>
        ))}
      </div>

      {/* Evidence ledger */}
      <div className={s.commentsSection}>
        <div className={s.sectionLabel} style={{ display: "flex", alignItems: "center", gap: "8px" }}>
          <ShieldCheckmarkRegular fontSize={13} />
          Evidence Ledger
          {!evidenceLoaded && (
            <Button size="small" appearance="subtle" onClick={fetchEvidence} disabled={evidenceLoading}>
              {evidenceLoading ? <Spinner size="tiny" /> : "Load"}
            </Button>
          )}
        </div>
        {evidenceLoaded && evidence.length === 0 && (
          <div style={{ fontSize: "12px", color: "var(--aa-muted)", marginTop: "4px" }}>No evidence recorded</div>
        )}
        {evidence.length > 0 && (
          <table className={s.evidenceTable}>
            <thead>
              <tr>
                <th className={s.evidenceTh}>Phase</th>
                <th className={s.evidenceTh}>Check</th>
                <th className={s.evidenceTh}>Result</th>
                <th className={s.evidenceTh}>Tool</th>
                <th className={s.evidenceTh}>Agent</th>
              </tr>
            </thead>
            <tbody>
              {evidence.map((ev) => (
                <tr key={ev.id}>
                  <td className={s.evidenceTd}>
                    <V3Badge color={evidencePhaseBadge(ev.phase)}>{ev.phase}</V3Badge>
                  </td>
                  <td className={s.evidenceTd}>{ev.checkName}</td>
                  <td className={s.evidenceTd}>
                    <V3Badge color={ev.passed ? "ok" : "err"}>
                      {ev.passed ? "Pass" : "Fail"}
                    </V3Badge>
                  </td>
                  <td className={s.evidenceTd} title={ev.command ?? undefined}>{ev.tool}</td>
                  <td className={s.evidenceTd}>{ev.agentName}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {/* Gate status */}
      {canCheckGates && (
        <div className={s.commentsSection}>
          <div className={s.sectionLabel} style={{ display: "flex", alignItems: "center", gap: "8px" }}>
            <ShieldCheckmarkRegular fontSize={13} />
            Gate Status
            <Button size="small" appearance="subtle" onClick={checkGates} disabled={gateLoading}>
              {gateLoading ? <Spinner size="tiny" /> : gate ? "Recheck" : "Check Gates"}
            </Button>
          </div>
          {gate && (
            <div className={mergeClasses(s.gateBox, gate.met ? s.gateMetBorder : s.gateNotMetBorder)}>
              <V3Badge color={gate.met ? "ok" : "warn"}>
                {gate.met ? "Gate met" : `${gate.passedChecks}/${gate.requiredChecks} required`}
              </V3Badge>
              <span style={{ fontSize: "11px", color: "var(--aa-muted)" }}>
                {gate.currentPhase} → {gate.targetPhase}
              </span>
              {gate.missingChecks.length > 0 && (
                <span style={{ fontSize: "11px", color: "var(--aa-soft)" }}>
                  Missing: {gate.missingChecks.join(", ")}
                </span>
              )}
            </div>
          )}
        </div>
      )}

      {/* Comments */}
      <div className={s.commentsSection}>
        <div className={s.sectionLabel}>
          <CommentRegular fontSize={13} style={{ marginRight: 4 }} />
          Comments {task.commentCount != null && task.commentCount > 0 ? `(${task.commentCount})` : ""}
        </div>
        {commentsLoading && <Spinner size="tiny" label="Loading comments…" />}
        {!commentsLoading && commentsError && (
          <div style={{ fontSize: "12px", color: "var(--error)", marginTop: "4px", display: "flex", alignItems: "center", gap: "6px" }}>
            <ErrorCircleRegular fontSize={13} />
            Failed to load comments
            <Button size="small" appearance="subtle" onClick={fetchComments}>Retry</Button>
          </div>
        )}
        {!commentsLoading && !commentsError && comments.length === 0 && (
          <div style={{ fontSize: "12px", color: "var(--aa-muted)", marginTop: "4px" }}>No comments yet</div>
        )}
        {comments.map((c) => (
          <div key={c.id} className={s.commentCard}>
            <div className={s.commentHeader}>
              <span className={s.commentAuthor}>{c.agentName}</span>
              <V3Badge color={commentTypeBadge(c.commentType)}>
                {c.commentType}
              </V3Badge>
              <span className={s.commentTime}>{formatTime(c.createdAt)}</span>
            </div>
            <div className={s.commentContent}>{c.content}</div>
          </div>
        ))}
      </div>

      {/* Assign task */}
      {canAssign && (
        <div className={s.actionBar}>
          <Button
            size="small"
            appearance="primary"
            icon={<PersonAddRegular />}
            onClick={(e) => { e.stopPropagation(); setShowAssignPicker(!showAssignPicker); }}
            disabled={assignPending}
          >
            Assign Agent
          </Button>
          {showAssignPicker && (
            <div className={s.assignPicker}>
              {agents.map((agent) => (
                <button
                  key={agent.id}
                  className={s.assignPickerBtn}
                  onClick={(e) => { e.stopPropagation(); handleAssign(agent); }}
                  disabled={assignPending}
                >
                  {agent.name} ({agent.role})
                </button>
              ))}
            </div>
          )}
        </div>
      )}

      {/* Review actions */}
      {actions.length > 0 && (
        <div className={s.actionBar}>
          {actions.map((action) => {
            const meta = ACTION_META[action];
            return (
              <Button
                key={action}
                size="small"
                appearance={meta.appearance}
                icon={meta.icon}
                disabled={actionPending != null || (reasonAction != null && reasonAction !== action)}
                onClick={(e) => {
                  e.stopPropagation();
                  handleAction(action);
                }}
              >
                {actionPending === action ? <Spinner size="tiny" /> : meta.label}
              </Button>
            );
          })}
        </div>
      )}

      {/* Reason input */}
      {reasonAction && (
        <div className={s.reasonArea}>
          <Textarea
            placeholder={reasonAction === "requestChanges" ? "Describe the changes needed…" : "Reason for rejection…"}
            value={reasonText}
            onChange={(_, data) => setReasonText(data.value)}
            rows={3}
            style={{ fontSize: "13px" }}
          />
          <div className={s.reasonActions}>
            <Button size="small" appearance="subtle" onClick={cancelReason}>Cancel</Button>
            <Button
              size="small"
              appearance="primary"
              disabled={!reasonText.trim() || actionPending != null}
              onClick={() => handleAction(reasonAction)}
            >
              {actionPending ? <Spinner size="tiny" /> : `Submit ${ACTION_META[reasonAction].label}`}
            </Button>
          </div>
        </div>
      )}

      {/* Action feedback */}
      {actionResult && (
        <div className={mergeClasses(s.actionFeedback, actionResult.ok ? s.actionSuccess : s.actionError)}>
          {actionResult.ok ? <CheckmarkCircleRegular fontSize={14} /> : <ErrorCircleRegular fontSize={14} />}
          {actionResult.message}
        </div>
      )}
    </div>
  );
}
