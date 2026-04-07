import { describe, expect, it } from "vitest";
import type { CollaborationPhase, TaskStatus } from "../api";
import {
  PHASES,
  phaseBadge,
  taskStatusBadge,
  workstreamBadge,
} from "../taskStatePanelUtils";

/* ------------------------------------------------------------------ */
/*  PHASES constant                                                    */
/* ------------------------------------------------------------------ */

describe("PHASES", () => {
  it("contains 6 phases in the expected order", () => {
    expect(PHASES).toEqual([
      "Intake", "Planning", "Discussion",
      "Validation", "Implementation", "FinalSynthesis",
    ]);
  });

  it("is a readonly array (as const)", () => {
    expect(Array.isArray(PHASES)).toBe(true);
    expect(PHASES).toHaveLength(6);
  });
});

/* ------------------------------------------------------------------ */
/*  taskStatusColor                                                    */
/* ------------------------------------------------------------------ */

describe("taskStatusBadge", () => {
  const cases: Array<[TaskStatus, string]> = [
    ["Active", "active"],
    ["Blocked", "err"],
    ["AwaitingValidation", "warn"],
    ["Completed", "done"],
    ["Cancelled", "cancel"],
    ["Queued", "info"],
  ];

  it.each(cases)("returns '%s' → '%s'", (status, expected) => {
    expect(taskStatusBadge(status)).toBe(expected);
  });

  it("returns 'info' for InReview (default branch)", () => {
    expect(taskStatusBadge("InReview")).toBe("info");
  });

  it("returns 'info' for ChangesRequested (default branch)", () => {
    expect(taskStatusBadge("ChangesRequested")).toBe("info");
  });

  it("returns 'info' for Approved (default branch)", () => {
    expect(taskStatusBadge("Approved")).toBe("info");
  });

  it("returns 'info' for Merging (default branch)", () => {
    expect(taskStatusBadge("Merging")).toBe("info");
  });
});

/* ------------------------------------------------------------------ */
/*  workstreamColor                                                    */
/* ------------------------------------------------------------------ */

describe("workstreamBadge", () => {
  const cases: Array<[string, string]> = [
    ["Completed", "done"],
    ["InProgress", "active"],
    ["Blocked", "warn"],
    ["Ready", "info"],
  ];

  it.each(cases)("returns '%s' → '%s'", (status, expected) => {
    expect(workstreamBadge(status)).toBe(expected);
  });

  it("returns 'muted' for unknown statuses", () => {
    expect(workstreamBadge("")).toBe("muted");
    expect(workstreamBadge("Pending")).toBe("muted");
    expect(workstreamBadge("Unknown")).toBe("muted");
  });
});

/* ------------------------------------------------------------------ */
/*  phaseColor                                                         */
/* ------------------------------------------------------------------ */

describe("phaseBadge", () => {
  it("returns 'done' for phases before the current phase", () => {
    expect(phaseBadge("Intake", "Discussion")).toBe("done");
    expect(phaseBadge("Planning", "Discussion")).toBe("done");
  });

  it("returns 'active' for the current phase", () => {
    expect(phaseBadge("Discussion", "Discussion")).toBe("active");
    expect(phaseBadge("Intake", "Intake")).toBe("active");
    expect(phaseBadge("FinalSynthesis", "FinalSynthesis")).toBe("active");
  });

  it("returns 'muted' for phases after the current phase", () => {
    expect(phaseBadge("Validation", "Discussion")).toBe("muted");
    expect(phaseBadge("Implementation", "Discussion")).toBe("muted");
    expect(phaseBadge("FinalSynthesis", "Discussion")).toBe("muted");
  });

  it("handles first phase as current", () => {
    expect(phaseBadge("Intake", "Intake")).toBe("active");
    expect(phaseBadge("Planning", "Intake")).toBe("muted");
    expect(phaseBadge("FinalSynthesis", "Intake")).toBe("muted");
  });

  it("handles last phase as current", () => {
    expect(phaseBadge("Intake", "FinalSynthesis")).toBe("done");
    expect(phaseBadge("Implementation", "FinalSynthesis")).toBe("done");
    expect(phaseBadge("FinalSynthesis", "FinalSynthesis")).toBe("active");
  });

  it("maps every phase to 'done' or 'active' when current is FinalSynthesis", () => {
    const results = PHASES.map((p) => phaseBadge(p, "FinalSynthesis"));
    expect(results.filter((c) => c === "muted")).toHaveLength(0);
  });

  it("maps every phase to 'muted' or 'active' when current is Intake", () => {
    const results = PHASES.map((p) => phaseBadge(p, "Intake"));
    expect(results.filter((c) => c === "done")).toHaveLength(0);
  });

  it("returns correct gradient across all phases for midpoint current", () => {
    const current: CollaborationPhase = "Validation";
    expect(phaseBadge("Intake", current)).toBe("done");
    expect(phaseBadge("Planning", current)).toBe("done");
    expect(phaseBadge("Discussion", current)).toBe("done");
    expect(phaseBadge("Validation", current)).toBe("active");
    expect(phaseBadge("Implementation", current)).toBe("muted");
    expect(phaseBadge("FinalSynthesis", current)).toBe("muted");
  });
});
