import { describe, expect, it } from "vitest";
import type { CollaborationPhase, TaskStatus } from "../api";
import {
  PHASES,
  phaseColor,
  taskStatusColor,
  workstreamColor,
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

describe("taskStatusColor", () => {
  const cases: Array<[TaskStatus, string]> = [
    ["Active", "success"],
    ["Blocked", "danger"],
    ["AwaitingValidation", "warning"],
    ["Completed", "informative"],
    ["Cancelled", "subtle"],
    ["Queued", "important"],
  ];

  it.each(cases)("returns '%s' → '%s'", (status, expected) => {
    expect(taskStatusColor(status)).toBe(expected);
  });

  it("returns 'important' for InReview (default branch)", () => {
    expect(taskStatusColor("InReview")).toBe("important");
  });

  it("returns 'important' for ChangesRequested (default branch)", () => {
    expect(taskStatusColor("ChangesRequested")).toBe("important");
  });

  it("returns 'important' for Approved (default branch)", () => {
    expect(taskStatusColor("Approved")).toBe("important");
  });

  it("returns 'important' for Merging (default branch)", () => {
    expect(taskStatusColor("Merging")).toBe("important");
  });
});

/* ------------------------------------------------------------------ */
/*  workstreamColor                                                    */
/* ------------------------------------------------------------------ */

describe("workstreamColor", () => {
  const cases: Array<[string, string]> = [
    ["Completed", "success"],
    ["InProgress", "informative"],
    ["Blocked", "warning"],
    ["Ready", "important"],
  ];

  it.each(cases)("returns '%s' → '%s'", (status, expected) => {
    expect(workstreamColor(status)).toBe(expected);
  });

  it("returns 'subtle' for unknown statuses", () => {
    expect(workstreamColor("")).toBe("subtle");
    expect(workstreamColor("Pending")).toBe("subtle");
    expect(workstreamColor("Unknown")).toBe("subtle");
  });
});

/* ------------------------------------------------------------------ */
/*  phaseColor                                                         */
/* ------------------------------------------------------------------ */

describe("phaseColor", () => {
  it("returns 'success' for phases before the current phase", () => {
    expect(phaseColor("Intake", "Discussion")).toBe("success");
    expect(phaseColor("Planning", "Discussion")).toBe("success");
  });

  it("returns 'informative' for the current phase", () => {
    expect(phaseColor("Discussion", "Discussion")).toBe("informative");
    expect(phaseColor("Intake", "Intake")).toBe("informative");
    expect(phaseColor("FinalSynthesis", "FinalSynthesis")).toBe("informative");
  });

  it("returns 'subtle' for phases after the current phase", () => {
    expect(phaseColor("Validation", "Discussion")).toBe("subtle");
    expect(phaseColor("Implementation", "Discussion")).toBe("subtle");
    expect(phaseColor("FinalSynthesis", "Discussion")).toBe("subtle");
  });

  it("handles first phase as current", () => {
    expect(phaseColor("Intake", "Intake")).toBe("informative");
    expect(phaseColor("Planning", "Intake")).toBe("subtle");
    expect(phaseColor("FinalSynthesis", "Intake")).toBe("subtle");
  });

  it("handles last phase as current", () => {
    expect(phaseColor("Intake", "FinalSynthesis")).toBe("success");
    expect(phaseColor("Implementation", "FinalSynthesis")).toBe("success");
    expect(phaseColor("FinalSynthesis", "FinalSynthesis")).toBe("informative");
  });

  it("maps every phase to 'success' or 'informative' when current is FinalSynthesis", () => {
    const results = PHASES.map((p) => phaseColor(p, "FinalSynthesis"));
    expect(results.filter((c) => c === "subtle")).toHaveLength(0);
  });

  it("maps every phase to 'subtle' or 'informative' when current is Intake", () => {
    const results = PHASES.map((p) => phaseColor(p, "Intake"));
    expect(results.filter((c) => c === "success")).toHaveLength(0);
  });

  it("returns correct gradient across all phases for midpoint current", () => {
    // With current = "Validation" (index 3):
    // Intake(0)=success, Planning(1)=success, Discussion(2)=success,
    // Validation(3)=informative, Implementation(4)=subtle, FinalSynthesis(5)=subtle
    const current: CollaborationPhase = "Validation";
    expect(phaseColor("Intake", current)).toBe("success");
    expect(phaseColor("Planning", current)).toBe("success");
    expect(phaseColor("Discussion", current)).toBe("success");
    expect(phaseColor("Validation", current)).toBe("informative");
    expect(phaseColor("Implementation", current)).toBe("subtle");
    expect(phaseColor("FinalSynthesis", current)).toBe("subtle");
  });
});
