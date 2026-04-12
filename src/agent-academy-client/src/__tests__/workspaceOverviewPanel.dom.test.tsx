// @vitest-environment jsdom
/**
 * DOM tests for WorkspaceOverviewPanel.
 *
 * Covers: phase section with room, phase buttons (current disabled, transitioning,
 * readOnly), read-only note, room stats section, room status summary, empty rooms,
 * and no-room rendering.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

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

vi.mock("../RoomStatsPanel", () => ({
  default: ({ roomId }: { roomId: string }) =>
    createElement("div", { "data-testid": "room-stats-panel", "data-room-id": roomId }, "RoomStatsPanel stub"),
}));

import WorkspaceOverviewPanel from "../WorkspaceOverviewPanel";
import type {
  CollaborationPhase,
  RoomSnapshot,
  WorkspaceOverview,
  AgentPresence,
} from "../api";

// ── Factories ──────────────────────────────────────────────────────────

function makePresence(overrides: Partial<AgentPresence> = {}): AgentPresence {
  return {
    agentId: "arch-1",
    name: "Architect",
    role: "architect",
    availability: "Available",
    isPreferred: false,
    lastActivityAt: "2026-06-01T12:00:00Z",
    activeCapabilities: [],
    ...overrides,
  };
}

function makeRoom(overrides: Partial<RoomSnapshot> = {}): RoomSnapshot {
  return {
    id: "room-1",
    name: "Main Room",
    topic: "Build a widget",
    status: "Active",
    currentPhase: "Discussion",
    activeTask: null,
    participants: [makePresence(), makePresence({ agentId: "eng-1", name: "Engineer", role: "engineer" })],
    recentMessages: [],
    createdAt: "2026-06-01T10:00:00Z",
    updatedAt: "2026-06-01T12:00:00Z",
    ...overrides,
  };
}

function makeOverview(overrides: Partial<WorkspaceOverview> = {}): WorkspaceOverview {
  return {
    configuredAgents: [],
    rooms: [makeRoom()],
    recentActivity: [],
    agentLocations: [],
    breakoutRooms: [],
    generatedAt: "2026-06-01T12:00:00Z",
    ...overrides,
  };
}

// ── Helpers ─────────────────────────────────────────────────────────────

const PHASES: readonly CollaborationPhase[] = [
  "Intake", "Planning", "Discussion", "Validation", "Implementation", "FinalSynthesis",
];

function renderPanel(props: Partial<Parameters<typeof WorkspaceOverviewPanel>[0]> = {}) {
  const defaults = {
    overview: makeOverview(),
    room: makeRoom(),
    onPhaseTransition: vi.fn(),
    transitioning: false,
    readOnly: false,
  };
  const merged = { ...defaults, ...props };
  return {
    ...render(
      createElement(
        FluentProvider,
        { theme: webDarkTheme },
        createElement(WorkspaceOverviewPanel, merged),
      ),
    ),
    onPhaseTransition: merged.onPhaseTransition,
  };
}

// ── Tests ───────────────────────────────────────────────────────────────

describe("WorkspaceOverviewPanel", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  afterEach(() => {
    cleanup();
  });

  // ── Room phase section ──

  describe("room phase section", () => {
    it("shows current phase label and room name", () => {
      renderPanel();
      expect(screen.getByText(/Current Phase — Main Room/)).toBeInTheDocument();
      // "Discussion" appears in the phase label, button, and badge — verify at least one
      expect(screen.getAllByText("Discussion").length).toBeGreaterThanOrEqual(1);
    });

    it("renders progress bar (ProgressBar exists in the DOM)", () => {
      const { container } = renderPanel();
      expect(container.querySelector('[role="progressbar"]')).toBeInTheDocument();
    });
  });

  // ── Phase transition buttons ──

  describe("phase transition buttons", () => {
    it("renders a button for every phase", () => {
      renderPanel();
      for (const phase of PHASES) {
        expect(screen.getByRole("button", { name: new RegExp(phase) })).toBeInTheDocument();
      }
    });

    it("disables button for the current phase", () => {
      renderPanel({ room: makeRoom({ currentPhase: "Discussion" }) });
      expect(screen.getByRole("button", { name: /Discussion/ })).toBeDisabled();
    });

    it("enables buttons for non-current phases", () => {
      renderPanel({ room: makeRoom({ currentPhase: "Discussion" }) });
      const enabled = PHASES.filter((p) => p !== "Discussion");
      for (const phase of enabled) {
        expect(screen.getByRole("button", { name: new RegExp(phase) })).toBeEnabled();
      }
    });

    it("calls onPhaseTransition when a non-current phase button is clicked", async () => {
      const user = userEvent.setup();
      const { onPhaseTransition } = renderPanel({ room: makeRoom({ currentPhase: "Intake" }) });
      await user.click(screen.getByRole("button", { name: /Planning/ }));
      expect(onPhaseTransition).toHaveBeenCalledWith("Planning");
    });

    it("disables all buttons when transitioning", () => {
      renderPanel({ transitioning: true });
      for (const phase of PHASES) {
        expect(screen.getByRole("button", { name: new RegExp(phase) })).toBeDisabled();
      }
    });

    it("disables all buttons when readOnly", () => {
      renderPanel({ readOnly: true });
      for (const phase of PHASES) {
        expect(screen.getByRole("button", { name: new RegExp(phase) })).toBeDisabled();
      }
    });
  });

  // ── Read-only note ──

  describe("read-only mode", () => {
    it("shows reconnect note when readOnly is true", () => {
      renderPanel({ readOnly: true });
      expect(screen.getByText(/Phase changes are paused/)).toBeInTheDocument();
    });

    it("hides reconnect note when readOnly is false", () => {
      renderPanel({ readOnly: false });
      expect(screen.queryByText(/Phase changes are paused/)).not.toBeInTheDocument();
    });
  });

  // ── Room stats section ──

  describe("room stats section", () => {
    it("renders RoomStatsPanel stub with correct roomId when room is provided", () => {
      renderPanel({ room: makeRoom({ id: "room-42" }) });
      const stats = screen.getByTestId("room-stats-panel");
      expect(stats).toBeInTheDocument();
      expect(stats).toHaveAttribute("data-room-id", "room-42");
    });

    it("shows 'Room Stats' section title with room name", () => {
      renderPanel({ room: makeRoom({ name: "Design Room" }) });
      expect(screen.getByText(/Room Stats — Design Room/)).toBeInTheDocument();
    });
  });

  // ── No room ──

  describe("when room is null", () => {
    it("hides phase section and room stats section", () => {
      renderPanel({ room: null });
      expect(screen.queryByText(/Current Phase —/)).not.toBeInTheDocument();
      expect(screen.queryByTestId("room-stats-panel")).not.toBeInTheDocument();
    });

    it("still shows room status summary", () => {
      renderPanel({ room: null });
      expect(screen.getByText("Room Status Summary")).toBeInTheDocument();
    });
  });

  // ── Room status summary ──

  describe("room status summary", () => {
    it("renders room name and badges for each room", () => {
      const rooms = [
        makeRoom({ id: "r1", name: "Alpha", status: "Active", currentPhase: "Planning", participants: [makePresence()] }),
        makeRoom({ id: "r2", name: "Beta", status: "Completed", currentPhase: "FinalSynthesis", participants: [] }),
      ];
      renderPanel({ overview: makeOverview({ rooms }), room: null });
      expect(screen.getByText("Alpha")).toBeInTheDocument();
      expect(screen.getByText("Beta")).toBeInTheDocument();
      // Status badges
      expect(screen.getByText("Active")).toBeInTheDocument();
      expect(screen.getByText("Completed")).toBeInTheDocument();
      // Phase badges
      expect(screen.getByText("Planning")).toBeInTheDocument();
      expect(screen.getByText("FinalSynthesis")).toBeInTheDocument();
    });

    it("shows agent count badge with correct pluralization", () => {
      const rooms = [
        makeRoom({ id: "r1", name: "Solo", participants: [makePresence()] }),
        makeRoom({ id: "r2", name: "Team", participants: [makePresence(), makePresence({ agentId: "eng-1" })] }),
      ];
      renderPanel({ overview: makeOverview({ rooms }), room: null });
      expect(screen.getByText("1 agent")).toBeInTheDocument();
      expect(screen.getByText("2 agents")).toBeInTheDocument();
    });

    it("shows zero agents correctly", () => {
      const rooms = [makeRoom({ id: "r1", name: "Empty", participants: [] })];
      renderPanel({ overview: makeOverview({ rooms }), room: null });
      expect(screen.getByText("0 agents")).toBeInTheDocument();
    });

    it("shows 'No rooms yet' when rooms array is empty", () => {
      renderPanel({ overview: makeOverview({ rooms: [] }), room: null });
      expect(screen.getByText("No rooms yet")).toBeInTheDocument();
    });
  });

  // ── Status → badge color mapping ──

  describe("status badge colors", () => {
    it("maps Active to 'ok' badge", () => {
      renderPanel({ overview: makeOverview({ rooms: [makeRoom({ status: "Active" })] }), room: null });
      expect(screen.getByTestId("badge-ok")).toHaveTextContent("Active");
    });

    it("maps AttentionRequired to 'warn' badge", () => {
      renderPanel({ overview: makeOverview({ rooms: [makeRoom({ status: "AttentionRequired" })] }), room: null });
      expect(screen.getByTestId("badge-warn")).toHaveTextContent("AttentionRequired");
    });

    it("maps Completed to 'info' badge", () => {
      renderPanel({ overview: makeOverview({ rooms: [makeRoom({ status: "Completed" })] }), room: null });
      // Both status and phase badges use "info" — find the one with status text
      const infoBadges = screen.getAllByTestId("badge-info");
      expect(infoBadges.some((el) => el.textContent === "Completed")).toBe(true);
    });

    it("maps Archived to 'muted' badge", () => {
      renderPanel({ overview: makeOverview({ rooms: [makeRoom({ status: "Archived" })] }), room: null });
      // Both status and agent-count badges use "muted" — find the one with status text
      const mutedBadges = screen.getAllByTestId("badge-muted");
      expect(mutedBadges.some((el) => el.textContent === "Archived")).toBe(true);
    });
  });
});
