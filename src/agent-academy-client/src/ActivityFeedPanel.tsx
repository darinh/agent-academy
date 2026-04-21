import { useCallback, useEffect, useState } from "react";
import { makeStyles, shorthands, Spinner } from "@fluentui/react-components";
import { ArrowSyncRegular } from "@fluentui/react-icons";
import V3Badge from "./V3Badge";
import type { BadgeColor } from "./V3Badge";
import EmptyState from "./EmptyState";
import { getRecentActivity, type ActivityEvent } from "./api";
import { relativeTime, eventCategory } from "./timelinePanelUtils";

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
    justifyContent: "space-between",
    ...shorthands.padding("12px", "20px"),
    borderBottom: "1px solid var(--aa-border)",
  },
  headerLeft: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
  },
  list: {
    flex: 1,
    overflow: "auto",
    margin: "0",
    ...shorthands.padding("0"),
    listStyle: "none",
  },
  item: {
    display: "flex",
    alignItems: "flex-start",
    gap: "10px",
    ...shorthands.padding("8px", "20px"),
    borderBottom: "1px solid var(--aa-hairline)",
    fontSize: "12px",
  },
  time: {
    flexShrink: 0,
    width: "60px",
    color: "var(--aa-muted)",
    fontSize: "11px",
    fontFamily: "var(--mono)",
    textAlign: "right" as const,
  },
  msg: {
    flex: 1,
    color: "var(--aa-text)",
    lineHeight: "1.4",
  },
  refreshBtn: {
    background: "none",
    ...shorthands.border("none"),
    color: "var(--aa-soft)",
    cursor: "pointer",
    ...shorthands.padding("4px"),
    ...shorthands.borderRadius("4px"),
    ":hover": { background: "rgba(255,255,255,0.05)" },
  },
});

function severityColor(severity: string): BadgeColor {
  switch (severity) {
    case "Error": return "err";
    case "Warning": return "warn";
    default: return "muted";
  }
}

export default function ActivityFeedPanel() {
  const s = useLocalStyles();
  const [events, setEvents] = useState<ActivityEvent[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const fetchData = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await getRecentActivity(50);
      setEvents(data);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load activity");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { fetchData(); }, [fetchData]);

  if (loading) {
    return (
      <div className={s.root}>
        <Spinner size="small" label="Loading activity…" />
      </div>
    );
  }

  if (error) {
    return (
      <div className={s.root}>
        <EmptyState
          icon={<span style={{ fontSize: 48 }}>⚡</span>}
          title="Failed to load activity"
          detail={error}
        />
      </div>
    );
  }

  return (
    <div className={s.root}>
      <div className={s.header}>
        <div className={s.headerLeft}>
          <span style={{ fontSize: 14 }}>⚡</span>
          <span style={{ fontWeight: 600, fontSize: 13, color: "var(--aa-text)" }}>
            Recent Activity
          </span>
          <V3Badge color="muted">{events.length}</V3Badge>
        </div>
        <button className={s.refreshBtn} onClick={fetchData} aria-label="Refresh activity">
          <ArrowSyncRegular fontSize={14} />
        </button>
      </div>

      {events.length === 0 ? (
        <EmptyState
          icon={<span style={{ fontSize: 48 }}>📭</span>}
          title="No activity yet"
          detail="Events will appear here as the workspace is used."
        />
      ) : (
        <ul className={s.list}>
          {events.map((ev) => (
            <li key={ev.id} className={s.item}>
              <span className={s.time}>{relativeTime(ev.occurredAt)}</span>
              <V3Badge color={severityColor(ev.severity)}>
                {eventCategory(ev.type)}
              </V3Badge>
              <span className={s.msg}>{ev.message}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
