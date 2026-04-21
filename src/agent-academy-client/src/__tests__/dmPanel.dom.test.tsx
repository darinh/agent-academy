// @vitest-environment jsdom
/**
 * Interactive RTL tests for DmPanel.
 *
 * Uses @testing-library/react + jsdom.
 * Covers: loading state, error state with retry, empty threads, thread list
 * rendering, thread selection, new-conversation agent picker, message
 * rendering (agent vs human bubbles), send flow (Enter + button), read-only
 * mode (disabled composer, limited-mode notice), auto-scroll, and outside-click
 * dismiss of the agent picker.
 */
import "@testing-library/jest-dom/vitest";
import {
  cleanup,
  render,
  screen,
  waitFor,
  within,
  act,
  fireEvent,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("react-markdown", () => ({
  default: ({ children }: { children?: string }) =>
    createElement("div", { "data-testid": "markdown" }, children),
}));

vi.mock("remark-gfm", () => ({ default: () => {} }));

vi.mock("../api", () => ({
  getDmThreads: vi.fn(),
  getDmThreadMessages: vi.fn(),
  sendDmToAgent: vi.fn(),
}));

vi.mock("../useMessageSSE", () => ({
  useMessageSSE: () => "disconnected",
}));

vi.mock("../useDmThreadSSE", () => ({
  useDmThreadSSE: () => "disconnected",
}));

import DmPanel from "../DmPanel";
import type { DmThreadSummary, DmMessage } from "../api";
import { getDmThreads, getDmThreadMessages, sendDmToAgent } from "../api";

const mockGetDmThreads = vi.mocked(getDmThreads);
const mockGetDmThreadMessages = vi.mocked(getDmThreadMessages);
const mockSendDmToAgent = vi.mocked(sendDmToAgent);

// ── Factories ──────────────────────────────────────────────────────────

function makeThread(
  overrides: Partial<DmThreadSummary> = {},
): DmThreadSummary {
  return {
    agentId: "agent-1",
    agentName: "Hephaestus",
    agentRole: "SoftwareEngineer",
    lastMessage: "Sure, I can handle that.",
    lastMessageAt: "2026-04-10T12:00:00Z",
    messageCount: 5,
    ...overrides,
  };
}

function makeMsg(overrides: Partial<DmMessage> = {}): DmMessage {
  return {
    id: "dm-1",
    senderId: "agent-1",
    senderName: "Hephaestus",
    content: "Hello from the agent.",
    sentAt: "2026-04-10T12:00:00Z",
    isFromHuman: false,
    ...overrides,
  };
}

const AGENTS = [
  { id: "agent-1", name: "Hephaestus", role: "SoftwareEngineer" },
  { id: "agent-2", name: "Athena", role: "Architect" },
  { id: "agent-3", name: "Apollo", role: "ProductManager" },
];

// ── Render helper ──────────────────────────────────────────────────────

interface RenderOpts {
  agents?: typeof AGENTS;
  readOnly?: boolean;
}

function renderDm(opts: RenderOpts = {}) {
  const { agents = AGENTS, readOnly = false } = opts;
  return render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(DmPanel, { agents, readOnly }),
    ),
  );
}

// ── Setup & teardown ───────────────────────────────────────────────────

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  // Polyfill scrollTo for jsdom
  Element.prototype.scrollTo = vi.fn();
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.restoreAllMocks();
});

// ── Tests ──────────────────────────────────────────────────────────────

describe("DmPanel (interactive)", () => {
  // ── Loading & error states ─────────────────────────────────────────

  describe("loading state", () => {
    it("shows skeleton loader while threads are loading", () => {
      mockGetDmThreads.mockReturnValue(new Promise(() => {})); // never resolves
      const { container } = renderDm();
      // SkeletonLoader renders shimmer divs; verify positive presence
      expect(container.querySelectorAll("div").length).toBeGreaterThan(0);
      // And the normal UI is not yet rendered
      expect(screen.queryByText("Messages")).not.toBeInTheDocument();
      expect(screen.queryByText("Direct Messages")).not.toBeInTheDocument();
    });
  });

  describe("error state", () => {
    it("shows error state when thread fetch fails", async () => {
      mockGetDmThreads.mockRejectedValue(new Error("Network error"));
      renderDm();

      await waitFor(() => {
        expect(
          screen.getByText("Failed to load conversations"),
        ).toBeInTheDocument();
      });
    });

    it("retries on error state retry button", async () => {
      const threads = [makeThread()];
      mockGetDmThreads
        .mockRejectedValueOnce(new Error("fail"))
        .mockResolvedValue(threads);

      renderDm();

      await waitFor(() => {
        expect(
          screen.getByText("Failed to load conversations"),
        ).toBeInTheDocument();
      });

      const retryBtn = screen.getByRole("button", { name: /try again/i });
      await act(async () => {
        retryBtn.click();
      });

      await waitFor(() => {
        expect(screen.getByText("Hephaestus")).toBeInTheDocument();
      });
    });
  });

  // ── Empty state ────────────────────────────────────────────────────

  describe("empty threads", () => {
    it("shows empty state when no threads exist", async () => {
      mockGetDmThreads.mockResolvedValue([]);
      renderDm();

      await waitFor(() => {
        expect(
          screen.getByText("No conversations yet"),
        ).toBeInTheDocument();
      });
    });
  });

  // ── Thread list ────────────────────────────────────────────────────

  describe("thread list", () => {
    it("renders thread items with agent name and preview", async () => {
      const threads = [
        makeThread({
          agentId: "agent-1",
          agentName: "Hephaestus",
          lastMessage: "Working on it now.",
        }),
        makeThread({
          agentId: "agent-2",
          agentName: "Athena",
          lastMessage: "Architecture review done.",
        }),
      ];
      mockGetDmThreads.mockResolvedValue(threads);
      mockGetDmThreadMessages.mockResolvedValue([]);
      renderDm();

      await waitFor(() => {
        expect(screen.getByText("Hephaestus")).toBeInTheDocument();
        expect(screen.getByText("Athena")).toBeInTheDocument();
      });
      expect(screen.getByText("Working on it now.")).toBeInTheDocument();
      expect(
        screen.getByText("Architecture review done."),
      ).toBeInTheDocument();
    });

    it("selects thread on click and loads messages", async () => {
      const threads = [makeThread()];
      const msgs = [
        makeMsg({ id: "dm-1", content: "Hello there" }),
        makeMsg({
          id: "dm-2",
          content: "Hi from human",
          isFromHuman: true,
          senderId: "user",
          senderName: "Darin",
        }),
      ];
      mockGetDmThreads.mockResolvedValue(threads);
      mockGetDmThreadMessages.mockResolvedValue(msgs);

      renderDm();

      await waitFor(() => {
        expect(screen.getByText("Hephaestus")).toBeInTheDocument();
      });

      // Click on the thread
      const threadBtn = screen.getByRole("button", { name: /Hephaestus/i });
      await act(async () => {
        threadBtn.click();
      });

      await waitFor(() => {
        expect(screen.getByText("Hello there")).toBeInTheDocument();
        expect(screen.getByText("Hi from human")).toBeInTheDocument();
      });
    });
  });

  // ── Chat area default ──────────────────────────────────────────────

  describe("default chat area", () => {
    it("shows placeholder when no thread is selected", async () => {
      mockGetDmThreads.mockResolvedValue([makeThread()]);
      renderDm();

      await waitFor(() => {
        expect(screen.getByText("Direct Messages")).toBeInTheDocument();
      });
      expect(
        screen.getByText(
          "Select a conversation or click + to message an agent directly.",
        ),
      ).toBeInTheDocument();
    });
  });

  // ── New conversation (agent picker) ────────────────────────────────

  describe("new conversation picker", () => {
    it("opens agent picker dropdown on + button click", async () => {
      mockGetDmThreads.mockResolvedValue([]);
      renderDm();

      await waitFor(() => {
        expect(
          screen.getByText("No conversations yet"),
        ).toBeInTheDocument();
      });

      const newBtn = screen.getByTitle("New conversation");
      await act(async () => {
        newBtn.click();
      });

      // All three agents should be listed
      await waitFor(() => {
        expect(screen.getByText("Hephaestus")).toBeInTheDocument();
        expect(screen.getByText("Athena")).toBeInTheDocument();
        expect(screen.getByText("Apollo")).toBeInTheDocument();
      });
    });

    it("starts new thread when agent is selected from picker", async () => {
      mockGetDmThreads.mockResolvedValue([]);
      mockGetDmThreadMessages.mockResolvedValue([]);
      renderDm();

      await waitFor(() => {
        expect(
          screen.getByText("No conversations yet"),
        ).toBeInTheDocument();
      });

      const newBtn = screen.getByTitle("New conversation");
      await act(async () => {
        newBtn.click();
      });

      // Find and click Athena in the dropdown
      const agentButtons = screen.getAllByRole("button");
      const athenaBtn = agentButtons.find(
        (b) => b.textContent?.includes("Athena"),
      );
      expect(athenaBtn).toBeTruthy();

      await act(async () => {
        athenaBtn!.click();
      });

      // Chat area should transition to Athena's conversation
      await waitFor(() => {
        expect(mockGetDmThreadMessages).toHaveBeenCalledWith("agent-2");
        expect(screen.getByLabelText("Message Athena")).toBeInTheDocument();
      });
    });

    it("shows 'All agents have threads' when none available", async () => {
      const threads = AGENTS.map((a) =>
        makeThread({
          agentId: a.id,
          agentName: a.name,
          agentRole: a.role,
        }),
      );
      mockGetDmThreads.mockResolvedValue(threads);
      mockGetDmThreadMessages.mockResolvedValue([]);
      renderDm();

      await waitFor(() => {
        expect(screen.getByText("Hephaestus")).toBeInTheDocument();
      });

      const newBtn = screen.getByTitle("New conversation");
      await act(async () => {
        newBtn.click();
      });

      expect(
        screen.getByText("All agents have threads"),
      ).toBeInTheDocument();
    });

    it("closes picker on outside click", async () => {
      mockGetDmThreads.mockResolvedValue([]);
      renderDm();

      await waitFor(() => {
        expect(
          screen.getByText("No conversations yet"),
        ).toBeInTheDocument();
      });

      const newBtn = screen.getByTitle("New conversation");
      await act(async () => {
        newBtn.click();
      });

      // Picker is open — agents visible
      expect(screen.getByText("Athena")).toBeInTheDocument();

      // Click outside (on the document body)
      await act(async () => {
        fireEvent.mouseDown(document.body);
      });

      // Picker should close — Athena should no longer be visible
      // (Athena only appears in the picker, not the thread list)
      await waitFor(() => {
        expect(screen.queryByText("Athena")).not.toBeInTheDocument();
      });
    });

    it("disables + button in read-only mode", async () => {
      mockGetDmThreads.mockResolvedValue([]);
      renderDm({ readOnly: true });

      await waitFor(() => {
        expect(
          screen.getByText("No conversations yet"),
        ).toBeInTheDocument();
      });

      const newBtn = screen.getByTitle("New conversation");
      expect(newBtn).toBeDisabled();
    });
  });

  // ── Message bubbles ────────────────────────────────────────────────

  describe("message rendering", () => {
    it("renders agent messages with sender name", async () => {
      mockGetDmThreads.mockResolvedValue([makeThread()]);
      mockGetDmThreadMessages.mockResolvedValue([
        makeMsg({
          id: "dm-1",
          senderName: "Hephaestus",
          content: "Working on the fix now.",
          isFromHuman: false,
        }),
      ]);
      renderDm();

      await waitFor(() => {
        screen.getByText("Hephaestus").click(); // select thread
      });

      await waitFor(() => {
        expect(
          screen.getByText("Working on the fix now."),
        ).toBeInTheDocument();
      });

      // Agent messages show sender name in the bubble meta
      const messageLog = screen.getByRole("log", {
        name: "Direct messages",
      });
      expect(
        within(messageLog).getByText("Hephaestus"),
      ).toBeInTheDocument();
    });

    it("renders human messages without sender name", async () => {
      mockGetDmThreads.mockResolvedValue([makeThread()]);
      mockGetDmThreadMessages.mockResolvedValue([
        makeMsg({
          id: "dm-h1",
          senderName: "Darin",
          content: "Please look into this.",
          isFromHuman: true,
          senderId: "user",
        }),
      ]);
      renderDm();

      await waitFor(() => {
        screen.getByText("Hephaestus").click();
      });

      await waitFor(() => {
        expect(
          screen.getByText("Please look into this."),
        ).toBeInTheDocument();
      });

      // Human messages should NOT render the sender name in the bubble
      const messageLog = screen.getByRole("log", {
        name: "Direct messages",
      });
      expect(
        within(messageLog).queryByText("Darin"),
      ).not.toBeInTheDocument();
    });

    it("renders consultant messages with sender name and role badge", async () => {
      mockGetDmThreads.mockResolvedValue([makeThread()]);
      mockGetDmThreadMessages.mockResolvedValue([
        makeMsg({
          id: "dm-c1",
          senderId: "consultant",
          senderName: "Consultant",
          senderRole: "Consultant",
          content: "I need a status update.",
          isFromHuman: true,
        }),
      ]);
      renderDm();

      await waitFor(() => {
        screen.getByText("Hephaestus").click();
      });

      await waitFor(() => {
        expect(
          screen.getByText("I need a status update."),
        ).toBeInTheDocument();
      });

      // Consultant messages render sender name AND role badge — both say "Consultant"
      const messageLog = screen.getByRole("log", {
        name: "Direct messages",
      });
      const consultantTexts = within(messageLog).getAllByText("Consultant");
      // Name span + badge span = at least 2 elements
      expect(consultantTexts.length).toBeGreaterThanOrEqual(2);

      // The badge span has the role-specific background color
      const badge = consultantTexts.find(
        (el) => el.style.fontSize === "10px",
      );
      expect(badge).toBeDefined();
      expect(badge!.style.color).toBe("rgb(224, 151, 110)");
    });

    it("consultant messages do not use human bubble styling", async () => {
      mockGetDmThreads.mockResolvedValue([makeThread()]);
      mockGetDmThreadMessages.mockResolvedValue([
        makeMsg({
          id: "dm-c2",
          senderId: "consultant",
          senderName: "Consultant",
          senderRole: "Consultant",
          content: "Consultant-styled message.",
          isFromHuman: true,
        }),
        makeMsg({
          id: "dm-h2",
          senderId: "human",
          senderName: "Human",
          content: "Human-styled message.",
          isFromHuman: true,
        }),
      ]);
      renderDm();

      await waitFor(() => {
        screen.getByText("Hephaestus").click();
      });

      await waitFor(() => {
        expect(
          screen.getByText("Consultant-styled message."),
        ).toBeInTheDocument();
        expect(
          screen.getByText("Human-styled message."),
        ).toBeInTheDocument();
      });

      // Consultant bubble: name is visible; human bubble: name is hidden
      const messageLog = screen.getByRole("log", {
        name: "Direct messages",
      });
      // Consultant shows its name (at least once)
      const consultantTexts = within(messageLog).getAllByText("Consultant");
      expect(consultantTexts.length).toBeGreaterThanOrEqual(1);
      // Human name should not appear
      expect(
        within(messageLog).queryByText("Human"),
      ).not.toBeInTheDocument();
    });

    it("human messages without senderRole do not show consultant badge", async () => {
      mockGetDmThreads.mockResolvedValue([makeThread()]);
      mockGetDmThreadMessages.mockResolvedValue([
        makeMsg({
          id: "dm-h3",
          senderId: "human",
          senderName: "Darin",
          content: "Regular human message.",
          isFromHuman: true,
        }),
      ]);
      renderDm();

      await waitFor(() => {
        screen.getByText("Hephaestus").click();
      });

      await waitFor(() => {
        expect(
          screen.getByText("Regular human message."),
        ).toBeInTheDocument();
      });

      // Should NOT have any "Consultant" text or role badge
      const messageLog = screen.getByRole("log", {
        name: "Direct messages",
      });
      expect(
        within(messageLog).queryByText("Consultant"),
      ).not.toBeInTheDocument();
    });

    it("shows empty state when thread has no messages", async () => {
      mockGetDmThreads.mockResolvedValue([makeThread()]);
      mockGetDmThreadMessages.mockResolvedValue([]);
      renderDm();

      await waitFor(() => {
        screen.getByText("Hephaestus").click();
      });

      await waitFor(() => {
        expect(screen.getByText("No messages yet")).toBeInTheDocument();
      });
    });
  });

  // ── Send flow ──────────────────────────────────────────────────────

  describe("send flow", () => {
    async function setupWithThread() {
      mockGetDmThreads.mockResolvedValue([makeThread()]);
      mockGetDmThreadMessages.mockResolvedValue([
        makeMsg({ id: "dm-1", content: "Initial message" }),
      ]);
      mockSendDmToAgent.mockResolvedValue(
        makeMsg({
          id: "dm-sent",
          content: "My message",
          isFromHuman: true,
          senderId: "user",
          senderName: "Darin",
        }),
      );

      renderDm();

      await waitFor(() => {
        screen.getByText("Hephaestus").click();
      });

      await waitFor(() => {
        expect(screen.getByText("Initial message")).toBeInTheDocument();
      });
    }

    it("sends message on Enter key", async () => {
      const user = userEvent.setup({
        advanceTimers: vi.advanceTimersByTime,
      });
      await setupWithThread();

      const textarea = screen.getByLabelText("Message Hephaestus");
      await user.click(textarea);
      await user.type(textarea, "Hello agent{Enter}");

      await waitFor(() => {
        expect(mockSendDmToAgent).toHaveBeenCalledWith(
          "agent-1",
          "Hello agent",
        );
      });
    });

    it("sends message on Send button click", async () => {
      const user = userEvent.setup({
        advanceTimers: vi.advanceTimersByTime,
      });
      await setupWithThread();

      const textarea = screen.getByLabelText("Message Hephaestus");
      await user.click(textarea);
      await user.type(textarea, "Button send test");

      const sendBtn = screen.getByTitle("Send");
      await user.click(sendBtn);

      await waitFor(() => {
        expect(mockSendDmToAgent).toHaveBeenCalledWith(
          "agent-1",
          "Button send test",
        );
      });
    });

    it("clears input after sending", async () => {
      const user = userEvent.setup({
        advanceTimers: vi.advanceTimersByTime,
      });
      await setupWithThread();

      const textarea = screen.getByLabelText(
        "Message Hephaestus",
      ) as HTMLTextAreaElement;
      await user.click(textarea);
      await user.type(textarea, "Will be cleared{Enter}");

      await waitFor(() => {
        expect(mockSendDmToAgent).toHaveBeenCalled();
      });

      await waitFor(() => {
        expect(textarea.value).toBe("");
      });
    });

    it("disables Send button when input is empty", async () => {
      await setupWithThread();

      const sendBtn = screen.getByTitle("Send");
      expect(sendBtn).toBeDisabled();
    });

    it("refreshes messages and threads after sending", async () => {
      const user = userEvent.setup({
        advanceTimers: vi.advanceTimersByTime,
      });
      await setupWithThread();

      // Clear call counts
      mockGetDmThreadMessages.mockClear();
      mockGetDmThreads.mockClear();
      mockGetDmThreads.mockResolvedValue([makeThread()]);
      mockGetDmThreadMessages.mockResolvedValue([
        makeMsg({ id: "dm-1", content: "Initial message" }),
      ]);

      const textarea = screen.getByLabelText("Message Hephaestus");
      await user.click(textarea);
      await user.type(textarea, "trigger refresh{Enter}");

      await waitFor(() => {
        expect(mockSendDmToAgent).toHaveBeenCalled();
      });

      await waitFor(() => {
        // After send, both refreshMessages and refreshThreads should be called
        expect(mockGetDmThreadMessages).toHaveBeenCalledWith("agent-1");
        expect(mockGetDmThreads).toHaveBeenCalled();
      });
    });

    it("allows Shift+Enter for newline without sending", async () => {
      const user = userEvent.setup({
        advanceTimers: vi.advanceTimersByTime,
      });
      await setupWithThread();

      // Clear previous send calls from shared mock
      mockSendDmToAgent.mockClear();

      const textarea = screen.getByLabelText("Message Hephaestus") as HTMLTextAreaElement;
      await user.click(textarea);
      await user.type(textarea, "line1{Shift>}{Enter}{/Shift}line2");

      // Should NOT have sent
      expect(mockSendDmToAgent).not.toHaveBeenCalled();

      // Verify textarea contains both lines (newline was inserted)
      expect(textarea.value).toContain("line1");
      expect(textarea.value).toContain("line2");
    });

    it("handles send failure gracefully — input preserved", async () => {
      const user = userEvent.setup({
        advanceTimers: vi.advanceTimersByTime,
      });
      await setupWithThread();
      mockSendDmToAgent.mockClear();
      mockSendDmToAgent.mockRejectedValueOnce(new Error("Network error"));

      const textarea = screen.getByLabelText(
        "Message Hephaestus",
      ) as HTMLTextAreaElement;
      await user.click(textarea);
      await user.type(textarea, "this will fail{Enter}");

      await waitFor(() => {
        expect(mockSendDmToAgent).toHaveBeenCalledWith(
          "agent-1",
          "this will fail",
        );
      });

      // Input should be preserved on failure so user can retry
      await waitFor(() => {
        expect(textarea.value).toBe("this will fail");
      });

      // Send button should be re-enabled (sending state cleared)
      await waitFor(() => {
        const sendBtn = screen.getByTitle("Send");
        expect(sendBtn).not.toBeDisabled();
      });

      // Error message should be visible to the user
      await waitFor(() => {
        expect(screen.getByText(/Failed to send message/)).toBeInTheDocument();
      });
    });
  });

  // ── Read-only mode ─────────────────────────────────────────────────

  describe("read-only mode", () => {
    it("shows limited mode notice instead of composer", async () => {
      mockGetDmThreads.mockResolvedValue([makeThread()]);
      mockGetDmThreadMessages.mockResolvedValue([
        makeMsg({ id: "dm-1", content: "old msg" }),
      ]);
      renderDm({ readOnly: true });

      await waitFor(() => {
        screen.getByText("Hephaestus").click();
      });

      await waitFor(() => {
        expect(
          screen.getByText(/Limited mode is active/),
        ).toBeInTheDocument();
      });

      // No textarea should be present
      expect(
        screen.queryByLabelText("Message Hephaestus"),
      ).not.toBeInTheDocument();
    });

    it("still renders messages in read-only mode", async () => {
      mockGetDmThreads.mockResolvedValue([makeThread()]);
      mockGetDmThreadMessages.mockResolvedValue([
        makeMsg({ id: "dm-1", content: "Readable in readonly" }),
      ]);
      renderDm({ readOnly: true });

      await waitFor(() => {
        screen.getByText("Hephaestus").click();
      });

      await waitFor(() => {
        expect(
          screen.getByText("Readable in readonly"),
        ).toBeInTheDocument();
      });
    });
  });

  // ── Auto-scroll ────────────────────────────────────────────────────

  describe("auto-scroll", () => {
    it("scrolls message list to bottom when messages load", async () => {
      mockGetDmThreads.mockResolvedValue([makeThread()]);
      mockGetDmThreadMessages.mockResolvedValue([
        makeMsg({ id: "dm-1", content: "Message 1" }),
        makeMsg({ id: "dm-2", content: "Message 2" }),
        makeMsg({ id: "dm-3", content: "Message 3" }),
      ]);

      renderDm();

      await waitFor(() => {
        screen.getByText("Hephaestus").click();
      });

      await waitFor(() => {
        expect(screen.getByText("Message 3")).toBeInTheDocument();
      });

      await waitFor(() => {
        expect(Element.prototype.scrollTo).toHaveBeenCalled();
      });
    });
  });

  // ── Polling ────────────────────────────────────────────────────────

  describe("polling", () => {
    it("does not poll threads on a timer — SSE handles live updates", async () => {
      mockGetDmThreads.mockResolvedValue([makeThread()]);
      renderDm();

      await waitFor(() => {
        expect(screen.getByText("Hephaestus")).toBeInTheDocument();
      });

      const initialCalls = mockGetDmThreads.mock.calls.length;

      await act(async () => {
        vi.advanceTimersByTime(30_000);
      });

      // No additional fetches — SSE invalidation replaces 10s polling
      expect(mockGetDmThreads.mock.calls.length).toBe(initialCalls);
    });

    it("does not poll for threads in read-only mode", async () => {
      mockGetDmThreads.mockResolvedValue([makeThread()]);
      renderDm({ readOnly: true });

      await waitFor(() => {
        expect(screen.getByText("Hephaestus")).toBeInTheDocument();
      });

      const callsAfterLoad = mockGetDmThreads.mock.calls.length;

      await act(async () => {
        vi.advanceTimersByTime(30_000);
      });

      expect(mockGetDmThreads.mock.calls.length).toBe(callsAfterLoad);
    });

    it("does not poll messages — SSE handles live delivery", async () => {
      mockGetDmThreads.mockResolvedValue([makeThread()]);
      mockGetDmThreadMessages.mockResolvedValue([
        makeMsg({ id: "dm-1", content: "Initial" }),
      ]);
      renderDm();

      await waitFor(() => {
        screen.getByText("Hephaestus").click();
      });

      await waitFor(() => {
        expect(screen.getByText("Initial")).toBeInTheDocument();
      });

      const msgCalls = mockGetDmThreadMessages.mock.calls.length;

      await act(async () => {
        vi.advanceTimersByTime(15_000);
      });

      // No additional fetches — SSE replaces polling for message delivery
      expect(mockGetDmThreadMessages.mock.calls.length).toBe(msgCalls);
    });

    it("does not poll messages in read-only mode", async () => {
      mockGetDmThreads.mockResolvedValue([makeThread()]);
      mockGetDmThreadMessages.mockResolvedValue([
        makeMsg({ id: "dm-1", content: "Initial" }),
      ]);
      renderDm({ readOnly: true });

      await waitFor(() => {
        screen.getByText("Hephaestus").click();
      });

      await waitFor(() => {
        expect(screen.getByText("Initial")).toBeInTheDocument();
      });

      const msgCalls = mockGetDmThreadMessages.mock.calls.length;

      await act(async () => {
        vi.advanceTimersByTime(15_000);
      });

      expect(mockGetDmThreadMessages.mock.calls.length).toBe(msgCalls);
    });
  });

  // ── Composer placeholder ───────────────────────────────────────────

  describe("composer", () => {
    it("shows agent name in placeholder", async () => {
      mockGetDmThreads.mockResolvedValue([makeThread()]);
      mockGetDmThreadMessages.mockResolvedValue([]);
      renderDm();

      await waitFor(() => {
        screen.getByText("Hephaestus").click();
      });

      await waitFor(() => {
        const textarea = screen.getByLabelText("Message Hephaestus");
        expect(textarea).toHaveAttribute(
          "placeholder",
          "Message Hephaestus…",
        );
      });
    });
  });
});
