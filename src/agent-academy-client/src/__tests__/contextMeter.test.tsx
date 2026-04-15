import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import ContextMeter from "../ContextMeter";
import type { AgentContextUsage } from "../api";

function render(el: React.ReactElement) {
  return renderToStaticMarkup(
    createElement(FluentProvider, { theme: webDarkTheme }, el),
  );
}

function mockUsage(overrides?: Partial<AgentContextUsage>): AgentContextUsage {
  return {
    agentId: "agent-1",
    roomId: "room-1",
    model: "gpt-4o",
    currentTokens: 64000,
    maxTokens: 128000,
    percentage: 50,
    updatedAt: new Date().toISOString(),
    ...overrides,
  };
}

describe("ContextMeter", () => {
  it("renders percentage text", () => {
    const html = render(createElement(ContextMeter, { usage: mockUsage() }));
    expect(html).toContain("50%");
  });

  it("renders low percentage", () => {
    const html = render(createElement(ContextMeter, { usage: mockUsage({ percentage: 10 }) }));
    expect(html).toContain("10%");
  });

  it("renders high percentage (warning level)", () => {
    const html = render(createElement(ContextMeter, { usage: mockUsage({ percentage: 85 }) }));
    expect(html).toContain("85%");
  });

  it("renders critical percentage", () => {
    const html = render(createElement(ContextMeter, { usage: mockUsage({ percentage: 95 }) }));
    expect(html).toContain("95%");
  });

  it("clamps percentage display to 100", () => {
    const html = render(createElement(ContextMeter, { usage: mockUsage({ percentage: 120 }) }));
    // Progress bar width should be clamped
    expect(html).toContain("100%");
  });
});
