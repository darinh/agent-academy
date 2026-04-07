import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ActivityEventType } from "../api";
import {
  eventCategory,
  relativeTime,
  severityColor,
} from "../timelinePanelUtils";

/* ------------------------------------------------------------------ */
/*  relativeTime                                                       */
/* ------------------------------------------------------------------ */

describe("relativeTime", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-04-07T12:00:00Z"));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("returns 'just now' for timestamps less than 60 seconds ago", () => {
    const iso = new Date("2026-04-07T11:59:30Z").toISOString();
    expect(relativeTime(iso)).toBe("just now");
  });

  it("returns 'just now' for timestamps 0 seconds ago", () => {
    const iso = new Date("2026-04-07T12:00:00Z").toISOString();
    expect(relativeTime(iso)).toBe("just now");
  });

  it("returns minutes for 1–59 minutes ago", () => {
    const oneMin = new Date("2026-04-07T11:59:00Z").toISOString();
    expect(relativeTime(oneMin)).toBe("1m ago");

    const thirtyMin = new Date("2026-04-07T11:30:00Z").toISOString();
    expect(relativeTime(thirtyMin)).toBe("30m ago");

    const fiftyNine = new Date("2026-04-07T11:01:00Z").toISOString();
    expect(relativeTime(fiftyNine)).toBe("59m ago");
  });

  it("returns hours for 1–23 hours ago", () => {
    const oneHour = new Date("2026-04-07T11:00:00Z").toISOString();
    expect(relativeTime(oneHour)).toBe("1h ago");

    const twelveHours = new Date("2026-04-07T00:00:00Z").toISOString();
    expect(relativeTime(twelveHours)).toBe("12h ago");

    const twentyThreeHours = new Date("2026-04-06T13:00:00Z").toISOString();
    expect(relativeTime(twentyThreeHours)).toBe("23h ago");
  });

  it("returns days for 24+ hours ago", () => {
    const oneDay = new Date("2026-04-06T12:00:00Z").toISOString();
    expect(relativeTime(oneDay)).toBe("1d ago");

    const sevenDays = new Date("2026-03-31T12:00:00Z").toISOString();
    expect(relativeTime(sevenDays)).toBe("7d ago");
  });

  it("handles future timestamps (negative diff) as 'just now'", () => {
    const future = new Date("2026-04-07T13:00:00Z").toISOString();
    // Negative diff → secs < 60 → "just now"
    expect(relativeTime(future)).toBe("just now");
  });
});

/* ------------------------------------------------------------------ */
/*  severityColor                                                      */
/* ------------------------------------------------------------------ */

describe("severityColor", () => {
  it("returns 'informative' for Info", () => {
    expect(severityColor("Info")).toBe("informative");
  });

  it("returns 'warning' for Warning", () => {
    expect(severityColor("Warning")).toBe("warning");
  });

  it("returns 'danger' for Error", () => {
    expect(severityColor("Error")).toBe("danger");
  });
});

/* ------------------------------------------------------------------ */
/*  eventCategory                                                      */
/* ------------------------------------------------------------------ */

describe("eventCategory", () => {
  it("categorises Agent* events as 'agent'", () => {
    expect(eventCategory("AgentLoaded")).toBe("agent");
    expect(eventCategory("AgentThinking")).toBe("agent");
    expect(eventCategory("AgentFinished")).toBe("agent");
  });

  it("categorises Message* events as 'message'", () => {
    expect(eventCategory("MessageSent" as ActivityEventType)).toBe("message");
  });

  it("categorises Task* events as 'task'", () => {
    expect(eventCategory("TaskCreated")).toBe("task");
  });

  it("categorises PhaseChanged as 'phase'", () => {
    expect(eventCategory("PhaseChanged" as ActivityEventType)).toBe("phase");
  });

  it("categorises Subagent* events as 'subagent'", () => {
    expect(eventCategory("SubagentSpawned" as ActivityEventType)).toBe("subagent");
  });

  it("categorises Room* events as 'room'", () => {
    expect(eventCategory("RoomCreated")).toBe("room");
    expect(eventCategory("RoomClosed")).toBe("room");
  });

  it("returns 'other' for unrecognized event types", () => {
    expect(eventCategory("Unknown" as ActivityEventType)).toBe("other");
  });
});
