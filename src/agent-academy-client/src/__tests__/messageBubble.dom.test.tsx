// @vitest-environment jsdom
/**
 * DOM tests for MessageBubble.
 *
 * Covers: system messages, command result messages, normal agent/user bubbles,
 * role pill rendering, long message expand/collapse, markdown rendering.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("react-markdown", () => ({
  default: ({ children }: { children?: string }) =>
    createElement("div", { "data-testid": "markdown" }, children),
}));

vi.mock("remark-gfm", () => ({ default: () => {} }));

import { MessageBubble } from "../chat/MessageBubble";
import type { ChatEnvelope } from "../api";
import { MESSAGE_LENGTH_THRESHOLD } from "../chatUtils";

// ── Helpers ────────────────────────────────────────────────────────────

afterEach(cleanup);

function makeMsg(overrides: Partial<ChatEnvelope> = {}): ChatEnvelope {
  return {
    id: "msg-1",
    roomId: "room-1",
    senderId: "agent-1",
    senderName: "Athena",
    senderRole: "Architect",
    senderKind: "Agent",
    kind: "chat",
    content: "Hello world",
    sentAt: new Date().toISOString(),
    correlationId: null,
    replyToMessageId: null,
    ...overrides,
  };
}

function renderBubble(
  msg: ChatEnvelope,
  expanded = false,
  onToggle = vi.fn(),
) {
  return render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(MessageBubble, {
        message: msg,
        expanded,
        onToggle,
      }),
    ),
  );
}

// ── Tests ──────────────────────────────────────────────────────────────

describe("MessageBubble", () => {
  describe("system messages", () => {
    it("renders plain system message text", () => {
      const msg = makeMsg({
        senderKind: "System",
        content: "Agent joined the room",
      });
      renderBubble(msg);
      expect(screen.getByText("Agent joined the room")).toBeInTheDocument();
    });

    it("renders command result system message with status indicators", () => {
      const content = [
        "=== COMMAND RESULTS ===",
        "[Success] RUN_BUILD (abc-123)",
        "  Build succeeded.",
      ].join("\n");

      const msg = makeMsg({ senderKind: "System", content });
      renderBubble(msg);
      expect(screen.getByText("✅")).toBeInTheDocument();
      expect(screen.getByText("RUN_BUILD")).toBeInTheDocument();
    });

    it("renders error command results with error icon", () => {
      const content = [
        "=== COMMAND RESULTS ===",
        "[Error] RUN_TESTS (abc-456)",
        "  Error: Build failed",
      ].join("\n");

      const msg = makeMsg({ senderKind: "System", content });
      renderBubble(msg);
      expect(screen.getByText("❌")).toBeInTheDocument();
    });

    it("renders denied command results with denied icon", () => {
      const content = [
        "=== COMMAND RESULTS ===",
        "[Denied] DEPLOY (abc-789)",
        "  Error: Permission denied",
      ].join("\n");

      const msg = makeMsg({ senderKind: "System", content });
      renderBubble(msg);
      expect(screen.getByText("🚫")).toBeInTheDocument();
    });
  });

  describe("agent/user messages", () => {
    it("renders sender name and role pill", () => {
      renderBubble(makeMsg());
      expect(screen.getByText("Athena")).toBeInTheDocument();
      expect(screen.getByText("Architect")).toBeInTheDocument();
    });

    it("formats User senderKind with Human role when no senderRole", () => {
      const msg = makeMsg({ senderKind: "User", senderRole: null });
      renderBubble(msg);
      expect(screen.getByText("Human")).toBeInTheDocument();
    });

    it("renders markdown content", () => {
      const msg = makeMsg({ content: "**bold text**" });
      renderBubble(msg);
      expect(screen.getByTestId("markdown")).toHaveTextContent("**bold text**");
    });

    it("renders time", () => {
      const msg = makeMsg({ sentAt: "2026-01-15T10:30:00Z" });
      renderBubble(msg);
      // formatTime returns a string with hours/minutes — just verify something rendered
      const article = screen.getByRole("article");
      expect(article).toBeInTheDocument();
    });
  });

  describe("long message expand/collapse", () => {
    const longContent = "A".repeat(MESSAGE_LENGTH_THRESHOLD + 100);

    it("shows truncated content with 'Show more' button when collapsed", () => {
      renderBubble(makeMsg({ content: longContent }), false);
      expect(screen.getByText("Show more")).toBeInTheDocument();
      // Content should be truncated
      const md = screen.getByTestId("markdown");
      expect(md.textContent!.length).toBeLessThan(longContent.length);
    });

    it("shows full content with 'Show less' when expanded", () => {
      renderBubble(makeMsg({ content: longContent }), true);
      expect(screen.getByText("Show less")).toBeInTheDocument();
      const md = screen.getByTestId("markdown");
      expect(md.textContent).toBe(longContent);
    });

    it("calls onToggle with message id when button clicked", async () => {
      const onToggle = vi.fn();
      const msg = makeMsg({ id: "long-msg", content: longContent });
      renderBubble(msg, false, onToggle);

      await userEvent.click(screen.getByText("Show more"));
      expect(onToggle).toHaveBeenCalledWith("long-msg");
    });

    it("does not show expand button for short messages", () => {
      renderBubble(makeMsg({ content: "short" }));
      expect(screen.queryByText("Show more")).not.toBeInTheDocument();
      expect(screen.queryByText("Show less")).not.toBeInTheDocument();
    });
  });
});
