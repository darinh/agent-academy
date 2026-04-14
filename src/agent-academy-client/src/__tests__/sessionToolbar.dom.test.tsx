// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, fireEvent } from "@testing-library/react";
import type { AgentDefinition, AgentLocation, ConversationSessionSnapshot } from "../api";

import { SessionToolbar } from "../chat/SessionToolbar";

function makeSession(overrides: Partial<ConversationSessionSnapshot> = {}): ConversationSessionSnapshot {
  return {
    id: "session-1",
    roomId: "room-1",
    roomType: "Main",
    sequenceNumber: 1,
    status: "Archived",
    summary: null,
    messageCount: 12,
    createdAt: "2026-04-01T00:00:00Z",
    archivedAt: "2026-04-01T01:00:00Z",
    ...overrides,
  };
}

function makeAgent(overrides: Partial<AgentDefinition> = {}): AgentDefinition {
  return {
    id: "agent-1",
    name: "Planner",
    role: "Planner",
    summary: "Plans things",
    startupPrompt: "You are a planner",
    capabilityTags: [],
    enabledTools: [],
    autoJoinDefaultRoom: false,
    ...overrides,
  };
}

function makeLocation(overrides: Partial<AgentLocation> = {}): AgentLocation {
  return {
    agentId: "agent-1",
    roomId: "room-1",
    state: "Idle",
    breakoutRoomId: null,
    updatedAt: "2026-04-01T00:00:00Z",
    ...overrides,
  };
}

const defaultProps = {
  roomId: "room-1",
  sessions: [] as ConversationSessionSnapshot[],
  selectedSessionId: null as string | null,
  onSessionChange: vi.fn(),
  onNewSession: vi.fn(),
  configuredAgents: [] as AgentDefinition[],
  agentLocations: [] as AgentLocation[],
};

describe("SessionToolbar", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  // ── New Session button ──

  it("renders '+ New Session' button when onCreateSession is provided", () => {
    const { container } = render(
      <SessionToolbar {...defaultProps} onCreateSession={vi.fn()} />,
    );
    const btn = findButton(container, /New Session/);
    expect(btn).toBeTruthy();
  });

  it("does not render '+ New Session' button when onCreateSession is absent", () => {
    const { container } = render(
      <SessionToolbar {...defaultProps} />,
    );
    const btn = findButton(container, /New Session/);
    expect(btn).toBeNull();
  });

  it("calls onNewSession when '+ New Session' is clicked", () => {
    const onNewSession = vi.fn();
    const { container } = render(
      <SessionToolbar {...defaultProps} onNewSession={onNewSession} onCreateSession={vi.fn()} />,
    );
    const btn = findButton(container, /New Session/)!;
    fireEvent.click(btn);
    expect(onNewSession).toHaveBeenCalledOnce();
  });

  // ── Session selector ──

  it("renders session dropdown with 'Current session' default option", () => {
    const { container } = render(
      <SessionToolbar {...defaultProps} />,
    );
    const select = container.querySelector("select");
    expect(select).toBeTruthy();
    const options = select!.querySelectorAll("option");
    expect(options[0].textContent).toBe("Current session");
    expect(options[0].value).toBe("");
  });

  it("shows only archived sessions in the dropdown", () => {
    const sessions = [
      makeSession({ id: "s1", sequenceNumber: 1, status: "Archived", messageCount: 5 }),
      makeSession({ id: "s2", sequenceNumber: 2, status: "Active", messageCount: 8 }),
      makeSession({ id: "s3", sequenceNumber: 3, status: "Archived", messageCount: 3 }),
    ];
    const { container } = render(
      <SessionToolbar {...defaultProps} sessions={sessions} />,
    );
    const select = container.querySelector("select")!;
    const options = Array.from(select.querySelectorAll("option"));
    // Default + 2 archived
    expect(options).toHaveLength(3);
    expect(options[1].textContent).toContain("#1");
    expect(options[1].textContent).toContain("5 msgs");
    expect(options[2].textContent).toContain("#3");
    expect(options[2].textContent).toContain("3 msgs");
  });

  it("sets selected value from selectedSessionId", () => {
    const sessions = [
      makeSession({ id: "s1", sequenceNumber: 1 }),
      makeSession({ id: "s2", sequenceNumber: 2 }),
    ];
    const { container } = render(
      <SessionToolbar {...defaultProps} sessions={sessions} selectedSessionId="s2" />,
    );
    const select = container.querySelector("select") as HTMLSelectElement;
    expect(select.value).toBe("s2");
  });

  it("calls onSessionChange with session id on change", () => {
    const onSessionChange = vi.fn();
    const sessions = [makeSession({ id: "s1", sequenceNumber: 1 })];
    const { container } = render(
      <SessionToolbar {...defaultProps} sessions={sessions} onSessionChange={onSessionChange} />,
    );
    const select = container.querySelector("select")!;
    fireEvent.change(select, { target: { value: "s1" } });
    expect(onSessionChange).toHaveBeenCalledWith("s1");
  });

  it("calls onSessionChange with null when 'Current session' is selected", () => {
    const onSessionChange = vi.fn();
    const sessions = [makeSession({ id: "s1", sequenceNumber: 1 })];
    const { container } = render(
      <SessionToolbar {...defaultProps} sessions={sessions} selectedSessionId="s1" onSessionChange={onSessionChange} />,
    );
    const select = container.querySelector("select")!;
    fireEvent.change(select, { target: { value: "" } });
    expect(onSessionChange).toHaveBeenCalledWith(null);
  });

  // ── Agents dropdown ──

  it("does not render agents button when no agents configured", () => {
    const { container } = render(
      <SessionToolbar {...defaultProps} onToggleAgent={vi.fn()} />,
    );
    const agentsBtn = findButton(container, /Agents/);
    expect(agentsBtn).toBeNull();
  });

  it("does not render agents button when onToggleAgent is absent", () => {
    const agents = [makeAgent()];
    const { container } = render(
      <SessionToolbar {...defaultProps} configuredAgents={agents} />,
    );
    const agentsBtn = findButton(container, /Agents/);
    expect(agentsBtn).toBeNull();
  });

  it("renders agents button with count when agents and onToggleAgent are present", () => {
    const agents = [makeAgent({ id: "a1" }), makeAgent({ id: "a2", name: "Coder" })];
    const locations = [makeLocation({ agentId: "a1", roomId: "room-1" })];
    const { container } = render(
      <SessionToolbar
        {...defaultProps}
        configuredAgents={agents}
        agentLocations={locations}
        onToggleAgent={vi.fn()}
      />,
    );
    const agentsBtn = findButton(container, /Agents/)!;
    expect(agentsBtn.textContent).toContain("1"); // 1 agent in room
  });

  it("toggles agent dropdown on button click", () => {
    const agents = [makeAgent({ id: "a1" })];
    const { container } = render(
      <SessionToolbar
        {...defaultProps}
        configuredAgents={agents}
        agentLocations={[]}
        onToggleAgent={vi.fn()}
      />,
    );
    const agentsBtn = findButton(container, /Agents/)!;

    // Dropdown not visible initially
    expect(container.querySelectorAll("input[type='checkbox']")).toHaveLength(0);

    // Click to open
    fireEvent.click(agentsBtn);
    expect(container.querySelectorAll("input[type='checkbox']")).toHaveLength(1);

    // Click to close
    fireEvent.click(agentsBtn);
    expect(container.querySelectorAll("input[type='checkbox']")).toHaveLength(0);
  });

  it("renders agent list with checkboxes in dropdown", () => {
    const agents = [
      makeAgent({ id: "a1", name: "Planner", role: "Planner" }),
      makeAgent({ id: "a2", name: "Coder", role: "Developer" }),
    ];
    const locations = [makeLocation({ agentId: "a1", roomId: "room-1" })];
    const { container } = render(
      <SessionToolbar
        {...defaultProps}
        configuredAgents={agents}
        agentLocations={locations}
        onToggleAgent={vi.fn()}
      />,
    );
    // Open the dropdown
    fireEvent.click(findButton(container, /Agents/)!);

    const checkboxes = container.querySelectorAll("input[type='checkbox']") as NodeListOf<HTMLInputElement>;
    expect(checkboxes).toHaveLength(2);
    expect(checkboxes[0].checked).toBe(true);  // a1 is in room
    expect(checkboxes[1].checked).toBe(false); // a2 is not in room

    // Agent names and roles are rendered
    expect(container.textContent).toContain("Planner");
    expect(container.textContent).toContain("Coder");
    expect(container.textContent).toContain("Developer");
  });

  it("calls onToggleAgent when checkbox is toggled", () => {
    const onToggleAgent = vi.fn();
    const agents = [makeAgent({ id: "a1" })];
    const locations = [makeLocation({ agentId: "a1", roomId: "room-1" })];
    const { container } = render(
      <SessionToolbar
        {...defaultProps}
        configuredAgents={agents}
        agentLocations={locations}
        onToggleAgent={onToggleAgent}
      />,
    );
    fireEvent.click(findButton(container, /Agents/)!);

    const checkbox = container.querySelector("input[type='checkbox']") as HTMLInputElement;
    // The onChange fires from the native change event on checkbox toggle
    fireEvent.click(checkbox);
    expect(onToggleAgent).toHaveBeenCalledWith("room-1", "a1", true); // currently in room
  });

  it("only counts agents in the current room for the button badge", () => {
    const agents = [makeAgent({ id: "a1" }), makeAgent({ id: "a2", name: "Coder" })];
    const locations = [
      makeLocation({ agentId: "a1", roomId: "room-1" }),
      makeLocation({ agentId: "a2", roomId: "other-room" }),
    ];
    const { container } = render(
      <SessionToolbar
        {...defaultProps}
        configuredAgents={agents}
        agentLocations={locations}
        onToggleAgent={vi.fn()}
      />,
    );
    const agentsBtn = findButton(container, /Agents/)!;
    expect(agentsBtn.textContent).toContain("1");
  });

  // ── Outside click closes dropdown ──

  it("closes dropdown on outside mousedown", () => {
    const agents = [makeAgent({ id: "a1" })];
    const { container } = render(
      <SessionToolbar
        {...defaultProps}
        configuredAgents={agents}
        agentLocations={[]}
        onToggleAgent={vi.fn()}
      />,
    );
    fireEvent.click(findButton(container, /Agents/)!);
    expect(container.querySelectorAll("input[type='checkbox']")).toHaveLength(1);

    // Simulate outside click
    fireEvent.mouseDown(document.body);
    expect(container.querySelectorAll("input[type='checkbox']")).toHaveLength(0);
  });

  // ── Memoization ──

  it("is exported as a memoized component", () => {
    expect(SessionToolbar).toBeDefined();
    // memo wraps the component - verify it renders correctly after re-render
    const agents = [makeAgent({ id: "a1" })];
    const { rerender, container } = render(
      <SessionToolbar {...defaultProps} configuredAgents={agents} onToggleAgent={vi.fn()} />,
    );
    rerender(
      <SessionToolbar {...defaultProps} configuredAgents={agents} onToggleAgent={vi.fn()} />,
    );
    expect(findButton(container, /Agents/)).toBeTruthy();
  });

  // ── Compact button ──

  it("renders Compact button", () => {
    const { container } = render(
      <SessionToolbar {...defaultProps} />,
    );
    const btn = findButton(container, /Compact/);
    expect(btn).toBeTruthy();
    expect(btn!.textContent).toContain("Compact");
  });

  it("has a title tooltip on the Compact button", () => {
    const { container } = render(
      <SessionToolbar {...defaultProps} />,
    );
    const btn = findButton(container, /Compact/)!;
    expect(btn.title).toContain("context window");
  });

  it("shows Compacting… text while compact is in progress", async () => {
    // Create a promise we control to simulate in-flight request
    let resolveCompact!: (value: unknown) => void;
    const compactPromise = new Promise((resolve) => { resolveCompact = resolve; });
    const { compactRoom: _orig } = await import("../api");
    const apiMod = await import("../api/rooms");
    const spy = vi.spyOn(apiMod, "compactRoom").mockReturnValue(compactPromise as ReturnType<typeof apiMod.compactRoom>);

    const { container } = render(
      <SessionToolbar {...defaultProps} />,
    );
    const btn = findButton(container, /Compact/)!;
    fireEvent.click(btn);

    // Should show compacting state
    expect(btn.textContent).toContain("Compacting");
    expect(btn.disabled).toBe(true);

    // Resolve and clean up
    resolveCompact({ compactedSessions: 2, totalAgents: 2 });
    spy.mockRestore();
  });

  it("shows success result message after compaction", async () => {
    const { waitFor } = await import("@testing-library/react");
    const apiMod = await import("../api/rooms");
    const spy = vi.spyOn(apiMod, "compactRoom").mockResolvedValue({
      compactedSessions: 3, totalAgents: 3,
    });

    const { container } = render(
      <SessionToolbar {...defaultProps} />,
    );
    fireEvent.click(findButton(container, /Compact/)!);

    await waitFor(() => {
      const status = container.querySelector("[role='status']");
      expect(status).toBeTruthy();
      expect(status!.textContent).toContain("Compacted 3 session(s)");
    });

    spy.mockRestore();
  });

  it("shows note from server when present in compaction result", async () => {
    const { waitFor } = await import("@testing-library/react");
    const apiMod = await import("../api/rooms");
    const spy = vi.spyOn(apiMod, "compactRoom").mockResolvedValue({
      compactedSessions: 0, totalAgents: 2,
      note: "Executor is not fully operational; no sessions to compact.",
    });

    const { container } = render(
      <SessionToolbar {...defaultProps} />,
    );
    fireEvent.click(findButton(container, /Compact/)!);

    await waitFor(() => {
      const status = container.querySelector("[role='status']");
      expect(status).toBeTruthy();
      expect(status!.textContent).toContain("not fully operational");
    });

    spy.mockRestore();
  });

  it("shows error message when compaction fails", async () => {
    const { waitFor } = await import("@testing-library/react");
    const apiMod = await import("../api/rooms");
    const spy = vi.spyOn(apiMod, "compactRoom").mockRejectedValue(new Error("Network error"));

    const { container } = render(
      <SessionToolbar {...defaultProps} />,
    );
    fireEvent.click(findButton(container, /Compact/)!);

    await waitFor(() => {
      const status = container.querySelector("[role='status']");
      expect(status).toBeTruthy();
      expect(status!.textContent).toContain("Failed");
    });

    spy.mockRestore();
  });

  it("calls onCompacted callback after successful compaction", async () => {
    const { waitFor } = await import("@testing-library/react");
    const apiMod = await import("../api/rooms");
    const spy = vi.spyOn(apiMod, "compactRoom").mockResolvedValue({
      compactedSessions: 2, totalAgents: 2,
    });
    const onCompacted = vi.fn();

    const { container } = render(
      <SessionToolbar {...defaultProps} onCompacted={onCompacted} />,
    );
    fireEvent.click(findButton(container, /Compact/)!);

    await waitFor(() => {
      expect(onCompacted).toHaveBeenCalledOnce();
    });

    spy.mockRestore();
  });
});

// ── Helpers ──

function findButton(container: HTMLElement, text: RegExp): HTMLButtonElement | null {
  const buttons = Array.from(container.querySelectorAll("button"));
  return buttons.find((b) => text.test(b.textContent ?? "")) ?? null;
}
