import { useState } from "react";
import {
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import {
  TaskListLtrRegular,
  ArrowSyncRegular,
  CheckmarkCircleRegular,
  CircleRegular,
  ErrorCircleRegular,
  ClockRegular,
} from "@fluentui/react-icons";
import type {
  RoomSnapshot,
  TaskSnapshot,
  TaskStatus,
} from "./api";
import {
  PHASES,
  phaseBadge,
  taskStatusBadge,
  workstreamBadge,
} from "./taskStatePanelUtils";
import V3Badge from "./V3Badge";

// -- Styles --

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    height: "100%",
    overflow: "auto",
    gap: "20px",
  },
  sectionTitle: {
    fontSize: "13px",
    fontWeight: 600,
    color: "var(--aa-text)",
    marginBottom: "10px",
  },
  card: {
    display: "flex",
    flexDirection: "column",
    gap: "10px",
    ...shorthands.padding("12px"),
    border: "1px solid var(--aa-border)",
    backgroundColor: "var(--aa-panel)",
    ...shorthands.borderRadius("8px"),
  },
  roomLabel: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    marginBottom: "10px",
  },
  taskTitle: {
    fontSize: "13px",
    fontWeight: 600,
    color: "var(--aa-text-strong)",
  },
  taskDesc: {
    fontSize: "13px",
    color: "var(--aa-muted)",
    marginBottom: "8px",
    lineHeight: 1.5,
    whiteSpace: "pre-wrap",
  },
  taskDescClamped: {
    display: "-webkit-box",
    WebkitLineClamp: 3,
    WebkitBoxOrient: "vertical",
    overflow: "hidden",
  },
  taskDescToggle: {
    fontSize: "12px",
    color: "var(--aa-cyan)",
    cursor: "pointer",
    marginBottom: "12px",
    ":hover": { textDecoration: "underline" },
  },
  phaseTrack: {
    display: "flex",
    gap: "4px",
    flexWrap: "wrap",
    marginBottom: "14px",
  },
  row: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    ...shorthands.padding("8px", "0"),
    borderBottom: "1px solid var(--aa-hairline)",
    fontSize: "13px",
    color: "var(--aa-text)",
  },
  rowLabel: {
    color: "var(--aa-soft)",
    fontSize: "13px",
  },
  rowSummary: {
    fontSize: "12px",
    color: "var(--aa-soft)",
    ...shorthands.padding("4px", "0"),
  },
  agentList: {
    display: "flex",
    flexWrap: "wrap",
    gap: "8px",
    marginTop: "8px",
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

// -- Helpers --

function taskStatusIcon(status: TaskStatus) {
  switch (status) {
    case "Active":             return <ArrowSyncRegular />;
    case "Completed":          return <CheckmarkCircleRegular />;
    case "Blocked":            return <ErrorCircleRegular />;
    case "AwaitingValidation": return <ClockRegular />;
    default:                   return <CircleRegular />;
  }
}

// -- Main Component --

interface TaskStatePanelProps {
  rooms: RoomSnapshot[];
  room: RoomSnapshot | null;
}

export default function TaskStatePanel({ rooms, room }: TaskStatePanelProps) {
  const s = useLocalStyles();
  const [expandedTasks, setExpandedTasks] = useState<Set<string>>(new Set());

  const roomsWithTasks = rooms.filter((r) => r.activeTask);

  if (roomsWithTasks.length === 0) {
    return (
      <div className={s.empty}>
        <TaskListLtrRegular className={s.emptyIcon} />
        <span>No active tasks</span>
      </div>
    );
  }

  // Show selected room task first, then others
  const ordered = room?.activeTask
    ? [room, ...roomsWithTasks.filter((r) => r.id !== room.id)]
    : roomsWithTasks;

  return (
    <div className={s.root}>
      {ordered.map((r) => {
        const task = r.activeTask as TaskSnapshot;
        return (
          <div key={r.id}>
            <div className={s.roomLabel}>
              <V3Badge color={r.id === room?.id ? "active" : "info"}>
                {r.name}
              </V3Badge>
              <V3Badge color="muted">
                {r.participants.length} agent{r.participants.length !== 1 ? "s" : ""}
              </V3Badge>
            </div>

            <div className={s.card}>
              <div style={{ display: "flex", alignItems: "center", gap: "8px" }}>
                {taskStatusIcon(task.status)}
                <span className={s.taskTitle}>{task.title}</span>
                <V3Badge color={taskStatusBadge(task.status)}>
                  {task.status}
                </V3Badge>
              </div>

              {task.description && (
                <>
                  <div
                    className={`${s.taskDesc} ${expandedTasks.has(task.id) ? "" : s.taskDescClamped}`}
                  >
                    {task.description}
                  </div>
                  <div
                    className={s.taskDescToggle}
                    onClick={() =>
                      setExpandedTasks((prev) => {
                        const next = new Set(prev);
                        next.has(task.id) ? next.delete(task.id) : next.add(task.id);
                        return next;
                      })
                    }
                  >
                    {expandedTasks.has(task.id) ? "▲ Collapse" : "▼ Show full description"}
                  </div>
                </>
              )}

              {/* Phase indicator */}
              <div className={s.phaseTrack}>
                {PHASES.map((p) => (
                  <V3Badge key={p} color={phaseBadge(p, task.currentPhase)}>
                    {p}
                  </V3Badge>
                ))}
              </div>

              {/* Workstreams */}
              <div className={s.row}>
                <span className={s.rowLabel}>Validation</span>
                <V3Badge color={workstreamBadge(task.validationStatus)}>
                  {task.validationStatus}
                </V3Badge>
              </div>
              {task.validationSummary && <div className={s.rowSummary}>{task.validationSummary}</div>}

              <div className={s.row}>
                <span className={s.rowLabel}>Implementation</span>
                <V3Badge color={workstreamBadge(task.implementationStatus)}>
                  {task.implementationStatus}
                </V3Badge>
              </div>
              {task.implementationSummary && <div className={s.rowSummary}>{task.implementationSummary}</div>}

              {/* Preferred roles */}
              {task.preferredRoles.length > 0 && (
                <div style={{ marginTop: "12px" }}>
                  <div className={s.sectionTitle}>Preferred Roles</div>
                  <div className={s.agentList}>
                    {task.preferredRoles.map((role) => (
                      <V3Badge key={role} color="info">👤 {role}</V3Badge>
                    ))}
                  </div>
                </div>
              )}
            </div>

            {/* Assigned agents */}
            {r.participants.length > 0 && (
              <div style={{ marginTop: "10px" }}>
                <div className={s.sectionTitle}>Assigned Agents</div>
                <div className={s.agentList}>
                  {r.participants.map((agent) => (
                    <V3Badge
                      key={agent.agentId}
                      color={agent.availability === "Active" ? "ok" : "muted"}
                    >
                      👤 {agent.name} ({agent.role})
                    </V3Badge>
                  ))}
                </div>
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}
