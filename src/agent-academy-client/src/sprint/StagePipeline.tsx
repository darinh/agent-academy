import {
  makeStyles,
  mergeClasses,
  shorthands,
} from "@fluentui/react-components";
import type { SprintStage, SprintDetailResponse } from "../api";
import { STAGE_META, ALL_STAGES } from "./sprintConstants";
import type { StageMetrics } from "./sprintMetrics";
import { formatDurationCompact } from "./sprintMetrics";

const useStyles = makeStyles({
  pipeline: {
    display: "grid",
    gridTemplateColumns: "repeat(6, 1fr)",
    gap: "2px",
    ...shorthands.padding("0"),
    "@media (max-width: 900px)": {
      gridTemplateColumns: "repeat(3, 1fr)",
    },
    "@media (max-width: 600px)": {
      gridTemplateColumns: "1fr",
    },
  },
  stageCard: {
    display: "grid",
    gap: "4px",
    alignContent: "start",
    ...shorthands.padding("12px"),
    background: "var(--aa-surface)",
    ...shorthands.borderRadius("6px"),
    ...shorthands.border("1px", "solid", "var(--aa-border)"),
    cursor: "pointer",
    transitionProperty: "border-color, background",
    transitionDuration: "0.15s",
    "&:hover": {
      ...shorthands.borderColor("var(--aa-border-hover, rgba(139,148,158,0.3))"),
    },
  },
  stageCardActive: {
    ...shorthands.borderColor("var(--aa-cyan, #5b8def)"),
    background:
      "linear-gradient(135deg, rgba(91,141,239,0.06), transparent 60%)",
  },
  stageCardCompleted: {
    ...shorthands.borderColor("var(--aa-lime, #4caf50)"),
    background:
      "linear-gradient(135deg, rgba(76,175,80,0.04), transparent 60%)",
  },
  stageCardSelected: {
    boxShadow: "inset 0 0 0 1px var(--aa-cyan, #5b8def)",
  },
  stageIcon: {
    fontSize: "16px",
    lineHeight: 1,
  },
  stageLabel: {
    fontSize: "11px",
    fontWeight: 600,
    textTransform: "uppercase",
    letterSpacing: "0.06em",
    color: "var(--aa-text)",
    fontFamily: "var(--mono)",
  },
  stageLabelMuted: {
    color: "var(--aa-muted)",
  },
  stageDesc: {
    fontSize: "11px",
    color: "var(--aa-soft)",
    lineHeight: 1.4,
  },
});

interface StagePipelineProps {
  detail: SprintDetailResponse;
  selectedStage: SprintStage | null;
  stageMetrics: StageMetrics[] | null;
  onSelectStage: (stage: SprintStage) => void;
}

export default function StagePipeline({
  detail,
  selectedStage,
  stageMetrics,
  onSelectStage,
}: StagePipelineProps) {
  const s = useStyles();
  const currentStageIndex = ALL_STAGES.indexOf(detail.sprint.currentStage);

  return (
    <div className={s.pipeline}>
      {ALL_STAGES.map((stage, idx) => {
        const meta = STAGE_META[stage];
        const isCurrent = stage === detail.sprint.currentStage;
        const isCompleted =
          detail.sprint.status === "Completed" || idx < currentStageIndex;
        const isSelected = stage === selectedStage;
        const artifactCount = detail.artifacts.filter(
          (a) => a.stage === stage,
        ).length;
        const stageMetric = stageMetrics?.find((m) => m.stage === stage);
        return (
          <div
            key={stage}
            className={mergeClasses(
              s.stageCard,
              isCurrent && s.stageCardActive,
              isCompleted && !isCurrent && s.stageCardCompleted,
              isSelected && s.stageCardSelected,
            )}
            onClick={() => onSelectStage(stage)}
            role="button"
            tabIndex={0}
            onKeyDown={(e) => {
              if (e.key === "Enter" || e.key === " ") onSelectStage(stage);
            }}
          >
            <span className={s.stageIcon}>{meta.icon}</span>
            <span
              className={mergeClasses(
                s.stageLabel,
                !isCurrent && !isCompleted && s.stageLabelMuted,
              )}
            >
              {meta.label}
              {isCurrent && " ●"}
            </span>
            <span className={s.stageDesc}>
              {artifactCount > 0
                ? `${artifactCount} artifact${artifactCount !== 1 ? "s" : ""}`
                : meta.description}
            </span>
            {stageMetric?.durationMs != null && (
              <span className={s.stageDesc}>
                ⏱ {formatDurationCompact(stageMetric.durationMs)}
                {stageMetric.totalWords > 0 &&
                  ` · ${stageMetric.totalWords.toLocaleString()}w`}
              </span>
            )}
          </div>
        );
      })}
    </div>
  );
}
