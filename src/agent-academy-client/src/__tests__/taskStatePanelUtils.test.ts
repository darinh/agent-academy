import { describe, expect, it } from "vitest";
import {
  PHASES,
  taskStatusBadge,
  workstreamBadge,
  phaseBadge,
} from "../taskStatePanelUtils";
import type { TaskStatus } from "../api";

describe("taskStatePanelUtils", () => {
  /* ── PHASES constant ────────────────────────────────────────── */

  describe("PHASES", () => {
    it("has 6 phases in correct order", () => {
      expect(PHASES).toEqual([
        "Intake", "Planning", "Discussion",
        "Validation", "Implementation", "FinalSynthesis",
      ]);
    });

  });

  /* ── taskStatusBadge ────────────────────────────────────────── */

  describe("taskStatusBadge", () => {
    const cases: [TaskStatus, string][] = [
      ["Active", "active"],
      ["Blocked", "err"],
      ["AwaitingValidation", "warn"],
      ["Completed", "done"],
      ["Cancelled", "cancel"],
      ["Queued", "info"],
    ];

    it.each(cases)("returns %s badge for %s status", (status, badge) => {
      expect(taskStatusBadge(status)).toBe(badge);
    });
  });

  /* ── workstreamBadge ────────────────────────────────────────── */

  describe("workstreamBadge", () => {
    const cases: [string, string][] = [
      ["Completed", "done"],
      ["InProgress", "active"],
      ["Blocked", "warn"],
      ["Ready", "info"],
      ["Unknown", "muted"],
      ["", "muted"],
    ];

    it.each(cases)('returns "%s" badge for "%s" workstream status', (status, badge) => {
      expect(workstreamBadge(status)).toBe(badge);
    });
  });

  /* ── phaseBadge ─────────────────────────────────────────────── */

  describe("phaseBadge", () => {
    it("returns done for phases before current", () => {
      expect(phaseBadge("Intake", "Discussion")).toBe("done");
      expect(phaseBadge("Planning", "Discussion")).toBe("done");
    });

    it("returns active for current phase", () => {
      expect(phaseBadge("Discussion", "Discussion")).toBe("active");
    });

    it("returns muted for phases after current", () => {
      expect(phaseBadge("Validation", "Discussion")).toBe("muted");
      expect(phaseBadge("FinalSynthesis", "Discussion")).toBe("muted");
    });

    it("handles first phase as current", () => {
      expect(phaseBadge("Intake", "Intake")).toBe("active");
      expect(phaseBadge("Planning", "Intake")).toBe("muted");
    });

    it("handles last phase as current", () => {
      expect(phaseBadge("Implementation", "FinalSynthesis")).toBe("done");
      expect(phaseBadge("FinalSynthesis", "FinalSynthesis")).toBe("active");
    });
  });
});
