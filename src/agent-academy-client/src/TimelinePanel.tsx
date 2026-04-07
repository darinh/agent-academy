import {
  Badge,
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import {
  PersonRegular,
  ChatRegular,
  TaskListLtrRegular,
  ArrowSyncRegular,
  WarningRegular,
  ErrorCircleRegular,
  CheckmarkCircleRegular,
  InfoRegular,
  PlayRegular,
} from "@fluentui/react-icons";
import type { ActivityEvent, ActivityEventType } from "./api";
import { relativeTime, severityColor, eventCategory } from "./timelinePanelUtils";
import EmptyState from "./EmptyState";
import SkeletonLoader from "./SkeletonLoader";

// ── Styles ──

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    height: "100%",
    overflow: "hidden",
  },
  header: {
    display: "none",
  },
  headerTitle: { display: "none" },
  list: {
    flex: 1,
    overflow: "auto",
    ...shorthands.padding("0"),
    margin: "0",
    listStyle: "none",
  },
  item: {
    display: "flex",
    alignItems: "center",
    gap: "12px",
    ...shorthands.padding("7px", "20px"),
    borderBottom: "1px solid rgba(48, 54, 61, 0.4)",
    fontFamily: "var(--mono)",
    fontSize: "11px",
    transitionProperty: "background",
    transitionDuration: "0.1s",
    ":hover": {
      background: "rgba(91, 141, 239, 0.04)",
    },
  },
  iconCol: {
    flexShrink: 0,
    width: "20px",
    textAlign: "center",
    color: "var(--aa-text)",
    fontSize: "13px",
  },
  body: {
    flex: 1,
    minWidth: 0,
    display: "flex",
    alignItems: "center",
    gap: "8px",
  },
  message: {
    flex: 1,
    color: "var(--aa-text)",
    fontSize: "11px",
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap" as const,
  },
  meta: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    fontSize: "10px",
    color: "var(--aa-soft)",
    whiteSpace: "nowrap" as const,
    minWidth: "50px",
    textAlign: "right",
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

function eventIcon(type: ActivityEventType) {
  const cat = eventCategory(type);
  switch (cat) {
    case "agent": return <PersonRegular />;
    case "message": return <ChatRegular />;
    case "task": return <TaskListLtrRegular />;
    case "phase": return <ArrowSyncRegular />;
    case "subagent": return <PlayRegular />;
    case "room": return <InfoRegular />;
    default: return <InfoRegular />;
  }
}

function severityBadge(severity: "Info" | "Warning" | "Error") {
  const iconMap = {
    Info: <CheckmarkCircleRegular />,
    Warning: <WarningRegular />,
    Error: <ErrorCircleRegular />,
  };
  return { color: severityColor(severity), icon: iconMap[severity] };
}

// ── Component ──

interface TimelinePanelProps {
  /** Pre-fetched activity events from useWorkspace (room-filtered). */
  activity: ActivityEvent[];
  loading?: boolean;
}

export default function TimelinePanel({ activity, loading }: TimelinePanelProps) {
  const s = useLocalStyles();

  // Sort newest-first (activity from useWorkspace may already be ordered, but be safe)
  const sorted = [...activity].sort(
    (a, b) => new Date(b.occurredAt).getTime() - new Date(a.occurredAt).getTime(),
  );

  return (
    <div className={s.root}>
      <div className={s.header}>
        <span className={s.headerTitle}>Activity Timeline</span>
        <Badge appearance="filled" color="informative" size="small">
          {sorted.length}
        </Badge>
      </div>

      {loading && sorted.length === 0 ? (
        <SkeletonLoader rows={5} variant="list" />
      ) : sorted.length === 0 ? (
        <EmptyState
          icon={<ArrowSyncRegular />}
          title="No activity yet"
          detail="Events will appear here as agents collaborate — messages, task updates, and phase changes."
        />
      ) : (
        <ul className={s.list}>
          {sorted.map((ev) => {
            const badge = severityBadge(ev.severity);
            return (
              <li key={ev.id} className={s.item}>
                <span className={s.iconCol}>{eventIcon(ev.type)}</span>
                <div className={s.body}>
                  <div className={s.message}>{ev.message}</div>
                  <div className={s.meta}>
                    <Badge
                      appearance="outline"
                      color={badge.color}
                      icon={badge.icon}
                      size="small"
                    >
                      {ev.type}
                    </Badge>
                    <span>{relativeTime(ev.occurredAt)}</span>
                  </div>
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
