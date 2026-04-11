import { describe, expect, it, beforeEach } from "vitest";
import type {
  TaskSnapshot,
  TaskStatus,
  SpecTaskLink,
  SpecLinkType,
  EvidenceRow,
  EvidencePhase,
  GateCheckResult,
} from "../api";

// ── Helpers (mirrored from TaskListPanel) ─────────────────────────────

function specLinkBadge(type: string): string {
  switch (type) {
    case "Implements": return "ok";
    case "Modifies":   return "warn";
    case "Fixes":      return "err";
    case "References": return "info";
    default:           return "muted";
  }
}

function evidencePhaseBadge(phase: string): string {
  switch (phase) {
    case "Baseline": return "info";
    case "After":    return "ok";
    case "Review":   return "review";
    default:         return "muted";
  }
}

function makeTask(overrides: Partial<TaskSnapshot> = {}): TaskSnapshot {
  return {
    id: "task-1",
    title: "Test task",
    description: "A test task",
    successCriteria: "",
    status: "Active",
    currentPhase: "Implementation",
    currentPlan: "",
    validationStatus: "",
    validationSummary: "",
    implementationStatus: "",
    implementationSummary: "",
    preferredRoles: [],
    createdAt: "2026-04-01T00:00:00Z",
    updatedAt: "2026-04-01T12:00:00Z",
    ...overrides,
  };
}

function makeSpecLink(overrides: Partial<SpecTaskLink> = {}): SpecTaskLink {
  return {
    id: "link-1",
    taskId: "task-1",
    specSectionId: "010-task-management/§3",
    linkType: "Implements",
    linkedByAgentId: "software-engineer-1",
    linkedByAgentName: "Hephaestus",
    note: null,
    createdAt: "2026-04-01T00:00:00Z",
    ...overrides,
  };
}

function makeEvidence(overrides: Partial<EvidenceRow> = {}): EvidenceRow {
  return {
    id: "ev-1",
    phase: "After",
    checkName: "build",
    tool: "bash",
    command: "dotnet build",
    exitCode: 0,
    output: "Build succeeded",
    passed: true,
    agentName: "Hephaestus",
    createdAt: "2026-04-01T00:00:00Z",
    ...overrides,
  };
}

// ── Detail Cache Logic ────────────────────────────────────────────────

interface DetailCacheEntry {
  updatedAt: string;
  specLinks?: SpecTaskLink[];
  evidence?: EvidenceRow[];
  gate?: GateCheckResult;
}

const detailCache = new Map<string, DetailCacheEntry>();

function getCached(taskId: string, updatedAt: string): DetailCacheEntry {
  const c = detailCache.get(taskId);
  if (c && c.updatedAt === updatedAt) return c;
  const fresh: DetailCacheEntry = { updatedAt };
  detailCache.set(taskId, fresh);
  return fresh;
}

// ── Tests ─────────────────────────────────────────────────────────────

describe("TaskListPanel enhanced features", () => {

  describe("specLinkBadge", () => {
    it("Implements returns ok", () => {
      expect(specLinkBadge("Implements")).toBe("ok");
    });

    it("Modifies returns warn", () => {
      expect(specLinkBadge("Modifies")).toBe("warn");
    });

    it("Fixes returns err", () => {
      expect(specLinkBadge("Fixes")).toBe("err");
    });

    it("References returns info", () => {
      expect(specLinkBadge("References")).toBe("info");
    });

    it("unknown type returns muted", () => {
      expect(specLinkBadge("Unknown")).toBe("muted");
    });
  });

  describe("evidencePhaseBadge", () => {
    it("Baseline returns info", () => {
      expect(evidencePhaseBadge("Baseline")).toBe("info");
    });

    it("After returns ok", () => {
      expect(evidencePhaseBadge("After")).toBe("ok");
    });

    it("Review returns review", () => {
      expect(evidencePhaseBadge("Review")).toBe("review");
    });

    it("unknown phase returns muted", () => {
      expect(evidencePhaseBadge("Unknown")).toBe("muted");
    });
  });

  describe("assign eligibility", () => {
    it("Queued task with no assignee can be assigned", () => {
      const task = makeTask({ status: "Queued", assignedAgentId: null });
      const canAssign = task.status === "Queued" && !task.assignedAgentId;
      expect(canAssign).toBe(true);
    });

    it("Queued task with an assignee cannot be assigned", () => {
      const task = makeTask({ status: "Queued", assignedAgentId: "eng-1" });
      const canAssign = task.status === "Queued" && !task.assignedAgentId;
      expect(canAssign).toBe(false);
    });

    it("Active task cannot be assigned even without assignee", () => {
      const task = makeTask({ status: "Active", assignedAgentId: null });
      const canAssign = task.status === "Queued" && !task.assignedAgentId;
      expect(canAssign).toBe(false);
    });

    it.each<TaskStatus>([
      "Active", "Blocked", "InReview", "Completed", "Cancelled",
      "ChangesRequested", "Approved", "Merging", "AwaitingValidation",
    ])("status %s cannot be assigned", (status) => {
      const task = makeTask({ status, assignedAgentId: null });
      const canAssign = task.status === "Queued" && !task.assignedAgentId;
      expect(canAssign).toBe(false);
    });
  });

  describe("gate check eligibility", () => {
    const GATE_ELIGIBLE: TaskStatus[] = ["Active", "AwaitingValidation", "InReview"];

    it.each(GATE_ELIGIBLE)("status %s is eligible for gate check", (status) => {
      const canCheck = GATE_ELIGIBLE.includes(status);
      expect(canCheck).toBe(true);
    });

    it.each<TaskStatus>([
      "Queued", "Blocked", "ChangesRequested", "Approved", "Merging", "Completed", "Cancelled",
    ])("status %s is NOT eligible for gate check", (status) => {
      const canCheck = GATE_ELIGIBLE.includes(status);
      expect(canCheck).toBe(false);
    });
  });

  describe("detail cache", () => {
    beforeEach(() => {
      detailCache.clear();
    });

    it("creates a fresh entry for unknown task", () => {
      const entry = getCached("task-new", "2026-04-01T00:00:00Z");
      expect(entry.updatedAt).toBe("2026-04-01T00:00:00Z");
      expect(entry.specLinks).toBeUndefined();
      expect(entry.evidence).toBeUndefined();
      expect(entry.gate).toBeUndefined();
    });

    it("returns same entry when updatedAt matches", () => {
      const entry1 = getCached("task-1", "2026-04-01T00:00:00Z");
      entry1.specLinks = [makeSpecLink()];
      const entry2 = getCached("task-1", "2026-04-01T00:00:00Z");
      expect(entry2.specLinks).toHaveLength(1);
      expect(entry2).toBe(entry1);
    });

    it("invalidates cache when updatedAt changes", () => {
      const entry1 = getCached("task-1", "2026-04-01T00:00:00Z");
      entry1.specLinks = [makeSpecLink()];
      const entry2 = getCached("task-1", "2026-04-01T12:00:00Z");
      expect(entry2.specLinks).toBeUndefined();
      expect(entry2).not.toBe(entry1);
    });

    it("stores evidence in cache entry", () => {
      const entry = getCached("task-1", "2026-04-01T00:00:00Z");
      const ev = makeEvidence();
      entry.evidence = [ev];
      const retrieved = getCached("task-1", "2026-04-01T00:00:00Z");
      expect(retrieved.evidence).toHaveLength(1);
      expect(retrieved.evidence![0].checkName).toBe("build");
    });

    it("stores gate result in cache entry", () => {
      const entry = getCached("task-1", "2026-04-01T00:00:00Z");
      entry.gate = {
        taskId: "task-1",
        currentPhase: "Active",
        targetPhase: "AwaitingValidation",
        met: false,
        requiredChecks: 2,
        passedChecks: 1,
        missingChecks: ["tests"],
        evidence: [{ phase: "After", checkName: "build", passed: true, agentName: "Hephaestus" }],
        message: "❌ Gates NOT met",
      };
      const retrieved = getCached("task-1", "2026-04-01T00:00:00Z");
      expect(retrieved.gate!.met).toBe(false);
      expect(retrieved.gate!.missingChecks).toEqual(["tests"]);
    });
  });

  describe("SpecTaskLink model", () => {
    it("supports all link types", () => {
      const types: SpecLinkType[] = ["Implements", "Modifies", "Fixes", "References"];
      types.forEach((t) => {
        const link = makeSpecLink({ linkType: t });
        expect(link.linkType).toBe(t);
      });
    });

    it("note is optional", () => {
      const withNote = makeSpecLink({ note: "See section 3.1" });
      const withoutNote = makeSpecLink({ note: null });
      expect(withNote.note).toBe("See section 3.1");
      expect(withoutNote.note).toBeNull();
    });
  });

  describe("EvidenceRow model", () => {
    it("supports all evidence phases", () => {
      const phases: EvidencePhase[] = ["Baseline", "After", "Review"];
      phases.forEach((p) => {
        const ev = makeEvidence({ phase: p });
        expect(ev.phase).toBe(p);
      });
    });

    it("passed can be true or false", () => {
      expect(makeEvidence({ passed: true }).passed).toBe(true);
      expect(makeEvidence({ passed: false }).passed).toBe(false);
    });

    it("command and output are optional", () => {
      const ev = makeEvidence({ command: null, output: null, exitCode: null });
      expect(ev.command).toBeNull();
      expect(ev.output).toBeNull();
      expect(ev.exitCode).toBeNull();
    });
  });

  describe("GateCheckResult model", () => {
    it("met gate has passedChecks >= requiredChecks", () => {
      const gate: GateCheckResult = {
        taskId: "task-1",
        currentPhase: "Active",
        targetPhase: "AwaitingValidation",
        met: true,
        requiredChecks: 2,
        passedChecks: 3,
        missingChecks: [],
        evidence: [],
        message: "✅ Gates met",
      };
      expect(gate.met).toBe(true);
      expect(gate.passedChecks).toBeGreaterThanOrEqual(gate.requiredChecks);
    });

    it("unmet gate lists missing checks", () => {
      const gate: GateCheckResult = {
        taskId: "task-1",
        currentPhase: "AwaitingValidation",
        targetPhase: "InReview",
        met: false,
        requiredChecks: 2,
        passedChecks: 1,
        missingChecks: ["tests", "lint"],
        evidence: [{ phase: "After", checkName: "build", passed: true, agentName: "Hephaestus" }],
        message: "❌ Gates NOT met",
      };
      expect(gate.met).toBe(false);
      expect(gate.missingChecks).toContain("tests");
      expect(gate.missingChecks).toContain("lint");
    });
  });
});
