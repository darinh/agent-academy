// @vitest-environment jsdom
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createElement } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import WorkspaceToolbar, { type ToolbarModel } from "../WorkspaceToolbar";

// Mock V3Badge to a simple span (matches project convention).
vi.mock("../V3Badge", () => ({
  default: ({ children, color }: { children: React.ReactNode; color: string }) =>
    createElement("span", { "data-testid": `badge-${color}` }, children),
}));

const EMPTY_STYLES: Record<string, string> = {
  tabBar: "",
  tabStrip: "",
  toolbarSelect: "",
  filterMenuButton: "",
  filterBadge: "",
  workspaceMetaText: "",
};

function makeChatToolbar(overrides: Partial<ToolbarModel["chatToolbar"] & object> = {}): NonNullable<ToolbarModel["chatToolbar"]> {
  return {
    currentPhase: "Discussion",
    onPhaseChange: vi.fn(),
    disabled: false,
    filterChecked: {},
    hiddenFilterCount: 0,
    onFilterChange: vi.fn(),
    ...overrides,
  };
}

function renderToolbar(model: ToolbarModel) {
  return render(
    <FluentProvider theme={webDarkTheme}>
      <WorkspaceToolbar model={model} styles={EMPTY_STYLES} />
    </FluentProvider>,
  );
}

afterEach(() => {
  cleanup();
  document.body.innerHTML = "";
});

describe("WorkspaceToolbar", () => {
  describe("with chatToolbar", () => {
    it("renders phase select", () => {
      renderToolbar({ tab: "chat", chatToolbar: makeChatToolbar() });
      expect(screen.getByTitle("Change room phase")).toBeInTheDocument();
    });

    it("phase select reflects currentPhase", () => {
      renderToolbar({ tab: "chat", chatToolbar: makeChatToolbar({ currentPhase: "Planning" }) });
      const select = screen.getByTitle("Change room phase") as HTMLSelectElement;
      expect(select.value).toBe("Planning");
    });

    it("phase select is disabled when disabled is true", () => {
      renderToolbar({ tab: "chat", chatToolbar: makeChatToolbar({ disabled: true }) });
      expect(screen.getByTitle("Change room phase")).toBeDisabled();
    });

    it("shows filter button with Filter text", () => {
      renderToolbar({ tab: "chat", chatToolbar: makeChatToolbar() });
      expect(screen.getByText(/filter/i)).toBeInTheDocument();
    });

    it("shows filter badge count when hiddenFilterCount > 0", () => {
      renderToolbar({ tab: "chat", chatToolbar: makeChatToolbar({ hiddenFilterCount: 3 }) });
      expect(screen.getByTestId("badge-info")).toHaveTextContent("3");
    });

    it("does not show filter badge when hiddenFilterCount is 0", () => {
      renderToolbar({ tab: "chat", chatToolbar: makeChatToolbar({ hiddenFilterCount: 0 }) });
      expect(screen.queryByTestId("badge-info")).not.toBeInTheDocument();
    });

    it("changing phase select calls onPhaseChange", async () => {
      const onPhaseChange = vi.fn();
      renderToolbar({ tab: "chat", chatToolbar: makeChatToolbar({ onPhaseChange }) });
      const user = userEvent.setup();
      await user.selectOptions(screen.getByTitle("Change room phase"), "Implementation");
      expect(onPhaseChange).toHaveBeenCalledWith("Implementation");
    });
  });

  describe("without chatToolbar (meta text)", () => {
    it("shows meta text for tasks tab", () => {
      renderToolbar({ tab: "tasks", chatToolbar: null });
      expect(screen.getByText("Sorted by newest")).toBeInTheDocument();
    });

    it("shows meta text for commands tab", () => {
      renderToolbar({ tab: "commands", chatToolbar: null });
      expect(screen.getByText("Command Deck")).toBeInTheDocument();
    });

    it("shows meta text for dashboard tab", () => {
      renderToolbar({ tab: "dashboard", chatToolbar: null });
      expect(screen.getByText("System telemetry")).toBeInTheDocument();
    });

    it("does not show meta text for chat tab (not in TOOLBAR_META)", () => {
      renderToolbar({ tab: "chat", chatToolbar: null });
      // None of the known meta texts should appear
      expect(screen.queryByText("Sorted by newest")).not.toBeInTheDocument();
      expect(screen.queryByText("Command Deck")).not.toBeInTheDocument();
      expect(screen.queryByText("System telemetry")).not.toBeInTheDocument();
    });
  });
});
