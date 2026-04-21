// @vitest-environment jsdom
import "@testing-library/jest-dom/vitest";
import { describe, expect, it, vi, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent } from "@testing-library/react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import TaskActionsBar from "../taskList/TaskActionsBar";
import type { AgentDefinition } from "../api";
import type { TaskAction } from "../taskList/taskListHelpers";

function makeAgent(overrides: Partial<AgentDefinition> = {}): AgentDefinition {
  return {
    id: "agent-1",
    name: "Planner",
    role: "Planner",
    summary: "Plans",
    startupPrompt: "",
    capabilityTags: [],
    enabledTools: [],
    autoJoinDefaultRoom: false,
    ...overrides,
  };
}

const defaultProps = {
  actions: [] as TaskAction[],
  actionPending: null as TaskAction | null,
  actionResult: null as { ok: boolean; message: string } | null,
  reasonAction: null as TaskAction | null,
  reasonText: "",
  onReasonTextChange: vi.fn(),
  onAction: vi.fn(),
  onCancelReason: vi.fn(),
  canAssign: false,
  agents: [] as AgentDefinition[],
  showAssignPicker: false,
  onToggleAssignPicker: vi.fn(),
  assignPending: false,
  onAssign: vi.fn(),
};

function renderBar(overrides: Partial<typeof defaultProps> = {}) {
  return render(
    <FluentProvider theme={webDarkTheme}>
      <TaskActionsBar {...defaultProps} {...overrides} />
    </FluentProvider>,
  );
}

describe("TaskActionsBar", () => {
  afterEach(() => { cleanup(); document.body.innerHTML = ""; vi.clearAllMocks(); });

  // ── Action buttons ──

  it("renders action buttons based on actions prop", () => {
    renderBar({ actions: ["approve", "requestChanges"] });
    expect(screen.getByText("Approve")).toBeInTheDocument();
    expect(screen.getByText("Request Changes")).toBeInTheDocument();
  });

  it("calls onAction with the right action when clicked", () => {
    const fn = vi.fn();
    renderBar({ actions: ["approve", "merge"], onAction: fn });
    fireEvent.click(screen.getByText("Approve"));
    expect(fn).toHaveBeenCalledWith("approve");
  });

  it("disables buttons when actionPending is set", () => {
    renderBar({ actions: ["approve", "merge"], actionPending: "approve" });
    expect(screen.getByText("Merge").closest("button")).toBeDisabled();
  });

  it("disables non-reason buttons when reasonAction is set", () => {
    renderBar({ actions: ["approve", "requestChanges"], reasonAction: "requestChanges" });
    expect(screen.getByText("Approve").closest("button")).toBeDisabled();
  });

  // ── Reason textarea ──

  it("shows reason textarea when reasonAction is set", () => {
    renderBar({ reasonAction: "requestChanges", reasonText: "" });
    expect(screen.getByPlaceholderText("Describe the changes needed…")).toBeInTheDocument();
    expect(screen.getByText("Cancel")).toBeInTheDocument();
  });

  it("shows rejection placeholder for reject action", () => {
    renderBar({ reasonAction: "reject", reasonText: "" });
    expect(screen.getByPlaceholderText("Reason for rejection…")).toBeInTheDocument();
  });

  it("calls onCancelReason when Cancel is clicked", () => {
    const fn = vi.fn();
    renderBar({ reasonAction: "requestChanges", reasonText: "", onCancelReason: fn });
    fireEvent.click(screen.getByText("Cancel"));
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it("disables submit when reasonText is empty", () => {
    renderBar({ reasonAction: "requestChanges", reasonText: "" });
    expect(screen.getByText("Submit Request Changes").closest("button")).toBeDisabled();
  });

  it("enables submit when reasonText has content", () => {
    renderBar({ reasonAction: "requestChanges", reasonText: "Fix the bug" });
    expect(screen.getByText("Submit Request Changes").closest("button")).not.toBeDisabled();
  });

  // ── Action result feedback ──

  it("shows success feedback", () => {
    renderBar({ actionResult: { ok: true, message: "Task approved" } });
    expect(screen.getByText("Task approved")).toBeInTheDocument();
  });

  it("shows error feedback", () => {
    renderBar({ actionResult: { ok: false, message: "Failed to approve" } });
    expect(screen.getByText("Failed to approve")).toBeInTheDocument();
  });

  // ── Assign agent ──

  it("shows Assign Agent button when canAssign is true", () => {
    renderBar({ canAssign: true });
    expect(screen.getByText("Assign Agent")).toBeInTheDocument();
  });

  it("hides Assign Agent button when canAssign is false", () => {
    renderBar({ canAssign: false });
    expect(screen.queryByText("Assign Agent")).not.toBeInTheDocument();
  });

  it("shows agent picker when showAssignPicker is true", () => {
    const agents = [
      makeAgent({ id: "a1", name: "Planner", role: "Planner" }),
      makeAgent({ id: "a2", name: "Coder", role: "Coder" }),
    ];
    renderBar({ canAssign: true, showAssignPicker: true, agents });
    expect(screen.getByText("Planner (Planner)")).toBeInTheDocument();
    expect(screen.getByText("Coder (Coder)")).toBeInTheDocument();
  });

  it("calls onAssign when agent picker button is clicked", () => {
    const fn = vi.fn();
    const agent = makeAgent({ id: "a1", name: "Bot" });
    renderBar({ canAssign: true, showAssignPicker: true, agents: [agent], onAssign: fn });
    fireEvent.click(screen.getByText("Bot (Planner)"));
    expect(fn).toHaveBeenCalledWith(agent);
  });

  it("calls onToggleAssignPicker when Assign Agent is clicked", () => {
    const fn = vi.fn();
    renderBar({ canAssign: true, onToggleAssignPicker: fn });
    fireEvent.click(screen.getByText("Assign Agent"));
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it("disables Assign Agent when assignPending is true", () => {
    renderBar({ canAssign: true, assignPending: true });
    expect(screen.getByText("Assign Agent").closest("button")).toBeDisabled();
  });
});
