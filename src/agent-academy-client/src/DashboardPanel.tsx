import {
  Card,
  CardHeader,
  Badge,
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import {
  PeopleRegular,
  ChatMultipleRegular,
  TaskListLtrRegular,
  ArrowSyncRegular,
} from "@fluentui/react-icons";
import type { CollaborationPhase, WorkspaceOverview } from "./api";

// ── Styles ──

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    height: "100%",
    overflow: "auto",
    gap: "18px",
  },
  grid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
    gap: "14px",
  },
  card: {
    border: "1px solid rgba(155, 176, 210, 0.16)",
    background: "linear-gradient(180deg, rgba(255, 255, 255, 0.045), rgba(255, 255, 255, 0.02))",
    boxShadow: "inset 0 1px 0 rgba(255, 255, 255, 0.04)",
    ...shorthands.borderRadius("24px"),
    ...shorthands.padding("18px"),
  },
  cardHeader: {
    color: "#9bb0d2",
    fontSize: "11px",
    fontWeight: 700,
    letterSpacing: "0.1em",
    textTransform: "uppercase",
  },
  bigNumber: {
    fontSize: "40px",
    fontWeight: 780,
    color: "#eff5ff",
    lineHeight: 1,
    marginTop: "14px",
    marginBottom: "8px",
    letterSpacing: "-0.03em",
  },
  label: {
    color: "#7c90b2",
    fontSize: "13px",
    lineHeight: 1.5,
  },
  section: {
    display: "grid",
    gap: "10px",
    border: "1px solid rgba(155, 176, 210, 0.16)",
    background: "linear-gradient(180deg, rgba(255, 255, 255, 0.035), rgba(255, 255, 255, 0.015))",
    ...shorthands.borderRadius("24px"),
    ...shorthands.padding("20px"),
  },
  sectionTitle: {
    fontSize: "15px",
    fontWeight: 680,
    color: "#eff5ff",
    letterSpacing: "-0.02em",
  },
  phaseRow: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    ...shorthands.padding("10px", "0"),
    borderBottom: "1px solid rgba(155, 176, 210, 0.08)",
    fontSize: "14px",
    color: "#dbe7fb",
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
});

// ── Helpers ──

function phaseColor(
  phase: CollaborationPhase,
): "informative" | "success" | "warning" | "important" | "severe" | "subtle" {
  const map: Record<CollaborationPhase, "informative" | "success" | "warning" | "important" | "severe" | "subtle"> = {
    Intake: "informative",
    Planning: "warning",
    Discussion: "important",
    Validation: "severe",
    Implementation: "success",
    FinalSynthesis: "subtle",
  };
  return map[phase];
}

// ── Component ──

interface DashboardPanelProps {
  overview: WorkspaceOverview;
}

export default function DashboardPanel({ overview }: DashboardPanelProps) {
  const s = useLocalStyles();

  const roomCount = overview.rooms.length;
  const agentCount = overview.configuredAgents.length;
  const activeTasks = overview.rooms.filter((r) => r.activeTask).length;
  const eventCount = overview.recentActivity.length;

  // Phase distribution
  const phaseCounts = new Map<CollaborationPhase, number>();
  for (const room of overview.rooms) {
    phaseCounts.set(room.currentPhase, (phaseCounts.get(room.currentPhase) ?? 0) + 1);
  }

  return (
    <div className={s.root}>
      <div className={s.grid}>
        <Card className={s.card}>
          <CardHeader
            image={<ChatMultipleRegular style={{ fontSize: 24, color: "#6cb6ff" }} />}
            header={<span className={s.cardHeader}>Rooms</span>}
          />
          <div className={s.bigNumber}>{roomCount}</div>
          <div className={s.label}>Shared collaboration rooms currently available.</div>
        </Card>

        <Card className={s.card}>
          <CardHeader
            image={<PeopleRegular style={{ fontSize: 24, color: "#b794ff" }} />}
            header={<span className={s.cardHeader}>Agents</span>}
          />
          <div className={s.bigNumber}>{agentCount}</div>
          <div className={s.label}>Configured contributors ready for planning, coding, and review.</div>
        </Card>

        <Card className={s.card}>
          <CardHeader
            image={<TaskListLtrRegular style={{ fontSize: 24, color: "#48d67a" }} />}
            header={<span className={s.cardHeader}>Active Tasks</span>}
          />
          <div className={s.bigNumber}>{activeTasks}</div>
          <div className={s.label}>Rooms with in-flight work needing active coordination.</div>
        </Card>

        <Card className={s.card}>
          <CardHeader
            image={<ArrowSyncRegular style={{ fontSize: 24, color: "#ffbe70" }} />}
            header={<span className={s.cardHeader}>Recent Events</span>}
          />
          <div className={s.bigNumber}>{eventCount}</div>
          <div className={s.label}>Latest workflow activity across the workspace.</div>
        </Card>
      </div>

      {phaseCounts.size > 0 && (
        <div className={s.section}>
          <div className={s.sectionTitle}>Phase Distribution</div>
          {[...phaseCounts.entries()].map(([phase, count]) => (
            <div key={phase} className={s.phaseRow}>
              <Badge appearance="filled" color={phaseColor(phase)}>
                {phase}
              </Badge>
              <span>
                {count} room{count !== 1 ? "s" : ""}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
