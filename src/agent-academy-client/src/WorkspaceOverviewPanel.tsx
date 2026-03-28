import {
  Badge,
  Button,
  Card,
  ProgressBar,
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import { ArrowRightRegular } from "@fluentui/react-icons";
import type { CollaborationPhase, RoomSnapshot, WorkspaceOverview } from "./api";

const PHASES: readonly CollaborationPhase[] = [
  "Intake",
  "Planning",
  "Discussion",
  "Validation",
  "Implementation",
  "FinalSynthesis",
] as const;

// ── Styles ──

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
    marginBottom: "12px",
  },
  phaseBar: {
    display: "flex",
    alignItems: "center",
    gap: "12px",
    marginBottom: "8px",
  },
  phaseLabel: {
    fontSize: "14px",
    color: "#dbe7fb",
    minWidth: "120px",
    fontWeight: 650,
  },
  progressContainer: { flex: 1 },
  transitionRow: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    flexWrap: "wrap",
    marginTop: "12px",
  },
  card: {
    ...shorthands.padding("12px", "16px"),
    border: "1px solid rgba(155, 176, 210, 0.16)",
    backgroundColor: "rgba(255, 255, 255, 0.03)",
    ...shorthands.borderRadius("18px"),
  },
  roomRow: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    ...shorthands.padding("10px", "0"),
    borderBottom: "1px solid rgba(155, 176, 210, 0.08)",
    flexWrap: "wrap",
    gap: "8px",
  },
  roomName: {
    fontWeight: 650,
    color: "#eff5ff",
    fontSize: "14px",
  },
  badges: {
    display: "flex",
    gap: "8px",
    alignItems: "center",
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

function statusColor(
  status: string,
): "informative" | "success" | "warning" | "important" | "danger" | "subtle" {
  switch (status) {
    case "Active":      return "success";
    case "AttentionRequired": return "warning";
    case "Completed":   return "informative";
    case "Archived":    return "subtle";
    default:            return "important";
  }
}

function phaseProgress(phase: CollaborationPhase): number {
  const idx = PHASES.indexOf(phase);
  return idx >= 0 ? (idx + 1) / PHASES.length : 0;
}

// ── Component ──

interface WorkspaceOverviewPanelProps {
  overview: WorkspaceOverview;
  room: RoomSnapshot | null;
  onPhaseTransition: (phase: CollaborationPhase) => void;
  transitioning: boolean;
}

export default function WorkspaceOverviewPanel({
  overview,
  room,
  onPhaseTransition,
  transitioning,
}: WorkspaceOverviewPanelProps) {
  const s = useLocalStyles();

  return (
    <div className={s.root}>
      {/* Current phase for selected room */}
      {room && (
        <div>
          <div className={s.sectionTitle}>Current Phase — {room.name}</div>

          <div className={s.phaseBar}>
            <span className={s.phaseLabel}>{room.currentPhase}</span>
            <div className={s.progressContainer}>
              <ProgressBar
                value={phaseProgress(room.currentPhase)}
                thickness="large"
                color="brand"
              />
            </div>
          </div>

          <div className={s.transitionRow}>
            {PHASES.map((phase) => (
              <Button
                key={phase}
                size="small"
                appearance={phase === room.currentPhase ? "primary" : "outline"}
                disabled={phase === room.currentPhase || transitioning}
                icon={<ArrowRightRegular />}
                onClick={() => onPhaseTransition(phase)}
              >
                {phase}
              </Button>
            ))}
          </div>
        </div>
      )}

      {/* Room status summary */}
      <div>
        <div className={s.sectionTitle}>Room Status Summary</div>
        <Card className={s.card}>
          {overview.rooms.length === 0 ? (
            <span style={{ color: "#a1b3d2" }}>No rooms yet</span>
          ) : (
            overview.rooms.map((r) => (
              <div key={r.id} className={s.roomRow}>
                <span className={s.roomName}>{r.name}</span>
                <div className={s.badges}>
                  <Badge appearance="outline" color={statusColor(r.status)}>
                    {r.status}
                  </Badge>
                  <Badge appearance="filled" color="informative">
                    {r.currentPhase}
                  </Badge>
                  <Badge appearance="outline" color="subtle">
                    {r.participants.length} agent{r.participants.length !== 1 ? "s" : ""}
                  </Badge>
                </div>
              </div>
            ))
          )}
        </Card>
      </div>
    </div>
  );
}
