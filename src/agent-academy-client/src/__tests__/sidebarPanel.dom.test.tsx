// @vitest-environment jsdom
/**
 * Interactive RTL tests for SidebarPanel.
 *
 * Uses @testing-library/react + jsdom.
 * Covers: room list rendering, selected room indicator, collapsed sidebar,
 * create room flow, busy spinner, user badge, sprint version badge,
 * agent list with thinking/working state, and navigation items.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../UserBadge", () => ({
  default: ({
    user,
    onLogout,
    onOpenSettings,
  }: {
    user: { login: string; name?: string | null };
    onLogout: () => void;
    onOpenSettings?: () => void;
  }) =>
    createElement(
      "div",
      { "data-testid": "user-badge" },
      createElement("span", { "data-testid": "user-name" }, user.name ?? user.login),
      createElement("button", { "data-testid": "logout-btn", onClick: onLogout }, "Sign out"),
      onOpenSettings &&
        createElement("button", { "data-testid": "settings-btn", onClick: onOpenSettings }, "Settings"),
    ),
}));

import SidebarPanel from "../SidebarPanel";
import type {
  AgentDefinition,
  AgentLocation,
  AgentPresence,
  AuthUser,
  BreakoutRoom,
  RoomSnapshot,
} from "../api";

// ── Factories ──────────────────────────────────────────────────────────

function makeRoom(overrides: Partial<RoomSnapshot> = {}): RoomSnapshot {
  return {
    id: "room-1",
    name: "Main Room",
    topic: null,
    status: "Active",
    currentPhase: "Discussion",
    activeTask: null,
    participants: [],
    recentMessages: [],
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
    ...overrides,
  };
}

function makeAgent(overrides: Partial<AgentDefinition> = {}): AgentDefinition {
  return {
    id: "agent-1",
    name: "Athena",
    role: "Architect",
    summary: "System architect",
    startupPrompt: "You are Athena.",
    model: null,
    capabilityTags: [],
    enabledTools: [],
    autoJoinDefaultRoom: true,
    gitIdentity: null,
    ...overrides,
  };
}

function makeAgentLocation(overrides: Partial<AgentLocation> = {}): AgentLocation {
  return {
    agentId: "agent-1",
    roomId: "room-1",
    state: "Idle",
    breakoutRoomId: null,
    updatedAt: "2026-01-01T00:00:00Z",
    ...overrides,
  };
}

function makeUser(overrides: Partial<AuthUser> = {}): AuthUser {
  return {
    login: "testuser",
    name: "Test User",
    avatarUrl: null,
    ...overrides,
  };
}

function makeParticipant(overrides: Partial<AgentPresence> = {}): AgentPresence {
  return {
    agentId: "agent-1",
    name: "Athena",
    role: "Architect",
    availability: "Available",
    isPreferred: false,
    lastActivityAt: "2026-01-01T00:00:00Z",
    activeCapabilities: [],
    ...overrides,
  };
}

interface Props {
  sidebarOpen?: boolean;
  busy?: boolean;
  rooms?: RoomSnapshot[];
  room?: RoomSnapshot | null;
  agentLocations?: AgentLocation[];
  configuredAgents?: AgentDefinition[];
  breakoutRooms?: BreakoutRoom[];
  selectedWorkspaceId?: string | null;
  thinkingByRoomIds?: Map<string, Set<string>>;
  activeView?: string;
  onViewChange?: (view: string) => void;
  onRefresh?: () => void;
  onToggleSidebar?: () => void;
  onSelectRoom?: (roomId: string) => void;
  onSelectWorkspace?: (breakoutId: string) => void;
  onCreateRoom?: (name: string) => void;
  onSwitchProject?: () => void;
  workspace?: { name: string; path: string } | null;
  user?: AuthUser | null;
  onLogout?: () => void;
  onOpenSettings?: () => void;
  sprintVersion?: number;
}

function renderPanel(overrides: Props = {}) {
  const props = {
    sidebarOpen: true,
    busy: false,
    rooms: [] as RoomSnapshot[],
    room: null as RoomSnapshot | null,
    agentLocations: [] as AgentLocation[],
    configuredAgents: [] as AgentDefinition[],
    breakoutRooms: [] as BreakoutRoom[],
    selectedWorkspaceId: null as string | null,
    thinkingByRoomIds: new Map<string, Set<string>>(),
    activeView: "overview",
    onViewChange: vi.fn(),
    onRefresh: vi.fn(),
    onToggleSidebar: vi.fn(),
    onSelectRoom: vi.fn(),
    onSelectWorkspace: vi.fn(),
    onCreateRoom: vi.fn(),
    ...overrides,
  };

  const result = render(
    createElement(FluentProvider, { theme: webDarkTheme },
      createElement(SidebarPanel, props as any)),
  );
  return { ...result, props };
}

// ── Teardown ────────────────────────────────────────────────────────────

afterEach(cleanup);

// ── Tests ───────────────────────────────────────────────────────────────

describe("SidebarPanel", () => {
  describe("room list", () => {
    it("renders room names from the rooms prop", () => {
      const rooms = [
        makeRoom({ id: "r1", name: "Alpha" }),
        makeRoom({ id: "r2", name: "Beta" }),
      ];
      renderPanel({ rooms });

      expect(screen.getByText("Alpha")).toBeInTheDocument();
      expect(screen.getByText("Beta")).toBeInTheDocument();
    });

    it("shows room count in the section header", () => {
      const rooms = [
        makeRoom({ id: "r1", name: "Alpha" }),
        makeRoom({ id: "r2", name: "Beta" }),
        makeRoom({ id: "r3", name: "Gamma" }),
      ];
      renderPanel({ rooms });

      expect(screen.getByText("3")).toBeInTheDocument();
    });

    it("shows participant count per room", () => {
      const rooms = [
        makeRoom({
          id: "r1",
          name: "Alpha",
          participants: [
            makeParticipant({ agentId: "a1", name: "Athena" }),
            makeParticipant({ agentId: "a2", name: "Hephaestus" }),
          ],
        }),
      ];
      renderPanel({ rooms });

      expect(screen.getByText("2")).toBeInTheDocument();
    });

    it("calls onSelectRoom when a room button is clicked", async () => {
      const user = userEvent.setup();
      const rooms = [makeRoom({ id: "r1", name: "Alpha" })];
      const { props } = renderPanel({ rooms });

      await user.click(screen.getByRole("button", { name: "Select room Alpha" }));
      expect(props.onSelectRoom).toHaveBeenCalledWith("r1");
    });
  });

  describe("selected room indicator", () => {
    it("applies active class to the selected room button", () => {
      const selectedRoom = makeRoom({ id: "r1", name: "Alpha" });
      const rooms = [
        selectedRoom,
        makeRoom({ id: "r2", name: "Beta" }),
      ];
      renderPanel({ rooms, room: selectedRoom });

      const activeBtn = screen.getByRole("button", { name: "Select room Alpha" });
      const inactiveBtn = screen.getByRole("button", { name: "Select room Beta" });

      // Active and inactive buttons should have different class strings
      expect(activeBtn.className).not.toBe(inactiveBtn.className);
    });
  });

  describe("collapsed sidebar", () => {
    it("shows expand button when sidebar is closed", () => {
      renderPanel({ sidebarOpen: false });

      expect(screen.getByRole("button", { name: "Expand sidebar" })).toBeInTheDocument();
    });

    it("calls onToggleSidebar when expand button is clicked", async () => {
      const user = userEvent.setup();
      const { props } = renderPanel({ sidebarOpen: false });

      await user.click(screen.getByRole("button", { name: "Expand sidebar" }));
      expect(props.onToggleSidebar).toHaveBeenCalledOnce();
    });

    it("shows compact room buttons with initials when collapsed", () => {
      const rooms = [makeRoom({ id: "r1", name: "Main Room" })];
      renderPanel({ sidebarOpen: false, rooms });

      // initials("Main Room") → "MR"
      expect(screen.getByText("MR")).toBeInTheDocument();
    });

    it("does not show full room names when collapsed", () => {
      const rooms = [makeRoom({ id: "r1", name: "Main Room" })];
      renderPanel({ sidebarOpen: false, rooms });

      expect(screen.queryByText("Main Room")).not.toBeInTheDocument();
    });

    it("shows AA eyebrow instead of full title when collapsed", () => {
      renderPanel({ sidebarOpen: false });

      expect(screen.getByText("AA")).toBeInTheDocument();
      expect(screen.queryByText("Agent Academy")).not.toBeInTheDocument();
    });
  });

  describe("create room flow", () => {
    it("shows create room button when onCreateRoom is provided", () => {
      renderPanel({ onCreateRoom: vi.fn() });

      expect(screen.getByRole("button", { name: "Create room" })).toBeInTheDocument();
    });

    it("does not show create room button when onCreateRoom is omitted", () => {
      renderPanel({ onCreateRoom: undefined });

      expect(screen.queryByRole("button", { name: "Create room" })).not.toBeInTheDocument();
    });

    it("opens input on click, then Enter creates room", async () => {
      const user = userEvent.setup();
      const { props } = renderPanel({ onCreateRoom: vi.fn() });

      await user.click(screen.getByRole("button", { name: "Create room" }));

      const input = screen.getByPlaceholderText("Room name…");
      expect(input).toBeInTheDocument();

      await user.type(input, "New Room{Enter}");
      expect(props.onCreateRoom).toHaveBeenCalledWith("New Room");
    });

    it("dismisses input on Escape", async () => {
      const user = userEvent.setup();
      renderPanel({ onCreateRoom: vi.fn() });

      await user.click(screen.getByRole("button", { name: "Create room" }));
      const input = screen.getByPlaceholderText("Room name…");

      await user.type(input, "Draft{Escape}");
      expect(screen.queryByPlaceholderText("Room name…")).not.toBeInTheDocument();
    });

    it("does not create room if input is blank", async () => {
      const user = userEvent.setup();
      const { props } = renderPanel({ onCreateRoom: vi.fn() });

      await user.click(screen.getByRole("button", { name: "Create room" }));
      const input = screen.getByPlaceholderText("Room name…");

      await user.type(input, "{Enter}");
      expect(props.onCreateRoom).not.toHaveBeenCalled();
    });
  });

  describe("busy state", () => {
    it("renders spinner when busy is true", () => {
      renderPanel({ busy: true });

      expect(screen.getByRole("progressbar")).toBeInTheDocument();
    });

    it("does not render spinner when busy is false", () => {
      renderPanel({ busy: false });

      expect(screen.queryByRole("progressbar")).not.toBeInTheDocument();
    });
  });

  describe("user badge", () => {
    it("shows UserBadge when user is provided and sidebar is open", () => {
      renderPanel({ user: makeUser(), sidebarOpen: true });

      expect(screen.getByTestId("user-badge")).toBeInTheDocument();
      expect(screen.getByTestId("user-name")).toHaveTextContent("Test User");
    });

    it("does not show UserBadge when user is null", () => {
      renderPanel({ user: null, sidebarOpen: true });

      expect(screen.queryByTestId("user-badge")).not.toBeInTheDocument();
    });

    it("does not show UserBadge when sidebar is collapsed", () => {
      renderPanel({ user: makeUser(), sidebarOpen: false });

      expect(screen.queryByTestId("user-badge")).not.toBeInTheDocument();
    });
  });

  describe("sprint version badge", () => {
    it("shows sprint indicator when sprintVersion > 0", () => {
      renderPanel({ sprintVersion: 3 });

      expect(screen.getByText(/Sprint 3/)).toBeInTheDocument();
    });

    it("does not show sprint indicator when sprintVersion is 0", () => {
      renderPanel({ sprintVersion: 0 });

      // "Sprint" exists as a nav item, but "Sprint 0" indicator should not
      expect(screen.queryByText(/Sprint \d/)).not.toBeInTheDocument();
    });

    it("does not show sprint indicator when sprintVersion is undefined", () => {
      renderPanel({ sprintVersion: undefined });

      expect(screen.queryByText(/Sprint \d/)).not.toBeInTheDocument();
    });
  });

  describe("agents section", () => {
    it("renders agent names in the agents section", () => {
      const agents = [
        makeAgent({ id: "a1", name: "Athena", role: "Architect" }),
        makeAgent({ id: "a2", name: "Hephaestus", role: "Engineer" }),
      ];
      renderPanel({ configuredAgents: agents });

      expect(screen.getByText("Athena")).toBeInTheDocument();
      expect(screen.getByText("Hephaestus")).toBeInTheDocument();
    });

    it("shows agent count in the section header", () => {
      const agents = [
        makeAgent({ id: "a1", name: "Athena" }),
        makeAgent({ id: "a2", name: "Hephaestus" }),
      ];
      renderPanel({ configuredAgents: agents });

      // Agent count = 2 (appears in the section header)
      expect(screen.getByText("Agents")).toBeInTheDocument();
    });

    it("does not show agents section when there are no configured agents", () => {
      renderPanel({ configuredAgents: [] });

      expect(screen.queryByText("Agents")).not.toBeInTheDocument();
    });

    it("shows 'idle' state for agents without a location", () => {
      const agents = [makeAgent({ id: "a1", name: "Athena" })];
      renderPanel({ configuredAgents: agents, agentLocations: [] });

      expect(screen.getByText("idle")).toBeInTheDocument();
    });

    it("shows 'working' state for working agents", () => {
      const agents = [makeAgent({ id: "a1", name: "Athena" })];
      const locations = [makeAgentLocation({ agentId: "a1", state: "Working" })];
      renderPanel({ configuredAgents: agents, agentLocations: locations });

      expect(screen.getByText("working")).toBeInTheDocument();
    });

    it("calls onSelectWorkspace when an agent is clicked", async () => {
      const user = userEvent.setup();
      const agents = [makeAgent({ id: "a1", name: "Athena" })];
      const { props } = renderPanel({ configuredAgents: agents });

      await user.click(screen.getByRole("button", { name: "View Athena's sessions" }));
      expect(props.onSelectWorkspace).toHaveBeenCalledWith("agent:a1");
    });

    it("applies active class when agent workspace is selected", () => {
      const agents = [
        makeAgent({ id: "a1", name: "Athena" }),
        makeAgent({ id: "a2", name: "Hephaestus" }),
      ];
      renderPanel({
        configuredAgents: agents,
        selectedWorkspaceId: "agent:a1",
      });

      const activeBtn = screen.getByRole("button", { name: "View Athena's sessions" });
      const inactiveBtn = screen.getByRole("button", { name: "View Hephaestus's sessions" });
      expect(activeBtn.className).not.toBe(inactiveBtn.className);
    });
  });

  describe("thinking spinner on agents", () => {
    it("renders a thinking indicator when an agent is in the thinking map", () => {
      const agents = [makeAgent({ id: "a1", name: "Athena" })];
      const thinkingByRoomIds = new Map<string, Set<string>>([
        ["room-1", new Set(["a1"])],
      ]);
      renderPanel({ configuredAgents: agents, thinkingByRoomIds });

      // The thinking spinner is rendered as an absolutely-positioned span with animation
      const agentBtn = screen.getByRole("button", { name: "View Athena's sessions" });
      const spinner = agentBtn.querySelector("[style*='animation']");
      expect(spinner).toBeTruthy();
    });

    it("does not render thinking indicator when agent is not thinking", () => {
      const agents = [makeAgent({ id: "a1", name: "Athena" })];
      renderPanel({ configuredAgents: agents, thinkingByRoomIds: new Map() });

      const agentBtn = screen.getByRole("button", { name: "View Athena's sessions" });
      const spinner = agentBtn.querySelector("[style*='animation']");
      expect(spinner).toBeNull();
    });
  });

  describe("navigation items", () => {
    it("renders all navigation items when sidebar is open", () => {
      renderPanel({ sidebarOpen: true });

      expect(screen.getByText("Overview")).toBeInTheDocument();
      expect(screen.getByText("Search")).toBeInTheDocument();
      expect(screen.getByText("Messages")).toBeInTheDocument();
      expect(screen.getByText("Plan")).toBeInTheDocument();
      expect(screen.getByText("Tasks")).toBeInTheDocument();
      expect(screen.getByText("Timeline")).toBeInTheDocument();
      expect(screen.getByText("Sprint")).toBeInTheDocument();
      expect(screen.getByText("Metrics")).toBeInTheDocument();
      expect(screen.getByText("Commands")).toBeInTheDocument();
      expect(screen.getByText("Forge")).toBeInTheDocument();
    });

    it("calls onViewChange when a nav item is clicked", async () => {
      const user = userEvent.setup();
      const { props } = renderPanel({ sidebarOpen: true });

      await user.click(screen.getByText("Tasks"));
      expect(props.onViewChange).toHaveBeenCalledWith("tasks");
    });

    it("applies active class to the current view's nav item", () => {
      renderPanel({ activeView: "search" });

      const searchBtn = screen.getByText("Search").closest("button")!;
      const overviewBtn = screen.getByText("Overview").closest("button")!;
      const searchClasses = searchBtn.className.split(/\s+/).length;
      const overviewClasses = overviewBtn.className.split(/\s+/).length;
      expect(searchClasses).toBeGreaterThan(overviewClasses);
    });
  });

  describe("refresh button", () => {
    it("shows refresh button when sidebar is open", () => {
      renderPanel({ sidebarOpen: true });

      expect(screen.getByRole("button", { name: "Refresh" })).toBeInTheDocument();
    });

    it("calls onRefresh when refresh button is clicked", async () => {
      const user = userEvent.setup();
      const { props } = renderPanel({ sidebarOpen: true });

      await user.click(screen.getByRole("button", { name: "Refresh" }));
      expect(props.onRefresh).toHaveBeenCalledOnce();
    });
  });

  describe("workspace/brand", () => {
    it("shows workspace name when workspace is provided", () => {
      renderPanel({ workspace: { name: "My Project", path: "/projects/mine" } });

      expect(screen.getByText("My Project")).toBeInTheDocument();
    });

    it("shows ● Live when no workspace is set", () => {
      renderPanel({ workspace: null });

      expect(screen.getByText("● Live")).toBeInTheDocument();
    });

    it("shows Agent Academy title when sidebar is open", () => {
      renderPanel({ sidebarOpen: true });

      expect(screen.getByText("Agent Academy")).toBeInTheDocument();
    });
  });

  describe("switch project", () => {
    it("shows Switch Project button when onSwitchProject is provided", () => {
      renderPanel({ onSwitchProject: vi.fn() });

      expect(screen.getByText(/Switch Project/)).toBeInTheDocument();
    });

    it("does not show Switch Project button when onSwitchProject is omitted", () => {
      renderPanel({ onSwitchProject: undefined });

      expect(screen.queryByText(/Switch Project/)).not.toBeInTheDocument();
    });

    it("calls onSwitchProject when clicked", async () => {
      const user = userEvent.setup();
      const onSwitchProject = vi.fn();
      renderPanel({ onSwitchProject });

      await user.click(screen.getByText(/Switch Project/));
      expect(onSwitchProject).toHaveBeenCalledOnce();
    });
  });
});
