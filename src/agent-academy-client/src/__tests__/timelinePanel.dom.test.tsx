// @vitest-environment jsdom
/**
 * RTL tests for TimelinePanel.
 *
 * Covers: loading skeleton, empty state, sorted event list, event type badges,
 * severity badge colours, and relative timestamps.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

import TimelinePanel from "../TimelinePanel";
import type { ActivityEvent, ActivityEventType } from "../api";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../V3Badge", () => ({
  default: ({
    children,
    color,
  }: {
    children: React.ReactNode;
    color: string;
  }) => createElement("span", { "data-testid": `badge-${color}` }, children),
}));

vi.mock("../EmptyState", () => ({
  default: ({
    title,
    detail,
  }: {
    icon?: React.ReactNode;
    title: string;
    detail?: string;
  }) =>
    createElement(
      "div",
      { "data-testid": "empty-state" },
      createElement("span", null, title),
      detail && createElement("span", null, detail),
    ),
}));

vi.mock("../SkeletonLoader", () => ({
  default: ({ rows, variant }: { rows: number; variant?: string }) =>
    createElement("div", { "data-testid": "skeleton-loader" }, `Loading ${rows} ${variant ?? "rows"}`),
}));

// ── Factories ──────────────────────────────────────────────────────────

let nextId = 1;

function makeEvent(overrides: Partial<ActivityEvent> = {}): ActivityEvent {
  const id = String(nextId++);
  return {
    id,
    type: "MessagePosted",
    severity: "Info",
    message: `Event message ${id}`,
    occurredAt: "2026-06-15T12:00:00Z",
    roomId: null,
    actorId: null,
    taskId: null,
    correlationId: null,
    metadata: null,
    ...overrides,
  };
}

function makeEvents(count: number, base: Partial<ActivityEvent> = {}): ActivityEvent[] {
  return Array.from({ length: count }, (_, i) =>
    makeEvent({
      id: String(nextId++),
      occurredAt: `2026-06-15T${String(12 - i).padStart(2, "0")}:00:00Z`,
      message: `Event ${i}`,
      ...base,
    }),
  );
}

// ── Helpers ─────────────────────────────────────────────────────────────

afterEach(() => {
  cleanup();
  document.body.innerHTML = "";
  nextId = 1;
});

function renderPanel(props: { activity?: ActivityEvent[]; loading?: boolean } = {}) {
  const { activity = [], loading } = props;
  return render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(TimelinePanel, { activity, loading }),
    ),
  );
}

// ── Tests ───────────────────────────────────────────────────────────────

describe("TimelinePanel", () => {
  // ── Loading state ──

  describe("loading state", () => {
    it("shows skeleton loader when loading with no events", () => {
      renderPanel({ loading: true, activity: [] });
      expect(screen.getByTestId("skeleton-loader")).toBeInTheDocument();
      expect(screen.getByText("Loading 5 list")).toBeInTheDocument();
    });

    it("shows events instead of skeleton when loading with data", () => {
      const events = [makeEvent({ message: "Already here" })];
      renderPanel({ loading: true, activity: events });
      expect(screen.queryByTestId("skeleton-loader")).not.toBeInTheDocument();
      expect(screen.getByText("Already here")).toBeInTheDocument();
    });
  });

  // ── Empty state ──

  describe("empty state", () => {
    it("shows empty state when not loading and no events", () => {
      renderPanel({ loading: false, activity: [] });
      expect(screen.getByTestId("empty-state")).toBeInTheDocument();
      expect(screen.getByText("No activity yet")).toBeInTheDocument();
    });

    it("shows detail text in empty state", () => {
      renderPanel({ activity: [] });
      expect(
        screen.getByText(/Events will appear here as agents collaborate/),
      ).toBeInTheDocument();
    });
  });

  // ── Populated list ──

  describe("populated list", () => {
    it("renders all events as list items", () => {
      const events = makeEvents(3);
      renderPanel({ activity: events });
      const items = screen.getAllByRole("listitem");
      expect(items).toHaveLength(3);
    });

    it("displays event messages", () => {
      const events = [
        makeEvent({ message: "Agent joined the room" }),
        makeEvent({ message: "Build completed successfully" }),
      ];
      renderPanel({ activity: events });
      expect(screen.getByText("Agent joined the room")).toBeInTheDocument();
      expect(screen.getByText("Build completed successfully")).toBeInTheDocument();
    });

    it("sorts events newest-first", () => {
      const older = makeEvent({
        message: "Older event",
        occurredAt: "2026-06-15T08:00:00Z",
      });
      const newer = makeEvent({
        message: "Newer event",
        occurredAt: "2026-06-15T14:00:00Z",
      });
      // Pass in wrong order to verify sorting
      renderPanel({ activity: [older, newer] });
      const items = screen.getAllByRole("listitem");
      expect(within(items[0]).getByText("Newer event")).toBeInTheDocument();
      expect(within(items[1]).getByText("Older event")).toBeInTheDocument();
    });
  });

  // ── Event type badges ──

  describe("event type badges", () => {
    it("renders event type in a badge", () => {
      const events = [makeEvent({ type: "TaskCreated" })];
      renderPanel({ activity: events });
      expect(screen.getByText("TaskCreated")).toBeInTheDocument();
    });

    it("renders different event types", () => {
      const types: ActivityEventType[] = ["AgentLoaded", "PhaseChanged", "MessagePosted"];
      const events = types.map((type, i) =>
        makeEvent({
          type,
          occurredAt: `2026-06-15T${String(12 - i).padStart(2, "0")}:00:00Z`,
        }),
      );
      renderPanel({ activity: events });
      for (const t of types) {
        expect(screen.getByText(t)).toBeInTheDocument();
      }
    });
  });

  // ── Severity badge colours ──

  describe("severity badge colours", () => {
    it("renders Info severity with info badge colour", () => {
      renderPanel({ activity: [makeEvent({ severity: "Info" })] });
      expect(screen.getByTestId("badge-info")).toBeInTheDocument();
    });

    it("renders Warning severity with warn badge colour", () => {
      renderPanel({ activity: [makeEvent({ severity: "Warning" })] });
      expect(screen.getByTestId("badge-warn")).toBeInTheDocument();
    });

    it("renders Error severity with err badge colour", () => {
      renderPanel({ activity: [makeEvent({ severity: "Error" })] });
      expect(screen.getByTestId("badge-err")).toBeInTheDocument();
    });
  });

  // ── Timestamps ──

  describe("timestamps", () => {
    it("renders relative time for each event", () => {
      // Use a very old timestamp so relativeTime returns "Xd ago"
      const events = [
        makeEvent({ occurredAt: new Date(Date.now() - 2 * 60 * 1000).toISOString() }),
      ];
      renderPanel({ activity: events });
      expect(screen.getByText("2m ago")).toBeInTheDocument();
    });

    it("renders 'just now' for very recent events", () => {
      const events = [
        makeEvent({ occurredAt: new Date(Date.now() - 5 * 1000).toISOString() }),
      ];
      renderPanel({ activity: events });
      expect(screen.getByText("just now")).toBeInTheDocument();
    });
  });

  // ── Count badge in header ──

  describe("count badge", () => {
    it("renders event count in the header badge", () => {
      const events = makeEvents(7);
      renderPanel({ activity: events });
      // The header contains a V3Badge showing the count
      expect(screen.getByTestId("badge-muted")).toHaveTextContent("7");
    });
  });
});
