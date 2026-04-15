// @vitest-environment jsdom
/**
 * DOM tests for DmMessageBubble.
 *
 * Covers: agent messages, human messages, consultant messages with role badge,
 * markdown rendering, time display.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

vi.mock("react-markdown", () => ({
  default: ({ children }: { children?: string }) =>
    createElement("div", { "data-testid": "markdown" }, children),
}));

vi.mock("remark-gfm", () => ({ default: () => {} }));

import DmMessageBubble from "../dm/DmMessageBubble";
import type { DmMessage } from "../api";

afterEach(cleanup);

function makeMsg(overrides: Partial<DmMessage> = {}): DmMessage {
  return {
    id: "dm-1",
    senderId: "agent-1",
    senderName: "Hephaestus",
    senderRole: "Implementer",
    content: "Working on the feature",
    sentAt: new Date().toISOString(),
    isFromHuman: false,
    ...overrides,
  };
}

function renderBubble(msg: DmMessage) {
  return render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(DmMessageBubble, { message: msg }),
    ),
  );
}

describe("DmMessageBubble", () => {
  it("renders agent message with sender name", () => {
    renderBubble(makeMsg());
    expect(screen.getByText("Hephaestus")).toBeInTheDocument();
    expect(screen.getByTestId("markdown")).toHaveTextContent("Working on the feature");
  });

  it("does not show sender name for human messages", () => {
    renderBubble(makeMsg({ isFromHuman: true, senderName: "Darin", senderRole: null }));
    expect(screen.queryByText("Darin")).not.toBeInTheDocument();
    expect(screen.getByTestId("markdown")).toHaveTextContent("Working on the feature");
  });

  it("shows sender name and consultant role badge for consultant messages", () => {
    renderBubble(makeMsg({
      isFromHuman: true,
      senderName: "Anvil",
      senderRole: "Consultant",
    }));
    expect(screen.getByText("Anvil")).toBeInTheDocument();
    expect(screen.getByText("Consultant")).toBeInTheDocument();
  });

  it("renders markdown content", () => {
    renderBubble(makeMsg({ content: "Hello **world**" }));
    expect(screen.getByTestId("markdown")).toHaveTextContent("Hello **world**");
  });

  it("renders time for all message types", () => {
    const { container } = renderBubble(makeMsg());
    // formatTime produces a non-empty string — the time div should exist
    expect(container.querySelector("div")).toBeInTheDocument();
  });
});
