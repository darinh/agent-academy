import {
  makeStyles,
  mergeClasses,
  shorthands,
} from "@fluentui/react-components";
import type { SprintSnapshot } from "../api";
import V3Badge from "../V3Badge";
import { statusBadgeColor } from "./sprintConstants";
import { formatElapsed } from "../panelUtils";

const useStyles = makeStyles({
  detailSection: {
    display: "grid",
    gap: "12px",
  },
  sectionTitle: {
    fontFamily: "var(--mono)",
    fontSize: "11px",
    fontWeight: 600,
    textTransform: "uppercase",
    letterSpacing: "0.08em",
    color: "var(--aa-muted)",
  },
  historyList: {
    display: "grid",
    gap: "6px",
  },
  historyItem: {
    display: "flex",
    alignItems: "center",
    gap: "10px",
    ...shorthands.padding("8px", "12px"),
    background: "var(--aa-surface)",
    ...shorthands.borderRadius("6px"),
    ...shorthands.border("1px", "solid", "var(--aa-border)"),
    cursor: "pointer",
    fontSize: "13px",
    transitionProperty: "border-color",
    transitionDuration: "0.15s",
    "&:hover": {
      ...shorthands.borderColor("var(--aa-border-hover, rgba(139,148,158,0.3))"),
    },
  },
  historyItemActive: {
    ...shorthands.borderColor("var(--aa-cyan, #5b8def)"),
  },
  sprintNumber: {
    fontFamily: "var(--mono)",
    fontWeight: 600,
    color: "var(--aa-text)",
    minWidth: "24px",
  },
  sprintMeta: {
    flex: 1,
    color: "var(--aa-soft)",
    fontSize: "12px",
  },
});

interface SprintHistoryProps {
  history: SprintSnapshot[];
  selectedSprintId: string | null;
  onSelectSprint: (id: string) => void;
}

export default function SprintHistory({
  history,
  selectedSprintId,
  onSelectSprint,
}: SprintHistoryProps) {
  const s = useStyles();

  if (history.length <= 1) return null;

  return (
    <div className={s.detailSection}>
      <span className={s.sectionTitle}>Sprint History</span>
      <div className={s.historyList}>
        {history.map((sp) => (
          <div
            key={sp.id}
            className={mergeClasses(
              s.historyItem,
              sp.id === selectedSprintId && s.historyItemActive,
            )}
            onClick={() => onSelectSprint(sp.id)}
            role="button"
            tabIndex={0}
            onKeyDown={(e) => {
              if (e.key === "Enter" || e.key === " ")
                onSelectSprint(sp.id);
            }}
          >
            <span className={s.sprintNumber}>#{sp.number}</span>
            <V3Badge color={statusBadgeColor(sp.status)}>
              {sp.status}
            </V3Badge>
            <span className={s.sprintMeta}>
              {sp.currentStage} · {formatElapsed(sp.createdAt)}
            </span>
          </div>
        ))}
      </div>
    </div>
  );
}
