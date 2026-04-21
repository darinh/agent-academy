// @vitest-environment jsdom
/**
 * DOM tests for AgentKnowledgePanel.
 *
 * Covers: no-agents empty state, loading, error, empty knowledge, knowledge
 * entries list, agent selector, refresh button, auto-select first agent,
 * entry count badge.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../api", () => ({
  getAgentKnowledge: vi.fn(),
}));

vi.mock("../V3Badge", () => ({
  default: ({
    children,
    color,
  }: {
    children: React.ReactNode;
    color: string;
  }) => createElement("span", { "data-testid": `badge-${color}` }, children),
}));

vi.mock("../EmptyState", () => ({
  default: ({ title, detail }: { title: string; detail?: string }) =>
    createElement(
      "div",
      { "data-testid": "empty-state" },
      createElement("span", null, title),
      detail && createElement("span", null, detail),
    ),
}));

import AgentKnowledgePanel from "../AgentKnowledgePanel";
import type { AgentDefinition, AgentKnowledgeResponse } from "../api";
import { getAgentKnowledge } from "../api";

const mockGetKnowledge = vi.mocked(getAgentKnowledge);

// ── Helpers ────────────────────────────────────────────────────────────

function wrap(ui: React.ReactNode) {
  return createElement(FluentProvider, { theme: webDarkTheme }, ui);
}

function makeAgents(count = 2): AgentDefinition[] {
  return Array.from({ length: count }, (_, i) => ({
    id: `agent-${i + 1}`,
    name: `Agent ${i + 1}`,
    role: i === 0 ? "Engineer" : "Reviewer",
    summary: `Agent ${i + 1} summary`,
    startupPrompt: "",
    capabilityTags: [],
    enabledTools: [],
    autoJoinDefaultRoom: true,
  })) as AgentDefinition[];
}

function makeKnowledge(entries: string[] = []): AgentKnowledgeResponse {
  return { entries };
}

// ── Tests ──────────────────────────────────────────────────────────────

describe("AgentKnowledgePanel", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(cleanup);

  it("shows empty state when no agents are configured", () => {
    render(
      wrap(createElement(AgentKnowledgePanel, { agents: [] })),
    );
    expect(screen.getByText("No agents configured")).toBeInTheDocument();
  });

  it("shows loading spinner while fetching knowledge", () => {
    mockGetKnowledge.mockReturnValue(new Promise(() => {}));
    render(
      wrap(createElement(AgentKnowledgePanel, { agents: makeAgents() })),
    );
    expect(screen.getByText("Loading knowledge…")).toBeInTheDocument();
  });

  it("shows error on fetch failure", async () => {
    mockGetKnowledge.mockRejectedValue(new Error("Not found"));
    render(
      wrap(createElement(AgentKnowledgePanel, { agents: makeAgents() })),
    );
    await waitFor(() => {
      expect(screen.getByText(/Not found/)).toBeInTheDocument();
    });
  });

  it("shows generic error for non-Error rejection", async () => {
    mockGetKnowledge.mockRejectedValue("boom");
    render(
      wrap(createElement(AgentKnowledgePanel, { agents: makeAgents() })),
    );
    await waitFor(() => {
      expect(
        screen.getByText(/Failed to load knowledge/),
      ).toBeInTheDocument();
    });
  });

  it("shows empty knowledge state for agent with no entries", async () => {
    mockGetKnowledge.mockResolvedValue(makeKnowledge([]));
    render(
      wrap(createElement(AgentKnowledgePanel, { agents: makeAgents() })),
    );
    await waitFor(() => {
      expect(screen.getByText("No knowledge entries")).toBeInTheDocument();
    });
  });

  it("renders knowledge entries", async () => {
    mockGetKnowledge.mockResolvedValue(
      makeKnowledge(["Use dependency injection", "Prefer async/await"]),
    );
    render(
      wrap(createElement(AgentKnowledgePanel, { agents: makeAgents() })),
    );
    await waitFor(() => {
      expect(
        screen.getByText("Use dependency injection"),
      ).toBeInTheDocument();
    });
    expect(screen.getByText("Prefer async/await")).toBeInTheDocument();
  });

  it("shows entry count badge", async () => {
    mockGetKnowledge.mockResolvedValue(
      makeKnowledge(["Entry 1", "Entry 2", "Entry 3"]),
    );
    render(
      wrap(createElement(AgentKnowledgePanel, { agents: makeAgents() })),
    );
    await waitFor(() => {
      expect(screen.getByText("3 entries")).toBeInTheDocument();
    });
  });

  it("auto-selects first agent", async () => {
    mockGetKnowledge.mockResolvedValue(makeKnowledge(["Fact"]));
    render(
      wrap(createElement(AgentKnowledgePanel, { agents: makeAgents() })),
    );
    expect(mockGetKnowledge).toHaveBeenCalledWith("agent-1");
  });

  it("renders agent selector with all agents", async () => {
    mockGetKnowledge.mockResolvedValue(makeKnowledge([]));
    render(
      wrap(createElement(AgentKnowledgePanel, { agents: makeAgents() })),
    );
    await waitFor(() => {
      expect(
        screen.getByLabelText("Select agent"),
      ).toBeInTheDocument();
    });
    const select = screen.getByLabelText("Select agent") as HTMLSelectElement;
    expect(select.options).toHaveLength(2);
    expect(select.options[0].textContent).toBe("Agent 1 (Engineer)");
    expect(select.options[1].textContent).toBe("Agent 2 (Reviewer)");
  });

  it("fetches knowledge for selected agent on change", async () => {
    mockGetKnowledge.mockResolvedValue(makeKnowledge(["Knowledge A"]));
    const user = userEvent.setup();
    render(
      wrap(createElement(AgentKnowledgePanel, { agents: makeAgents() })),
    );

    await waitFor(() => {
      expect(mockGetKnowledge).toHaveBeenCalledWith("agent-1");
    });

    mockGetKnowledge.mockResolvedValue(makeKnowledge(["Knowledge B"]));
    await user.selectOptions(
      screen.getByLabelText("Select agent"),
      "agent-2",
    );

    await waitFor(() => {
      expect(mockGetKnowledge).toHaveBeenCalledWith("agent-2");
    });
  });

  it("refresh button re-fetches knowledge", async () => {
    mockGetKnowledge.mockResolvedValue(makeKnowledge(["Initial"]));
    render(
      wrap(createElement(AgentKnowledgePanel, { agents: makeAgents() })),
    );
    await waitFor(() => {
      expect(screen.getByText("Initial")).toBeInTheDocument();
    });

    mockGetKnowledge.mockResolvedValue(makeKnowledge(["Refreshed"]));
    const user = userEvent.setup();
    await user.click(screen.getByLabelText("Refresh knowledge"));

    await waitFor(() => {
      expect(screen.getByText("Refreshed")).toBeInTheDocument();
    });
  });

  it("renders Agent Knowledge header", async () => {
    mockGetKnowledge.mockResolvedValue(makeKnowledge([]));
    render(
      wrap(createElement(AgentKnowledgePanel, { agents: makeAgents() })),
    );
    await waitFor(() => {
      expect(screen.getByText("Agent Knowledge")).toBeInTheDocument();
    });
  });
});
