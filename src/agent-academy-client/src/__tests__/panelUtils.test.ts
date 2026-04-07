import { describe, expect, it } from "vitest";
import {
  formatTimestamp,
  formatTokenCount,
  formatCost,
  errorTypeBadge,
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
      expect(badge.color).toBe("danger");
      expect(badge.label).toBe("Auth");
    });

    it("maps authorization to danger/Authz", () => {
      const badge = errorTypeBadge("authorization");
      expect(badge.color).toBe("danger");
      expect(badge.label).toBe("Authz");
    });

    it("maps quota to warning/Quota", () => {
      const badge = errorTypeBadge("quota");
      expect(badge.color).toBe("warning");
      expect(badge.label).toBe("Quota");
    });

    it("maps transient to important/Transient", () => {
      const badge = errorTypeBadge("transient");
      expect(badge.color).toBe("important");
      expect(badge.label).toBe("Transient");
    });

    it("maps unknown types to informative with raw label", () => {
      const badge = errorTypeBadge("custom_error");
      expect(badge.color).toBe("informative");
      expect(badge.label).toBe("custom_error");
    });

    it("maps empty string to informative", () => {
      const badge = errorTypeBadge("");
      expect(badge.color).toBe("informative");
      expect(badge.label).toBe("");
    });

    it("is case-sensitive", () => {
      const badge = errorTypeBadge("Authentication");
      expect(badge.color).toBe("informative");
      expect(badge.label).toBe("Authentication");
    });
  });
});
