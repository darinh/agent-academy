import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi, beforeEach } from "vitest";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import { createElement } from "react";

// ── Mocks ──────────────────────────────────────────────────────────────

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
  AgentLocation,
  AgentDefinition,
  AgentPresence,
} from "../api";
import { getRoomSessions, getRoomMessages } from "../api";
import type { ThinkingAgent } from "../useWorkspace";
import type { ConnectionStatus, MessageFilter } from "../chatUtils";

const mockGetRoomSessions = vi.mocked(getRoomSessions);
const mockGetRoomMessages = vi.mocked(getRoomMessages);

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
    senderName: "Darin Hoover",
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

function renderChatPanel(props: RenderProps = {}) {
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

  return renderToStaticMarkup(
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
}

// ── Tests ──────────────────────────────────────────────────────────────

describe("ChatPanel component", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // Default mock: no sessions, no archived
    mockGetRoomSessions.mockResolvedValue({ sessions: [], totalCount: 0 });
    mockGetRoomMessages.mockResolvedValue({ messages: [], hasMore: false });
  });

  // ── Empty / Loading States ──

  describe("empty and loading states", () => {
    it("renders empty state when room is null", () => {
      const html = renderChatPanel({ room: null });
      expect(html).toContain("No messages yet");
      expect(html).toContain("Messages will appear here");
    });

    it("renders skeleton loader when loading with no messages", () => {
      const html = renderChatPanel({
        room: makeRoom({ recentMessages: [] }),
        loading: true,
      });
      // SkeletonLoader renders shimmer divs
      expect(html).not.toContain("No messages yet");
    });

    it("renders empty state when room has no messages and not loading", () => {
      const html = renderChatPanel({
        room: makeRoom({ recentMessages: [] }),
        loading: false,
      });
      expect(html).toContain("No messages yet");
      expect(html).toContain("Messages will appear here when the team starts collaborating");
    });

    it("renders 'All messages filtered' when messages exist but all are hidden", () => {
      const room = makeRoom({
        recentMessages: [makeSystemMessage("Phase changed")],
      });
      const html = renderChatPanel({
        room,
        hiddenFilters: new Set<MessageFilter>(["system"]),
      });
      expect(html).toContain("All messages filtered");
      expect(html).toContain("Adjust filters above to see hidden messages");
    });
  });

  // ── Message Rendering ──

  describe("message rendering", () => {
    it("renders agent messages with sender name", () => {
      const room = makeRoom({
        recentMessages: [makeMessage({ senderName: "Archimedes" })],
      });
      const html = renderChatPanel({ room });
      expect(html).toContain("Archimedes");
    });

    it("renders user messages with sender name", () => {
      const room = makeRoom({
        recentMessages: [makeUserMessage("Hello team")],
      });
      const html = renderChatPanel({ room });
      expect(html).toContain("Darin Hoover");
      expect(html).toContain("Hello team");
    });

    it("renders system messages with system styling", () => {
      const room = makeRoom({
        recentMessages: [makeSystemMessage("Phase changed to Review")],
      });
      const html = renderChatPanel({ room });
      expect(html).toContain("Phase changed to Review");
    });

    it("renders multiple messages in order", () => {
      const room = makeRoom({
        recentMessages: [
          makeMessage({ id: "m1", senderName: "Hephaestus", content: "First" }),
          makeMessage({ id: "m2", senderName: "Athena", content: "Second" }),
          makeMessage({ id: "m3", senderName: "Socrates", content: "Third" }),
        ],
      });
      const html = renderChatPanel({ room });
      expect(html).toContain("Hephaestus");
      expect(html).toContain("Athena");
      expect(html).toContain("Socrates");
      // Verify order: Hephaestus appears before Athena
      expect(html.indexOf("Hephaestus")).toBeLessThan(html.indexOf("Athena"));
      expect(html.indexOf("Athena")).toBeLessThan(html.indexOf("Socrates"));
    });

    it("renders role pill for agent messages", () => {
      const room = makeRoom({
        recentMessages: [makeMessage({ senderRole: "Architect" })],
      });
      const html = renderChatPanel({ room });
      // formatRole transforms "Architect" -> display string
      expect(html).toContain("Architect");
    });

    it("renders role pill as Human for user messages", () => {
      const room = makeRoom({
        recentMessages: [makeUserMessage("Test")],
      });
      const html = renderChatPanel({ room });
      expect(html).toContain("Human");
    });

    it("renders message timestamps", () => {
      const room = makeRoom({
        recentMessages: [makeMessage({ sentAt: "2026-04-10T14:30:00Z" })],
      });
      const html = renderChatPanel({ room });
      // formatTime produces some time representation
      expect(html).toMatch(/\d{1,2}:\d{2}/);
    });
  });

  // ── Long Message Truncation ──

  describe("long message truncation", () => {
    it("renders short messages in full", () => {
      const room = makeRoom({
        recentMessages: [makeMessage({ content: "Short message" })],
      });
      const html = renderChatPanel({ room });
      expect(html).toContain("Short message");
      expect(html).not.toContain("Show more");
    });

    it("truncates messages over 300 characters", () => {
      const longContent = "A".repeat(400);
      const room = makeRoom({
        recentMessages: [makeMessage({ content: longContent })],
      });
      const html = renderChatPanel({ room });
      expect(html).toContain("Show more");
      // The full 400-char string should NOT appear (truncated at 300 + "…")
      expect(html).not.toContain(longContent);
    });

    it("shows exactly 300 characters before truncation ellipsis", () => {
      const longContent = "B".repeat(350);
      const room = makeRoom({
        recentMessages: [makeMessage({ content: longContent })],
      });
      const html = renderChatPanel({ room });
      // Should contain the first 300 chars
      expect(html).toContain("B".repeat(300));
      expect(html).toContain("…");
    });

    it("does not add Show more for exactly 300 character messages", () => {
      const exact = "C".repeat(300);
      const room = makeRoom({
        recentMessages: [makeMessage({ content: exact })],
      });
      const html = renderChatPanel({ room });
      expect(html).not.toContain("Show more");
    });
  });

  // ── Command Result Messages ──

  describe("command result messages", () => {
    it("renders command result bubble for command result content", () => {
      const content = [
        "=== COMMAND RESULTS ===",
        "[Success] LIST_ROOMS (corr-001)",
        "  Room 1: Main Room",
      ].join("\n");
      const room = makeRoom({
        recentMessages: [makeSystemMessage(content)],
      });
      const html = renderChatPanel({ room });
      expect(html).toContain("LIST_ROOMS");
      expect(html).toContain("✅");
    });

    it("renders error status emoji for failed commands", () => {
      const content = [
        "=== COMMAND RESULTS ===",
        "[Error] READ_FILE (corr-002)",
        "  Error: File not found",
      ].join("\n");
      const room = makeRoom({
        recentMessages: [makeSystemMessage(content)],
      });
      const html = renderChatPanel({ room });
      expect(html).toContain("❌");
      expect(html).toContain("READ_FILE");
    });

    it("renders denied status emoji", () => {
      const content = [
        "=== COMMAND RESULTS ===",
        "[Denied] CLOSE_ROOM (corr-003)",
        "  Error: Insufficient permissions",
      ].join("\n");
      const room = makeRoom({
        recentMessages: [makeSystemMessage(content)],
      });
      const html = renderChatPanel({ room });
      expect(html).toContain("🚫");
      expect(html).toContain("CLOSE_ROOM");
    });

    it("renders multiple command results in one message", () => {
      const content = [
        "=== COMMAND RESULTS ===",
        "[Success] LIST_ROOMS (c1)",
        "  Room 1",
        "[Error] RUN_TESTS (c2)",
        "  Error: Failed",
      ].join("\n");
      const room = makeRoom({
        recentMessages: [makeSystemMessage(content)],
      });
      const html = renderChatPanel({ room });
      expect(html).toContain("LIST_ROOMS");
      expect(html).toContain("RUN_TESTS");
    });
  });

  // ── Thinking Indicators ──

  describe("thinking indicators", () => {
    it("renders thinking bubble for agents that are thinking", () => {
      const html = renderChatPanel({
        thinkingAgents: [{ id: "agent-1", name: "Hephaestus", role: "SoftwareEngineer" }],
      });
      expect(html).toContain("thinking");
      expect(html).toContain("● ● ●");
    });

    it("renders multiple thinking bubbles", () => {
      const html = renderChatPanel({
        thinkingAgents: [
          { id: "agent-1", name: "Hephaestus", role: "SoftwareEngineer" },
          { id: "agent-2", name: "Athena", role: "SoftwareEngineer" },
        ],
      });
      expect(html).toContain("Hephaestus");
      expect(html).toContain("Athena");
    });

    it("renders thinking bubble with accessibility label", () => {
      const html = renderChatPanel({
        thinkingAgents: [{ id: "agent-1", name: "Archimedes", role: "Architect" }],
      });
      expect(html).toContain("Archimedes is thinking");
    });

    it("renders no thinking bubbles when array is empty", () => {
      const html = renderChatPanel({ thinkingAgents: [] });
      expect(html).not.toContain("● ● ●");
    });
  });

  // ── Connection Status ──

  describe("connection status", () => {
    it("does not show status bar when connected", () => {
      const html = renderChatPanel({ connectionStatus: "connected" });
      // Connected state hides the status bar
      expect(html).not.toContain("Reconnecting");
      expect(html).not.toContain("Connecting");
      expect(html).not.toContain("Disconnected");
    });

    it("shows status bar when disconnected", () => {
      const html = renderChatPanel({ connectionStatus: "disconnected" });
      expect(html).toContain("Disconnected");
    });

    it("shows status bar when connecting", () => {
      const html = renderChatPanel({ connectionStatus: "connecting" });
      expect(html).toContain("Connecting");
    });

    it("shows status bar when reconnecting", () => {
      const html = renderChatPanel({ connectionStatus: "reconnecting" });
      expect(html).toContain("Reconnecting");
    });

    it("status bar includes role=status for accessibility", () => {
      const html = renderChatPanel({ connectionStatus: "disconnected" });
      expect(html).toContain('role="status"');
    });
  });

  // ── Composer ──

  describe("composer area", () => {
    it("renders composer when room exists and not readOnly", () => {
      const html = renderChatPanel({ room: makeRoom(), readOnly: false });
      expect(html).toContain("Message the team");
      expect(html).toContain("Type a message to the agents");
      expect(html).toContain("Send message");
    });

    it("hides composer when readOnly", () => {
      const html = renderChatPanel({ room: makeRoom(), readOnly: true });
      expect(html).not.toContain("Message the team");
      expect(html).not.toContain("Send message");
    });

    it("hides composer when room is null", () => {
      const html = renderChatPanel({ room: null });
      expect(html).not.toContain("Message the team");
      expect(html).not.toContain("Send message");
    });

    it("renders keyboard shortcut hint", () => {
      const html = renderChatPanel({ room: makeRoom() });
      expect(html).toContain("Enter to send");
      expect(html).toContain("Shift+Enter for new line");
    });

    it("renders Clear button", () => {
      const html = renderChatPanel({ room: makeRoom() });
      expect(html).toContain("Clear");
    });

    it("textarea has aria-label for accessibility", () => {
      const html = renderChatPanel({ room: makeRoom() });
      expect(html).toContain("Message to agents");
    });
  });

  // ── Read-Only Mode ──

  describe("read-only mode", () => {
    it("renders messages in read-only mode", () => {
      const room = makeRoom({
        recentMessages: [makeMessage({ content: "Agent response" })],
      });
      const html = renderChatPanel({ room, readOnly: true });
      expect(html).toContain("Agent response");
    });

    it("hides session toolbar in read-only mode", () => {
      const html = renderChatPanel({ room: makeRoom(), readOnly: true });
      expect(html).not.toContain("+ New Session");
      expect(html).not.toContain("Current session");
    });
  });

  // ── Session Toolbar ──

  describe("session toolbar", () => {
    it("renders New Session button when onCreateSession is provided", () => {
      const html = renderChatPanel({
        room: makeRoom(),
        onCreateSession: vi.fn(),
      });
      expect(html).toContain("+ New Session");
    });

    it("hides New Session button when onCreateSession is not provided", () => {
      const html = renderChatPanel({
        room: makeRoom(),
        onCreateSession: undefined,
      });
      expect(html).not.toContain("+ New Session");
    });

    it("renders session selector with Current session option", () => {
      const html = renderChatPanel({ room: makeRoom() });
      expect(html).toContain("Current session");
    });

    it("hides session toolbar when room is null", () => {
      const html = renderChatPanel({ room: null });
      expect(html).not.toContain("+ New Session");
      expect(html).not.toContain("Current session");
    });
  });

  // ── Agent Dropdown ──

  describe("agent dropdown", () => {
    it("renders agent count button when agents and onToggleAgent are provided", () => {
      const html = renderChatPanel({
        room: makeRoom(),
        configuredAgents: [makeAgentDef()],
        agentLocations: [makeAgentLocation()],
        onToggleAgent: vi.fn(),
      });
      expect(html).toContain("Agents (1)");
    });

    it("shows count of agents currently in the room", () => {
      const html = renderChatPanel({
        room: makeRoom(),
        configuredAgents: [
          makeAgentDef({ id: "a1", name: "Alpha" }),
          makeAgentDef({ id: "a2", name: "Beta" }),
          makeAgentDef({ id: "a3", name: "Gamma" }),
        ],
        agentLocations: [
          makeAgentLocation({ agentId: "a1", roomId: "room-1" }),
          makeAgentLocation({ agentId: "a2", roomId: "room-1" }),
          makeAgentLocation({ agentId: "a3", roomId: "other-room" }),
        ],
        onToggleAgent: vi.fn(),
      });
      // 2 agents in room-1
      expect(html).toContain("Agents (2)");
    });

    it("hides agent button when no configuredAgents", () => {
      const html = renderChatPanel({
        room: makeRoom(),
        configuredAgents: [],
        onToggleAgent: vi.fn(),
      });
      expect(html).not.toContain("Agents (");
    });

    it("hides agent button when onToggleAgent is not provided", () => {
      const html = renderChatPanel({
        room: makeRoom(),
        configuredAgents: [makeAgentDef()],
        onToggleAgent: undefined,
      });
      expect(html).not.toContain("Agents (");
    });
  });

  // ── Message Filtering ──

  describe("message filtering with hiddenFilters prop", () => {
    it("hides system messages when system filter is active", () => {
      const room = makeRoom({
        recentMessages: [
          makeMessage({ id: "m1", content: "Agent says hi" }),
          makeSystemMessage("Phase changed to Review"),
        ],
      });
      const html = renderChatPanel({
        room,
        hiddenFilters: new Set<MessageFilter>(["system"]),
      });
      expect(html).toContain("Agent says hi");
      expect(html).not.toContain("Phase changed to Review");
    });

    it("hides command result messages when commands filter is active", () => {
      const cmdContent = "=== COMMAND RESULTS ===\n[Success] LIST_ROOMS (c1)\n  Room 1";
      const room = makeRoom({
        recentMessages: [
          makeMessage({ id: "m1", content: "Agent says hi" }),
          makeSystemMessage(cmdContent),
        ],
      });
      const html = renderChatPanel({
        room,
        hiddenFilters: new Set<MessageFilter>(["commands"]),
      });
      expect(html).toContain("Agent says hi");
      expect(html).not.toContain("LIST_ROOMS");
    });

    it("shows all messages when no filters active", () => {
      const room = makeRoom({
        recentMessages: [
          makeMessage({ id: "m1", content: "Agent message" }),
          makeSystemMessage("System notification"),
        ],
      });
      const html = renderChatPanel({
        room,
        hiddenFilters: new Set<MessageFilter>(),
      });
      expect(html).toContain("Agent message");
      expect(html).toContain("System notification");
    });

    it("never hides agent messages regardless of filters", () => {
      const room = makeRoom({
        recentMessages: [
          makeMessage({ id: "m1", senderKind: "Agent", content: "Important update" }),
        ],
      });
      const html = renderChatPanel({
        room,
        hiddenFilters: new Set<MessageFilter>(["system", "commands"]),
      });
      expect(html).toContain("Important update");
    });

    it("never hides user messages regardless of filters", () => {
      const room = makeRoom({
        recentMessages: [makeUserMessage("My question")],
      });
      const html = renderChatPanel({
        room,
        hiddenFilters: new Set<MessageFilter>(["system", "commands"]),
      });
      expect(html).toContain("My question");
    });
  });

  // ── Message List Accessibility ──

  describe("message list accessibility", () => {
    it("has role=log on the message list", () => {
      const html = renderChatPanel();
      expect(html).toContain('role="log"');
    });

    it("has aria-label on the message list", () => {
      const html = renderChatPanel();
      expect(html).toContain("Conversation messages");
    });

    it("has aria-live=polite for screen readers", () => {
      const html = renderChatPanel();
      expect(html).toContain('aria-live="polite"');
    });
  });

  // ── Markdown Rendering ──

  describe("markdown content rendering", () => {
    it("renders markdown bold text", () => {
      const room = makeRoom({
        recentMessages: [makeMessage({ content: "This is **bold** text" })],
      });
      const html = renderChatPanel({ room });
      expect(html).toContain("<strong>bold</strong>");
    });

    it("renders markdown code blocks", () => {
      const room = makeRoom({
        recentMessages: [makeMessage({ content: "Use `console.log`" })],
      });
      const html = renderChatPanel({ room });
      expect(html).toContain("<code>console.log</code>");
    });

    it("renders markdown links", () => {
      const room = makeRoom({
        recentMessages: [makeMessage({ content: "[click here](https://example.com)" })],
      });
      const html = renderChatPanel({ room });
      expect(html).toContain("href=\"https://example.com\"");
      expect(html).toContain("click here");
    });

    it("renders markdown lists", () => {
      const room = makeRoom({
        recentMessages: [makeMessage({ content: "- item one\n- item two" })],
      });
      const html = renderChatPanel({ room });
      expect(html).toContain("item one");
      expect(html).toContain("item two");
      expect(html).toContain("<li>");
    });
  });

  // ── Mixed Message Types ──

  describe("mixed message types in conversation", () => {
    it("renders a realistic conversation with all message types", () => {
      const room = makeRoom({
        recentMessages: [
          makeSystemMessage("Project loaded: agent-academy"),
          makeUserMessage("Hello team, let's start"),
          makeMessage({ id: "a1", senderName: "Aristotle", senderRole: "Planner", content: "I'll coordinate" }),
          makeSystemMessage("=== COMMAND RESULTS ===\n[Success] LIST_TASKS (c1)\n  0 tasks"),
          makeMessage({ id: "a2", senderName: "Hephaestus", senderRole: "SoftwareEngineer", content: "Ready to code" }),
        ],
      });
      const html = renderChatPanel({ room });

      expect(html).toContain("Project loaded: agent-academy");
      expect(html).toContain("Hello team");
      expect(html).toContain("Aristotle");
      expect(html).toContain("LIST_TASKS");
      expect(html).toContain("Ready to code");
    });
  });
});
