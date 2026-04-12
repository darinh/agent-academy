import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { createElement } from "react";
import AgentActivityBar from "../AgentActivityBar";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import type { AgentDefinition, AgentLocation } from "../api";

function mockAgent(overrides?: Partial<AgentDefinition>): AgentDefinition {
  return {
    id: "arch",
    name: "Athena",
    role: "Architect",
    summary: "",
    startupPrompt: "",
    model: "gpt-5",
    capabilityTags: [],
    enabledTools: [],
    autoJoinDefaultRoom: true,
    gitIdentity: null,
    ...overrides,
  };
}

function mockLocation(overrides?: Partial<AgentLocation>): AgentLocation {
  return {
    agentId: "arch",
    roomId: "r1",
    state: "Idle",
    updatedAt: new Date().toISOString(),
    ...overrides,
  };
}

function render(props: {
  agents: AgentDefinition[];
  locations: AgentLocation[];
  thinkingAgentIds: Set<string>;
}) {
  return renderToStaticMarkup(
    createElement(FluentProvider, { theme: webDarkTheme },
      createElement(AgentActivityBar, props),
    ),
  );
}

describe("AgentActivityBar", () => {
  it("returns empty output when agents is empty", () => {
    const html = render({ agents: [], locations: [], thinkingAgentIds: new Set() });
    // Component returns null, but FluentProvider wrapper still renders
    expect(html).not.toContain("Agents");
  });

  it("renders 'Agents' label", () => {
    const html = render({
      agents: [mockAgent()],
      locations: [mockLocation()],
      thinkingAgentIds: new Set(),
    });
    expect(html).toContain("Agents");
  });

  it("renders agent name for each agent", () => {
    const html = render({
      agents: [
        mockAgent({ id: "a1", name: "Athena" }),
        mockAgent({ id: "a2", name: "Hermes", role: "Runner" }),
      ],
      locations: [
        mockLocation({ agentId: "a1" }),
        mockLocation({ agentId: "a2" }),
      ],
      thinkingAgentIds: new Set(),
    });
    expect(html).toContain("Athena");
    expect(html).toContain("Hermes");
  });

  it("shows 'Working' state label for working agent", () => {
    const html = render({
      agents: [mockAgent()],
      locations: [mockLocation({ agentId: "arch", state: "Working" })],
      thinkingAgentIds: new Set(),
    });
    expect(html).toContain("Working");
  });

  it("shows 'Thinking' state label for thinking agent", () => {
    const html = render({
      agents: [mockAgent()],
      locations: [mockLocation({ agentId: "arch" })],
      thinkingAgentIds: new Set(["arch"]),
    });
    expect(html).toContain("Thinking");
  });

  it("does not show state label for idle agent", () => {
    const html = render({
      agents: [mockAgent()],
      locations: [mockLocation({ agentId: "arch", state: "Idle" })],
      thinkingAgentIds: new Set(),
    });
    expect(html).not.toContain("Working");
    expect(html).not.toContain("Thinking");
  });
});
