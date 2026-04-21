import { Button, makeStyles, shorthands } from "@fluentui/react-components";
import { ArrowSyncRegular } from "@fluentui/react-icons";
import type { SprintDetailResponse } from "../api";
import V3Badge from "../V3Badge";
import { statusBadgeColor } from "./sprintConstants";
import { formatElapsed } from "../panelUtils";

const useStyles = makeStyles({
  header: {
    display: "flex",
    alignItems: "center",
    gap: "12px",
    flexWrap: "wrap",
  },
  headerTitle: {
    fontFamily: "var(--heading)",
    fontSize: "18px",
    fontWeight: 600,
    color: "var(--aa-text)",
    ...shorthands.margin("0"),
  },
  headerMeta: {
    color: "var(--aa-muted)",
    fontSize: "12px",
    fontFamily: "var(--mono)",
  },
});

interface SprintHeaderProps {
  detail: SprintDetailResponse | null;
  hasActiveSprint: boolean;
  actionBusy: boolean;
  onRefresh: () => void;
  onStartSprint: () => void;
  onAdvanceSprint: () => void;
  onCompleteSprint: () => void;
  onCancelSprint: () => void;
  onApproveAdvance: () => void;
  onRejectAdvance: () => void;
}

export default function SprintHeader({
  detail,
  hasActiveSprint,
  actionBusy,
  onRefresh,
  onStartSprint,
  onAdvanceSprint,
  onCompleteSprint,
  onCancelSprint,
  onApproveAdvance,
  onRejectAdvance,
}: SprintHeaderProps) {
  const s = useStyles();
  const isActive = detail?.sprint.status === "Active";
  const isFinalStage = detail?.sprint.currentStage === "FinalSynthesis";

  return (
    <div className={s.header}>
      <h2 className={s.headerTitle}>
        {detail ? `Sprint #${detail.sprint.number}` : "Sprints"}
      </h2>
      {detail && (
        <V3Badge color={statusBadgeColor(detail.sprint.status)}>
          {detail.sprint.status}
        </V3Badge>
      )}
      {detail && (
        <span className={s.headerMeta}>
          started {formatElapsed(detail.sprint.createdAt)}
          {detail.sprint.completedAt &&
            ` · finished ${formatElapsed(detail.sprint.completedAt)}`}
        </span>
      )}
      <Button
        appearance="subtle"
        size="small"
        icon={<ArrowSyncRegular />}
        onClick={onRefresh}
        style={{ marginLeft: "auto" }}
      />
      {!hasActiveSprint && (
        <Button appearance="primary" size="small" onClick={onStartSprint} disabled={actionBusy}>
          Start Sprint
        </Button>
      )}
      {isActive && !isFinalStage && !detail?.sprint.awaitingSignOff && (
        <Button appearance="primary" size="small" onClick={onAdvanceSprint} disabled={actionBusy}>
          Advance Stage
        </Button>
      )}
      {isActive && detail?.sprint.awaitingSignOff && (
        <>
          <Button appearance="primary" size="small" onClick={onApproveAdvance} disabled={actionBusy}>
            ✅ Approve → {detail.sprint.pendingStage}
          </Button>
          <Button appearance="subtle" size="small" onClick={onRejectAdvance} disabled={actionBusy}>
            ✗ Reject
          </Button>
        </>
      )}
      {isActive && isFinalStage && (
        <Button appearance="primary" size="small" onClick={onCompleteSprint} disabled={actionBusy}>
          Complete Sprint
        </Button>
      )}
      {isActive && (
        <Button appearance="subtle" size="small" onClick={onCancelSprint} disabled={actionBusy}>
          Cancel
        </Button>
      )}
    </div>
  );
}
