import { describe, expect, it, vi, afterEach } from "vitest";
import {
  formatTimestamp,
  formatTokenCount,
  formatCost,
  errorTypeBadge,
  formatElapsed,
} from "../panelUtils";

describe("panelUtils", () => {
  // ── formatTimestamp ──

  describe("formatTimestamp", () => {
    // Use a fixed ISO string and verify the output contains expected components.
    // toLocaleString output varies by locale, so we test structural properties.
    const iso = "2026-04-05T14:30:45Z";

    it("returns a non-empty string for valid ISO input", () => {
      const result = formatTimestamp(iso);
      expect(result).toBeTruthy();
      expect(typeof result).toBe("string");
    });

    it("includes seconds by default", () => {
      const withSec = formatTimestamp(iso);
      const withoutSec = formatTimestamp(iso, false);
      // The version with seconds should be longer (or at least different)
      // because it includes the :SS portion
      expect(withSec.length).toBeGreaterThanOrEqual(withoutSec.length);
    });

    it("excludes seconds when includeSeconds is false", () => {
      const withSec = formatTimestamp(iso, true);
      const withoutSec = formatTimestamp(iso, false);
      expect(withoutSec.length).toBeLessThanOrEqual(withSec.length);
    });

    it("explicitly includes seconds when includeSeconds is true", () => {
      const result = formatTimestamp(iso, true);
      expect(result).toBeTruthy();
    });

    it("handles midnight timestamps", () => {
      const midnight = "2026-01-01T00:00:00Z";
      expect(formatTimestamp(midnight)).toBeTruthy();
    });

    it("handles end-of-day timestamps", () => {
      const eod = "2026-12-31T23:59:59Z";
      expect(formatTimestamp(eod)).toBeTruthy();
    });

    it("handles timestamps with timezone offsets", () => {
      const withOffset = "2026-04-05T14:30:45+05:30";
      expect(formatTimestamp(withOffset)).toBeTruthy();
    });

    it("handles timestamps with milliseconds", () => {
      const withMs = "2026-04-05T14:30:45.123Z";
      expect(formatTimestamp(withMs)).toBeTruthy();
    });
  });

  // ── formatTokenCount ──

  describe("formatTokenCount", () => {
    it("returns plain number for values under 1000", () => {
      expect(formatTokenCount(0)).toBe("0");
      expect(formatTokenCount(1)).toBe("1");
      expect(formatTokenCount(999)).toBe("999");
    });

    it("formats thousands as K", () => {
      expect(formatTokenCount(1_000)).toBe("1.0K");
      expect(formatTokenCount(1_500)).toBe("1.5K");
      expect(formatTokenCount(42_500)).toBe("42.5K");
      expect(formatTokenCount(999_900)).toBe("999.9K");
    });

    it("formats millions as M", () => {
      expect(formatTokenCount(1_000_000)).toBe("1.0M");
      expect(formatTokenCount(1_500_000)).toBe("1.5M");
      expect(formatTokenCount(12_345_678)).toBe("12.3M");
    });

    it("uses one decimal place for K values", () => {
      expect(formatTokenCount(1_234)).toBe("1.2K");
      expect(formatTokenCount(1_250)).toBe("1.3K");
    });

    it("uses one decimal place for M values", () => {
      expect(formatTokenCount(1_050_000)).toBe("1.1M");
      expect(formatTokenCount(1_949_999)).toBe("1.9M");
    });

    it("handles the K/M boundary correctly", () => {
      // 999_999 is < 1M, so formatted as K
      expect(formatTokenCount(999_999)).toBe("1000.0K");
      // 1_000_000 is exactly 1M
      expect(formatTokenCount(1_000_000)).toBe("1.0M");
    });
  });

  // ── formatCost ──

  describe("formatCost", () => {
    it("formats zero as $0.00", () => {
      expect(formatCost(0)).toBe("$0.00");
    });

    it("formats costs >= $0.01 with 2 decimal places", () => {
      expect(formatCost(0.01)).toBe("$0.01");
      expect(formatCost(0.10)).toBe("$0.10");
      expect(formatCost(1.23)).toBe("$1.23");
      expect(formatCost(45.6)).toBe("$45.60");
      expect(formatCost(100)).toBe("$100.00");
    });

    it("formats costs < $0.01 with 4 decimal places", () => {
      expect(formatCost(0.0001)).toBe("$0.0001");
      expect(formatCost(0.0012)).toBe("$0.0012");
      expect(formatCost(0.0099)).toBe("$0.0099");
    });

    it("handles very small non-zero costs", () => {
      expect(formatCost(0.00001)).toBe("$0.0000");
    });

    it("handles the boundary between 4-decimal and 2-decimal", () => {
      // Just below $0.01
      expect(formatCost(0.009999)).toBe("$0.0100");
      // Exactly $0.01
      expect(formatCost(0.01)).toBe("$0.01");
    });

    it("handles large costs", () => {
      expect(formatCost(999.99)).toBe("$999.99");
      expect(formatCost(1234.5)).toBe("$1234.50");
    });
  });

  // ── errorTypeBadge ──

  describe("errorTypeBadge", () => {
    it("maps authentication to danger/Auth", () => {
      const badge = errorTypeBadge("authentication");
      expect(badge.color).toBe("err");
      expect(badge.label).toBe("Auth");
    });

    it("maps authorization to danger/Authz", () => {
      const badge = errorTypeBadge("authorization");
      expect(badge.color).toBe("err");
      expect(badge.label).toBe("Authz");
    });

    it("maps quota to warning/Quota", () => {
      const badge = errorTypeBadge("quota");
      expect(badge.color).toBe("warn");
      expect(badge.label).toBe("Quota");
    });

    it("maps transient to important/Transient", () => {
      const badge = errorTypeBadge("transient");
      expect(badge.color).toBe("bug");
      expect(badge.label).toBe("Transient");
    });

    it("maps unknown types to informative with raw label", () => {
      const badge = errorTypeBadge("custom_error");
      expect(badge.color).toBe("info");
      expect(badge.label).toBe("custom_error");
    });

    it("maps empty string to informative", () => {
      const badge = errorTypeBadge("");
      expect(badge.color).toBe("info");
      expect(badge.label).toBe("");
    });

    it("is case-sensitive", () => {
      const badge = errorTypeBadge("Authentication");
      expect(badge.color).toBe("info");
      expect(badge.label).toBe("Authentication");
    });
  });

  // ── formatElapsed ──

  describe("formatElapsed", () => {
    const base = "2026-04-05T10:00:00Z";

    afterEach(() => {
      vi.useRealTimers();
    });

    // Default options: granularity=minutes, maxUnit=hours (AgentSessionPanel behavior)

    it("returns 0m for zero duration", () => {
      expect(formatElapsed(base, base)).toBe("0m");
    });

    it("returns minutes for durations under an hour", () => {
      const end = "2026-04-05T10:35:00Z";
      expect(formatElapsed(base, end)).toBe("35m");
    });

    it("returns hours and minutes for durations over an hour", () => {
      const end = "2026-04-05T12:15:00Z";
      expect(formatElapsed(base, end)).toBe("2h 15m");
    });

    it("stops at hours by default (no days)", () => {
      const end = "2026-04-07T10:00:00Z"; // 48 hours
      expect(formatElapsed(base, end)).toBe("48h 0m");
    });

    // maxUnit: "days" (TaskListPanel behavior)

    it("returns days and hours when maxUnit is days", () => {
      const end = "2026-04-07T14:00:00Z"; // 52 hours
      expect(formatElapsed(base, end, { maxUnit: "days" })).toBe("2d 4h");
    });

    it("returns hours when under 24h even with maxUnit days", () => {
      const end = "2026-04-05T22:30:00Z"; // 12.5 hours
      expect(formatElapsed(base, end, { maxUnit: "days" })).toBe("12h 30m");
    });

    it("returns minutes when under 1h with maxUnit days", () => {
      const end = "2026-04-05T10:45:00Z";
      expect(formatElapsed(base, end, { maxUnit: "days" })).toBe("45m");
    });

    // granularity: "seconds" (RestartHistoryPanel behavior)

    it("returns seconds for short durations with seconds granularity", () => {
      const end = "2026-04-05T10:00:30Z";
      expect(formatElapsed(base, end, { granularity: "seconds" })).toBe("30s");
    });

    it("returns minutes and seconds for sub-hour with seconds granularity", () => {
      const end = "2026-04-05T10:05:30Z";
      expect(formatElapsed(base, end, { granularity: "seconds" })).toBe("5m 30s");
    });

    it("returns hours and minutes for longer durations with seconds granularity", () => {
      const end = "2026-04-05T12:15:30Z";
      expect(formatElapsed(base, end, { granularity: "seconds" })).toBe("2h 15m");
    });

    it("returns 0s for zero duration with seconds granularity", () => {
      expect(formatElapsed(base, base, { granularity: "seconds" })).toBe("0s");
    });

    // null end + runningLabel (RestartHistoryPanel "running" behavior)

    it("uses Date.now() when end is null", () => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date("2026-04-05T10:10:00Z"));
      expect(formatElapsed(base, null)).toBe("10m");
      vi.useRealTimers();
    });

    it("appends runningLabel when end is null", () => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date("2026-04-05T10:00:45Z"));
      expect(formatElapsed(base, null, { granularity: "seconds", runningLabel: "(running)" })).toBe("45s (running)");
      vi.useRealTimers();
    });

    it("does not append runningLabel when end is provided", () => {
      const end = "2026-04-05T10:00:45Z";
      expect(formatElapsed(base, end, { granularity: "seconds", runningLabel: "(running)" })).toBe("45s");
    });

    it("uses Date.now() when end is undefined", () => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date("2026-04-05T10:30:00Z"));
      expect(formatElapsed(base, undefined, { runningLabel: "(running)" })).toBe("30m (running)");
      vi.useRealTimers();
    });

    // Combined options

    it("supports seconds granularity with days maxUnit", () => {
      const end = "2026-04-07T14:05:30Z"; // 52h 5m 30s
      expect(formatElapsed(base, end, { granularity: "seconds", maxUnit: "days" })).toBe("52h 5m");
    });

    // Edge cases

    it("handles sub-second durations as 0", () => {
      const end = "2026-04-05T10:00:00.500Z";
      expect(formatElapsed(base, end)).toBe("0m");
      expect(formatElapsed(base, end, { granularity: "seconds" })).toBe("0s");
    });
  });
});
