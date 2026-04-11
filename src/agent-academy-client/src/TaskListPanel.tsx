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
  TaskListLtrRegular,
  ArrowSyncRegular,
  CheckmarkCircleRegular,
  ErrorCircleRegular,
  ClockRegular,
  ChevronDownRegular,
  ChevronRightRegular,
  CheckmarkRegular,
  DismissRegular,
  EditRegular,
  MergeRegular,
  CommentRegular,
  FilterRegular,
  LinkRegular,
  ShieldCheckmarkRegular,
  PersonAddRegular,
} from "@fluentui/react-icons";
import EmptyState from "./EmptyState";
import ErrorState from "./ErrorState";
import SkeletonLoader from "./SkeletonLoader";
import { formatElapsed } from "./panelUtils";
import type {
  TaskSnapshot,
  TaskStatus,
  TaskSize,
  TaskComment,
  TaskCommentType,
  CommandExecutionResponse,
  SpecTaskLink,
  EvidenceRow,
  GateCheckResult,
  AgentDefinition,
} from "./api";
import { executeCommand, getTaskComments, getTaskSpecLinks, assignTask } from "./api";
import V3Badge from "./V3Badge";
import type { BadgeColor } from "./V3Badge";

// ── Filter definitions ──────────────────────────────────────────────────

type TaskFilter = "all" | "review" | "active" | "completed";

const FILTER_ITEMS: { value: TaskFilter; label: string; icon: React.ReactNode }[] = [
  { value: "all", label: "All", icon: <FilterRegular fontSize={14} /> },
  { value: "review", label: "Review Queue", icon: <ClockRegular fontSize={14} /> },
  { value: "active", label: "Active", icon: <ArrowSyncRegular fontSize={14} /> },
  { value: "completed", label: "Completed", icon: <CheckmarkCircleRegular fontSize={14} /> },
];

const REVIEW_STATUSES: TaskStatus[] = ["InReview", "AwaitingValidation", "Approved", "ChangesRequested"];
const ACTIVE_STATUSES: TaskStatus[] = ["Active", "Merging", "Blocked", "Queued"];
const COMPLETED_STATUSES: TaskStatus[] = ["Completed", "Cancelled"];

function filterTasks(tasks: TaskSnapshot[], filter: TaskFilter): TaskSnapshot[] {
  switch (filter) {
    case "review":
      return tasks.filter((t) => REVIEW_STATUSES.includes(t.status));
    case "active":
      return tasks.filter((t) => ACTIVE_STATUSES.includes(t.status));
    case "completed":
      return tasks.filter((t) => COMPLETED_STATUSES.includes(t.status));
    default:
      return tasks;
  }
}

function filterCount(tasks: TaskSnapshot[], filter: TaskFilter): number {
  return filterTasks(tasks, filter).length;
}

// ── Styles ──────────────────────────────────────────────────────────────

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    height: "100%",
    overflow: "auto",
    gap: "5px",
    ...shorthands.padding("14px", "20px"),
  },
  filterBar: {
    display: "flex",
    gap: "6px",
    flexWrap: "wrap",
    ...shorthands.padding("0", "2px"),
  },
  filterChip: {
    cursor: "pointer",
    display: "inline-flex",
    alignItems: "center",
    gap: "5px",
    ...shorthands.padding("6px", "12px"),
    ...shorthands.borderRadius("6px"),
    fontSize: "12px",
    fontWeight: 600,
    letterSpacing: "0.02em",
    color: "var(--aa-soft)",
    border: "1px solid var(--aa-border)",
    background: "transparent",
    transitionProperty: "background, border-color, color",
    transitionDuration: "0.15s",
    ":hover": {
      background: "var(--aa-border)",
    },
  },
  filterChipActive: {
    background: "rgba(91, 141, 239, 0.15)",
    ...shorthands.borderColor("rgba(91, 141, 239, 0.4)"),
    color: "var(--aa-text-strong)",
  },
  filterCount: {
    fontSize: "11px",
    fontWeight: 400,
    color: "var(--aa-muted)",
    marginLeft: "2px",
  },
  card: {
    display: "flex",
    flexDirection: "column",
    gap: "6px",
    border: "1px solid var(--aa-border)",
    background: "var(--aa-panel)",
    ...shorthands.borderRadius("8px"),
    ...shorthands.padding("10px", "12px"),
    cursor: "pointer",
    transitionProperty: "border-color",
    transitionDuration: "0.15s",
    ":hover": {
      ...shorthands.borderColor("var(--aa-border-strong)"),
    },
  },
  cardExpanded: {
    ...shorthands.borderColor("rgba(91, 141, 239, 0.3)"),
    cursor: "default",
  },
  cardHeader: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
  },
  cardTitle: {
    fontSize: "13px",
    fontWeight: 600,
    color: "var(--aa-text-strong)",
    flex: 1,
    display: "flex",
    alignItems: "center",
    gap: "6px",
    cursor: "pointer",
  },
  meta: {
    display: "flex",
    flexWrap: "wrap",
    alignItems: "center",
    gap: "10px",
    fontFamily: "var(--mono)",
    fontSize: "10px",
    color: "var(--aa-soft)",
  },
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
  empty: {
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    justifyContent: "center",
    height: "100%",
    gap: "12px",
    color: "var(--aa-soft)",
  },
  emptyIcon: { fontSize: "26px" },
});

// ── Helpers ─────────────────────────────────────────────────────────────

function statusBadgeColor(status: TaskStatus): BadgeColor {
  switch (status) {
    case "Active": case "Merging":       return "active";
    case "InReview":                     return "review";
    case "AwaitingValidation":           return "warn";
    case "Approved":                     return "ok";
    case "ChangesRequested":             return "warn";
    case "Blocked":                      return "err";
    case "Completed":                    return "done";
    case "Cancelled":                    return "cancel";
    case "Queued": default:              return "info";
  }
}

function typeBadgeColor(type: string): BadgeColor {
  switch (type) {
    case "Bug":     return "bug";
    case "Feature": return "feat";
    default:        return "muted";
  }
}

function sizeBadgeColor(size: TaskSize): BadgeColor {
  switch (size) {
    case "XS": case "S": return "muted";
    case "M":            return "info";
    case "L": case "XL": return "warn";
  }
}

function commentTypeBadge(type: TaskCommentType): BadgeColor {
  switch (type) {
    case "Finding":  return "warn";
    case "Blocker":  return "err";
    case "Evidence": return "ok";
    case "Comment": default: return "muted";
  }
}

function specLinkBadge(type: string): BadgeColor {
  switch (type) {
    case "Implements": return "ok";
    case "Modifies":   return "warn";
    case "Fixes":      return "err";
    case "References": return "info";
    default:           return "muted";
  }
}

function evidencePhaseBadge(phase: string): BadgeColor {
  switch (phase) {
    case "Baseline": return "info";
    case "After":    return "ok";
    case "Review":   return "review";
    default:         return "muted";
  }
}

function formatTime(iso: string): string {
  const d = new Date(iso);
  const now = new Date();
  const diffMs = now.getTime() - d.getTime();
  if (diffMs < 60_000) return "just now";
  if (diffMs < 3_600_000) return `${Math.floor(diffMs / 60_000)}m ago`;
  if (diffMs < 86_400_000) return `${Math.floor(diffMs / 3_600_000)}h ago`;
  return d.toLocaleDateString();
}

// Actions available per task status
type TaskAction = "approve" | "requestChanges" | "reject" | "merge";

function getAvailableActions(status: TaskStatus): TaskAction[] {
  switch (status) {
    case "InReview":
    case "AwaitingValidation":
      return ["approve", "requestChanges"];
    case "Approved":
      return ["merge", "reject"];
    case "Completed":
      return ["reject"];
    default:
      return [];
  }
}

const ACTION_META: Record<TaskAction, { label: string; icon: React.ReactElement; appearance: "primary" | "subtle" | "outline"; command: string; needsReason: boolean }> = {
  approve:        { label: "Approve",         icon: <CheckmarkRegular />, appearance: "primary", command: "APPROVE_TASK",     needsReason: false },
  requestChanges: { label: "Request Changes", icon: <EditRegular />,     appearance: "outline", command: "REQUEST_CHANGES",  needsReason: true },
  reject:         { label: "Reject",          icon: <DismissRegular />,  appearance: "subtle",  command: "REJECT_TASK",      needsReason: true },
  merge:          { label: "Merge",           icon: <MergeRegular />,    appearance: "primary", command: "MERGE_TASK",        needsReason: false },
};

// ── Task detail / expanded card ─────────────────────────────────────────

interface DetailCacheEntry {
  updatedAt: string;
  specLinks?: SpecTaskLink[];
  evidence?: EvidenceRow[];
  gate?: GateCheckResult;
  comments?: TaskComment[];
}

const CACHE_MAX_SIZE = 50;

// Bounded LRU cache for task detail data
const detailCache = new Map<string, DetailCacheEntry>();

function getCached(taskId: string, updatedAt: string): DetailCacheEntry {
  const c = detailCache.get(taskId);
  if (c && c.updatedAt === updatedAt) {
    // Move to end for LRU ordering
    detailCache.delete(taskId);
    detailCache.set(taskId, c);
    return c;
  }
  // Evict oldest entries if at capacity
  if (detailCache.size >= CACHE_MAX_SIZE) {
    const oldest = detailCache.keys().next().value;
    if (oldest !== undefined) detailCache.delete(oldest);
  }
  const fresh: DetailCacheEntry = { updatedAt };
  detailCache.set(taskId, fresh);
  return fresh;
}

interface TaskDetailProps {
  task: TaskSnapshot;
  agents: AgentDefinition[];
  onRefresh: () => void;
}

function TaskDetail({ task, agents, onRefresh }: TaskDetailProps) {
  const s = useLocalStyles();
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
  // Request versioning: ignore stale responses when task changes
  const fetchVersionRef = useRef(0);

  useEffect(() => {
    mountedRef.current = true;
    return () => { mountedRef.current = false; };
  }, []);

  // Bump version on task identity change
  useEffect(() => {
    fetchVersionRef.current += 1;
  }, [task.id, task.updatedAt]);

  // Fetch comments on expand (cached)
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

  // Fetch spec links on expand (cached)
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

  // Fetch evidence on demand (user clicks "Load")
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

  // Check gates on demand
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

  // Gate-relevant statuses — user can check gates for these
  const canCheckGates = ["Active", "AwaitingValidation", "InReview"].includes(task.status);
  // Can assign — only Queued or unassigned tasks
  const canAssign = task.status === "Queued" && !task.assignedAgentId;

  return (
    <div className={s.expandedSection}>
      {/* Description */}
      {task.description && (
        <>
          <div className={s.sectionLabel}>Description</div>
          <div className={s.descriptionText}>{task.description}</div>
        </>
      )}

      {/* Success criteria */}
      {task.successCriteria && (
        <>
          <div className={s.sectionLabel}>Success Criteria</div>
          <div className={s.descriptionText}>{task.successCriteria}</div>
        </>
      )}

      {/* Review metadata */}
      {(task.reviewRounds != null && task.reviewRounds > 0) && (
        <div className={s.reviewMeta}>
          <span>Review round {task.reviewRounds}</span>
          {task.reviewerAgentId && <span>Reviewer: {task.reviewerAgentId}</span>}
          {task.mergeCommitSha && <span>Merge: {task.mergeCommitSha.slice(0, 8)}</span>}
        </div>
      )}

      {/* Implementation / Validation summaries */}
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

      {/* Tests created */}
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

      {/* Assign task (for Queued tasks without assignee) */}
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

      {/* Reason input for request changes / reject */}
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

// ── Main Component ──────────────────────────────────────────────────────

interface TaskListPanelProps {
  tasks: TaskSnapshot[];
  loading?: boolean;
  error?: boolean;
  onRefresh?: () => void;
  activeSprintId?: string | null;
  agents?: AgentDefinition[];
}

export default function TaskListPanel({ tasks, loading, error, onRefresh, activeSprintId, agents = [] }: TaskListPanelProps) {
  const s = useLocalStyles();
  const [filter, setFilter] = useState<TaskFilter>("all");
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [sprintOnly, setSprintOnly] = useState(false);

  const baseTasks = sprintOnly && activeSprintId
    ? tasks.filter((t) => t.sprintId === activeSprintId)
    : tasks;

  const sortedTasks = [...filterTasks(baseTasks, filter)].sort(
    (a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime(),
  );

  const handleRefresh = useCallback(() => {
    onRefresh?.();
  }, [onRefresh]);

  if (loading) {
    return <SkeletonLoader rows={5} variant="list" />;
  }

  if (error) {
    return (
      <ErrorState
        message="Failed to load tasks"
        detail="Could not retrieve the task list. Check your connection and try again."
        onRetry={onRefresh}
      />
    );
  }

  if (tasks.length === 0) {
    return (
      <EmptyState
        icon={<TaskListLtrRegular />}
        title="No tasks assigned"
        detail="Tasks will appear here when work is submitted to the team."
      />
    );
  }

  return (
    <div className={s.root}>
      {/* Filter bar */}
      <div className={s.filterBar}>
        {FILTER_ITEMS.map((f) => {
          const count = filterCount(baseTasks, f.value);
          return (
            <button
              key={f.value}
              className={mergeClasses(s.filterChip, filter === f.value && s.filterChipActive)}
              onClick={() => setFilter(f.value)}
            >
              {f.icon}
              {f.label}
              <span className={s.filterCount}>{count}</span>
            </button>
          );
        })}
        {activeSprintId && (
          <>
            <span style={{ borderLeft: "1px solid #333", height: "16px", margin: "0 4px" }} />
            <button
              className={mergeClasses(s.filterChip, sprintOnly && s.filterChipActive)}
              onClick={() => setSprintOnly((v) => !v)}
            >
              🏃 Sprint
            </button>
          </>
        )}
      </div>

      {/* Task list */}
      {sortedTasks.length === 0 && (
        <div className={s.empty} style={{ height: "auto", padding: "40px 0" }}>
          <span style={{ fontSize: "13px" }}>No tasks match this filter</span>
        </div>
      )}

      {sortedTasks.map((task) => {
        const isExpanded = expandedId === task.id;
        return (
          <div
            key={task.id}
            className={mergeClasses(s.card, isExpanded && s.cardExpanded)}
            onClick={() => !isExpanded && setExpandedId(task.id)}
          >
            <div className={s.cardHeader}>
              <span
                className={s.cardTitle}
                onClick={(e) => {
                  e.stopPropagation();
                  setExpandedId(isExpanded ? null : task.id);
                }}
              >
                {isExpanded ? <ChevronDownRegular fontSize={12} /> : <ChevronRightRegular fontSize={12} />}
                {task.title}
              </span>
              {task.size && (
                <V3Badge color={sizeBadgeColor(task.size)}>{task.size}</V3Badge>
              )}
              <V3Badge color={statusBadgeColor(task.status)}>{task.status}</V3Badge>
              {task.type && task.type !== "Feature" && (
                <V3Badge color={typeBadgeColor(task.type)}>{task.type}</V3Badge>
              )}
            </div>
            <div className={s.meta}>
              {task.assignedAgentName && (
                <span>👤 {task.assignedAgentName}</span>
              )}
              {task.branchName && (
                <span>🌿 {task.branchName}</span>
              )}
              {(task.commitCount ?? 0) > 0 && (
                <span>{task.commitCount} commit{task.commitCount !== 1 ? "s" : ""}</span>
              )}
              {task.reviewRounds != null && task.reviewRounds > 0 && (
                <span>Round {task.reviewRounds}</span>
              )}
              {task.startedAt && (
                <span>{formatElapsed(task.startedAt, task.completedAt, { maxUnit: "days" })}</span>
              )}
              {task.commentCount != null && task.commentCount > 0 && (
                <span>💬 {task.commentCount}</span>
              )}
              {task.usedFleet && (
                <V3Badge color="info">Fleet</V3Badge>
              )}
            </div>

            {/* Expanded detail */}
            {isExpanded && <TaskDetail task={task} agents={agents} onRefresh={handleRefresh} />}
          </div>
        );
      })}
    </div>
  );
}
