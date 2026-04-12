// @vitest-environment jsdom
/**
 * Interactive RTL tests for DashboardPanel.
 *
 * Uses @testing-library/react + jsdom.
 * Covers: stat cards, phase distribution (present / absent), time range
 * selector with localStorage persistence, and child panel stub rendering.
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

vi.mock("../AgentAnalyticsPanel", () => ({
  default: ({ hoursBack }: { hoursBack: unknown }) =>
    createElement("div", { "data-testid": "agent-analytics-panel", "data-hours": String(hoursBack) }),
}));

vi.mock("../UsagePanel", () => ({
  default: ({ hoursBack }: { hoursBack: unknown }) =>
    createElement("div", { "data-testid": "usage-panel", "data-hours": String(hoursBack) }),
}));

vi.mock("../ErrorsPanel", () => ({
  default: ({ hoursBack, circuitBreakerState }: { hoursBack: unknown; circuitBreakerState?: unknown }) =>
    createElement("div", {
      "data-testid": "errors-panel",
      "data-hours": String(hoursBack),
      "data-cb": circuitBreakerState != null ? String(circuitBreakerState) : undefined,
    }),
}));

vi.mock("../AuditLogPanel", () => ({
  default: ({ hoursBack }: { hoursBack: unknown }) =>
    createElement("div", { "data-testid": "audit-log-panel", "data-hours": String(hoursBack) }),
}));

vi.mock("../SessionHistoryPanel", () => ({
  default: ({ hoursBack }: { hoursBack: unknown }) =>
    createElement("div", { "data-testid": "session-history-panel", "data-hours": String(hoursBack) }),
}));

vi.mock("../RestartHistoryPanel", () => ({
  default: ({ hoursBack }: { hoursBack: unknown }) =>
    createElement("div", { "data-testid": "restart-history-panel", "data-hours": String(hoursBack) }),
}));

import DashboardPanel from "../DashboardPanel";
import type {
  WorkspaceOverview,
  RoomSnapshot,
  AgentDefinition,
  ActivityEvent,
} from "../api";
import type { CircuitBreakerState } from "../useCircuitBreakerPolling";
import { TIME_RANGE_KEY } from "../dashboardUtils";

// ── Factories ──────────────────────────────────────────────────────────

function makeAgent(overrides: Partial<AgentDefinition> = {}): AgentDefinition {
  return {
    id: "agent-1",
    name: "Software Engineer",
    role: "engineer",
    summary: "Writes code",
    startupPrompt: "You are an engineer.",
    model: null,
    capabilityTags: [],
    enabledTools: [],
    autoJoinDefaultRoom: true,
    gitIdentity: null,
    ...overrides,
  };
}

function makeRoom(overrides: Partial<RoomSnapshot> = {}): RoomSnapshot {
  return {
    id: "room-1",
    name: "Main Room",
    topic: null,
    status: "Active",
    currentPhase: "Planning",
    activeTask: null,
    participants: [],
    recentMessages: [],
    createdAt: "2026-04-01T00:00:00Z",
    updatedAt: "2026-04-01T01:00:00Z",
    ...overrides,
  };
}

function makeEvent(overrides: Partial<ActivityEvent> = {}): ActivityEvent {
  return {
    id: "evt-1",
    type: "MessagePosted",
    severity: "Info",
    roomId: "room-1",
    actorId: "agent-1",
    taskId: null,
    message: "Something happened",
    correlationId: null,
    occurredAt: "2026-04-01T00:00:00Z",
    metadata: null,
    ...overrides,
  };
}

function makeOverview(overrides: Partial<WorkspaceOverview> = {}): WorkspaceOverview {
  return {
    configuredAgents: [makeAgent()],
    rooms: [makeRoom()],
    recentActivity: [makeEvent()],
    agentLocations: [],
    breakoutRooms: [],
    generatedAt: "2026-04-01T00:00:00Z",
    ...overrides,
  };
}

// ── Helpers ─────────────────────────────────────────────────────────────

function renderPanel(props: {
  overview?: WorkspaceOverview;
  circuitBreakerState?: CircuitBreakerState;
} = {}) {
  const overview = props.overview ?? makeOverview();
  return render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(DashboardPanel, { overview, circuitBreakerState: props.circuitBreakerState }),
    ),
  );
}

// ── Tests ───────────────────────────────────────────────────────────────

describe("DashboardPanel (interactive)", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    localStorage.clear();
  });

  afterEach(() => {
    cleanup();
  });

  // ── Stat Cards ──

  describe("stat cards", () => {
    it("renders room count", () => {
      renderPanel({
        overview: makeOverview({
          rooms: [makeRoom({ id: "r1" }), makeRoom({ id: "r2" }), makeRoom({ id: "r3" })],
        }),
      });
      expect(screen.getByText("Rooms")).toBeInTheDocument();
      expect(screen.getByText("3")).toBeInTheDocument();
    });

    it("renders agent count", () => {
      renderPanel({
        overview: makeOverview({
          configuredAgents: [makeAgent({ id: "a1" }), makeAgent({ id: "a2" })],
        }),
      });
      expect(screen.getByText("Agents")).toBeInTheDocument();
      expect(screen.getByText("2")).toBeInTheDocument();
    });

    it("renders active task count", () => {
      renderPanel({
        overview: makeOverview({
          configuredAgents: [],
          rooms: [
            makeRoom({ id: "r1", activeTask: { id: "t1", title: "Fix bug" } as RoomSnapshot["activeTask"] }),
            makeRoom({ id: "r2", activeTask: null }),
            makeRoom({ id: "r3", activeTask: { id: "t2", title: "Add feat" } as RoomSnapshot["activeTask"] }),
          ],
          recentActivity: [],
        }),
      });
      expect(screen.getByText("Active Tasks")).toBeInTheDocument();
      expect(screen.getByText("2")).toBeInTheDocument();
    });

    it("renders recent event count", () => {
      renderPanel({
        overview: makeOverview({
          recentActivity: [makeEvent({ id: "e1" }), makeEvent({ id: "e2" }), makeEvent({ id: "e3" })],
        }),
      });
      expect(screen.getByText("Recent Events")).toBeInTheDocument();
      expect(screen.getByText("3")).toBeInTheDocument();
    });

    it("shows zeroes for empty overview", () => {
      renderPanel({
        overview: makeOverview({
          configuredAgents: [],
          rooms: [],
          recentActivity: [],
        }),
      });
      const zeroes = screen.getAllByText("0");
      expect(zeroes.length).toBeGreaterThanOrEqual(4);
    });
  });

  // ── Phase Distribution ──

  describe("phase distribution", () => {
    it("hides phase distribution when rooms are empty", () => {
      renderPanel({ overview: makeOverview({ rooms: [] }) });
      expect(screen.queryByText("Phase Distribution")).not.toBeInTheDocument();
    });

    it("shows phase distribution section when rooms exist", () => {
      renderPanel({
        overview: makeOverview({
          rooms: [makeRoom({ currentPhase: "Planning" })],
        }),
      });
      expect(screen.getByText("Phase Distribution")).toBeInTheDocument();
    });

    it("renders phase badges with correct labels", () => {
      renderPanel({
        overview: makeOverview({
          rooms: [
            makeRoom({ id: "r1", currentPhase: "Planning" }),
            makeRoom({ id: "r2", currentPhase: "Planning" }),
            makeRoom({ id: "r3", currentPhase: "Implementation" }),
          ],
        }),
      });
      expect(screen.getByText("Planning")).toBeInTheDocument();
      expect(screen.getByText("Implementation")).toBeInTheDocument();
    });

    it("displays correct room counts per phase with plural forms", () => {
      renderPanel({
        overview: makeOverview({
          rooms: [
            makeRoom({ id: "r1", currentPhase: "Planning" }),
            makeRoom({ id: "r2", currentPhase: "Planning" }),
            makeRoom({ id: "r3", currentPhase: "Intake" }),
          ],
        }),
      });
      expect(screen.getByText("2 rooms")).toBeInTheDocument();
      expect(screen.getByText("1 room")).toBeInTheDocument();
    });

    it("renders V3Badge with phase-appropriate colors", () => {
      renderPanel({
        overview: makeOverview({
          rooms: [
            makeRoom({ id: "r1", currentPhase: "Planning" }),
            makeRoom({ id: "r2", currentPhase: "Implementation" }),
          ],
        }),
      });
      expect(screen.getByTestId("badge-warn")).toBeInTheDocument(); // Planning
      expect(screen.getByTestId("badge-ok")).toBeInTheDocument();   // Implementation
    });

    it("groups all rooms into a single phase row when same phase", () => {
      renderPanel({
        overview: makeOverview({
          rooms: [
            makeRoom({ id: "r1", currentPhase: "Validation" }),
            makeRoom({ id: "r2", currentPhase: "Validation" }),
            makeRoom({ id: "r3", currentPhase: "Validation" }),
          ],
        }),
      });
      expect(screen.getByText("3 rooms")).toBeInTheDocument();
      expect(screen.getByTestId("badge-review")).toBeInTheDocument();
    });
  });

  // ── Time Range Selector ──

  describe("time range selector", () => {
    it("renders all time range buttons", () => {
      renderPanel();
      expect(screen.getByText("24h")).toBeInTheDocument();
      expect(screen.getByText("7d")).toBeInTheDocument();
      expect(screen.getByText("30d")).toBeInTheDocument();
      expect(screen.getByText("All")).toBeInTheDocument();
    });

    it("renders Time Range label", () => {
      renderPanel();
      expect(screen.getByText("Time Range")).toBeInTheDocument();
    });

    it("defaults to All when localStorage is empty", () => {
      renderPanel();
      // The "All" button should have the active class — we can verify child
      // panels receive undefined as hoursBack (the default for "All").
      expect(screen.getByTestId("agent-analytics-panel")).toHaveAttribute("data-hours", "undefined");
    });

    it("restores persisted time range from localStorage", () => {
      localStorage.setItem(TIME_RANGE_KEY, "24");
      renderPanel();
      expect(screen.getByTestId("agent-analytics-panel")).toHaveAttribute("data-hours", "24");
    });

    it("updates child panels when a time range button is clicked", async () => {
      const user = userEvent.setup();
      renderPanel();
      await user.click(screen.getByText("7d"));
      expect(screen.getByTestId("agent-analytics-panel")).toHaveAttribute("data-hours", "168");
      expect(screen.getByTestId("usage-panel")).toHaveAttribute("data-hours", "168");
      expect(screen.getByTestId("errors-panel")).toHaveAttribute("data-hours", "168");
      expect(screen.getByTestId("audit-log-panel")).toHaveAttribute("data-hours", "168");
      expect(screen.getByTestId("session-history-panel")).toHaveAttribute("data-hours", "168");
      expect(screen.getByTestId("restart-history-panel")).toHaveAttribute("data-hours", "168");
    });

    it("persists selected time range to localStorage", async () => {
      const user = userEvent.setup();
      renderPanel();
      await user.click(screen.getByText("24h"));
      expect(localStorage.getItem(TIME_RANGE_KEY)).toBe("24");
    });

    it("persists All as 'all' in localStorage", async () => {
      localStorage.setItem(TIME_RANGE_KEY, "168");
      const user = userEvent.setup();
      renderPanel();
      await user.click(screen.getByText("All"));
      expect(localStorage.getItem(TIME_RANGE_KEY)).toBe("all");
    });
  });

  // ── Child Panel Rendering ──

  describe("child panels", () => {
    it("renders AgentAnalyticsPanel", () => {
      renderPanel();
      expect(screen.getByTestId("agent-analytics-panel")).toBeInTheDocument();
    });

    it("renders UsagePanel", () => {
      renderPanel();
      expect(screen.getByTestId("usage-panel")).toBeInTheDocument();
    });

    it("renders ErrorsPanel", () => {
      renderPanel();
      expect(screen.getByTestId("errors-panel")).toBeInTheDocument();
    });

    it("renders AuditLogPanel", () => {
      renderPanel();
      expect(screen.getByTestId("audit-log-panel")).toBeInTheDocument();
    });

    it("renders SessionHistoryPanel", () => {
      renderPanel();
      expect(screen.getByTestId("session-history-panel")).toBeInTheDocument();
    });

    it("renders RestartHistoryPanel", () => {
      renderPanel();
      expect(screen.getByTestId("restart-history-panel")).toBeInTheDocument();
    });

    it("renders section titles for all child panels", () => {
      renderPanel();
      expect(screen.getByText("Agent Performance")).toBeInTheDocument();
      expect(screen.getByText("LLM Usage")).toBeInTheDocument();
      expect(screen.getByText("Agent Errors")).toBeInTheDocument();
      expect(screen.getByText("Command Audit Log")).toBeInTheDocument();
      expect(screen.getByText("Conversation Sessions")).toBeInTheDocument();
      expect(screen.getByText("Server Instance History")).toBeInTheDocument();
    });
  });

  // ── Circuit Breaker Passthrough ──

  describe("circuit breaker state", () => {
    it("passes circuit breaker state to ErrorsPanel", () => {
      renderPanel({ circuitBreakerState: "Open" });
      expect(screen.getByTestId("errors-panel")).toHaveAttribute("data-cb", "Open");
    });

    it("does not set circuit breaker attr when state is undefined", () => {
      renderPanel();
      expect(screen.getByTestId("errors-panel")).not.toHaveAttribute("data-cb");
    });
  });
});
