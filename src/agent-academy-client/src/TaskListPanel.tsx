import { useState, useCallback, useEffect, useRef } from "react";
import {
  makeStyles,
  mergeClasses,
  shorthands,
} from "@fluentui/react-components";
import {
  TaskListLtrRegular,
  ChevronDownRegular,
  ChevronRightRegular,
  CheckmarkRegular,
  DismissRegular,
} from "@fluentui/react-icons";
import EmptyState from "./EmptyState";
import ErrorState from "./ErrorState";
import SkeletonLoader from "./SkeletonLoader";
import { formatElapsed } from "./panelUtils";
import type { TaskSnapshot, AgentDefinition, TaskStatus, BulkOperationResult } from "./api";
import { bulkUpdateStatus, bulkAssign } from "./api";
import V3Badge from "./V3Badge";
import {
  TaskDetail,
  type TaskFilter,
  FILTER_ITEMS,
  filterTasks,
  filterCount,
  statusBadgeColor,
  typeBadgeColor,
  sizeBadgeColor,
} from "./taskList";

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
  empty: {
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    justifyContent: "center",
    height: "100%",
    gap: "12px",
    color: "var(--aa-soft)",
  },
  checkbox: {
    width: "16px",
    height: "16px",
    ...shorthands.borderRadius("3px"),
    border: "1.5px solid var(--aa-border-strong)",
    background: "transparent",
    cursor: "pointer",
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    flexShrink: 0,
    transitionProperty: "background, border-color",
    transitionDuration: "0.15s",
    ":hover": {
      ...shorthands.borderColor("rgba(91, 141, 239, 0.6)"),
    },
  },
  checkboxChecked: {
    background: "rgba(91, 141, 239, 0.8)",
    ...shorthands.borderColor("rgba(91, 141, 239, 0.8)"),
  },
  cardSelected: {
    ...shorthands.borderColor("rgba(91, 141, 239, 0.4)"),
    background: "rgba(91, 141, 239, 0.05)",
  },
  bulkBar: {
    position: "sticky",
    bottom: 0,
    display: "flex",
    alignItems: "center",
    gap: "10px",
    ...shorthands.padding("10px", "14px"),
    ...shorthands.borderRadius("8px"),
    background: "var(--aa-panel)",
    border: "1px solid rgba(91, 141, 239, 0.3)",
    boxShadow: "0 -2px 12px rgba(0,0,0,0.3)",
    marginTop: "8px",
    flexWrap: "wrap",
  },
  bulkCount: {
    fontSize: "12px",
    fontWeight: 600,
    color: "var(--aa-text-strong)",
    whiteSpace: "nowrap",
  },
  bulkSelect: {
    fontSize: "11px",
    ...shorthands.padding("4px", "8px"),
    ...shorthands.borderRadius("4px"),
    background: "transparent",
    border: "1px solid var(--aa-border)",
    color: "var(--aa-text-strong)",
    cursor: "pointer",
  },
  bulkBtn: {
    fontSize: "11px",
    fontWeight: 600,
    ...shorthands.padding("5px", "10px"),
    ...shorthands.borderRadius("4px"),
    background: "rgba(91, 141, 239, 0.15)",
    border: "1px solid rgba(91, 141, 239, 0.3)",
    color: "var(--aa-text-strong)",
    cursor: "pointer",
    ":hover": {
      background: "rgba(91, 141, 239, 0.25)",
    },
    ":disabled": {
      opacity: 0.4,
      cursor: "default",
    },
  },
  bulkClear: {
    marginLeft: "auto",
    fontSize: "11px",
    ...shorthands.padding("4px", "8px"),
    ...shorthands.borderRadius("4px"),
    background: "transparent",
    border: "1px solid var(--aa-border)",
    color: "var(--aa-soft)",
    cursor: "pointer",
    display: "flex",
    alignItems: "center",
    gap: "4px",
  },
  bulkResult: {
    fontSize: "11px",
    color: "var(--aa-soft)",
  },
});

// ── Constants ───────────────────────────────────────────────────────────

const BULK_SAFE_STATUSES: TaskStatus[] = [
  "Queued", "Active", "Blocked", "AwaitingValidation", "InReview",
];

// ── Main Component ──────────────────────────────────────────────────────

interface TaskListPanelProps {
  tasks: TaskSnapshot[];
  loading?: boolean;
  error?: boolean;
  onRefresh?: () => void;
  activeSprintId?: string | null;
  agents?: AgentDefinition[];
  focusTaskId?: string | null;
  onFocusHandled?: () => void;
}

export default function TaskListPanel({ tasks, loading, error, onRefresh, activeSprintId, agents = [], focusTaskId, onFocusHandled }: TaskListPanelProps) {
  const s = useLocalStyles();
  const [filter, setFilter] = useState<TaskFilter>("all");
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [sprintOnly, setSprintOnly] = useState(false);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [bulkBusy, setBulkBusy] = useState(false);
  const [bulkResult, setBulkResult] = useState<BulkOperationResult | null>(null);
  const [bulkError, setBulkError] = useState<string | null>(null);

  // Auto-expand a task when navigating from another panel (e.g. RetrospectivePanel)
  const lastFocusRef = useRef<string | null>(null);
  useEffect(() => {
    if (focusTaskId && focusTaskId !== lastFocusRef.current && tasks.length > 0) {
      const exists = tasks.some((t) => t.id === focusTaskId);
      if (exists) {
        lastFocusRef.current = focusTaskId;
        setFilter("all");
        setSprintOnly(false);
        setExpandedId(focusTaskId);
        onFocusHandled?.();
      }
    }
  }, [focusTaskId, tasks]);

  const baseTasks = sprintOnly && activeSprintId
    ? tasks.filter((t) => t.sprintId === activeSprintId)
    : tasks;

  const sortedTasks = [...filterTasks(baseTasks, filter)].sort(
    (a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime(),
  );

  const visibleIds = new Set(sortedTasks.map((t) => t.id));

  // Prune selection when filter or task list changes (remove hidden/deleted tasks)
  const visibleIdKey = sortedTasks.map((t) => t.id).join(",");
  useEffect(() => {
    setSelected((prev) => {
      const pruned = new Set([...prev].filter((id) => visibleIds.has(id)));
      return pruned.size === prev.size ? prev : pruned;
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filter, sprintOnly, activeSprintId, visibleIdKey]);

  // Escape to clear selection (only when no input is focused)
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key === "Escape" && selected.size > 0) {
        const tag = (e.target as HTMLElement)?.tagName;
        if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return;
        setSelected(new Set());
        setBulkResult(null);
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [selected.size]);

  const toggleSelect = useCallback((taskId: string) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(taskId)) next.delete(taskId);
      else next.add(taskId);
      return next;
    });
    setBulkResult(null);
  }, []);

  const selectAllVisible = useCallback(() => {
    setSelected(new Set(sortedTasks.map((t) => t.id)));
    setBulkResult(null);
  }, [sortedTasks]);

  const clearSelection = useCallback(() => {
    setSelected(new Set());
    setBulkResult(null);
  }, []);

  const handleBulkStatus = useCallback(async (status: TaskStatus) => {
    if (selected.size === 0) return;
    setBulkBusy(true);
    setBulkResult(null);
    setBulkError(null);
    try {
      const result = await bulkUpdateStatus([...selected], status);
      setBulkResult(result);
      // Remove successfully updated tasks from selection
      const failedIds = new Set(result.errors.map((e) => e.taskId));
      setSelected(failedIds);
      onRefresh?.();
    } catch (err) {
      setBulkError(err instanceof Error ? err.message : "Bulk status update failed");
    } finally {
      setBulkBusy(false);
    }
  }, [selected, onRefresh]);

  const handleBulkAssign = useCallback(async (agentId: string, agentName: string) => {
    if (selected.size === 0) return;
    setBulkBusy(true);
    setBulkResult(null);
    setBulkError(null);
    try {
      const result = await bulkAssign([...selected], agentId, agentName);
      setBulkResult(result);
      const failedIds = new Set(result.errors.map((e) => e.taskId));
      setSelected(failedIds);
      onRefresh?.();
    } catch (err) {
      setBulkError(err instanceof Error ? err.message : "Bulk assign failed");
    } finally {
      setBulkBusy(false);
    }
  }, [selected, onRefresh]);

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
        {sortedTasks.length > 0 && (
          <>
            <span style={{ borderLeft: "1px solid #333", height: "16px", margin: "0 4px" }} />
            <button
              className={s.filterChip}
              onClick={() => selected.size === sortedTasks.length ? clearSelection() : selectAllVisible()}
            >
              {selected.size === sortedTasks.length && sortedTasks.length > 0 ? "Deselect all" : "Select all"}
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
        const isSelected = selected.has(task.id);
        return (
          <div
            key={task.id}
            className={mergeClasses(
              s.card,
              isExpanded && s.cardExpanded,
              isSelected && !isExpanded && s.cardSelected,
            )}
            onClick={() => !isExpanded && setExpandedId(task.id)}
          >
            <div className={s.cardHeader}>
              <button
                className={mergeClasses(s.checkbox, isSelected && s.checkboxChecked)}
                onClick={(e) => {
                  e.stopPropagation();
                  toggleSelect(task.id);
                }}
                aria-label={isSelected ? `Deselect ${task.title}` : `Select ${task.title}`}
              >
                {isSelected && <CheckmarkRegular fontSize={11} style={{ color: "#fff" }} />}
              </button>
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
              {task.blockingTaskIds && task.blockingTaskIds.length > 0 && (
                <V3Badge color="err">🔗 {task.blockingTaskIds.length} blocked</V3Badge>
              )}
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

            {isExpanded && <TaskDetail task={task} agents={agents} onRefresh={handleRefresh} />}
          </div>
        );
      })}

      {/* Bulk action bar */}
      {(selected.size > 0 || bulkResult !== null || bulkError !== null) && (
        <div className={s.bulkBar}>
          {selected.size > 0 && (
            <span className={s.bulkCount}>{selected.size} selected</span>
          )}

          {selected.size > 0 && (
            <select
              className={s.bulkSelect}
              defaultValue=""
              disabled={bulkBusy}
              onChange={(e) => {
                if (e.target.value) handleBulkStatus(e.target.value as TaskStatus);
                e.target.value = "";
              }}
            >
              <option value="" disabled>Set status…</option>
              {BULK_SAFE_STATUSES.map((st) => (
                <option key={st} value={st}>{st}</option>
              ))}
            </select>
          )}

          {selected.size > 0 && agents.length > 0 && (
            <select
              className={s.bulkSelect}
              defaultValue=""
              disabled={bulkBusy}
              onChange={(e) => {
                const agent = agents.find((a) => a.id === e.target.value);
                if (agent) handleBulkAssign(agent.id, agent.name);
                e.target.value = "";
              }}
            >
              <option value="" disabled>Assign to…</option>
              {agents.map((a) => (
                <option key={a.id} value={a.id}>{a.name}</option>
              ))}
            </select>
          )}

          {bulkResult && (
            <span className={s.bulkResult}>
              ✓ {bulkResult.succeeded} updated
              {bulkResult.failed > 0 && `, ${bulkResult.failed} failed`}
            </span>
          )}

          {bulkError && (
            <span className={s.bulkResult} style={{ color: "var(--aa-err, #f44)" }}>
              ✗ {bulkError}
            </span>
          )}

          <button
            className={s.bulkClear}
            onClick={() => { clearSelection(); setBulkResult(null); setBulkError(null); }}
            disabled={bulkBusy}
          >
            <DismissRegular fontSize={11} /> {(bulkResult || bulkError) && selected.size === 0 ? "Dismiss" : "Clear"}
          </button>
        </div>
      )}
    </div>
  );
}
