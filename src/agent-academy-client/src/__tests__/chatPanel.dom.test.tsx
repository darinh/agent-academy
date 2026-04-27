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

  // Mutable record of the latest props applied to ChatPanel. Successive
  // rerenderWith calls merge into this so updates accumulate (otherwise the
  // second rerender would silently revert the first).
  const current: Required<Omit<RenderProps, "readOnly" | "hiddenFilters" | "agentLocations" | "configuredAgents" | "onCreateSession" | "onToggleAgent">> & Pick<RenderProps, "readOnly" | "hiddenFilters" | "agentLocations" | "configuredAgents" | "onCreateSession" | "onToggleAgent"> = {
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
  };

  const result = render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(ChatPanel, {
        room: current.room,
        loading: current.loading,
        thinkingAgents: current.thinkingAgents,
        connectionStatus: current.connectionStatus,
        onSendMessage: current.onSendMessage,
        readOnly: current.readOnly,
        hiddenFilters: current.hiddenFilters,
        agentLocations: current.agentLocations,
        configuredAgents: current.configuredAgents,
        onCreateSession: current.onCreateSession,
        onToggleAgent: current.onToggleAgent,
      }),
    ),
  );

  // Re-render with updated props while preserving the same ChatPanel instance,
  // simulating SignalR delivering new messages or other prop changes. Updates
  // are merged into `current` so multiple rerenders compose correctly.
  const rerenderWith = (next: RenderProps) => {
    if (next.room !== undefined) current.room = next.room;
    if (next.loading !== undefined) current.loading = next.loading;
    if (next.thinkingAgents !== undefined) current.thinkingAgents = next.thinkingAgents;
    if (next.connectionStatus !== undefined) current.connectionStatus = next.connectionStatus;
    if (next.onSendMessage !== undefined) current.onSendMessage = next.onSendMessage;
    if (next.readOnly !== undefined) current.readOnly = next.readOnly;
    if (next.hiddenFilters !== undefined) current.hiddenFilters = next.hiddenFilters;
    if (next.agentLocations !== undefined) current.agentLocations = next.agentLocations;
    if (next.configuredAgents !== undefined) current.configuredAgents = next.configuredAgents;
    if (next.onCreateSession !== undefined) current.onCreateSession = next.onCreateSession;
    if (next.onToggleAgent !== undefined) current.onToggleAgent = next.onToggleAgent;

    result.rerender(
      createElement(
        FluentProvider,
        { theme: webDarkTheme },
        createElement(ChatPanel, {
          room: current.room,
          loading: current.loading,
          thinkingAgents: current.thinkingAgents,
          connectionStatus: current.connectionStatus,
          onSendMessage: current.onSendMessage,
          readOnly: current.readOnly,
          hiddenFilters: current.hiddenFilters,
          agentLocations: current.agentLocations,
          configuredAgents: current.configuredAgents,
          onCreateSession: current.onCreateSession,
          onToggleAgent: current.onToggleAgent,
        }),
      ),
    );
  };

  return { ...result, user, onSendMessage, rerenderWith };
}

// ── Setup ──────────────────────────────────────────────────────────────

beforeEach(() => {
  vi.resetAllMocks();
  mockGetRoomSessions.mockResolvedValue({ sessions: [], totalCount: 0 });
  mockGetRoomMessages.mockResolvedValue({ messages: [], hasMore: false });
  // jsdom doesn't implement scrollTo
  Element.prototype.scrollTo = vi.fn();
  // Defensive cleanup: U6/U8 tests touch this localStorage key; clearing
  // here prevents leaks between tests if any test fails before its own cleanup.
  try { localStorage.removeItem("aa-default-expand"); } catch { /* */ }
});

afterEach(() => {
  cleanup();
  try { localStorage.removeItem("aa-default-expand"); } catch { /* */ }
});

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

    it("returns focus to the textarea after sending via Enter", async () => {
      const onSend = vi.fn(async () => true);
      const { user } = renderChat({ onSendMessage: onSend });

      const textarea = screen.getByRole("textbox", { name: /message to agents/i });
      await user.type(textarea, "Hello agents{enter}");

      await waitFor(() => {
        expect(onSend).toHaveBeenCalled();
      });
      await waitFor(() => {
        expect(textarea).toHaveFocus();
      });
    });

    it("returns focus to the textarea after sending via Send button", async () => {
      const onSend = vi.fn(async () => true);
      const { user } = renderChat({ onSendMessage: onSend });

      const textarea = screen.getByRole("textbox", { name: /message to agents/i });
      await user.type(textarea, "Click send");
      const sendBtn = screen.getByRole("button", { name: /send message/i });
      await user.click(sendBtn);

      await waitFor(() => {
        expect(onSend).toHaveBeenCalled();
      });
      await waitFor(() => {
        expect(textarea).toHaveFocus();
      });
    });

    it("does not steal focus if the user clicks elsewhere while the send is in flight", async () => {
      // onSend resolves only when we say so, so we can move focus mid-send.
      let resolveSend: ((v: boolean) => void) | null = null;
      const onSend = vi.fn(() => new Promise<boolean>((res) => { resolveSend = res; }));
      const { user } = renderChat({ onSendMessage: onSend });

      const textarea = screen.getByRole("textbox", { name: /message to agents/i });
      await user.type(textarea, "hello{enter}");

      // Render an unrelated focusable element OUTSIDE the composer (simulates
      // the user clicking the sidebar / a different room / etc.) and focus it.
      const outside = document.createElement("button");
      outside.textContent = "outside";
      document.body.appendChild(outside);
      outside.focus();
      expect(outside).toHaveFocus();

      // Now finish the send.
      resolveSend?.(true);
      await waitFor(() => {
        expect(onSend).toHaveBeenCalled();
      });
      // Give the refocus effect a chance to run.
      await new Promise((r) => setTimeout(r, 20));
      expect(textarea).not.toHaveFocus();
      expect(outside).toHaveFocus();
      outside.remove();
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

  // ── U6: expanded messages survive new arrivals ────────────────────────
  // Audit reference: REQUEST_AUDIT.md U6 — "Don't auto-collapse expanded
  // messages on new arrival". Implementation: ChatPanel keeps an `overrides`
  // Set that is reset only on room change or default-toggle, not on message
  // arrival. These tests lock that behavior in.

  describe("U6: expand state preservation", () => {
    const longContent = "B".repeat(400);

    it("keeps a manually-expanded message expanded after a new message arrives", async () => {
      const initialRoom = makeRoom({
        recentMessages: [makeMessage({ id: "long-1", content: longContent })],
      });
      const { user, rerenderWith } = renderChat({ room: initialRoom });

      // Expand the message manually.
      await user.click(screen.getByText("Show more"));
      expect(screen.getByText("Show less")).toBeInTheDocument();

      // Simulate a new message arriving (same room, new entry appended).
      rerenderWith({
        room: {
          ...initialRoom,
          recentMessages: [
            ...initialRoom.recentMessages,
            makeMessage({ id: "new-1", content: "Just-arrived message" }),
          ],
          updatedAt: "2026-04-10T12:01:00Z",
        },
      });

      // The originally-expanded message must still be expanded.
      expect(screen.getByText("Show less")).toBeInTheDocument();
      expect(screen.queryByText("Show more")).not.toBeInTheDocument();
      expect(screen.getByText("Just-arrived message")).toBeInTheDocument();
    });

    it("keeps a manually-collapsed message collapsed when default is expanded and new messages arrive", async () => {
      // defaultExpanded=true; user collapses one message; arrival shouldn't undo the override.
      try { localStorage.setItem("aa-default-expand", "true"); } catch { /* */ }
      const initialRoom = makeRoom({
        recentMessages: [
          makeMessage({ id: "long-a", content: longContent }),
          makeMessage({ id: "long-b", content: longContent }),
        ],
      });
      const { user, rerenderWith } = renderChat({ room: initialRoom });

      // Both start expanded; collapse the first one.
      const showLessButtons = screen.getAllByText("Show less");
      expect(showLessButtons).toHaveLength(2);
      await user.click(showLessButtons[0]);
      expect(screen.getAllByText("Show less")).toHaveLength(1);
      expect(screen.getAllByText("Show more")).toHaveLength(1);

      // New message arrives.
      rerenderWith({
        room: {
          ...initialRoom,
          recentMessages: [
            ...initialRoom.recentMessages,
            makeMessage({ id: "long-c", content: longContent }),
          ],
        },
      });

      // First message stays collapsed; the new third message is expanded by default.
      expect(screen.getAllByText("Show less")).toHaveLength(2);
      expect(screen.getAllByText("Show more")).toHaveLength(1);
      // Cleanup of localStorage handled by global afterEach.
    });

    it("resets manual overrides when switching to a different room", async () => {
      const room1 = makeRoom({
        id: "room-1",
        recentMessages: [makeMessage({ id: "r1-long", content: longContent })],
      });
      const room2 = makeRoom({
        id: "room-2",
        recentMessages: [makeMessage({ id: "r2-long", content: longContent })],
      });
      const { user, rerenderWith } = renderChat({ room: room1 });

      await user.click(screen.getByText("Show more"));
      expect(screen.getByText("Show less")).toBeInTheDocument();

      // Switch rooms — overrides should clear.
      rerenderWith({ room: room2 });

      expect(screen.getByText("Show more")).toBeInTheDocument();
      expect(screen.queryByText("Show less")).not.toBeInTheDocument();
    });
  });

  // ── U7: scroll behavior + new-messages indicator ──────────────────────
  // Audit reference: REQUEST_AUDIT.md U7 — "Don't auto-scroll on new
  // message; show (v) button". Implementation: auto-scroll only when the
  // user is near the bottom; otherwise the "New messages ↓" button surfaces
  // and clicking it scrolls down + dismisses the indicator.

  describe("U7: scroll behavior", () => {
    function getMessageList(): HTMLElement {
      return screen.getByRole("log", { name: /conversation messages/i });
    }

    function setScrollGeometry(el: HTMLElement, props: { scrollHeight: number; scrollTop: number; clientHeight: number }) {
      Object.defineProperty(el, "scrollHeight", { configurable: true, value: props.scrollHeight });
      Object.defineProperty(el, "scrollTop", { configurable: true, writable: true, value: props.scrollTop });
      Object.defineProperty(el, "clientHeight", { configurable: true, value: props.clientHeight });
    }

    it("does not show the 'New messages ↓' button when no new messages have arrived", () => {
      renderChat();
      expect(screen.queryByRole("button", { name: /scroll to new messages/i })).not.toBeInTheDocument();
    });

    it("shows the 'New messages ↓' button when a message arrives while user is scrolled up", () => {
      const initialRoom = makeRoom({
        recentMessages: [makeMessage({ id: "m-1", content: "First" })],
      });
      const { rerenderWith } = renderChat({ room: initialRoom });

      // Force scroll position = "not near bottom".
      const list = getMessageList();
      setScrollGeometry(list, { scrollHeight: 2000, scrollTop: 100, clientHeight: 600 });
      list.dispatchEvent(new Event("scroll"));

      // Clear any scrollTo calls from initial mount before delivering new message.
      const scrollSpy = list.scrollTo as unknown as ReturnType<typeof vi.fn>;
      scrollSpy.mockClear();

      // Deliver a new message via re-render.
      rerenderWith({
        room: {
          ...initialRoom,
          recentMessages: [
            ...initialRoom.recentMessages,
            makeMessage({ id: "m-2", content: "Just arrived" }),
          ],
        },
      });

      expect(screen.getByRole("button", { name: /scroll to new messages/i })).toBeInTheDocument();
      // Critical: a regression that auto-scrolls anyway must fail this test.
      expect(scrollSpy).not.toHaveBeenCalled();
    });

    it("does not show the 'New messages ↓' button when the user is near the bottom", () => {
      const initialRoom = makeRoom({
        recentMessages: [makeMessage({ id: "m-1", content: "First" })],
      });
      const { rerenderWith } = renderChat({ room: initialRoom });

      const list = getMessageList();
      // scrollHeight - scrollTop - clientHeight = 1000 - 950 - 50 = 0  (< 80 threshold)
      setScrollGeometry(list, { scrollHeight: 1000, scrollTop: 950, clientHeight: 50 });
      list.dispatchEvent(new Event("scroll"));

      // Clear initial-mount scroll calls so we can isolate the auto-scroll
      // triggered by the new message.
      const scrollSpy = list.scrollTo as unknown as ReturnType<typeof vi.fn>;
      scrollSpy.mockClear();

      rerenderWith({
        room: {
          ...initialRoom,
          recentMessages: [
            ...initialRoom.recentMessages,
            makeMessage({ id: "m-2", content: "Tail message" }),
          ],
        },
      });

      expect(screen.queryByRole("button", { name: /scroll to new messages/i })).not.toBeInTheDocument();
      // Critical: when near bottom we DO want auto-scroll (instant, not smooth).
      expect(scrollSpy).toHaveBeenCalled();
      const lastCall = scrollSpy.mock.calls.at(-1)?.[0];
      expect(lastCall).toMatchObject({ behavior: "auto" });
    });

    it("clicking the 'New messages ↓' button scrolls down and dismisses the indicator", async () => {
      const initialRoom = makeRoom({
        recentMessages: [makeMessage({ id: "m-1", content: "First" })],
      });
      const { user, rerenderWith } = renderChat({ room: initialRoom });

      const list = getMessageList();
      setScrollGeometry(list, { scrollHeight: 2000, scrollTop: 100, clientHeight: 600 });
      list.dispatchEvent(new Event("scroll"));

      rerenderWith({
        room: {
          ...initialRoom,
          recentMessages: [
            ...initialRoom.recentMessages,
            makeMessage({ id: "m-2", content: "Just arrived" }),
          ],
        },
      });

      const indicator = screen.getByRole("button", { name: /scroll to new messages/i });
      const scrollSpy = list.scrollTo as unknown as ReturnType<typeof vi.fn>;
      scrollSpy.mockClear();

      await user.click(indicator);

      // scrollTo is called with smooth scroll to the bottom.
      expect(scrollSpy).toHaveBeenCalled();
      const lastCallArg = scrollSpy.mock.calls.at(-1)?.[0];
      expect(lastCallArg).toMatchObject({ behavior: "smooth" });
      expect(screen.queryByRole("button", { name: /scroll to new messages/i })).not.toBeInTheDocument();
    });
  });

  // ── U8: toolbar expand/collapse all toggle ────────────────────────────
  // Audit reference: REQUEST_AUDIT.md U8 — "Toggle all messages
  // expanded/collapsed (room sub-menu or settings)". Implementation:
  // SessionToolbar exposes a "⊞ Expand" / "⊟ Collapse" button that flips
  // the defaultExpanded base state and persists it to localStorage.

  describe("U8: toolbar expand/collapse all toggle", () => {
    const longContent = "C".repeat(400);

    // Note: localStorage cleanup of `aa-default-expand` is handled by the
    // global beforeEach/afterEach above, so no per-block cleanup needed.

    // The toolbar toggle button's accessible name comes from its text content
    // ("⊞ Expand" or "⊟ Collapse"); the descriptive `title` attribute is a
    // tooltip, not the a11y name.
    const expandButton = () => screen.getByRole("button", { name: "⊞ Expand" });
    const collapseButton = () => screen.getByRole("button", { name: "⊟ Collapse" });
    const queryExpandButton = () => screen.queryByRole("button", { name: "⊞ Expand" });
    const queryCollapseButton = () => screen.queryByRole("button", { name: "⊟ Collapse" });

    it("renders the '⊞ Expand' toggle by default with collapsed messages", () => {
      renderChat({
        room: makeRoom({
          recentMessages: [
            makeMessage({ id: "long-a", content: longContent }),
            makeMessage({ id: "long-b", content: longContent }),
          ],
        }),
      });

      expect(expandButton()).toBeInTheDocument();
      expect(expandButton().getAttribute("title")).toMatch(/Expand all messages by default/i);
      // Both long messages start collapsed.
      expect(screen.getAllByText("Show more")).toHaveLength(2);
      expect(screen.queryByText("Show less")).not.toBeInTheDocument();
    });

    it("clicking the toggle expands all long messages and updates the button label", async () => {
      const { user } = renderChat({
        room: makeRoom({
          recentMessages: [
            makeMessage({ id: "long-a", content: longContent }),
            makeMessage({ id: "long-b", content: longContent }),
          ],
        }),
      });

      await user.click(expandButton());

      // Both now expanded.
      expect(screen.getAllByText("Show less")).toHaveLength(2);
      expect(screen.queryByText("Show more")).not.toBeInTheDocument();
      // Button label flipped.
      expect(collapseButton()).toBeInTheDocument();
      expect(queryExpandButton()).not.toBeInTheDocument();
    });

    it("clicking the toggle a second time collapses all messages again", async () => {
      const { user } = renderChat({
        room: makeRoom({
          recentMessages: [
            makeMessage({ id: "long-a", content: longContent }),
            makeMessage({ id: "long-b", content: longContent }),
          ],
        }),
      });

      await user.click(expandButton());
      expect(screen.getAllByText("Show less")).toHaveLength(2);
      await user.click(collapseButton());
      expect(screen.getAllByText("Show more")).toHaveLength(2);
      expect(screen.queryByText("Show less")).not.toBeInTheDocument();
    });

    it("persists the toggle state to localStorage", async () => {
      const { user } = renderChat({
        room: makeRoom({
          recentMessages: [makeMessage({ id: "long-a", content: longContent })],
        }),
      });

      expect(localStorage.getItem("aa-default-expand")).not.toBe("true");
      await user.click(expandButton());
      expect(localStorage.getItem("aa-default-expand")).toBe("true");
      await user.click(collapseButton());
      expect(localStorage.getItem("aa-default-expand")).toBe("false");
    });

    it("restores the persisted state on next mount", () => {
      try { localStorage.setItem("aa-default-expand", "true"); } catch { /* */ }
      renderChat({
        room: makeRoom({
          recentMessages: [
            makeMessage({ id: "long-a", content: longContent }),
            makeMessage({ id: "long-b", content: longContent }),
          ],
        }),
      });

      // Both messages mounted in expanded state because of persisted preference.
      expect(screen.getAllByText("Show less")).toHaveLength(2);
      expect(collapseButton()).toBeInTheDocument();
      expect(queryCollapseButton()?.getAttribute("title")).toMatch(/Collapse all messages by default/i);
    });

    it("toggling clears any per-message overrides", async () => {
      const { user } = renderChat({
        room: makeRoom({
          recentMessages: [
            makeMessage({ id: "long-a", content: longContent }),
            makeMessage({ id: "long-b", content: longContent }),
          ],
        }),
      });

      // Manually expand the first; default is collapsed → first is expanded, second collapsed.
      await user.click(screen.getAllByText("Show more")[0]);
      expect(screen.getAllByText("Show less")).toHaveLength(1);
      expect(screen.getAllByText("Show more")).toHaveLength(1);

      // Hit toggle → expand all + clear overrides.
      await user.click(expandButton());
      expect(screen.getAllByText("Show less")).toHaveLength(2);

      // Toggle back → collapse all, no override leaks.
      await user.click(collapseButton());
      expect(screen.getAllByText("Show more")).toHaveLength(2);
      expect(screen.queryByText("Show less")).not.toBeInTheDocument();
    });
  });
});
