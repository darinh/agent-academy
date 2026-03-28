import {
  Badge,
  Card,
  CardHeader,
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import {
  TaskListLtrRegular,
  PersonRegular,
  ArrowSyncRegular,
  CheckmarkCircleRegular,
  CircleRegular,
  ErrorCircleRegular,
  ClockRegular,
} from "@fluentui/react-icons";
import type {
  CollaborationPhase,
  RoomSnapshot,
  TaskSnapshot,
  TaskStatus,
} from "./api";

const PHASES: readonly CollaborationPhase[] = [
  "Intake", "Planning", "Discussion",
  "Validation", "Implementation", "FinalSynthesis",
] as const;

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
    fontSize: "14px",
    fontWeight: 680,
    color: "#eff5ff",
    marginBottom: "10px",
  },
  card: {
    ...shorthands.padding("16px"),
    border: "1px solid rgba(155, 176, 210, 0.16)",
    backgroundColor: "rgba(255, 255, 255, 0.03)",
    ...shorthands.borderRadius("18px"),
  },
  roomLabel: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    marginBottom: "10px",
  },
  taskTitle: {
    fontSize: "16px",
    fontWeight: 680,
    color: "#eff5ff",
    letterSpacing: "-0.02em",
  },
  taskDesc: {
    fontSize: "14px",
    color: "#dbe7fb",
    marginBottom: "12px",
    lineHeight: 1.6,
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
    borderBottom: "1px solid rgba(155, 176, 210, 0.08)",
    fontSize: "14px",
    color: "#dbe7fb",
  },
  rowLabel: {
    color: "#7c90b2",
    fontSize: "13px",
  },
  rowSummary: {
    fontSize: "12px",
    color: "#7c90b2",
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
    color: "#a1b3d2",
  },
  emptyIcon: { fontSize: "48px" },
});

// -- Helpers --

type BadgeColor = "informative" | "success" | "warning" | "important" | "danger" | "subtle";

function taskStatusColor(status: TaskStatus): BadgeColor {
  switch (status) {
    case "Active":             return "success";
    case "Blocked":            return "danger";
    case "AwaitingValidation": return "warning";
    case "Completed":          return "informative";
    case "Cancelled":          return "subtle";
    case "Queued": default:    return "important";
  }
}

function taskStatusIcon(status: TaskStatus) {
  switch (status) {
    case "Active":             return <ArrowSyncRegular />;
    case "Completed":          return <CheckmarkCircleRegular />;
    case "Blocked":            return <ErrorCircleRegular />;
    case "AwaitingValidation": return <ClockRegular />;
    default:                   return <CircleRegular />;
  }
}

function workstreamColor(status: string): BadgeColor {
  switch (status) {
    case "Completed":  return "success";
    case "InProgress": return "informative";
    case "Blocked":    return "warning";
    case "Ready":      return "important";
    default:           return "subtle";
  }
}

function phaseColor(
  phase: CollaborationPhase,
  currentPhase: CollaborationPhase,
): "informative" | "success" | "subtle" {
  const currentIdx = PHASES.indexOf(currentPhase);
  const phaseIdx = PHASES.indexOf(phase);
  if (phaseIdx < currentIdx) return "success";
  if (phaseIdx === currentIdx) return "informative";
  return "subtle";
}

// -- Main Component --

interface TaskStatePanelProps {
  rooms: RoomSnapshot[];
  room: RoomSnapshot | null;
}

export default function TaskStatePanel({ rooms, room }: TaskStatePanelProps) {
  const s = useLocalStyles();

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
              <Badge appearance="outline" color={r.id === room?.id ? "brand" : "informative"} size="small">
                {r.name}
              </Badge>
              <Badge appearance="outline" color="subtle" size="small">
                {r.participants.length} agent{r.participants.length !== 1 ? "s" : ""}
              </Badge>
            </div>

            <Card className={s.card}>
              <CardHeader
                image={taskStatusIcon(task.status)}
                header={<span className={s.taskTitle}>{task.title}</span>}
                description={
                  <Badge appearance="filled" color={taskStatusColor(task.status)} size="small">
                    {task.status}
                  </Badge>
                }
              />

              {task.description && <div className={s.taskDesc}>{task.description}</div>}

              {/* Phase indicator */}
              <div className={s.phaseTrack}>
                {PHASES.map((p) => (
                  <Badge key={p} appearance="filled" color={phaseColor(p, task.currentPhase)} size="small">
                    {p}
                  </Badge>
                ))}
              </div>

              {/* Workstreams */}
              <div className={s.row}>
                <span className={s.rowLabel}>Validation</span>
                <Badge appearance="outline" color={workstreamColor(task.validationStatus)}>
                  {task.validationStatus}
                </Badge>
              </div>
              {task.validationSummary && <div className={s.rowSummary}>{task.validationSummary}</div>}

              <div className={s.row}>
                <span className={s.rowLabel}>Implementation</span>
                <Badge appearance="outline" color={workstreamColor(task.implementationStatus)}>
                  {task.implementationStatus}
                </Badge>
              </div>
              {task.implementationSummary && <div className={s.rowSummary}>{task.implementationSummary}</div>}

              {/* Preferred roles */}
              {task.preferredRoles.length > 0 && (
                <div style={{ marginTop: "12px" }}>
                  <div className={s.sectionTitle}>Preferred Roles</div>
                  <div className={s.agentList}>
                    {task.preferredRoles.map((role) => (
                      <Badge key={role} appearance="outline" color="informative" icon={<PersonRegular />}>
                        {role}
                      </Badge>
                    ))}
                  </div>
                </div>
              )}
            </Card>

            {/* Assigned agents */}
            {r.participants.length > 0 && (
              <div style={{ marginTop: "10px" }}>
                <div className={s.sectionTitle}>Assigned Agents</div>
                <div className={s.agentList}>
                  {r.participants.map((agent) => (
                    <Badge
                      key={agent.agentId}
                      appearance="filled"
                      color={agent.availability === "Active" ? "success" : "subtle"}
                      icon={<PersonRegular />}
                    >
                      {agent.name} ({agent.role})
                    </Badge>
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
