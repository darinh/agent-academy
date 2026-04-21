// @vitest-environment jsdom
/**
 * Interactive RTL tests for ChatPanel.
 *
 * Uses @testing-library/react + jsdom.
 * Covers: message rendering, send flow (Enter + button), clear, read-only mode,
 * connection status, thinking agents, long-message expand/collapse, session toolbar,
 * agent management dropdown, command result bubbles, and message filtering.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("react-markdown", () => ({
  default: ({ children }: { children?: string }) => createElement("div", { "data-testid": "markdown" }, children),
}));

vi.mock("remark-gfm", () => ({ default: () => {} }));

vi.mock("../api", () => ({
  getRoomSessions: vi.fn(),
  getRoomMessages: vi.fn(),
}));

vi.mock("../recovery", () => ({
  loadChatDraft: vi.fn(() => ""),
  saveChatDraft: vi.fn(),
  clearChatDraft: vi.fn(),
}));

import ChatPanel from "../ChatPanel";
import type {
  ChatEnvelope,
  RoomSnapshot,
  AgentPresence,
  AgentLocation,
  AgentDefinition,
} from "../api";
import { getRoomSessions, getRoomMessages } from "../api";
import { clearChatDraft } from "../recovery";
import type { ThinkingAgent } from "../useWorkspace";
import type { ConnectionStatus, MessageFilter } from "../chatUtils";

const mockGetRoomSessions = vi.mocked(getRoomSessions);
const mockGetRoomMessages = vi.mocked(getRoomMessages);
const mockClearChatDraft = vi.mocked(clearChatDraft);

// ── Factories ──────────────────────────────────────────────────────────

function makeMessage(overrides: Partial<ChatEnvelope> = {}): ChatEnvelope {
  return {
    id: "msg-1",
    roomId: "room-1",
    senderId: "agent-1",
    senderName: "Hephaestus",
    senderRole: "SoftwareEngineer",
    senderKind: "Agent",
    kind: "Response",
    content: "Hello, world!",
    sentAt: "2026-04-10T12:00:00Z",
    correlationId: null,
    replyToMessageId: null,
    ...overrides,
  };
}

function makeSystemMessage(content: string): ChatEnvelope {
  return makeMessage({
    id: `sys-${Math.random().toString(36).slice(2, 8)}`,
    senderId: "system",
    senderName: "System",
    senderRole: null,
    senderKind: "System",
    content,
  });
}

function makeUserMessage(content: string): ChatEnvelope {
  return makeMessage({
    id: `user-${Math.random().toString(36).slice(2, 8)}`,
    senderId: "darinh",
    senderName: "Darin",
    senderRole: "Human",
    senderKind: "User",
    content,
  });
}

function makeParticipant(overrides: Partial<AgentPresence> = {}): AgentPresence {
  return {
    agentId: "agent-1",
    name: "Hephaestus",
    role: "SoftwareEngineer",
    availability: "Ready",
    isPreferred: false,
    lastActivityAt: "2026-04-10T12:00:00Z",
    activeCapabilities: ["implementation"],
    ...overrides,
  };
}

function makeRoom(overrides: Partial<RoomSnapshot> = {}): RoomSnapshot {
  return {
    id: "room-1",
    name: "Main Room",
    topic: null,
    status: "Active",
    currentPhase: "Implementation",
    activeTask: null,
    participants: [makeParticipant()],
    recentMessages: [
      makeMessage({ id: "msg-1", content: "First message" }),
      makeMessage({ id: "msg-2", content: "Second message" }),
    ],
    createdAt: "2026-04-10T10:00:00Z",
    updatedAt: "2026-04-10T12:00:00Z",
    ...overrides,
  };
}

function makeAgentDef(overrides: Partial<AgentDefinition> = {}): AgentDefinition {
  return {
    id: "agent-1",
    name: "Hephaestus",
    role: "SoftwareEngineer",
    summary: "Backend engineer",
    startupPrompt: "",
    model: null,
    capabilityTags: ["implementation"],
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
    updatedAt: "2026-04-10T12:00:00Z",
    ...overrides,
  };
}

// ── Render helper ──────────────────────────────────────────────────────

interface RenderProps {
  room?: RoomSnapshot | null;
  loading?: boolean;
  thinkingAgents?: ThinkingAgent[];
  connectionStatus?: ConnectionStatus;
  onSendMessage?: (roomId: string, content: string) => Promise<boolean>;
  readOnly?: boolean;
  hiddenFilters?: Set<MessageFilter>;
  agentLocations?: AgentLocation[];
  configuredAgents?: AgentDefinition[];
  onCreateSession?: (roomId: string) => void;
  onToggleAgent?: (roomId: string, agentId: string, present: boolean) => void;
}

function renderChat(props: RenderProps = {}) {
  const {
    room = makeRoom(),
    loading = false,
    thinkingAgents = [],
    connectionStatus = "connected",
    onSendMessage = vi.fn(async () => true),
    readOnly,
    hiddenFilters,
    agentLocations,
    configuredAgents,
    onCreateSession,
    onToggleAgent,
  } = props;

  const user = userEvent.setup();
  const result = render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(ChatPanel, {
        room,
        loading,
        thinkingAgents,
        connectionStatus,
        onSendMessage,
        readOnly,
        hiddenFilters,
        agentLocations,
        configuredAgents,
        onCreateSession,
        onToggleAgent,
      }),
    ),
  );
  return { ...result, user, onSendMessage };
}

// ── Setup ──────────────────────────────────────────────────────────────

beforeEach(() => {
  vi.resetAllMocks();
  mockGetRoomSessions.mockResolvedValue({ sessions: [], totalCount: 0 });
  mockGetRoomMessages.mockResolvedValue({ messages: [], hasMore: false });
  // jsdom doesn't implement scrollTo
  Element.prototype.scrollTo = vi.fn();
});

afterEach(cleanup);

// ── Tests ──────────────────────────────────────────────────────────────

describe("ChatPanel (interactive)", () => {
  // ── Message rendering ────────────────────────────────────────────────

  describe("message rendering", () => {
    it("renders agent message bubbles with sender name and role", () => {
      renderChat({
        room: makeRoom({
          recentMessages: [
            makeMessage({ id: "m1", senderName: "Athena", senderRole: "Architect", content: "System design ready" }),
          ],
        }),
      });

      expect(screen.getByText("Athena")).toBeInTheDocument();
      expect(screen.getByText("Architect")).toBeInTheDocument();
      expect(screen.getByText("System design ready")).toBeInTheDocument();
    });

    it("renders user message bubbles with Human role", () => {
      renderChat({
        room: makeRoom({
          recentMessages: [makeUserMessage("Deploy now")],
        }),
      });

      expect(screen.getByText("Darin")).toBeInTheDocument();
      expect(screen.getByText("Human")).toBeInTheDocument();
      expect(screen.getByText("Deploy now")).toBeInTheDocument();
    });

    it("renders system messages without bubble framing", () => {
      renderChat({
        room: makeRoom({
          recentMessages: [makeSystemMessage("Agent joined the room")],
        }),
      });

      expect(screen.getByText("Agent joined the room")).toBeInTheDocument();
    });

    it("renders multiple messages in order", () => {
      const msgs = [
        makeMessage({ id: "m1", senderName: "Alpha", content: "First" }),
        makeMessage({ id: "m2", senderName: "Beta", content: "Second" }),
        makeMessage({ id: "m3", senderName: "Gamma", content: "Third" }),
      ];
      renderChat({ room: makeRoom({ recentMessages: msgs }) });

      const articles = screen.getAllByRole("article");
      expect(articles).toHaveLength(3);
    });
  });

  // ── Empty and loading states ─────────────────────────────────────────

  describe("empty and loading states", () => {
    it("shows empty state when room has no messages", () => {
      renderChat({ room: makeRoom({ recentMessages: [] }) });
      expect(screen.getByText("No messages yet")).toBeInTheDocument();
    });

    it("shows skeleton loader when loading with no messages", () => {
      renderChat({ loading: true, room: makeRoom({ recentMessages: [] }) });
      // SkeletonLoader renders shimmer divs; the empty state text should NOT appear
      expect(screen.queryByText("No messages yet")).not.toBeInTheDocument();
    });

    it("shows the conversation log role when room is present", () => {
      renderChat();
      expect(screen.getByRole("log", { name: /conversation messages/i })).toBeInTheDocument();
    });
  });

  // ── Composer – sending messages ──────────────────────────────────────

  describe("sending messages", () => {
    it("sends a message on Enter and clears the textarea", async () => {
      const onSend = vi.fn(async () => true);
      const { user } = renderChat({ onSendMessage: onSend });

      const textarea = screen.getByRole("textbox", { name: /message to agents/i });
      await user.type(textarea, "Hello agents{enter}");

      await waitFor(() => {
        expect(onSend).toHaveBeenCalledWith("room-1", "Hello agents");
      });
    });

    it("sends a message on Send button click", async () => {
      const onSend = vi.fn(async () => true);
      const { user } = renderChat({ onSendMessage: onSend });

      const textarea = screen.getByRole("textbox", { name: /message to agents/i });
      await user.type(textarea, "Click send");

      const sendBtn = screen.getByRole("button", { name: /send message/i });
      await user.click(sendBtn);

      await waitFor(() => {
        expect(onSend).toHaveBeenCalledWith("room-1", "Click send");
      });
    });

    it("disables Send button when textarea is empty", () => {
      renderChat();
      const sendBtn = screen.getByRole("button", { name: /send message/i });
      expect(sendBtn).toBeDisabled();
    });

    it("clears the draft on successful send", async () => {
      const onSend = vi.fn(async () => true);
      const { user } = renderChat({ onSendMessage: onSend });

      const textarea = screen.getByRole("textbox", { name: /message to agents/i });
      await user.type(textarea, "Draft message{enter}");

      await waitFor(() => {
        expect(mockClearChatDraft).toHaveBeenCalledWith("room-1");
      });
    });

    it("does not clear input when send returns false", async () => {
      const onSend = vi.fn(async () => false);
      const { user } = renderChat({ onSendMessage: onSend });

      const textarea = screen.getByRole("textbox", { name: /message to agents/i });
      await user.type(textarea, "Will fail{enter}");

      await waitFor(() => {
        expect(onSend).toHaveBeenCalled();
      });
      // Draft should NOT be cleared
      expect(mockClearChatDraft).not.toHaveBeenCalled();
    });
  });

  // ── Clear button ─────────────────────────────────────────────────────

  describe("clear button", () => {
    it("clears the textarea when clicked", async () => {
      const { user } = renderChat();

      const textarea = screen.getByRole("textbox", { name: /message to agents/i });
      await user.type(textarea, "Some draft text");

      const clearBtn = screen.getByRole("button", { name: /clear/i });
      await user.click(clearBtn);

      expect(textarea).toHaveValue("");
    });

    it("is disabled when textarea is empty", () => {
      renderChat();
      const clearBtn = screen.getByRole("button", { name: /clear/i });
      expect(clearBtn).toBeDisabled();
    });
  });

  // ── Read-only mode ───────────────────────────────────────────────────

  describe("read-only mode", () => {
    it("hides the composer when readOnly is true", () => {
      renderChat({ readOnly: true });
      expect(screen.queryByRole("textbox", { name: /message to agents/i })).not.toBeInTheDocument();
      expect(screen.queryByRole("button", { name: /send message/i })).not.toBeInTheDocument();
    });

    it("still renders messages when readOnly", () => {
      renderChat({
        readOnly: true,
        room: makeRoom({
          recentMessages: [makeMessage({ id: "m1", content: "Visible message" })],
        }),
      });
      expect(screen.getByText("Visible message")).toBeInTheDocument();
    });
  });

  // ── Connection status ────────────────────────────────────────────────

  describe("connection status", () => {
    it("shows no status bar when connected", () => {
      renderChat({ connectionStatus: "connected" });
      expect(screen.queryByRole("status")).not.toBeInTheDocument();
    });

    it("shows reconnecting status bar", () => {
      renderChat({ connectionStatus: "reconnecting" });
      const status = screen.getByRole("status");
      expect(status).toHaveTextContent(/reconnecting/i);
    });

    it("shows disconnected status bar", () => {
      renderChat({ connectionStatus: "disconnected" });
      const status = screen.getByRole("status");
      expect(status).toHaveTextContent(/disconnected/i);
    });

    it("shows connecting status bar", () => {
      renderChat({ connectionStatus: "connecting" });
      const status = screen.getByRole("status");
      expect(status).toHaveTextContent(/connecting/i);
    });
  });

  // ── Thinking agents ──────────────────────────────────────────────────

  describe("thinking agents", () => {
    it("renders thinking bubble for each thinking agent", () => {
      renderChat({
        thinkingAgents: [
          { id: "a1", name: "Athena", role: "Architect" },
          { id: "a2", name: "Ares", role: "QA" },
        ],
      });

      expect(screen.getByLabelText("Athena is thinking")).toBeInTheDocument();
      expect(screen.getByLabelText("Ares is thinking")).toBeInTheDocument();
    });

    it("renders no thinking bubbles when array is empty", () => {
      renderChat({ thinkingAgents: [] });
      expect(screen.queryByText(/is thinking/)).not.toBeInTheDocument();
    });
  });

  // ── Long message expand/collapse ─────────────────────────────────────

  describe("long message expand/collapse", () => {
    const longContent = "A".repeat(400);

    it("shows 'Show more' button for long messages", () => {
      renderChat({
        room: makeRoom({
          recentMessages: [makeMessage({ id: "long-1", content: longContent })],
        }),
      });

      expect(screen.getByText("Show more")).toBeInTheDocument();
    });

    it("expands to full content and toggles back to collapsed", async () => {
      const { user } = renderChat({
        room: makeRoom({
          recentMessages: [makeMessage({ id: "long-1", content: longContent })],
        }),
      });

      // Expand
      await user.click(screen.getByText("Show more"));
      expect(screen.getByText("Show less")).toBeInTheDocument();

      // Collapse
      await user.click(screen.getByText("Show less"));
      expect(screen.getByText("Show more")).toBeInTheDocument();
    });

    it("does not show expand button for short messages", () => {
      renderChat({
        room: makeRoom({
          recentMessages: [makeMessage({ id: "short-1", content: "Short msg" })],
        }),
      });

      expect(screen.queryByText("Show more")).not.toBeInTheDocument();
    });
  });

  // ── Command result messages ──────────────────────────────────────────

  describe("command result messages", () => {
    it("renders command result bubbles for system messages with result format", () => {
      const cmdContent = "=== COMMAND RESULTS ===\n[Success] RUN_BUILD (corr-123)\nBuild completed successfully.\n=== END ===";
      renderChat({
        room: makeRoom({
          recentMessages: [makeSystemMessage(cmdContent)],
        }),
      });

      expect(screen.getByText("RUN_BUILD")).toBeInTheDocument();
    });
  });

  // ── Session toolbar ──────────────────────────────────────────────────

  describe("session toolbar", () => {
    it("shows New Session button when onCreateSession is provided", () => {
      renderChat({ onCreateSession: vi.fn() });
      expect(screen.getByText("+ New Session")).toBeInTheDocument();
    });

    it("does not show New Session button when onCreateSession is omitted", () => {
      renderChat();
      expect(screen.queryByText("+ New Session")).not.toBeInTheDocument();
    });

    it("calls onCreateSession when New Session is clicked", async () => {
      const onCreateSession = vi.fn();
      const { user } = renderChat({ onCreateSession });

      await user.click(screen.getByText("+ New Session"));
      expect(onCreateSession).toHaveBeenCalledWith("room-1");
    });

    it("shows session dropdown with current session option", () => {
      renderChat();
      const select = screen.getByRole("combobox");
      expect(select).toBeInTheDocument();
      expect(within(select).getByText("Current session")).toBeInTheDocument();
    });
  });

  // ── Agent management dropdown ────────────────────────────────────────

  describe("agent management", () => {
    it("shows Agents button with count when agents and toggle handler provided", () => {
      renderChat({
        configuredAgents: [makeAgentDef()],
        agentLocations: [makeAgentLocation()],
        onToggleAgent: vi.fn(),
      });

      expect(screen.getByText("Agents (1)")).toBeInTheDocument();
    });

    it("opens agent dropdown on click and shows agent checkbox", async () => {
      const { user } = renderChat({
        configuredAgents: [
          makeAgentDef({ id: "a1", name: "Athena", role: "Architect" }),
          makeAgentDef({ id: "a2", name: "Ares", role: "QA" }),
        ],
        agentLocations: [makeAgentLocation({ agentId: "a1", roomId: "room-1" })],
        onToggleAgent: vi.fn(),
      });

      await user.click(screen.getByText(/^Agents/));

      // Dropdown should show both agents
      expect(screen.getByText("Athena")).toBeInTheDocument();
      expect(screen.getByText("Ares")).toBeInTheDocument();

      // Athena should be checked (in room), Ares unchecked
      const checkboxes = screen.getAllByRole("checkbox");
      expect(checkboxes[0]).toBeChecked(); // Athena
      expect(checkboxes[1]).not.toBeChecked(); // Ares
    });

    it("calls onToggleAgent when a checkbox is toggled", async () => {
      const onToggle = vi.fn();
      const { user } = renderChat({
        configuredAgents: [makeAgentDef({ id: "a1", name: "Athena" })],
        agentLocations: [makeAgentLocation({ agentId: "a1", roomId: "room-1" })],
        onToggleAgent: onToggle,
      });

      await user.click(screen.getByText(/^Agents/));
      const checkbox = screen.getByRole("checkbox");
      await user.click(checkbox);

      expect(onToggle).toHaveBeenCalledWith("room-1", "a1", true);
    });

    it("does not show Agents button when no configured agents", () => {
      renderChat({
        configuredAgents: [],
        onToggleAgent: vi.fn(),
      });

      expect(screen.queryByText(/^Agents/)).not.toBeInTheDocument();
    });
  });

  // ── Message filtering ────────────────────────────────────────────────

  describe("message filtering", () => {
    it("hides system messages when 'system' filter is active", () => {
      renderChat({
        hiddenFilters: new Set(["system"] as MessageFilter[]),
        room: makeRoom({
          recentMessages: [
            makeMessage({ id: "m1", content: "Agent response" }),
            makeSystemMessage("System notification"),
          ],
        }),
      });

      expect(screen.getByText("Agent response")).toBeInTheDocument();
      expect(screen.queryByText("System notification")).not.toBeInTheDocument();
    });

    it("shows 'All messages filtered' when all messages are hidden", () => {
      renderChat({
        hiddenFilters: new Set(["system"] as MessageFilter[]),
        room: makeRoom({
          recentMessages: [makeSystemMessage("Only system msg")],
        }),
      });

      expect(screen.getByText("All messages filtered")).toBeInTheDocument();
    });
  });

  // ── Null room ────────────────────────────────────────────────────────

  describe("null room", () => {
    it("renders empty state with no composer when room is null", () => {
      renderChat({ room: null });
      expect(screen.queryByRole("textbox", { name: /message to agents/i })).not.toBeInTheDocument();
    });
  });

  // ── Session load error states ─────────────────────────────────────────

  describe("session load error", () => {
    it("shows error message when session load fails", async () => {
      mockGetRoomSessions.mockRejectedValue(new Error("Network error"));
      renderChat();

      await waitFor(() => {
        expect(screen.getByText(/Failed to load sessions/)).toBeInTheDocument();
      });
    });

    it("shows retry button alongside error", async () => {
      mockGetRoomSessions.mockRejectedValue(new Error("timeout"));
      renderChat();

      await waitFor(() => {
        expect(screen.getByText(/Failed to load sessions/)).toBeInTheDocument();
      });
      expect(screen.getByRole("button", { name: /retry/i })).toBeInTheDocument();
    });

    it("retry button re-fetches sessions successfully", async () => {
      // getRoomSessions is called twice on mount: once for archived check, once for session list.
      // Use mockRejectedValue for all initial calls, then switch to resolved after error is shown.
      mockGetRoomSessions.mockReset();
      mockGetRoomSessions.mockRejectedValue(new Error("timeout"));

      const { user } = renderChat();

      await waitFor(() => {
        expect(screen.getByText(/Failed to load sessions/)).toBeInTheDocument();
      });

      const callsBeforeRetry = mockGetRoomSessions.mock.calls.length;

      // Now switch mock to resolve for the retry
      mockGetRoomSessions.mockResolvedValue({ sessions: [], totalCount: 0 });

      await user.click(screen.getByRole("button", { name: /retry/i }));

      await waitFor(() => {
        // Verify the retry actually made an API call, not just cleared the UI
        expect(mockGetRoomSessions.mock.calls.length).toBeGreaterThan(callsBeforeRetry);
        expect(screen.queryByText(/Failed to load sessions/)).not.toBeInTheDocument();
      });
    });

    it("retry shows error again if re-fetch also fails", async () => {
      mockGetRoomSessions.mockReset();
      mockGetRoomSessions.mockRejectedValue(new Error("persistent failure"));
      const { user } = renderChat();

      await waitFor(() => {
        expect(screen.getByText(/Failed to load sessions/)).toBeInTheDocument();
      });

      const callsBeforeRetry = mockGetRoomSessions.mock.calls.length;

      await user.click(screen.getByRole("button", { name: /retry/i }));

      await waitFor(() => {
        // Verify retry actually called the API again and got the same error
        expect(mockGetRoomSessions.mock.calls.length).toBeGreaterThan(callsBeforeRetry);
        expect(screen.getByText(/Failed to load sessions/)).toBeInTheDocument();
      });
    });

    it("clears error when room changes to null", async () => {
      mockGetRoomSessions.mockRejectedValue(new Error("fail"));
      const { rerender } = renderChat();

      await waitFor(() => {
        expect(screen.getByText(/Failed to load sessions/)).toBeInTheDocument();
      });

      // Re-render with null room
      rerender(
        createElement(
          FluentProvider,
          { theme: webDarkTheme },
          createElement(ChatPanel, {
            room: null,
            loading: false,
            thinkingAgents: [],
            connectionStatus: "connected" as ConnectionStatus,
            onSendMessage: vi.fn(async () => true),
          }),
        ),
      );

      await waitFor(() => {
        expect(screen.queryByText(/Failed to load sessions/)).not.toBeInTheDocument();
      });
    });

    it("clears error when room changes to a different room", async () => {
      mockGetRoomSessions.mockReset();
      mockGetRoomSessions.mockRejectedValue(new Error("fail"));
      const room1 = makeRoom({ id: "room-1" });
      const room2 = makeRoom({ id: "room-2", recentMessages: [makeMessage({ id: "m-new", content: "New room msg" })] });

      const { rerender } = renderChat({ room: room1 });

      await waitFor(() => {
        expect(screen.getByText(/Failed to load sessions/)).toBeInTheDocument();
      });

      // Switch to a different room — sessions mock should now succeed
      mockGetRoomSessions.mockResolvedValue({ sessions: [], totalCount: 0 });
      rerender(
        createElement(
          FluentProvider,
          { theme: webDarkTheme },
          createElement(ChatPanel, {
            room: room2,
            loading: false,
            thinkingAgents: [],
            connectionStatus: "connected" as ConnectionStatus,
            onSendMessage: vi.fn(async () => true),
          }),
        ),
      );

      await waitFor(() => {
        expect(screen.queryByText(/Failed to load sessions/)).not.toBeInTheDocument();
      });
    });
  });

  // ── Composer hint text ───────────────────────────────────────────────

  describe("composer hints", () => {
    it("shows keyboard shortcut hint", () => {
      renderChat();
      expect(screen.getByText(/enter to send/i)).toBeInTheDocument();
    });

    it("shows 'Message the team' label", () => {
      renderChat();
      expect(screen.getByText("Message the team")).toBeInTheDocument();
    });
  });

  // ── Previous-session tail (empty-active-session UX fix) ────────────

  describe("previous session tail", () => {
    function makeSession(overrides: Record<string, unknown> = {}) {
      return {
        id: "session-archived",
        roomId: "room-1",
        roomType: "Main",
        sequenceNumber: 2,
        status: "Archived",
        summary: null,
        messageCount: 50,
        createdAt: "2026-04-10T09:00:00Z",
        archivedAt: "2026-04-10T11:00:00Z",
        ...overrides,
      };
    }

    it("fetches and renders tail of most recent archived session when active session is nearly empty", async () => {
      const archived = makeSession({ id: "sess-old", sequenceNumber: 2, status: "Archived" });
      const active = makeSession({
        id: "sess-new", sequenceNumber: 3, status: "Active",
        messageCount: 0, archivedAt: null,
      });
      mockGetRoomSessions.mockResolvedValue({ sessions: [active, archived], totalCount: 2 });
      mockGetRoomMessages.mockResolvedValue({
        messages: [
          makeMessage({ id: "tail-1", content: "Tail message one" }),
          makeMessage({ id: "tail-2", content: "Tail message two" }),
        ],
        hasMore: false,
      });

      // Room with an empty active session (matches the reported bug state).
      renderChat({ room: makeRoom({ recentMessages: [] }) });

      await waitFor(() => {
        expect(mockGetRoomMessages).toHaveBeenCalledWith(
          "room-1",
          expect.objectContaining({ sessionId: "sess-old", limit: 20 }),
        );
      });

      await waitFor(() => {
        expect(screen.getByTestId("previous-session-divider")).toBeInTheDocument();
      });
      expect(screen.getByText("Tail message one")).toBeInTheDocument();
      expect(screen.getByText("Tail message two")).toBeInTheDocument();
    });

    it("does NOT fetch tail when active session already has enough messages", async () => {
      const archived = makeSession({ id: "sess-old", sequenceNumber: 2, status: "Archived" });
      const active = makeSession({
        id: "sess-new", sequenceNumber: 3, status: "Active",
        messageCount: 20, archivedAt: null,
      });
      mockGetRoomSessions.mockResolvedValue({ sessions: [active, archived], totalCount: 2 });

      // 10+ recent messages — above the near-empty threshold.
      const liveMsgs = Array.from({ length: 12 }, (_, i) =>
        makeMessage({ id: `live-${i}`, content: `Live ${i}` }),
      );
      renderChat({ room: makeRoom({ recentMessages: liveMsgs }) });

      // Let sessions load and effects settle.
      await waitFor(() => {
        expect(mockGetRoomSessions).toHaveBeenCalled();
      });

      // Tail fetch (by sessionId) must not have been called.
      const tailFetches = mockGetRoomMessages.mock.calls.filter(
        ([, opts]) => opts && (opts as { sessionId?: string }).sessionId === "sess-old",
      );
      expect(tailFetches).toHaveLength(0);
      expect(screen.queryByTestId("previous-session-divider")).not.toBeInTheDocument();
    });

    it("hides the 'agents have context' banner when the tail is shown (divider replaces it)", async () => {
      const archived = makeSession({ id: "sess-old", status: "Archived" });
      const active = makeSession({ id: "sess-new", sequenceNumber: 3, status: "Active", messageCount: 0, archivedAt: null });
      // First call (archived-count probe) returns 1; second call (full list) returns sessions.
      mockGetRoomSessions
        .mockResolvedValueOnce({ sessions: [archived], totalCount: 1 })
        .mockResolvedValueOnce({ sessions: [active, archived], totalCount: 2 });
      mockGetRoomMessages.mockResolvedValue({
        messages: [makeMessage({ id: "tail-1", content: "Tail msg" })],
        hasMore: false,
      });

      renderChat({ room: makeRoom({ recentMessages: [] }) });

      await waitFor(() => {
        expect(screen.getByTestId("previous-session-divider")).toBeInTheDocument();
      });
      expect(
        screen.queryByText(/Agents have context from a previous conversation session/i),
      ).not.toBeInTheDocument();
    });

    it("does NOT show tail when there is no archived session", async () => {
      const active = makeSession({ id: "sess-new", sequenceNumber: 1, status: "Active", messageCount: 0, archivedAt: null });
      mockGetRoomSessions.mockResolvedValue({ sessions: [active], totalCount: 1 });

      renderChat({ room: makeRoom({ recentMessages: [] }) });

      await waitFor(() => {
        expect(mockGetRoomSessions).toHaveBeenCalled();
      });
      // No tail fetch should happen
      const tailFetches = mockGetRoomMessages.mock.calls.filter(
        ([, opts]) => opts && (opts as { sessionId?: string }).sessionId !== undefined,
      );
      expect(tailFetches).toHaveLength(0);
      expect(screen.queryByTestId("previous-session-divider")).not.toBeInTheDocument();
    });
  });
});
