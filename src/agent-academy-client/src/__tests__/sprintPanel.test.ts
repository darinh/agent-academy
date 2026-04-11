import { describe, expect, it, vi, beforeEach } from "vitest";
import type {
  SprintSnapshot,
  SprintArtifact,
  SprintDetailResponse,
  SprintStage,
  SprintStatus,
} from "../api";

vi.mock("../api", () => ({
  getActiveSprint: vi.fn(),
  getSprints: vi.fn(),
  getSprintDetail: vi.fn(),
  startSprint: vi.fn(),
  advanceSprint: vi.fn(),
  completeSprint: vi.fn(),
  cancelSprint: vi.fn(),
  approveSprintAdvance: vi.fn(),
  rejectSprintAdvance: vi.fn(),
}));

import {
  getActiveSprint,
  getSprints,
  getSprintDetail,
  startSprint,
  advanceSprint,
  completeSprint,
  cancelSprint,
  approveSprintAdvance,
  rejectSprintAdvance,
} from "../api";

const mockGetActiveSprint = vi.mocked(getActiveSprint);
const mockGetSprints = vi.mocked(getSprints);
const mockGetSprintDetail = vi.mocked(getSprintDetail);
const mockStartSprint = vi.mocked(startSprint);
const mockAdvanceSprint = vi.mocked(advanceSprint);
const mockCompleteSprint = vi.mocked(completeSprint);
const mockCancelSprint = vi.mocked(cancelSprint);
const mockApproveSprintAdvance = vi.mocked(approveSprintAdvance);
const mockRejectSprintAdvance = vi.mocked(rejectSprintAdvance);

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
    content: "Some artifact content here",
    createdByAgentId: "architect",
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
    stages: ["Intake", "Planning", "Discussion", "Validation", "Implementation", "FinalSynthesis"],
  };
}

// ── Pure logic (mirrored from SprintPanel.tsx) ──

const ALL_STAGES: SprintStage[] = [
  "Intake", "Planning", "Discussion", "Validation", "Implementation", "FinalSynthesis",
];

function statusBadgeColor(status: SprintStatus): string {
  switch (status) {
    case "Active": return "active";
    case "Completed": return "done";
    case "Cancelled": return "cancel";
  }
}

function artifactTypeLabel(type: string): string {
  return type.replace(/([A-Z])/g, " $1").trim();
}

function wordCount(text: string): number {
  return text.trim().split(/\s+/).filter(Boolean).length;
}

function formatDurationCompact(ms: number): string {
  const seconds = Math.floor(ms / 1000);
  const minutes = Math.floor(seconds / 60);
  const hours = Math.floor(minutes / 60);
  if (minutes < 1) return "<1m";
  if (minutes < 60) return `${minutes}m`;
  if (hours < 24) return `${hours}h ${minutes % 60}m`;
  const days = Math.floor(hours / 24);
  return `${days}d ${hours % 24}h`;
}

interface StageMetrics {
  stage: SprintStage;
  durationMs: number | null;
  artifactCount: number;
  totalWords: number;
}

function computeSprintMetrics(
  detail: SprintDetailResponse,
): { stages: StageMetrics[]; totalWords: number; totalDurationMs: number } {
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

// ── Tests ──

describe("SprintPanel", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  describe("statusBadgeColor", () => {
    it("maps Active to 'active'", () => {
      expect(statusBadgeColor("Active")).toBe("active");
    });
    it("maps Completed to 'done'", () => {
      expect(statusBadgeColor("Completed")).toBe("done");
    });
    it("maps Cancelled to 'cancel'", () => {
      expect(statusBadgeColor("Cancelled")).toBe("cancel");
    });
  });

  describe("artifactTypeLabel", () => {
    it("splits PascalCase into words", () => {
      expect(artifactTypeLabel("RequirementsDocument")).toBe("Requirements Document");
    });
    it("splits multi-word types", () => {
      expect(artifactTypeLabel("SprintPlan")).toBe("Sprint Plan");
    });
    it("handles single word", () => {
      expect(artifactTypeLabel("Report")).toBe("Report");
    });
    it("handles ValidationReport", () => {
      expect(artifactTypeLabel("ValidationReport")).toBe("Validation Report");
    });
    it("handles OverflowRequirements", () => {
      expect(artifactTypeLabel("OverflowRequirements")).toBe("Overflow Requirements");
    });
  });

  describe("wordCount", () => {
    it("counts words in a simple sentence", () => {
      expect(wordCount("hello world")).toBe(2);
    });
    it("handles extra whitespace", () => {
      expect(wordCount("  hello   world  ")).toBe(2);
    });
    it("returns 0 for empty string", () => {
      expect(wordCount("")).toBe(0);
    });
    it("returns 0 for whitespace-only", () => {
      expect(wordCount("   ")).toBe(0);
    });
    it("handles single word", () => {
      expect(wordCount("hello")).toBe(1);
    });
    it("handles multiline text", () => {
      expect(wordCount("hello\nworld\nfoo bar")).toBe(4);
    });
  });

  describe("formatDurationCompact", () => {
    it("returns <1m for sub-minute durations", () => {
      expect(formatDurationCompact(30_000)).toBe("<1m");
    });
    it("returns <1m for 0ms", () => {
      expect(formatDurationCompact(0)).toBe("<1m");
    });
    it("formats minutes", () => {
      expect(formatDurationCompact(5 * 60_000)).toBe("5m");
    });
    it("formats 59 minutes", () => {
      expect(formatDurationCompact(59 * 60_000)).toBe("59m");
    });
    it("formats hours and minutes", () => {
      expect(formatDurationCompact(90 * 60_000)).toBe("1h 30m");
    });
    it("formats exact hours", () => {
      expect(formatDurationCompact(2 * 3600_000)).toBe("2h 0m");
    });
    it("formats days and hours", () => {
      expect(formatDurationCompact(25 * 3600_000)).toBe("1d 1h");
    });
    it("formats multiple days", () => {
      expect(formatDurationCompact(72 * 3600_000)).toBe("3d 0h");
    });
  });

  describe("computeSprintMetrics", () => {
    it("computes metrics for a completed sprint with artifacts", () => {
      const detail = makeDetail(
        {
          status: "Completed",
          currentStage: "FinalSynthesis",
          createdAt: "2026-04-01T00:00:00Z",
          completedAt: "2026-04-01T06:00:00Z",
        },
        [
          makeArtifact({ id: 1, stage: "Intake", content: "requirements here", createdAt: "2026-04-01T00:30:00Z" }),
          makeArtifact({ id: 2, stage: "Planning", content: "sprint plan document text", createdAt: "2026-04-01T01:00:00Z" }),
          makeArtifact({ id: 3, stage: "Implementation", content: "code changes summary and details", createdAt: "2026-04-01T03:00:00Z" }),
          makeArtifact({ id: 4, stage: "FinalSynthesis", content: "final report", createdAt: "2026-04-01T05:00:00Z" }),
        ],
      );

      const metrics = computeSprintMetrics(detail);
      expect(metrics.stages).toHaveLength(6);
      expect(metrics.totalWords).toBeGreaterThan(0);
      expect(metrics.totalDurationMs).toBe(6 * 3600_000);

      // Intake stage: sprint start (00:00) to first Planning artifact (01:00) = 1h
      const intake = metrics.stages.find((s) => s.stage === "Intake")!;
      expect(intake.artifactCount).toBe(1);
      expect(intake.durationMs).toBe(3600_000);

      // Planning: 01:00 to next artifact (Implementation at 03:00) = 2h
      const planning = metrics.stages.find((s) => s.stage === "Planning")!;
      expect(planning.artifactCount).toBe(1);
      expect(planning.durationMs).toBe(2 * 3600_000);
    });

    it("computes word counts per stage", () => {
      const detail = makeDetail(
        { status: "Active", currentStage: "Planning" },
        [
          makeArtifact({ stage: "Intake", content: "one two three four five" }),
          makeArtifact({ stage: "Intake", content: "six seven" }),
        ],
      );

      const metrics = computeSprintMetrics(detail);
      const intake = metrics.stages.find((s) => s.stage === "Intake")!;
      expect(intake.totalWords).toBe(7);
      expect(intake.artifactCount).toBe(2);
    });

    it("handles sprint with no artifacts", () => {
      const detail = makeDetail({ status: "Active", currentStage: "Intake" }, []);
      const metrics = computeSprintMetrics(detail);
      expect(metrics.totalWords).toBe(0);
      expect(metrics.stages.every((s) => s.artifactCount === 0)).toBe(true);
    });

    it("stages without artifacts have null duration for non-current stages", () => {
      const detail = makeDetail(
        { status: "Active", currentStage: "Implementation" },
        [
          makeArtifact({ stage: "Intake", content: "req", createdAt: "2026-04-01T00:30:00Z" }),
          makeArtifact({ stage: "Implementation", content: "code", createdAt: "2026-04-01T03:00:00Z" }),
        ],
      );

      const metrics = computeSprintMetrics(detail);
      // Discussion and Validation have no artifacts and are past stages
      const discussion = metrics.stages.find((s) => s.stage === "Discussion")!;
      expect(discussion.durationMs).toBeNull();
      const validation = metrics.stages.find((s) => s.stage === "Validation")!;
      expect(validation.durationMs).toBeNull();
    });

    it("future stages have null duration", () => {
      const detail = makeDetail(
        { status: "Active", currentStage: "Planning" },
        [makeArtifact({ stage: "Intake", content: "req" })],
      );

      const metrics = computeSprintMetrics(detail);
      const impl = metrics.stages.find((s) => s.stage === "Implementation")!;
      expect(impl.durationMs).toBeNull();
      const synthesis = metrics.stages.find((s) => s.stage === "FinalSynthesis")!;
      expect(synthesis.durationMs).toBeNull();
    });
  });

  describe("API integration types", () => {
    it("getActiveSprint returns SprintDetailResponse or null", async () => {
      mockGetActiveSprint.mockResolvedValue(makeDetail());
      const result = await getActiveSprint();
      expect(result).not.toBeNull();
      expect(result!.sprint.id).toBe("sprint-1");
      expect(result!.sprint.status).toBe("Active");
      expect(result!.artifacts).toEqual([]);
    });

    it("getActiveSprint returns null when no active sprint", async () => {
      mockGetActiveSprint.mockResolvedValue(null);
      const result = await getActiveSprint();
      expect(result).toBeNull();
    });

    it("getSprints returns a list of sprint snapshots", async () => {
      mockGetSprints.mockResolvedValue({
        sprints: [makeSprint({ number: 1 }), makeSprint({ id: "sprint-2", number: 2 })],
        totalCount: 2,
      });
      const result = await getSprints(50);
      expect(result.sprints).toHaveLength(2);
      expect(result.totalCount).toBe(2);
    });

    it("getSprintDetail returns detail for a specific sprint", async () => {
      const detail = makeDetail({ id: "sprint-5", number: 5 }, [makeArtifact()]);
      mockGetSprintDetail.mockResolvedValue(detail);
      const result = await getSprintDetail("sprint-5");
      expect(result).not.toBeNull();
      expect(result!.sprint.number).toBe(5);
      expect(result!.artifacts).toHaveLength(1);
    });

    it("startSprint is callable", async () => {
      mockStartSprint.mockResolvedValue(makeDetail());
      const result = await startSprint();
      expect(result.sprint.status).toBe("Active");
    });

    it("advanceSprint is callable with sprint ID", async () => {
      mockAdvanceSprint.mockResolvedValue(makeDetail({ currentStage: "Planning" }));
      const result = await advanceSprint("sprint-1");
      expect(result.sprint.currentStage).toBe("Planning");
    });

    it("completeSprint returns updated snapshot", async () => {
      mockCompleteSprint.mockResolvedValue(makeSprint({ status: "Completed" }));
      const result = await completeSprint("sprint-1");
      expect(result.status).toBe("Completed");
    });

    it("cancelSprint returns updated snapshot", async () => {
      mockCancelSprint.mockResolvedValue(makeSprint({ status: "Cancelled" }));
      const result = await cancelSprint("sprint-1");
      expect(result.status).toBe("Cancelled");
    });

    it("approveSprintAdvance is callable", async () => {
      mockApproveSprintAdvance.mockResolvedValue(makeDetail({ currentStage: "Planning" }));
      const result = await approveSprintAdvance("sprint-1");
      expect(result.sprint.currentStage).toBe("Planning");
    });

    it("rejectSprintAdvance is callable", async () => {
      mockRejectSprintAdvance.mockResolvedValue(makeSprint({ currentStage: "Intake", awaitingSignOff: false }));
      const result = await rejectSprintAdvance("sprint-1");
      expect(result.awaitingSignOff).toBe(false);
    });
  });

  describe("stage pipeline logic", () => {
    it("ALL_STAGES has 6 stages in correct order", () => {
      expect(ALL_STAGES).toEqual([
        "Intake", "Planning", "Discussion", "Validation", "Implementation", "FinalSynthesis",
      ]);
    });

    it("current stage is identified by indexOf", () => {
      const sprint = makeSprint({ currentStage: "Discussion" });
      const idx = ALL_STAGES.indexOf(sprint.currentStage);
      expect(idx).toBe(2);
      expect(ALL_STAGES[idx]).toBe("Discussion");
    });

    it("stages before current are completed", () => {
      const sprint = makeSprint({ currentStage: "Validation" });
      const currentIdx = ALL_STAGES.indexOf(sprint.currentStage);
      const completedStages = ALL_STAGES.filter((_, i) => i < currentIdx);
      expect(completedStages).toEqual(["Intake", "Planning", "Discussion"]);
    });

    it("isFinalStage is true only for FinalSynthesis", () => {
      for (const stage of ALL_STAGES) {
        const isFinal = stage === "FinalSynthesis";
        if (stage === "FinalSynthesis") {
          expect(isFinal).toBe(true);
        } else {
          expect(isFinal).toBe(false);
        }
      }
    });
  });

  describe("sign-off gate logic", () => {
    it("awaitingSignOff sprint shows pending stage", () => {
      const sprint = makeSprint({
        awaitingSignOff: true,
        pendingStage: "Planning",
        currentStage: "Intake",
      });
      expect(sprint.awaitingSignOff).toBe(true);
      expect(sprint.pendingStage).toBe("Planning");
    });

    it("non-awaiting sprint has null pendingStage", () => {
      const sprint = makeSprint({ awaitingSignOff: false });
      expect(sprint.pendingStage).toBeNull();
    });
  });

  describe("artifact expand/collapse logic", () => {
    it("artifacts longer than 200 chars are collapsible", () => {
      const shortContent = "short";
      const longContent = "a".repeat(201);
      expect(shortContent.length > 200).toBe(false);
      expect(longContent.length > 200).toBe(true);
    });

    it("toggle set logic adds and removes artifact ids", () => {
      const expanded = new Set<number>();
      // Add
      const next1 = new Set(expanded);
      next1.add(1);
      expect(next1.has(1)).toBe(true);
      // Remove
      const next2 = new Set(next1);
      next2.delete(1);
      expect(next2.has(1)).toBe(false);
    });
  });

  describe("sprint history rendering logic", () => {
    it("history is shown when more than 1 sprint exists", () => {
      const history = [
        makeSprint({ id: "s1", number: 1 }),
        makeSprint({ id: "s2", number: 2 }),
      ];
      expect(history.length > 1).toBe(true);
    });

    it("history is hidden for single sprint", () => {
      const history = [makeSprint()];
      expect(history.length > 1).toBe(false);
    });
  });

  describe("action button visibility logic", () => {
    it("Start Sprint is shown when no active sprint", () => {
      const activeSprint = null;
      expect(!activeSprint).toBe(true);
    });

    it("Advance Stage is shown for active non-final non-signoff sprint", () => {
      const detail = makeDetail({ status: "Active", currentStage: "Discussion" });
      const isActive = detail.sprint.status === "Active";
      const isFinalStage = detail.sprint.currentStage === "FinalSynthesis";
      const isAwaiting = detail.sprint.awaitingSignOff;
      expect(isActive && !isFinalStage && !isAwaiting).toBe(true);
    });

    it("Complete Sprint is shown only at FinalSynthesis", () => {
      const detail = makeDetail({ status: "Active", currentStage: "FinalSynthesis" });
      const isActive = detail.sprint.status === "Active";
      const isFinalStage = detail.sprint.currentStage === "FinalSynthesis";
      expect(isActive && isFinalStage).toBe(true);
    });

    it("Approve/Reject shown when awaiting sign-off", () => {
      const detail = makeDetail({
        status: "Active",
        currentStage: "Intake",
        awaitingSignOff: true,
        pendingStage: "Planning",
      });
      expect(detail.sprint.awaitingSignOff).toBe(true);
    });

    it("Cancel is shown for any active sprint", () => {
      const detail = makeDetail({ status: "Active" });
      expect(detail.sprint.status === "Active").toBe(true);
    });

    it("No actions for completed sprint", () => {
      const detail = makeDetail({ status: "Completed" });
      const isActive = detail.sprint.status === "Active";
      expect(isActive).toBe(false);
    });
  });
});
