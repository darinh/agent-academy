import { useState, useCallback } from "react";
import {
  makeStyles,
  mergeClasses,
  shorthands,
} from "@fluentui/react-components";
import {
  TaskListLtrRegular,
  ChevronDownRegular,
  ChevronRightRegular,
} from "@fluentui/react-icons";
import EmptyState from "./EmptyState";
import ErrorState from "./ErrorState";
import SkeletonLoader from "./SkeletonLoader";
import { formatElapsed } from "./panelUtils";
import type { TaskSnapshot, AgentDefinition } from "./api";
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
});

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

            {isExpanded && <TaskDetail task={task} agents={agents} onRefresh={handleRefresh} />}
          </div>
        );
      })}
    </div>
  );
}
