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
    gap: "20px",
  },
  grid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fill, minmax(200px, 1fr))",
    gap: "12px",
  },
  card: {
    ...shorthands.padding("16px"),
    border: "1px solid rgba(155, 176, 210, 0.16)",
    backgroundColor: "rgba(255, 255, 255, 0.03)",
    ...shorthands.borderRadius("18px"),
    textAlign: "center",
  },
  bigNumber: {
    fontSize: "36px",
    fontWeight: 780,
    color: "#eff5ff",
    lineHeight: 1,
    marginBottom: "4px",
    letterSpacing: "-0.03em",
  },
  label: {
    fontSize: "11px",
    color: "#7c90b2",
    textTransform: "uppercase",
    letterSpacing: "0.08em",
  },
  section: { marginTop: "4px" },
  sectionTitle: {
    fontSize: "14px",
    fontWeight: 680,
    color: "#eff5ff",
    marginBottom: "10px",
  },
  phaseRow: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    ...shorthands.padding("8px", "0"),
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
            header={<span className={s.label}>Rooms</span>}
          />
          <div className={s.bigNumber}>{roomCount}</div>
        </Card>

        <Card className={s.card}>
          <CardHeader
            image={<PeopleRegular style={{ fontSize: 24, color: "#b794ff" }} />}
            header={<span className={s.label}>Agents</span>}
          />
          <div className={s.bigNumber}>{agentCount}</div>
        </Card>

        <Card className={s.card}>
          <CardHeader
            image={<TaskListLtrRegular style={{ fontSize: 24, color: "#48d67a" }} />}
            header={<span className={s.label}>Active Tasks</span>}
          />
          <div className={s.bigNumber}>{activeTasks}</div>
        </Card>

        <Card className={s.card}>
          <CardHeader
            image={<ArrowSyncRegular style={{ fontSize: 24, color: "#ffbe70" }} />}
            header={<span className={s.label}>Recent Events</span>}
          />
          <div className={s.bigNumber}>{eventCount}</div>
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
