import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  formatRelativeTime,
  formatTimestamp,
  PAGE_SIZE,
  truncateSummary,
} from "../sessionHistoryPanelUtils";

/* ------------------------------------------------------------------ */
/*  PAGE_SIZE                                                          */
/* ------------------------------------------------------------------ */

describe("PAGE_SIZE", () => {
  it("is a positive number", () => {
    expect(PAGE_SIZE).toBeGreaterThan(0);
    expect(typeof PAGE_SIZE).toBe("number");
  });
});

/* ------------------------------------------------------------------ */
/*  truncateSummary                                                    */
/* ------------------------------------------------------------------ */

describe("truncateSummary", () => {
  it("returns the full string when shorter than maxLen", () => {
    expect(truncateSummary("hello", 120)).toBe("hello");
  });

  it("returns the full string when exactly maxLen", () => {
    const s = "a".repeat(120);
    expect(truncateSummary(s, 120)).toBe(s);
  });

  it("truncates and appends ellipsis when longer than maxLen", () => {
    const s = "a".repeat(130);
    const result = truncateSummary(s, 120);
    expect(result.endsWith("…")).toBe(true);
    expect(result.length).toBeLessThanOrEqual(121); // 120 chars + 1 ellipsis
  });

  it("trims trailing whitespace before adding ellipsis", () => {
    const s = "word ".repeat(30); // 150 chars
    const result = truncateSummary(s, 120);
    // Should not end with space before ellipsis
    expect(result).toMatch(/\S…$/);
  });

  it("uses default maxLen of 120", () => {
    const short = "a".repeat(120);
    expect(truncateSummary(short)).toBe(short);

    const long = "a".repeat(121);
    expect(truncateSummary(long)).toContain("…");
  });

  it("handles empty string", () => {
    expect(truncateSummary("")).toBe("");
  });

  it("handles custom maxLen", () => {
    expect(truncateSummary("hello world", 5)).toBe("hello…");
  });
});

/* ------------------------------------------------------------------ */
/*  formatRelativeTime                                                 */
/* ------------------------------------------------------------------ */

describe("formatRelativeTime", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-04-07T12:00:00Z"));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("returns 'just now' for < 60 seconds", () => {
    expect(formatRelativeTime("2026-04-07T11:59:30Z")).toBe("just now");
  });

  it("returns minutes for 1–59 min", () => {
    expect(formatRelativeTime("2026-04-07T11:55:00Z")).toBe("5m ago");
  });

  it("returns hours for 1–23 hours", () => {
    expect(formatRelativeTime("2026-04-07T06:00:00Z")).toBe("6h ago");
  });

  it("returns days for 24+ hours", () => {
    expect(formatRelativeTime("2026-04-04T12:00:00Z")).toBe("3d ago");
  });
});

/* ------------------------------------------------------------------ */
/*  formatTimestamp                                                     */
/* ------------------------------------------------------------------ */

describe("formatTimestamp", () => {
  it("returns a non-empty string for a valid ISO date", () => {
    const result = formatTimestamp("2026-04-07T12:30:00Z");
    expect(result.length).toBeGreaterThan(0);
    expect(typeof result).toBe("string");
  });

  it("includes month abbreviation", () => {
    const result = formatTimestamp("2026-01-15T08:30:00Z");
    // Should contain "Jan" in most locales
    expect(result).toMatch(/jan|1/i);
  });

  it("includes day number", () => {
    const result = formatTimestamp("2026-04-15T08:30:00Z");
    expect(result).toContain("15");
  });
});
