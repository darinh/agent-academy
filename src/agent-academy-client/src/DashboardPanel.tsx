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
    gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
    gap: "16px",
  },
  card: {
    border: "1px solid rgba(214, 188, 149, 0.14)",
    background:
      "linear-gradient(180deg, rgba(255, 244, 227, 0.055), rgba(255, 255, 255, 0.018) 42%, rgba(12, 15, 22, 0.72))",
    boxShadow: "inset 0 1px 0 rgba(255, 244, 227, 0.05)",
    ...shorthands.borderRadius("26px"),
    ...shorthands.padding("20px"),
  },
  cardHeader: {
    color: "var(--aa-soft)",
    fontSize: "11px",
    fontWeight: 700,
    letterSpacing: "0.14em",
    textTransform: "uppercase",
  },
  bigNumber: {
    fontFamily: "var(--heading)",
    fontSize: "44px",
    fontWeight: 780,
    color: "var(--aa-text-strong)",
    lineHeight: 1,
    marginTop: "14px",
    marginBottom: "8px",
    letterSpacing: "-0.05em",
  },
  label: {
    color: "var(--aa-muted)",
    fontSize: "13px",
    lineHeight: 1.7,
  },
  section: {
    display: "grid",
    gap: "12px",
    border: "1px solid rgba(214, 188, 149, 0.14)",
    background:
      "linear-gradient(180deg, rgba(255, 244, 227, 0.05), rgba(255, 255, 255, 0.02) 42%, rgba(12, 15, 22, 0.72))",
    ...shorthands.borderRadius("26px"),
    ...shorthands.padding("22px"),
  },
  sectionTitle: {
    fontSize: "18px",
    fontWeight: 680,
    color: "var(--aa-text-strong)",
    letterSpacing: "-0.03em",
  },
  phaseRow: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    ...shorthands.padding("12px", "0"),
    borderBottom: "1px solid rgba(255, 244, 227, 0.07)",
    fontSize: "14px",
    color: "var(--aa-text)",
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
