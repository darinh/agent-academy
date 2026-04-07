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
    display: "flex",
    alignItems: "center",
    gap: "8px",
    ...shorthands.padding("12px", "0"),
    borderBottom: "1px solid rgba(155, 176, 210, 0.16)",
    flexShrink: 0,
  },
  headerTitle: { fontWeight: 680, fontSize: "14px", color: "#eff5ff" },
  list: {
    flex: 1,
    overflow: "auto",
    ...shorthands.padding("0"),
    margin: "0",
    listStyle: "none",
  },
  item: {
    display: "flex",
    alignItems: "flex-start",
    gap: "12px",
    ...shorthands.padding("12px", "0"),
    borderBottom: "1px solid rgba(155, 176, 210, 0.08)",
  },
  iconCol: {
    flexShrink: 0,
    paddingTop: "2px",
    color: "#6cb6ff",
    fontSize: "16px",
  },
  body: {
    flex: 1,
    minWidth: 0,
  },
  message: {
    color: "#dbe7fb",
    fontSize: "14px",
    wordBreak: "break-word",
    lineHeight: 1.5,
  },
  meta: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    marginTop: "4px",
    fontSize: "12px",
    color: "#7c90b2",
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

function relativeTime(iso: string): string {
  const diff = Date.now() - new Date(iso).getTime();
  const secs = Math.floor(diff / 1000);
  if (secs < 60) return "just now";
  const mins = Math.floor(secs / 60);
  if (mins < 60) return `${mins}m ago`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs}h ago`;
  const days = Math.floor(hrs / 24);
  return `${days}d ago`;
}

function eventIcon(type: ActivityEventType) {
  if (type.startsWith("Agent")) return <PersonRegular />;
  if (type.startsWith("Message")) return <ChatRegular />;
  if (type.startsWith("Task")) return <TaskListLtrRegular />;
  if (type === "PhaseChanged") return <ArrowSyncRegular />;
  if (type.startsWith("Subagent")) return <PlayRegular />;
  if (type.startsWith("Room")) return <InfoRegular />;
  return <InfoRegular />;
}

function severityBadge(severity: "Info" | "Warning" | "Error") {
  const map = {
    Info: { color: "informative" as const, icon: <CheckmarkCircleRegular /> },
    Warning: { color: "warning" as const, icon: <WarningRegular /> },
    Error: { color: "danger" as const, icon: <ErrorCircleRegular /> },
  };
  return map[severity];
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
