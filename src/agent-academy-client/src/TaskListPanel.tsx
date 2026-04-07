import { useState, useEffect, useCallback, useRef } from "react";
import {
  Badge,
  Button,
  Card,
  makeStyles,
  mergeClasses,
  shorthands,
  Spinner,
  Textarea,
} from "@fluentui/react-components";
import {
  TaskListLtrRegular,
  PersonRegular,
  ArrowSyncRegular,
  CheckmarkCircleRegular,
  CircleRegular,
  ErrorCircleRegular,
  ClockRegular,
  BranchRegular,
  ChevronDownRegular,
  ChevronRightRegular,
  CheckmarkRegular,
  DismissRegular,
  EditRegular,
  MergeRegular,
  CommentRegular,
  FilterRegular,
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
} from "./api";
import { executeCommand, getTaskComments } from "./api";

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
    border: "1px solid var(--aa-border)",
    background:
      "var(--aa-panel)",
    boxShadow: "none",
    ...shorthands.borderRadius("6px"),
    ...shorthands.padding("10px", "12px"),
    cursor: "pointer",
    transitionProperty: "border-color, box-shadow",
    transitionDuration: "0.15s",
    ":hover": {
      ...shorthands.borderColor("var(--aa-border)"),
    },
  },
  cardExpanded: {
    ...shorthands.borderColor("rgba(91, 141, 239, 0.3)"),
    cursor: "default",
  },
  cardHeader: {
    display: "flex",
    alignItems: "flex-start",
    gap: "10px",
  },
  cardIcon: {
    flexShrink: 0,
    marginTop: "2px",
  },
  cardBody: {
    flex: 1,
    minWidth: 0,
  },
  cardTitleRow: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    flexWrap: "wrap",
  },
  cardTitle: {
    fontSize: "13px",
    fontWeight: 650,
    color: "var(--aa-text-strong)",
  },
  meta: {
    display: "flex",
    flexWrap: "wrap",
    alignItems: "center",
    gap: "8px",
    marginTop: "6px",
    fontSize: "12px",
    color: "var(--aa-muted)",
  },
  metaItem: {
    display: "flex",
    alignItems: "center",
    gap: "4px",
  },
  sizeBadge: {
    fontWeight: 700,
    fontSize: "11px",
    minWidth: "28px",
    textAlign: "center" as const,
  },
  expandedSection: {
    marginTop: "14px",
    ...shorthands.padding("12px", "0", "0"),
    borderTop: "1px solid rgba(91, 141, 239, 0.15)",
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
    ...shorthands.borderRadius("10px"),
    background: "rgba(255, 255, 255, 0.02)",
    border: "1px solid rgba(155, 176, 210, 0.08)",
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
    fontSize: "11px",
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
    fontSize: "12px",
    color: "var(--aa-muted)",
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

type BadgeColor = "informative" | "success" | "warning" | "important" | "danger" | "subtle";

function statusColor(status: TaskStatus): BadgeColor {
  switch (status) {
    case "Active": case "Merging":       return "success";
    case "InReview": case "Approved":    return "informative";
    case "AwaitingValidation":           return "warning";
    case "ChangesRequested":             return "warning";
    case "Blocked":                      return "danger";
    case "Completed":                    return "success";
    case "Cancelled":                    return "subtle";
    case "Queued": default:              return "important";
  }
}

function statusIcon(status: TaskStatus) {
  switch (status) {
    case "Active": case "Merging":       return <ArrowSyncRegular />;
    case "InReview": case "Approved":    return <ClockRegular />;
    case "AwaitingValidation":           return <ClockRegular />;
    case "ChangesRequested":             return <ErrorCircleRegular />;
    case "Completed":                    return <CheckmarkCircleRegular />;
    case "Blocked":                      return <ErrorCircleRegular />;
    default:                             return <CircleRegular />;
  }
}

function sizeColor(size: TaskSize): BadgeColor {
  switch (size) {
    case "XS": case "S": return "subtle";
    case "M":            return "informative";
    case "L": case "XL": return "warning";
  }
}

function commentTypeBadgeColor(type: TaskCommentType): BadgeColor {
  switch (type) {
    case "Finding":  return "warning";
    case "Blocker":  return "danger";
    case "Evidence": return "success";
    case "Comment": default: return "subtle";
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

interface TaskDetailProps {
  task: TaskSnapshot;
  onRefresh: () => void;
}

function TaskDetail({ task, onRefresh }: TaskDetailProps) {
  const s = useLocalStyles();
  const [comments, setComments] = useState<TaskComment[]>([]);
  const [commentsLoading, setCommentsLoading] = useState(false);
  const [commentsError, setCommentsError] = useState(false);
  const [actionPending, setActionPending] = useState<TaskAction | null>(null);
  const [actionResult, setActionResult] = useState<{ ok: boolean; message: string } | null>(null);
  const [reasonAction, setReasonAction] = useState<TaskAction | null>(null);
  const [reasonText, setReasonText] = useState("");
  const mountedRef = useRef(true);

  useEffect(() => {
    mountedRef.current = true;
    return () => { mountedRef.current = false; };
  }, []);

  // Fetch comments on expand and refetch when task changes
  const fetchComments = useCallback(() => {
    setCommentsLoading(true);
    setCommentsError(false);
    getTaskComments(task.id)
      .then((c) => { if (mountedRef.current) setComments(c); })
      .catch(() => { if (mountedRef.current) setCommentsError(true); })
      .finally(() => { if (mountedRef.current) setCommentsLoading(false); });
  }, [task.id]);

  useEffect(() => { fetchComments(); }, [task.id, task.updatedAt]);

  const actions = getAvailableActions(task.status);

  const handleAction = useCallback(async (action: TaskAction) => {
    const meta = ACTION_META[action];

    // For reason-required actions: open textarea if not already open for THIS action,
    // or if open but no text entered yet
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
              <Badge appearance="outline" color={commentTypeBadgeColor(c.commentType)} size="small">
                {c.commentType}
              </Badge>
              <span className={s.commentTime}>{formatTime(c.createdAt)}</span>
            </div>
            <div className={s.commentContent}>{c.content}</div>
          </div>
        ))}
      </div>

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
}

export default function TaskListPanel({ tasks, loading, error, onRefresh }: TaskListPanelProps) {
  const s = useLocalStyles();
  const [filter, setFilter] = useState<TaskFilter>("all");
  const [expandedId, setExpandedId] = useState<string | null>(null);

  const sortedTasks = [...filterTasks(tasks, filter)].sort(
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
          const count = filterCount(tasks, f.value);
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
          <Card
            key={task.id}
            className={mergeClasses(s.card, isExpanded && s.cardExpanded)}
            onClick={() => !isExpanded && setExpandedId(task.id)}
          >
            <div className={s.cardHeader}>
              <span className={s.cardIcon}>{statusIcon(task.status)}</span>
              <div className={s.cardBody}>
                <div className={s.cardTitleRow}>
                  <span
                    className={s.cardTitle}
                    style={{ cursor: "pointer" }}
                    onClick={(e) => {
                      e.stopPropagation();
                      setExpandedId(isExpanded ? null : task.id);
                    }}
                  >
                    {isExpanded ? <ChevronDownRegular fontSize={12} /> : <ChevronRightRegular fontSize={12} />}
                    {" "}
                    {task.title}
                  </span>
                  {task.size && (
                    <Badge className={s.sizeBadge} appearance="filled" color={sizeColor(task.size)} size="small">
                      {task.size}
                    </Badge>
                  )}
                  <Badge appearance="filled" color={statusColor(task.status)} size="small">
                    {task.status}
                  </Badge>
                  {task.type && task.type !== "Feature" && (
                    <Badge appearance="outline" color="subtle" size="small">{task.type}</Badge>
                  )}
                </div>
                <div className={s.meta}>
                  {task.assignedAgentName && (
                    <span className={s.metaItem}>
                      <PersonRegular fontSize={14} />
                      {task.assignedAgentName}
                    </span>
                  )}
                  {task.branchName && (
                    <span className={s.metaItem}>
                      <BranchRegular fontSize={14} />
                      {task.branchName}
                    </span>
                  )}
                  {(task.commitCount ?? 0) > 0 && (
                    <span className={s.metaItem}>
                      {task.commitCount} commit{task.commitCount !== 1 ? "s" : ""}
                    </span>
                  )}
                  {task.reviewRounds != null && task.reviewRounds > 0 && (
                    <span className={s.metaItem}>
                      Round {task.reviewRounds}
                    </span>
                  )}
                  {task.startedAt && (
                    <span className={s.metaItem}>
                      <ClockRegular fontSize={14} />
                      {formatElapsed(task.startedAt, task.completedAt, { maxUnit: "days" })}
                    </span>
                  )}
                  {task.commentCount != null && task.commentCount > 0 && (
                    <span className={s.metaItem}>
                      <CommentRegular fontSize={14} />
                      {task.commentCount}
                    </span>
                  )}
                  {task.usedFleet && (
                    <Badge appearance="outline" color="informative" size="small">Fleet</Badge>
                  )}
                </div>

                {/* Expanded detail */}
                {isExpanded && <TaskDetail task={task} onRefresh={handleRefresh} />}
              </div>
            </div>
          </Card>
        );
      })}
    </div>
  );
}
