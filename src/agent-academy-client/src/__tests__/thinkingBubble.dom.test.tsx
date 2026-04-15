// @vitest-environment jsdom
/**
 * DOM tests for ThinkingBubble.
 *
 * Covers: agent name display, role pill, thinking animation, accessibility.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

import { ThinkingBubble } from "../chat/ThinkingBubble";
import type { ThinkingAgent } from "../useWorkspace";

afterEach(cleanup);

function renderBubble(agent: ThinkingAgent) {
  return render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(ThinkingBubble, { agent }),
    ),
  );
}

describe("ThinkingBubble", () => {
  it("renders agent name", () => {
    renderBubble({ id: "a-1", name: "Athena", role: "Architect" });
    expect(screen.getByText("Athena")).toBeInTheDocument();
  });

  it("renders role pill with formatted role", () => {
    renderBubble({ id: "a-1", name: "Athena", role: "Architect" });
    expect(screen.getByText("Architect")).toBeInTheDocument();
  });

  it("renders thinking dots with status role", () => {
    renderBubble({ id: "a-1", name: "Hephaestus", role: "Implementer" });
    const status = screen.getByRole("status");
    expect(status).toHaveAttribute("aria-label", "Hephaestus is thinking");
    expect(status).toHaveTextContent(/thinking/);
  });

  it("renders inside an article element", () => {
    renderBubble({ id: "a-1", name: "Hermes", role: "Messenger" });
    expect(screen.getByRole("article")).toBeInTheDocument();
  });
});
