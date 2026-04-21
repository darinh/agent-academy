import type { SprintStage, SprintStatus } from "../api";
import type { BadgeColor } from "../V3Badge";

export const STAGE_META: Record<
  SprintStage,
  { label: string; icon: string; description: string }
> = {
  Intake: {
    label: "Intake",
    icon: "📥",
    description: "Requirements gathering and scope definition",
  },
  Planning: {
    label: "Planning",
    icon: "📋",
    description: "Sprint plan creation and phase breakdown",
  },
  Discussion: {
    label: "Discussion",
    icon: "💬",
    description: "Team discussion and design decisions",
  },
  Validation: {
    label: "Validation",
    icon: "✅",
    description: "Plan validation and readiness check",
  },
  Implementation: {
    label: "Implementation",
    icon: "🔨",
    description: "Active development and task execution",
  },
  FinalSynthesis: {
    label: "Final Synthesis",
    icon: "📊",
    description: "Sprint report and deliverable summary",
  },
};

export const ALL_STAGES: SprintStage[] = [
  "Intake",
  "Planning",
  "Discussion",
  "Validation",
  "Implementation",
  "FinalSynthesis",
];

export function statusBadgeColor(status: SprintStatus): BadgeColor {
  switch (status) {
    case "Active":
      return "active";
    case "Completed":
      return "done";
    case "Cancelled":
      return "cancel";
  }
}

export function artifactTypeLabel(type: string): string {
  return type.replace(/([A-Z])/g, " $1").trim();
}
