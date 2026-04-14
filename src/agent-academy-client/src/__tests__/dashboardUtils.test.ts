// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import {
  phaseColor,
  TIME_RANGES,
  TIME_RANGE_KEY,
  loadTimeRange,
  saveTimeRange,
} from "../dashboardUtils";
import type { CollaborationPhase } from "../api";

describe("dashboardUtils", () => {
  /* ── phaseColor ─────────────────────────────────────────────── */

  describe("phaseColor", () => {
    const expected: [CollaborationPhase, string][] = [
      ["Intake", "info"],
      ["Planning", "warn"],
      ["Discussion", "active"],
      ["Validation", "review"],
      ["Implementation", "ok"],
      ["FinalSynthesis", "muted"],
    ];

    it.each(expected)("returns %s badge for %s phase", (phase, color) => {
      expect(phaseColor(phase)).toBe(color);
    });
  });

  /* ── TIME_RANGES constant ───────────────────────────────────── */

  describe("TIME_RANGES", () => {
    it("has 4 entries", () => {
      expect(TIME_RANGES).toHaveLength(4);
    });

    it("includes 24h, 7d, 30d, and All", () => {
      const labels = TIME_RANGES.map((r) => r.label);
      expect(labels).toEqual(["24h", "7d", "30d", "All"]);
    });

    it("All range has undefined value", () => {
      const all = TIME_RANGES.find((r) => r.label === "All");
      expect(all?.value).toBeUndefined();
    });
  });

  /* ── Time range persistence ─────────────────────────────────── */

  describe("loadTimeRange / saveTimeRange", () => {
    let storage: Record<string, string>;

    beforeEach(() => {
      storage = {};
      vi.spyOn(Storage.prototype, "getItem").mockImplementation((key) => storage[key] ?? null);
      vi.spyOn(Storage.prototype, "setItem").mockImplementation((key, value) => {
        storage[key] = value;
      });
    });

    afterEach(() => {
      vi.restoreAllMocks();
    });

    it("returns undefined (all) when no stored value", () => {
      expect(loadTimeRange()).toBeUndefined();
    });

    it("round-trips 24h range", () => {
      saveTimeRange(24);
      expect(loadTimeRange()).toBe(24);
    });

    it("round-trips 7d range", () => {
      saveTimeRange(168);
      expect(loadTimeRange()).toBe(168);
    });

    it("round-trips 30d range", () => {
      saveTimeRange(720);
      expect(loadTimeRange()).toBe(720);
    });

    it("round-trips All (undefined) range", () => {
      saveTimeRange(undefined);
      expect(loadTimeRange()).toBeUndefined();
    });

    it("returns undefined for invalid stored value", () => {
      storage[TIME_RANGE_KEY] = "invalid";
      expect(loadTimeRange()).toBeUndefined();
    });

    it("returns undefined for unrecognized numeric value", () => {
      storage[TIME_RANGE_KEY] = "999";
      expect(loadTimeRange()).toBeUndefined();
    });

    it("uses correct storage key", () => {
      saveTimeRange(24);
      expect(Storage.prototype.setItem).toHaveBeenCalledWith(
        TIME_RANGE_KEY,
        "24",
      );
    });
  });
});
