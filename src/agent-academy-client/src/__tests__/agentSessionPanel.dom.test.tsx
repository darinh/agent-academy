// @vitest-environment jsdom
/**
 * Interactive RTL tests for AgentSessionPanel.
 *
 * Covers: loading state, error state with retry, empty sessions,
 * agent header rendering, active/archived session display,
 * session selection toggle, and refresh behaviour.
 */
import "@testing-library/jest-dom/vitest";
import {
  cleanup,
  render,
  screen,
  waitFor,
  act,
  fireEvent,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../ChatPanel", () => ({
  default: (props: Record<string, unknown>) => {
    const room = props.room as { id: string } | undefined;
    return createElement(
      "div",
      { "data-testid": "chat-panel" },
      createElement("span", { "data-testid": "chat-room-id" }, room?.id ?? ""),
      createElement(
        "span",
        { "data-testid": "chat-read-only" },
        String(props.readOnly ?? false),
      ),
    );
  },
}));

vi.mock("../api", () => ({
  getAgentSessions: vi.fn(),
}));

import AgentSessionPanel from "../AgentSessionPanel";
import type {
  AgentDefinition,
  AgentLocation,
  BreakoutRoom,
  ChatEnvelope,
} from "../api";
import { getAgentSessions } from "../api";

const mockGetAgentSessions = vi.mocked(getAgentSessions);

// ── Factories ──────────────────────────────────────────────────────────

function makeAgent(overrides: Partial<AgentDefinition> = {}): AgentDefinition {
  return {
    id: "agent-1",
    name: "Hephaestus",
    role: "SoftwareEngineer",
    summary: "A software engineer agent",
    startupPrompt: "You are a software engineer.",
    model: null,
    capabilityTags: [],
    enabledTools: [],
    autoJoinDefaultRoom: true,
    gitIdentity: null,
    ...overrides,
  };
}

function makeLocation(
  overrides: Partial<AgentLocation> = {},
): AgentLocation {
  return {
    agentId: "agent-1",
    roomId: "room-1",
    state: "Idle",
    breakoutRoomId: null,
    updatedAt: "2026-04-10T12:00:00Z",
    ...overrides,
  };
}

function makeMessage(
  overrides: Partial<ChatEnvelope> = {},
): ChatEnvelope {
  return {
    id: "msg-1",
    roomId: "session-1",
    senderId: "agent-1",
    senderName: "Hephaestus",
    senderRole: "SoftwareEngineer",
    senderKind: "Agent",
    kind: "chat",
    content: "Working on the task.",
    sentAt: "2026-04-10T12:00:00Z",
    correlationId: null,
    replyToMessageId: null,
    ...overrides,
  };
}

function makeSession(
  overrides: Partial<BreakoutRoom> = {},
): BreakoutRoom {
  return {
    id: "session-1",
    name: "BR: Implement auth module",
    parentRoomId: "room-1",
    assignedAgentId: "agent-1",
    tasks: [],
    status: "Active",
    recentMessages: [makeMessage()],
    createdAt: "2026-04-10T12:00:00Z",
    updatedAt: "2026-04-10T12:30:00Z",
    ...overrides,
  };
}

// ── Render helper ──────────────────────────────────────────────────────

interface RenderOpts {
  agent?: AgentDefinition;
  location?: AgentLocation;
}

function renderPanel(opts: RenderOpts = {}) {
  const { agent = makeAgent(), location = makeLocation() } = opts;
  const sendMessage = vi.fn().mockResolvedValue(true);
  return render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(AgentSessionPanel, {
        agent,
        location,
        thinkingAgents: [],
        connectionStatus: "connected" as const,
        onSendMessage: sendMessage,
      }),
    ),
  );
}

// ── Setup & teardown ───────────────────────────────────────────────────

beforeEach(() => {
  Element.prototype.scrollTo = vi.fn();
  mockGetAgentSessions.mockClear();
});

afterEach(() => {
  cleanup();
  document.body.innerHTML = "";
  vi.restoreAllMocks();
});

// ── Tests ──────────────────────────────────────────────────────────────

describe("AgentSessionPanel", () => {
  // ── Loading state ───────────────────────────────────────────────────

  describe("loading state", () => {
    it("shows spinner while sessions are loading", () => {
      mockGetAgentSessions.mockReturnValue(new Promise(() => {}));
      renderPanel();
      expect(screen.getByText("Loading sessions...")).toBeInTheDocument();
    });

    it("does not render session content during loading", () => {
      mockGetAgentSessions.mockReturnValue(new Promise(() => {}));
      renderPanel();
      expect(
        screen.queryByText(/No sessions yet/),
      ).not.toBeInTheDocument();
      expect(screen.queryByTestId("chat-panel")).not.toBeInTheDocument();
    });
  });

  // ── Error state ─────────────────────────────────────────────────────

  describe("error state", () => {
    it("shows error message when loading fails", async () => {
      mockGetAgentSessions.mockRejectedValue(new Error("Network error"));
      renderPanel();
      await waitFor(() => {
        expect(
          screen.getByText(/Failed to load sessions/),
        ).toBeInTheDocument();
      });
    });

    it("shows retry button on error", async () => {
      mockGetAgentSessions.mockRejectedValue(new Error("fail"));
      renderPanel();
      await waitFor(() => {
        expect(
          screen.getByRole("button", { name: /Retry/i }),
        ).toBeInTheDocument();
      });
    });

    it("retries loading when retry button is clicked", async () => {
      mockGetAgentSessions.mockRejectedValueOnce(new Error("fail"));
      mockGetAgentSessions.mockResolvedValueOnce([]);
      renderPanel();
      await waitFor(() => {
        expect(
          screen.getByText(/Failed to load sessions/),
        ).toBeInTheDocument();
      });
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: /Retry/i }));
      });
      await waitFor(() => {
        expect(screen.getByText(/No sessions yet/)).toBeInTheDocument();
      });
      expect(mockGetAgentSessions).toHaveBeenCalledTimes(2);
    });
  });

  // ── Empty state ─────────────────────────────────────────────────────

  describe("empty state", () => {
    it("shows empty message when no sessions exist", async () => {
      mockGetAgentSessions.mockResolvedValue([]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/No sessions yet/)).toBeInTheDocument();
      });
    });

    it("mentions breakout tasks in empty message", async () => {
      mockGetAgentSessions.mockResolvedValue([]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/breakout tasks/)).toBeInTheDocument();
      });
    });
  });

  // ── Agent header ────────────────────────────────────────────────────

  describe("agent header", () => {
    it("renders agent name and role", async () => {
      mockGetAgentSessions.mockResolvedValue([]);
      renderPanel({
        agent: makeAgent({ name: "Athena", role: "Architect" }),
      });
      await waitFor(() => {
        expect(screen.getByText("Athena")).toBeInTheDocument();
        expect(screen.getByText("Architect")).toBeInTheDocument();
      });
    });

    it("renders agent initial in avatar", async () => {
      mockGetAgentSessions.mockResolvedValue([]);
      renderPanel({ agent: makeAgent({ name: "Zeus" }) });
      await waitFor(() => {
        expect(screen.getByText("Z")).toBeInTheDocument();
      });
    });

    it("renders state badge from location", async () => {
      mockGetAgentSessions.mockResolvedValue([]);
      renderPanel({ location: makeLocation({ state: "Working" }) });
      await waitFor(() => {
        expect(screen.getByText("Working")).toBeInTheDocument();
      });
    });

    it("shows Idle state when no location provided", async () => {
      mockGetAgentSessions.mockResolvedValue([]);
      const sendMessage = vi.fn().mockResolvedValue(true);
      render(
        createElement(
          FluentProvider,
          { theme: webDarkTheme },
          createElement(AgentSessionPanel, {
            agent: makeAgent(),
            location: undefined,
            thinkingAgents: [],
            connectionStatus: "connected" as const,
            onSendMessage: sendMessage,
          }),
        ),
      );
      await waitFor(() => {
        expect(screen.getByText("Idle")).toBeInTheDocument();
      });
    });

    it("has a refresh button", async () => {
      mockGetAgentSessions.mockResolvedValue([]);
      renderPanel();
      await waitFor(() => {
        expect(
          screen.getByRole("button", { name: "↻" }),
        ).toBeInTheDocument();
      });
    });
  });

  // ── Active session display ──────────────────────────────────────────

  describe("active session", () => {
    it("renders ChatPanel for the active session", async () => {
      mockGetAgentSessions.mockResolvedValue([
        makeSession({ status: "Active" }),
      ]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByTestId("chat-panel")).toBeInTheDocument();
      });
    });

    it("passes correct room id to ChatPanel", async () => {
      mockGetAgentSessions.mockResolvedValue([
        makeSession({ id: "sess-42", status: "Active" }),
      ]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByTestId("chat-room-id")).toHaveTextContent(
          "sess-42",
        );
      });
    });

    it("passes readOnly=true to ChatPanel", async () => {
      mockGetAgentSessions.mockResolvedValue([
        makeSession({ status: "Active" }),
      ]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByTestId("chat-read-only")).toHaveTextContent(
          "true",
        );
      });
    });

    it("strips BR: prefix from session name", async () => {
      mockGetAgentSessions.mockResolvedValue([
        makeSession({
          name: "BR: Implement feature X",
          status: "Active",
        }),
      ]);
      renderPanel();
      await waitFor(() => {
        expect(
          screen.getByText("Implement feature X"),
        ).toBeInTheDocument();
      });
    });

    it("shows message count", async () => {
      const msgs = [
        makeMessage({ id: "m1" }),
        makeMessage({ id: "m2" }),
        makeMessage({ id: "m3" }),
      ];
      mockGetAgentSessions.mockResolvedValue([
        makeSession({ status: "Active", recentMessages: msgs }),
      ]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/3 messages/)).toBeInTheDocument();
      });
    });

    it("shows Active badge for active sessions", async () => {
      mockGetAgentSessions.mockResolvedValue([
        makeSession({ status: "Active" }),
      ]);
      renderPanel();
      await waitFor(() => {
        const badges = screen.getAllByText("Active");
        expect(badges.length).toBeGreaterThan(0);
      });
    });
  });

  // ── Archived sessions ───────────────────────────────────────────────

  describe("archived sessions", () => {
    it("renders Past Sessions heading", async () => {
      mockGetAgentSessions.mockResolvedValue([
        makeSession({ id: "s1", status: "Completed", name: "BR: Old task" }),
      ]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/Past Sessions/)).toBeInTheDocument();
        expect(screen.getByText("Old task")).toBeInTheDocument();
      });
    });

    it("shows count of archived sessions", async () => {
      mockGetAgentSessions.mockResolvedValue([
        makeSession({ id: "s1", status: "Completed" }),
        makeSession({ id: "s2", status: "Archived" }),
      ]);
      renderPanel();
      await waitFor(() => {
        expect(
          screen.getByText(/Past Sessions \(2\)/),
        ).toBeInTheDocument();
      });
    });

    it("shows message count for archived sessions", async () => {
      mockGetAgentSessions.mockResolvedValue([
        makeSession({
          id: "s1",
          status: "Completed",
          recentMessages: [makeMessage(), makeMessage({ id: "m2" })],
        }),
      ]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/2 msgs/)).toBeInTheDocument();
      });
    });

    it("expands archived session when clicked", async () => {
      mockGetAgentSessions.mockResolvedValue([
        makeSession({
          id: "s1",
          status: "Completed",
          name: "BR: Past task",
        }),
      ]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Past task")).toBeInTheDocument();
      });
      await act(async () => {
        fireEvent.click(screen.getByText("Past task"));
      });
      await waitFor(() => {
        expect(screen.getByTestId("chat-room-id")).toHaveTextContent("s1");
      });
    });

    it("collapses archived session when clicked again", async () => {
      mockGetAgentSessions.mockResolvedValue([
        makeSession({
          id: "s1",
          status: "Completed",
          name: "BR: Past task",
        }),
      ]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Past task")).toBeInTheDocument();
      });
      // The archived list button is the only <button type="button"> with this text
      const listBtn = () =>
        screen.getAllByText("Past task").find((el) => el.closest("button[type='button']"))!;
      // Click to expand
      await act(async () => {
        fireEvent.click(listBtn());
      });
      await waitFor(() => {
        expect(screen.getByTestId("chat-room-id")).toHaveTextContent("s1");
      });
      // Click to collapse — name now appears in header AND list
      await act(async () => {
        fireEvent.click(listBtn());
      });
      await waitFor(() => {
        expect(
          screen.queryByTestId("chat-panel"),
        ).not.toBeInTheDocument();
      });
    });
  });

  // ── Mixed active + archived ─────────────────────────────────────────

  describe("mixed active and archived sessions", () => {
    it("auto-expands the first active session", async () => {
      mockGetAgentSessions.mockResolvedValue([
        makeSession({
          id: "active-1",
          status: "Active",
          name: "BR: Current work",
        }),
        makeSession({
          id: "arch-1",
          status: "Completed",
          name: "BR: Old work",
        }),
      ]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByTestId("chat-room-id")).toHaveTextContent(
          "active-1",
        );
      });
    });

    it("does not list active sessions under Past Sessions", async () => {
      mockGetAgentSessions.mockResolvedValue([
        makeSession({
          id: "active-1",
          status: "Active",
          name: "BR: Active task",
        }),
        makeSession({
          id: "arch-1",
          status: "Completed",
          name: "BR: Done task",
        }),
      ]);
      renderPanel();
      await waitFor(() => {
        expect(
          screen.getByText(/Past Sessions \(1\)/),
        ).toBeInTheDocument();
      });
    });
  });

  // ── Refresh ─────────────────────────────────────────────────────────

  describe("refresh", () => {
    it("reloads sessions when refresh button is clicked", async () => {
      mockGetAgentSessions.mockResolvedValueOnce([]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/No sessions yet/)).toBeInTheDocument();
      });
      mockGetAgentSessions.mockResolvedValueOnce([makeSession()]);
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "↻" }));
      });
      await waitFor(() => {
        expect(screen.getByTestId("chat-panel")).toBeInTheDocument();
      });
      expect(mockGetAgentSessions).toHaveBeenCalledTimes(2);
    });

    it("calls getAgentSessions with the agent id", async () => {
      mockGetAgentSessions.mockResolvedValue([]);
      renderPanel({ agent: makeAgent({ id: "my-agent-42" }) });
      await waitFor(() => {
        expect(screen.getByText(/No sessions yet/)).toBeInTheDocument();
      });
      expect(mockGetAgentSessions).toHaveBeenCalledWith("my-agent-42");
    });
  });
});
