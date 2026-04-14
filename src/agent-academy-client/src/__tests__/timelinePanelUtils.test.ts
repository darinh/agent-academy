import { describe, expect, it, vi, afterEach } from "vitest";
import {
  relativeTime,
  severityColor,
  eventCategory,
} from "../timelinePanelUtils";
import type { ActivityEventType } from "../api";

describe("timelinePanelUtils", () => {
  /* ── relativeTime ───────────────────────────────────────────── */

  describe("relativeTime", () => {
    afterEach(() => {
      vi.useRealTimers();
    });

    it('returns "just now" for timestamps less than 60 seconds ago', () => {
      const now = new Date().toISOString();
      expect(relativeTime(now)).toBe("just now");
    });

    it("returns minutes ago for recent timestamps", () => {
      vi.useFakeTimers();
      const fiveMinAgo = new Date(Date.now() - 5 * 60_000).toISOString();
      expect(relativeTime(fiveMinAgo)).toBe("5m ago");
      vi.useRealTimers();
    });

    it("returns hours ago for older timestamps", () => {
      vi.useFakeTimers();
      const twoHrsAgo = new Date(Date.now() - 2 * 3600_000).toISOString();
      expect(relativeTime(twoHrsAgo)).toBe("2h ago");
      vi.useRealTimers();
    });

    it("returns days ago for timestamps over 24h", () => {
      vi.useFakeTimers();
      const threeDaysAgo = new Date(Date.now() - 3 * 86400_000).toISOString();
      expect(relativeTime(threeDaysAgo)).toBe("3d ago");
      vi.useRealTimers();
    });

    it("handles exactly 60 seconds as 1m ago", () => {
      vi.useFakeTimers();
      const oneMinAgo = new Date(Date.now() - 60_000).toISOString();
      expect(relativeTime(oneMinAgo)).toBe("1m ago");
      vi.useRealTimers();
    });

    it("handles exactly 60 minutes as 1h ago", () => {
      vi.useFakeTimers();
      const oneHrAgo = new Date(Date.now() - 3600_000).toISOString();
      expect(relativeTime(oneHrAgo)).toBe("1h ago");
      vi.useRealTimers();
    });

    it("handles exactly 24 hours as 1d ago", () => {
      vi.useFakeTimers();
      const oneDayAgo = new Date(Date.now() - 86400_000).toISOString();
      expect(relativeTime(oneDayAgo)).toBe("1d ago");
      vi.useRealTimers();
    });
  });

  /* ── severityColor ──────────────────────────────────────────── */

  describe("severityColor", () => {
    it('returns "informative" for Info', () => {
      expect(severityColor("Info")).toBe("informative");
    });

    it('returns "warning" for Warning', () => {
      expect(severityColor("Warning")).toBe("warning");
    });

    it('returns "danger" for Error', () => {
      expect(severityColor("Error")).toBe("danger");
    });

    it('returns "informative" as default for unknown severity', () => {
      expect(severityColor("Unknown" as "Info")).toBe("informative");
    });
  });

  /* ── eventCategory ──────────────────────────────────────────── */

  describe("eventCategory", () => {
    const agentEvents: ActivityEventType[] = ["AgentLoaded", "AgentThinking", "AgentFinished"];
    const messageEvents: ActivityEventType[] = ["MessagePosted", "MessageSent"];
    const taskEvents: ActivityEventType[] = ["TaskCreated"];
    const subagentEvents: ActivityEventType[] = ["SubagentStarted", "SubagentCompleted", "SubagentFailed"];
    const roomEvents: ActivityEventType[] = ["RoomCreated", "RoomClosed"];

    it.each(agentEvents)('categorizes %s as "agent"', (evt) => {
      expect(eventCategory(evt)).toBe("agent");
    });

    it.each(messageEvents)('categorizes %s as "message"', (evt) => {
      expect(eventCategory(evt)).toBe("message");
    });

    it.each(taskEvents)('categorizes %s as "task"', (evt) => {
      expect(eventCategory(evt)).toBe("task");
    });

    it('categorizes PhaseChanged as "phase"', () => {
      expect(eventCategory("PhaseChanged")).toBe("phase");
    });

    it.each(subagentEvents)('categorizes %s as "subagent"', (evt) => {
      expect(eventCategory(evt)).toBe("subagent");
    });

    it.each(roomEvents)('categorizes %s as "room"', (evt) => {
      expect(eventCategory(evt)).toBe("room");
    });

    it('categorizes unknown events as "other"', () => {
      expect(eventCategory("PresenceUpdated")).toBe("other");
      expect(eventCategory("CheckpointCreated")).toBe("other");
    });
  });
});
