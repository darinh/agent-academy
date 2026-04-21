import { describe, expect, it } from "vitest";
import type {
  SprintDetailResponse,
  SprintSnapshot,
  SprintArtifact,
  SprintStatus,
} from "../api";
import { STAGE_META, ALL_STAGES, statusBadgeColor, artifactTypeLabel } from "../sprint/sprintConstants";
import { computeSprintMetrics, formatDurationCompact } from "../sprint/sprintMetrics";

// ── Factories ──

function makeSprint(overrides: Partial<SprintSnapshot> = {}): SprintSnapshot {
  return {
    id: "sprint-1",
    number: 1,
    status: "Active",
    currentStage: "Intake",
    overflowFromSprintId: null,
    awaitingSignOff: false,
    pendingStage: null,
    signOffRequestedAt: null,
    createdAt: "2026-04-01T00:00:00Z",
    completedAt: null,
    ...overrides,
  };
}

function makeArtifact(overrides: Partial<SprintArtifact> = {}): SprintArtifact {
  return {
    id: 1,
    sprintId: "sprint-1",
    stage: "Intake",
    type: "RequirementsDocument",
    content: "Some artifact content",
    createdByAgentId: "agent-1",
    createdAt: "2026-04-01T01:00:00Z",
    updatedAt: null,
    ...overrides,
  };
}

function makeDetail(
  sprintOverrides: Partial<SprintSnapshot> = {},
  artifacts: SprintArtifact[] = [],
): SprintDetailResponse {
  return {
    sprint: makeSprint(sprintOverrides),
    artifacts,
    stages: ALL_STAGES as unknown as string[],
  };
}

// ── sprintConstants tests ──

describe("sprintConstants", () => {
  describe("STAGE_META", () => {
    it("has metadata for all 6 stages", () => {
      expect(Object.keys(STAGE_META)).toHaveLength(6);
      for (const stage of ALL_STAGES) {
        expect(STAGE_META[stage]).toBeDefined();
        expect(STAGE_META[stage].label).toBeTruthy();
        expect(STAGE_META[stage].icon).toBeTruthy();
        expect(STAGE_META[stage].description).toBeTruthy();
      }
    });
  });

  describe("ALL_STAGES", () => {
    it("lists stages in correct order", () => {
      expect(ALL_STAGES).toEqual([
        "Intake",
        "Planning",
        "Discussion",
        "Validation",
        "Implementation",
        "FinalSynthesis",
      ]);
    });
  });

  describe("statusBadgeColor", () => {
    it.each<[SprintStatus, string]>([
      ["Active", "active"],
      ["Completed", "done"],
      ["Cancelled", "cancel"],
    ])("maps %s → %s", (status, expected) => {
      expect(statusBadgeColor(status)).toBe(expected);
    });
  });

  describe("artifactTypeLabel", () => {
    it("converts PascalCase to spaced words", () => {
      expect(artifactTypeLabel("RequirementsDocument")).toBe("Requirements Document");
      expect(artifactTypeLabel("SprintPlan")).toBe("Sprint Plan");
      expect(artifactTypeLabel("ValidationReport")).toBe("Validation Report");
      expect(artifactTypeLabel("SprintReport")).toBe("Sprint Report");
      expect(artifactTypeLabel("OverflowRequirements")).toBe("Overflow Requirements");
    });

    it("handles single word", () => {
      expect(artifactTypeLabel("Report")).toBe("Report");
    });
  });
});

// ── sprintMetrics tests ──

describe("sprintMetrics", () => {
  describe("formatDurationCompact", () => {
    it("returns <1m for sub-minute durations", () => {
      expect(formatDurationCompact(0)).toBe("<1m");
      expect(formatDurationCompact(30_000)).toBe("<1m");
      expect(formatDurationCompact(59_999)).toBe("<1m");
    });

    it("returns minutes for sub-hour durations", () => {
      expect(formatDurationCompact(60_000)).toBe("1m");
      expect(formatDurationCompact(300_000)).toBe("5m");
      expect(formatDurationCompact(3_540_000)).toBe("59m");
    });

    it("returns hours and minutes for sub-day durations", () => {
      expect(formatDurationCompact(3_600_000)).toBe("1h 0m");
      expect(formatDurationCompact(5_400_000)).toBe("1h 30m");
      expect(formatDurationCompact(82_800_000)).toBe("23h 0m");
    });

    it("returns days and hours for multi-day durations", () => {
      expect(formatDurationCompact(86_400_000)).toBe("1d 0h");
      expect(formatDurationCompact(90_000_000)).toBe("1d 1h");
      expect(formatDurationCompact(172_800_000)).toBe("2d 0h");
    });
  });

  describe("computeSprintMetrics", () => {
    it("returns metrics for all 6 stages", () => {
      const detail = makeDetail();
      const metrics = computeSprintMetrics(detail);
      expect(metrics.stages).toHaveLength(6);
      expect(metrics.stages.map((s) => s.stage)).toEqual(ALL_STAGES);
    });

    it("counts artifacts per stage", () => {
      const detail = makeDetail({}, [
        makeArtifact({ stage: "Intake", id: 1 }),
        makeArtifact({ stage: "Intake", id: 2 }),
        makeArtifact({ stage: "Planning", id: 3 }),
      ]);
      const metrics = computeSprintMetrics(detail);
      expect(metrics.stages.find((s) => s.stage === "Intake")!.artifactCount).toBe(2);
      expect(metrics.stages.find((s) => s.stage === "Planning")!.artifactCount).toBe(1);
      expect(metrics.stages.find((s) => s.stage === "Discussion")!.artifactCount).toBe(0);
    });

    it("counts words across artifacts", () => {
      const detail = makeDetail({}, [
        makeArtifact({ stage: "Intake", content: "one two three" }),
        makeArtifact({ stage: "Intake", content: "four five" }),
      ]);
      const metrics = computeSprintMetrics(detail);
      expect(metrics.stages.find((s) => s.stage === "Intake")!.totalWords).toBe(5);
      expect(metrics.totalWords).toBe(5);
    });

    it("computes totalDurationMs for active sprint", () => {
      const now = Date.now();
      const start = new Date(now - 3_600_000).toISOString();
      const detail = makeDetail({ createdAt: start });
      const metrics = computeSprintMetrics(detail);
      // Should be ~1 hour (within 100ms tolerance)
      expect(metrics.totalDurationMs).toBeGreaterThanOrEqual(3_599_000);
      expect(metrics.totalDurationMs).toBeLessThanOrEqual(3_700_000);
    });

    it("computes totalDurationMs for completed sprint", () => {
      const detail = makeDetail({
        status: "Completed",
        createdAt: "2026-04-01T00:00:00Z",
        completedAt: "2026-04-01T02:00:00Z",
      });
      const metrics = computeSprintMetrics(detail);
      expect(metrics.totalDurationMs).toBe(7_200_000); // 2 hours
    });

    it("computes stage duration for completed stages using artifact timestamps", () => {
      const detail = makeDetail(
        {
          currentStage: "Discussion",
          createdAt: "2026-04-01T00:00:00Z",
        },
        [
          makeArtifact({ stage: "Intake", createdAt: "2026-04-01T00:30:00Z", id: 1 }),
          makeArtifact({ stage: "Planning", createdAt: "2026-04-01T01:00:00Z", id: 2 }),
          makeArtifact({ stage: "Discussion", createdAt: "2026-04-01T02:00:00Z", id: 3 }),
        ],
      );
      const metrics = computeSprintMetrics(detail);
      const intake = metrics.stages.find((s) => s.stage === "Intake")!;
      // Intake: sprintStart (00:00) to first Planning artifact (01:00) = 1 hour
      expect(intake.durationMs).toBe(3_600_000);
    });

    it("returns null duration for future stages", () => {
      const detail = makeDetail({ currentStage: "Planning" });
      const metrics = computeSprintMetrics(detail);
      const impl = metrics.stages.find((s) => s.stage === "Implementation")!;
      expect(impl.durationMs).toBeNull();
    });

    it("handles empty artifact list", () => {
      const detail = makeDetail();
      const metrics = computeSprintMetrics(detail);
      expect(metrics.totalWords).toBe(0);
      for (const stage of metrics.stages) {
        expect(stage.artifactCount).toBe(0);
        expect(stage.totalWords).toBe(0);
      }
    });
  });
});
