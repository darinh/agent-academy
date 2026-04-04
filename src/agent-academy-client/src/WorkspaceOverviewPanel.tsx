import {
  Badge,
  Button,
  Card,
  ProgressBar,
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import {
  ArrowRightRegular,
  ChartMultipleRegular,
} from "@fluentui/react-icons";
import type { CollaborationPhase, RoomSnapshot, WorkspaceOverview } from "./api";
import RoomStatsPanel from "./RoomStatsPanel";

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
  section: {
    display: "grid",
    gap: "16px",
    border: "1px solid rgba(214, 188, 149, 0.14)",
    background:
      "linear-gradient(180deg, rgba(255, 244, 227, 0.05), rgba(255, 255, 255, 0.018) 42%, rgba(12, 15, 22, 0.72))",
    ...shorthands.borderRadius("26px"),
    ...shorthands.padding("22px"),
  },
  sectionTitle: {
    fontSize: "18px",
    fontWeight: 680,
    color: "var(--aa-text-strong)",
    letterSpacing: "-0.03em",
  },
  phaseBar: {
    display: "flex",
    alignItems: "center",
    gap: "12px",
    flexWrap: "wrap",
  },
  phaseLabel: {
    fontSize: "14px",
    color: "var(--aa-text)",
    minWidth: "120px",
    fontWeight: 650,
  },
  progressContainer: { flex: 1 },
  transitionRow: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    flexWrap: "wrap",
  },
  phaseButton: {
    border: "1px solid rgba(214, 188, 149, 0.18)",
    backgroundColor: "rgba(255, 244, 227, 0.03)",
  },
  phaseButtonActive: {
    boxShadow: "0 12px 28px rgba(0, 0, 0, 0.22)",
  },
  card: {
    border: "1px solid rgba(214, 188, 149, 0.12)",
    background: "rgba(255, 244, 227, 0.03)",
    ...shorthands.borderRadius("22px"),
    ...shorthands.padding("16px", "18px"),
  },
  roomRow: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    ...shorthands.padding("12px", "0"),
    borderBottom: "1px solid rgba(255, 244, 227, 0.07)",
    flexWrap: "wrap",
    gap: "8px",
  },
  roomName: {
    fontWeight: 650,
    color: "var(--aa-text-strong)",
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
    color: "var(--aa-soft)",
  },
  emptyText: {
    color: "var(--aa-soft)",
  },
  limitedModeNote: {
    color: "#f2d7b0",
    fontSize: "12px",
    lineHeight: 1.7,
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
  readOnly?: boolean;
}

export default function WorkspaceOverviewPanel({
  overview,
  room,
  onPhaseTransition,
  transitioning,
  readOnly = false,
}: WorkspaceOverviewPanelProps) {
  const s = useLocalStyles();

  return (
    <div className={s.root}>
      {/* Current phase for selected room */}
      {room && (
        <div className={s.section}>
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
                className={phase === room.currentPhase ? s.phaseButtonActive : s.phaseButton}
                appearance={phase === room.currentPhase ? "primary" : "outline"}
                disabled={phase === room.currentPhase || transitioning || readOnly}
                icon={<ArrowRightRegular />}
                onClick={() => onPhaseTransition(phase)}
              >
                {phase}
              </Button>
            ))}
          </div>
          {readOnly && (
            <div className={s.limitedModeNote}>
              Phase changes are paused while Copilot reconnects. Review the current plan and room state until full access returns.
            </div>
          )}
        </div>
      )}

      {/* Room usage & errors */}
      {room && (
        <div className={s.section}>
          <div className={s.sectionTitle}>
            <ChartMultipleRegular style={{ fontSize: 20 }} />
            Room Stats — {room.name}
          </div>
          <RoomStatsPanel roomId={room.id} />
        </div>
      )}

      {/* Room status summary */}
      <div className={s.section}>
        <div className={s.sectionTitle}>Room Status Summary</div>
        <Card className={s.card}>
          {overview.rooms.length === 0 ? (
            <span className={s.emptyText}>No rooms yet</span>
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
