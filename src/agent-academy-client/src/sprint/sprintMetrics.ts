import type { SprintDetailResponse, SprintArtifact, SprintStage } from "../api";
import { ALL_STAGES } from "./sprintConstants";

export interface StageMetrics {
  stage: SprintStage;
  durationMs: number | null;
  artifactCount: number;
  totalWords: number;
}

export interface SprintMetricsResult {
  stages: StageMetrics[];
  totalWords: number;
  totalDurationMs: number;
}

function wordCount(text: string): number {
  return text.trim().split(/\s+/).filter(Boolean).length;
}

export function computeSprintMetrics(
  detail: SprintDetailResponse,
): SprintMetricsResult {
  const now = Date.now();
  const sprintStart = new Date(detail.sprint.createdAt).getTime();
  const sprintEnd = detail.sprint.completedAt
    ? new Date(detail.sprint.completedAt).getTime()
    : now;
  const currentStageIdx = ALL_STAGES.indexOf(detail.sprint.currentStage);

  const stageFirstArtifact = new Map<SprintStage, number>();
  const stageArtifacts = new Map<SprintStage, SprintArtifact[]>();
  for (const a of detail.artifacts) {
    const ts = new Date(a.createdAt).getTime();
    const prev = stageFirstArtifact.get(a.stage);
    if (prev === undefined || ts < prev) stageFirstArtifact.set(a.stage, ts);
    const list = stageArtifacts.get(a.stage) ?? [];
    list.push(a);
    stageArtifacts.set(a.stage, list);
  }

  let totalWords = 0;
  const stages: StageMetrics[] = ALL_STAGES.map((stage, idx) => {
    const arts = stageArtifacts.get(stage) ?? [];
    const words = arts.reduce((sum, a) => sum + wordCount(a.content), 0);
    totalWords += words;

    let durationMs: number | null = null;
    const stageIdx = idx;

    if (detail.sprint.status === "Completed" || stageIdx < currentStageIdx) {
      const stageStart =
        stageIdx === 0
          ? sprintStart
          : stageFirstArtifact.get(stage) ?? null;
      let stageEnd: number | null = null;
      for (let j = stageIdx + 1; j < ALL_STAGES.length; j++) {
        const nextTs = stageFirstArtifact.get(ALL_STAGES[j]);
        if (nextTs !== undefined) {
          stageEnd = nextTs;
          break;
        }
      }
      if (stageStart !== null) {
        durationMs = (stageEnd ?? sprintEnd) - stageStart;
      }
    } else if (stageIdx === currentStageIdx && detail.sprint.status === "Active") {
      const stageStart =
        stageIdx === 0
          ? sprintStart
          : stageFirstArtifact.get(stage) ??
            (() => {
              for (let j = stageIdx - 1; j >= 0; j--) {
                const arts = stageArtifacts.get(ALL_STAGES[j]);
                if (arts?.length) {
                  return Math.max(...arts.map((a) => new Date(a.createdAt).getTime()));
                }
              }
              return sprintStart;
            })();
      durationMs = now - stageStart;
    }

    return { stage, durationMs, artifactCount: arts.length, totalWords: words };
  });

  return { stages, totalWords, totalDurationMs: sprintEnd - sprintStart };
}

export function formatDurationCompact(ms: number): string {
  const seconds = Math.floor(ms / 1000);
  const minutes = Math.floor(seconds / 60);
  const hours = Math.floor(minutes / 60);
  if (minutes < 1) return "<1m";
  if (minutes < 60) return `${minutes}m`;
  if (hours < 24) return `${hours}h ${minutes % 60}m`;
  const days = Math.floor(hours / 24);
  return `${days}d ${hours % 24}h`;
}
