import {
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import { STAGE_META } from "./sprintConstants";
import type { StageMetrics, SprintMetricsResult } from "./sprintMetrics";
import { formatDurationCompact } from "./sprintMetrics";

const useStyles = makeStyles({
  metricsBar: {
    display: "flex",
    gap: "16px",
    flexWrap: "wrap",
    ...shorthands.padding("10px", "14px"),
    background: "var(--aa-surface)",
    ...shorthands.borderRadius("6px"),
    ...shorthands.border("1px", "solid", "var(--aa-border)"),
  },
  metricItem: {
    display: "flex",
    alignItems: "baseline",
    gap: "6px",
  },
  metricValue: {
    fontFamily: "var(--mono)",
    fontSize: "13px",
    fontWeight: 600,
    color: "var(--aa-text)",
  },
  metricLabel: {
    fontSize: "11px",
    color: "var(--aa-muted)",
  },
  metricDivider: {
    width: "1px",
    alignSelf: "stretch",
    background: "var(--aa-border)",
  },
});

interface SprintMetricsBarProps {
  metrics: SprintMetricsResult;
  activeStageMetrics: StageMetrics | null;
  totalArtifacts: number;
}

export default function SprintMetricsBar({
  metrics,
  activeStageMetrics,
  totalArtifacts,
}: SprintMetricsBarProps) {
  const s = useStyles();

  return (
    <div className={s.metricsBar}>
      <div className={s.metricItem}>
        <span className={s.metricValue}>
          {formatDurationCompact(metrics.totalDurationMs)}
        </span>
        <span className={s.metricLabel}>total</span>
      </div>
      <div className={s.metricDivider} />
      {activeStageMetrics?.durationMs != null && (
        <>
          <div className={s.metricItem}>
            <span className={s.metricValue}>
              {formatDurationCompact(activeStageMetrics.durationMs)}
            </span>
            <span className={s.metricLabel}>
              {STAGE_META[activeStageMetrics.stage].label.toLowerCase()}
            </span>
          </div>
          <div className={s.metricDivider} />
        </>
      )}
      <div className={s.metricItem}>
        <span className={s.metricValue}>
          {metrics.totalWords.toLocaleString()}
        </span>
        <span className={s.metricLabel}>words</span>
      </div>
      {activeStageMetrics && activeStageMetrics.totalWords > 0 && (
        <>
          <div className={s.metricDivider} />
          <div className={s.metricItem}>
            <span className={s.metricValue}>
              {activeStageMetrics.totalWords.toLocaleString()}
            </span>
            <span className={s.metricLabel}>
              in {STAGE_META[activeStageMetrics.stage].label.toLowerCase()}
            </span>
          </div>
        </>
      )}
      <div className={s.metricDivider} />
      <div className={s.metricItem}>
        <span className={s.metricValue}>{totalArtifacts}</span>
        <span className={s.metricLabel}>
          artifact{totalArtifacts !== 1 ? "s" : ""}
        </span>
      </div>
    </div>
  );
}
