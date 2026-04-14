import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import ContextMeter from "../ContextMeter";
import AgentActivityBar from "../AgentActivityBar";
import type { AgentContextUsage, AgentDefinition, AgentLocation } from "../api";

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

function mockAgent(overrides?: Partial<AgentDefinition>): AgentDefinition {
  return {
    id: "agent-1",
    name: "Athena",
    role: "Architect",
    summary: "",
    startupPrompt: "",
    model: "gpt-4o",
    capabilityTags: [],
    enabledTools: [],
    autoJoinDefaultRoom: true,
    gitIdentity: null,
    ...overrides,
  };
}

function mockLocation(overrides?: Partial<AgentLocation>): AgentLocation {
  return {
    agentId: "agent-1",
    roomId: "r1",
    state: "Idle",
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

describe("AgentActivityBar with context usage", () => {
  it("shows context meter when usage is provided", () => {
    const contextMap = new Map<string, AgentContextUsage>();
    contextMap.set("agent-1", mockUsage());

    const html = render(
      createElement(AgentActivityBar, {
        agents: [mockAgent()],
        locations: [mockLocation()],
        thinkingAgentIds: new Set<string>(),
        contextUsage: contextMap,
      }),
    );
    expect(html).toContain("50%");
  });

  it("does not show context meter when no usage data", () => {
    const html = render(
      createElement(AgentActivityBar, {
        agents: [mockAgent()],
        locations: [mockLocation()],
        thinkingAgentIds: new Set<string>(),
      }),
    );
    // Should not have any percentage indicators
    expect(html).not.toMatch(/\d+%/);
  });

  it("shows different meters for different agents", () => {
    const contextMap = new Map<string, AgentContextUsage>();
    contextMap.set("a1", mockUsage({ agentId: "a1", percentage: 30 }));
    contextMap.set("a2", mockUsage({ agentId: "a2", percentage: 70 }));

    const html = render(
      createElement(AgentActivityBar, {
        agents: [
          mockAgent({ id: "a1", name: "Alpha" }),
          mockAgent({ id: "a2", name: "Beta" }),
        ],
        locations: [
          mockLocation({ agentId: "a1" }),
          mockLocation({ agentId: "a2" }),
        ],
        thinkingAgentIds: new Set<string>(),
        contextUsage: contextMap,
      }),
    );
    expect(html).toContain("30%");
    expect(html).toContain("70%");
  });
});
