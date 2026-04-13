import { useState } from "react";
import {
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
  BranchRegular,
} from "@fluentui/react-icons";
import V3Badge from "./V3Badge";
import type { WorkspaceOverview } from "./api";
import type { CollaborationPhase } from "./api";
import type { CircuitBreakerState } from "./useCircuitBreakerPolling";
import { phaseColor, loadTimeRange, saveTimeRange, TIME_RANGES } from "./dashboardUtils";
import type { TimeRange } from "./dashboardUtils";
import RestartHistoryPanel from "./RestartHistoryPanel";
import UsagePanel from "./UsagePanel";
import ErrorsPanel from "./ErrorsPanel";
import AuditLogPanel from "./AuditLogPanel";
import SessionHistoryPanel from "./SessionHistoryPanel";
import AgentAnalyticsPanel from "./AgentAnalyticsPanel";
import TaskAnalyticsPanel from "./TaskAnalyticsPanel";
import WorktreeStatusPanel from "./WorktreeStatusPanel";

// ── Styles ──

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    height: "100%",
    overflowY: "auto",
    overflowX: "hidden",
    gap: "10px",
    ...shorthands.padding("14px", "20px"),
  },
  grid: {
    display: "grid",
    gridTemplateColumns: "repeat(4, 1fr)",
    gap: "10px",
    "@media (max-width: 900px)": {
      gridTemplateColumns: "repeat(2, 1fr)",
    },
  },
  card: {
    border: "1px solid var(--aa-border)",
    background: "var(--aa-panel)",
    ...shorthands.borderRadius("8px"),
    ...shorthands.padding("12px", "14px"),
  },
  cardHeader: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    color: "var(--aa-soft)",
    fontFamily: "var(--mono)",
    fontSize: "10px",
    fontWeight: 600,
    letterSpacing: "0.04em",
    textTransform: "uppercase",
  },
  bigNumber: {
    fontSize: "26px",
    fontWeight: 700,
    color: "var(--aa-text)",
    lineHeight: 1,
    marginTop: "8px",
    marginBottom: "4px",
    letterSpacing: "-0.03em",
  },
  label: {
    color: "var(--aa-soft)",
    fontSize: "11px",
    lineHeight: 1.4,
  },
  section: {
    display: "grid",
    gap: "10px",
    border: "1px solid var(--aa-border)",
    background: "var(--aa-panel)",
    ...shorthands.borderRadius("8px"),
    ...shorthands.padding("14px"),
  },
  sectionTitle: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    fontSize: "13px",
    fontWeight: 600,
    color: "var(--aa-text)",
  },
  phaseRow: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    ...shorthands.padding("8px", "0"),
    borderBottom: "1px solid var(--aa-border)",
    fontSize: "12px",
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
    gap: "6px",
    flexWrap: "wrap",
  },
  timeRangeLabel: {
    display: "flex",
    alignItems: "center",
    gap: "4px",
    color: "var(--aa-soft)",
    fontFamily: "var(--mono)",
    fontSize: "10px",
    fontWeight: 600,
    letterSpacing: "0.04em",
    textTransform: "uppercase",
  },
  timeRangeBtn: {
    background: "none",
    ...shorthands.border("1px", "solid", "var(--aa-border)"),
    ...shorthands.borderRadius("4px"),
    ...shorthands.padding("3px", "9px"),
    color: "var(--aa-muted)",
    cursor: "pointer",
    fontSize: "11px",
    fontWeight: 500,
    ":hover": {
      ...shorthands.borderColor("var(--aa-border-strong)"),
      color: "var(--aa-text)",
      background: "rgba(91, 141, 239, 0.04)",
    },
  },
  timeRangeBtnActive: {
    background: "rgba(91, 141, 239, 0.12)",
    ...shorthands.border("1px", "solid", "rgba(91, 141, 239, 0.3)"),
    ...shorthands.borderRadius("4px"),
    ...shorthands.padding("3px", "9px"),
    color: "var(--aa-cyan)",
    cursor: "pointer",
    fontSize: "11px",
    fontWeight: 600,
  },
});

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
        <div className={s.card}>
          <div className={s.cardHeader}>
            <ChatMultipleRegular style={{ fontSize: 24, color: "var(--aa-cyan)" }} />
            <span>Rooms</span>
          </div>
          <div className={s.bigNumber}>{roomCount}</div>
          <div className={s.label}>Shared collaboration rooms currently available.</div>
        </div>

        <div className={s.card}>
          <div className={s.cardHeader}>
            <PeopleRegular style={{ fontSize: 24, color: "var(--aa-plum)" }} />
            <span>Agents</span>
          </div>
          <div className={s.bigNumber}>{agentCount}</div>
          <div className={s.label}>Configured contributors ready for planning, coding, and review.</div>
        </div>

        <div className={s.card}>
          <div className={s.cardHeader}>
            <TaskListLtrRegular style={{ fontSize: 24, color: "var(--aa-lime)" }} />
            <span>Active Tasks</span>
          </div>
          <div className={s.bigNumber}>{activeTasks}</div>
          <div className={s.label}>Rooms with in-flight work needing active coordination.</div>
        </div>

        <div className={s.card}>
          <div className={s.cardHeader}>
            <ArrowSyncRegular style={{ fontSize: 24, color: "var(--aa-gold)" }} />
            <span>Recent Events</span>
          </div>
          <div className={s.bigNumber}>{eventCount}</div>
          <div className={s.label}>Latest workflow activity across the workspace.</div>
        </div>
      </div>

      {phaseCounts.size > 0 && (
        <div className={s.section}>
          <div className={s.sectionTitle}>Phase Distribution</div>
          {[...phaseCounts.entries()].map(([phase, count]) => (
            <div key={phase} className={s.phaseRow}>
              <V3Badge color={phaseColor(phase)}>
                {phase}
              </V3Badge>
              <span>
                {count} room{count !== 1 ? "s" : ""}
              </span>
            </div>
          ))}
        </div>
      )}

      <div className={s.section}>
        <div className={s.sectionTitle}>
          <BranchRegular style={{ fontSize: 20 }} />
          Agent Worktrees
        </div>
        <WorktreeStatusPanel />
      </div>

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
          <PeopleRegular style={{ fontSize: 20 }} />
          Agent Performance
        </div>
        <AgentAnalyticsPanel hoursBack={hoursBack} />
      </div>

      <div className={s.section}>
        <div className={s.sectionTitle}>
          <TaskListLtrRegular style={{ fontSize: 20 }} />
          Task Effectiveness
        </div>
        <TaskAnalyticsPanel hoursBack={hoursBack} />
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
