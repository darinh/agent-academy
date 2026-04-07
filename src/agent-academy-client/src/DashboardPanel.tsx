import { useState } from "react";
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
  ServerRegular,
  MoneyRegular,
  ErrorCircleRegular,
  ClockRegular,
  ClipboardTaskListLtrRegular,
  ChatHistoryRegular,
} from "@fluentui/react-icons";
import type { CollaborationPhase, WorkspaceOverview } from "./api";
import type { CircuitBreakerState } from "./useCircuitBreakerPolling";
import RestartHistoryPanel from "./RestartHistoryPanel";
import UsagePanel from "./UsagePanel";
import ErrorsPanel from "./ErrorsPanel";
import AuditLogPanel from "./AuditLogPanel";
import SessionHistoryPanel from "./SessionHistoryPanel";

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
    transitionProperty: "transform, box-shadow, border-color",
    transitionDuration: "0.25s",
    transitionTimingFunction: "ease",
    ":hover": {
      transform: "translateY(-2px)",
      boxShadow: "inset 0 1px 0 rgba(255, 244, 227, 0.05), 0 8px 24px rgba(0, 0, 0, 0.25)",
      borderColor: "rgba(214, 188, 149, 0.24)",
    },
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
    transitionProperty: "border-color",
    transitionDuration: "0.25s",
    transitionTimingFunction: "ease",
    ":hover": {
      borderColor: "rgba(214, 188, 149, 0.22)",
    },
  },
  sectionTitle: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
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
  timeRangeRow: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    flexWrap: "wrap",
  },
  timeRangeLabel: {
    display: "flex",
    alignItems: "center",
    gap: "6px",
    color: "var(--aa-soft)",
    fontSize: "12px",
    fontWeight: 600,
    letterSpacing: "0.06em",
    textTransform: "uppercase",
  },
  timeRangeBtn: {
    background: "none",
    ...shorthands.border("1px", "solid", "rgba(155, 176, 210, 0.18)"),
    ...shorthands.borderRadius("10px"),
    ...shorthands.padding("4px", "14px"),
    color: "var(--aa-text)",
    cursor: "pointer",
    fontSize: "12px",
    fontWeight: 600,
    ":hover": {
      backgroundColor: "rgba(255, 255, 255, 0.06)",
    },
  },
  timeRangeBtnActive: {
    background: "rgba(108, 182, 255, 0.14)",
    ...shorthands.border("1px", "solid", "rgba(108, 182, 255, 0.35)"),
    ...shorthands.borderRadius("10px"),
    ...shorthands.padding("4px", "14px"),
    color: "#6cb6ff",
    cursor: "pointer",
    fontSize: "12px",
    fontWeight: 700,
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

type TimeRange = 24 | 168 | 720 | undefined; // 24h, 7d, 30d, All
const TIME_RANGES: { label: string; value: TimeRange }[] = [
  { label: "24h", value: 24 },
  { label: "7d", value: 168 },
  { label: "30d", value: 720 },
  { label: "All", value: undefined },
];

const TIME_RANGE_KEY = "agent-academy-dashboard-timerange";

function loadTimeRange(): TimeRange {
  try {
    const raw = localStorage.getItem(TIME_RANGE_KEY);
    if (raw === "all") return undefined;
    const n = Number(raw);
    if (n === 24 || n === 168 || n === 720) return n;
  } catch { /* ignore */ }
  return undefined;
}

function saveTimeRange(v: TimeRange) {
  try {
    localStorage.setItem(TIME_RANGE_KEY, v == null ? "all" : String(v));
  } catch { /* ignore */ }
}

// ── Component ──

interface DashboardPanelProps {
  overview: WorkspaceOverview;
  circuitBreakerState?: CircuitBreakerState;
}

export default function DashboardPanel({ overview, circuitBreakerState }: DashboardPanelProps) {
  const s = useLocalStyles();
  const [hoursBack, setHoursBack] = useState<TimeRange>(loadTimeRange);

  const handleTimeRange = (v: TimeRange) => {
    setHoursBack(v);
    saveTimeRange(v);
  };

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

      <div className={s.timeRangeRow}>
        <span className={s.timeRangeLabel}>
          <ClockRegular style={{ fontSize: 14 }} />
          Time Range
        </span>
        {TIME_RANGES.map((tr) => (
          <button
            key={tr.label}
            className={hoursBack === tr.value ? s.timeRangeBtnActive : s.timeRangeBtn}
            onClick={() => handleTimeRange(tr.value)}
          >
            {tr.label}
          </button>
        ))}
      </div>

      <div className={s.section}>
        <div className={s.sectionTitle}>
          <MoneyRegular style={{ fontSize: 20 }} />
          LLM Usage
        </div>
        <UsagePanel hoursBack={hoursBack} />
      </div>

      <div className={s.section}>
        <div className={s.sectionTitle}>
          <ErrorCircleRegular style={{ fontSize: 20 }} />
          Agent Errors
        </div>
        <ErrorsPanel hoursBack={hoursBack} circuitBreakerState={circuitBreakerState} />
      </div>

      <div className={s.section}>
        <div className={s.sectionTitle}>
          <ClipboardTaskListLtrRegular style={{ fontSize: 20 }} />
          Command Audit Log
        </div>
        <AuditLogPanel hoursBack={hoursBack} />
      </div>

      <div className={s.section}>
        <div className={s.sectionTitle}>
          <ChatHistoryRegular style={{ fontSize: 20 }} />
          Conversation Sessions
        </div>
        <SessionHistoryPanel hoursBack={hoursBack} />
      </div>

      <div className={s.section}>
        <div className={s.sectionTitle}>
          <ServerRegular style={{ fontSize: 20 }} />
          Server Instance History
        </div>
        <RestartHistoryPanel hoursBack={hoursBack} />
      </div>
    </div>
  );
}
