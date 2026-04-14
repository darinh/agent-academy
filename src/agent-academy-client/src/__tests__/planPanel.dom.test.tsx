// @vitest-environment jsdom
/**
 * RTL DOM tests for PlanPanel.
 *
 * Covers: no-room empty state, loading spinner, empty plan (no content),
 * populated markdown rendering, edit mode, save flow, cancel flow,
 * delete confirmation dialog, and API error display.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor, fireEvent, act, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

import PlanPanel from "../PlanPanel";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../api", () => ({
  getPlan: vi.fn(),
  setPlan: vi.fn(),
  deletePlan: vi.fn(),
}));

import { getPlan, setPlan, deletePlan } from "../api";

const mockGetPlan = vi.mocked(getPlan);
const mockSetPlan = vi.mocked(setPlan);
const mockDeletePlan = vi.mocked(deletePlan);

// ── Helpers ─────────────────────────────────────────────────────────────

afterEach(() => {
  cleanup();
  document.body.innerHTML = "";
});

beforeEach(() => {
  vi.resetAllMocks();
});

function renderPanel(roomId: string | null = "room-1") {
  return render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(PlanPanel, { roomId }),
    ),
  );
}

/** Resolve getPlan and wait for the component to settle. */
async function renderLoaded(roomId: string, content: string | null) {
  mockGetPlan.mockResolvedValue(content !== null ? { content } : null);
  renderPanel(roomId);
  await waitFor(() => {
    expect(mockGetPlan).toHaveBeenCalledWith(roomId);
  });
  // Wait for the loading state to clear
  await waitFor(() => {
    expect(screen.queryByText("Loading plan…")).not.toBeInTheDocument();
  });
}

/**
 * The toolbar has `display: none` (Griffel injects real CSS in jsdom),
 * so getByRole can't find those buttons. This helper finds a toolbar
 * button by its text content in the hidden toolbar.
 */
function getToolbarButton(name: string): HTMLElement {
  const matches = screen.getAllByText(name).filter(
    (el) => el.closest("button") !== null,
  );
  const btn = matches[0]?.closest("button");
  if (!btn) throw new Error(`Toolbar button "${name}" not found`);
  return btn;
}

// ── Tests ───────────────────────────────────────────────────────────────

describe("PlanPanel", () => {
  // ── No room selected ──

  describe("no room selected", () => {
    it("shows empty prompt when roomId is null", () => {
      mockGetPlan.mockResolvedValue(null);
      renderPanel(null);
      expect(screen.getByText("Select a room to view its plan")).toBeInTheDocument();
    });

    it("does not call getPlan when roomId is null", () => {
      renderPanel(null);
      expect(mockGetPlan).not.toHaveBeenCalled();
    });
  });

  // ── Loading state ──

  describe("loading state", () => {
    it("shows spinner while loading plan", () => {
      // Never resolve so we stay in loading
      mockGetPlan.mockReturnValue(new Promise(() => {}));
      renderPanel("room-1");
      expect(screen.getByText("Loading plan…")).toBeInTheDocument();
    });
  });

  // ── Empty plan (no content) ──

  describe("empty plan", () => {
    it("shows 'No plan yet' when API returns null", async () => {
      await renderLoaded("room-1", null);
      expect(screen.getByText("No plan yet")).toBeInTheDocument();
    });

    it("shows 'No plan yet' when API returns empty string", async () => {
      await renderLoaded("room-1", "");
      expect(screen.getByText("No plan yet")).toBeInTheDocument();
    });

    it("shows a 'Create plan' button when no content", async () => {
      await renderLoaded("room-1", "");
      expect(screen.getByRole("button", { name: /create plan/i })).toBeInTheDocument();
    });

    it("clicking 'Create plan' enters edit mode", async () => {
      await renderLoaded("room-1", "");
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: /create plan/i }));
      });
      expect(screen.getByRole("textbox")).toBeInTheDocument();
    });
  });

  // ── Populated plan ──

  describe("populated plan", () => {
    it("renders markdown content", async () => {
      await renderLoaded("room-1", "# My Plan\n\nSome details");
      expect(screen.getByText("My Plan")).toBeInTheDocument();
      expect(screen.getByText("Some details")).toBeInTheDocument();
    });

    it("does not show 'No plan yet' when content exists", async () => {
      await renderLoaded("room-1", "# Has Content");
      expect(screen.queryByText("No plan yet")).not.toBeInTheDocument();
    });

    it("renders GFM tables in markdown", async () => {
      const md = "| Col A | Col B |\n|-------|-------|\n| 1     | 2     |";
      await renderLoaded("room-1", md);
      expect(screen.getByText("Col A")).toBeInTheDocument();
      expect(screen.getByText("Col B")).toBeInTheDocument();
    });
  });

  // ── Edit mode ──

  describe("edit mode", () => {
    it("Edit button enters edit mode with current content as draft", async () => {
      await renderLoaded("room-1", "# Existing");
      await act(async () => {
        fireEvent.click(getToolbarButton("Edit"));
      });
      const textarea = screen.getByRole("textbox");
      expect(textarea).toBeInTheDocument();
      expect(textarea).toHaveValue("# Existing");
    });

    it("Cancel button exits edit mode without saving", async () => {
      await renderLoaded("room-1", "# Original");
      await act(async () => {
        fireEvent.click(getToolbarButton("Edit"));
      });
      expect(screen.getByRole("textbox")).toBeInTheDocument();

      await act(async () => {
        fireEvent.click(getToolbarButton("Cancel"));
      });
      // Back to markdown view — textarea gone
      expect(screen.queryByRole("textbox")).not.toBeInTheDocument();
      expect(screen.getByText("Original")).toBeInTheDocument();
      expect(mockSetPlan).not.toHaveBeenCalled();
    });

    it("typing in textarea updates draft value", async () => {
      await renderLoaded("room-1", "");
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: /create plan/i }));
      });
      const textarea = screen.getByRole("textbox");
      await act(async () => {
        fireEvent.change(textarea, { target: { value: "New draft content" } });
      });
      expect(textarea).toHaveValue("New draft content");
    });
  });

  // ── Save flow ──

  describe("save flow", () => {
    it("Save calls setPlan and exits edit mode on success", async () => {
      mockSetPlan.mockResolvedValue(undefined);
      await renderLoaded("room-1", "# Old");

      // Enter edit mode
      await act(async () => {
        fireEvent.click(getToolbarButton("Edit"));
      });

      // Modify draft
      const textarea = screen.getByRole("textbox");
      await act(async () => {
        fireEvent.change(textarea, { target: { value: "# Updated" } });
      });

      // Save
      await act(async () => {
        fireEvent.click(getToolbarButton("Save"));
      });

      expect(mockSetPlan).toHaveBeenCalledWith("room-1", "# Updated");
      // Should exit edit mode and render the updated markdown
      await waitFor(() => {
        expect(screen.queryByRole("textbox")).not.toBeInTheDocument();
      });
      expect(screen.getByText("Updated")).toBeInTheDocument();
    });

    it("shows error when save fails", async () => {
      mockSetPlan.mockRejectedValue(new Error("Save failed"));
      await renderLoaded("room-1", "# Content");

      await act(async () => {
        fireEvent.click(getToolbarButton("Edit"));
      });
      await act(async () => {
        fireEvent.click(getToolbarButton("Save"));
      });

      await waitFor(() => {
        expect(screen.getByText("Save failed")).toBeInTheDocument();
      });
    });
  });

  // ── Delete flow ──

  describe("delete flow", () => {
    it("Delete button opens confirmation dialog", async () => {
      await renderLoaded("room-1", "# Has Content");

      await act(async () => {
        fireEvent.click(getToolbarButton("Delete"));
      });

      expect(screen.getByText("Delete plan?")).toBeInTheDocument();
      expect(screen.getByText("This action cannot be undone.")).toBeInTheDocument();
    });

    it("confirming delete calls deletePlan and clears content", async () => {
      mockDeletePlan.mockResolvedValue(undefined);
      await renderLoaded("room-1", "# Will Be Deleted");

      // Open dialog
      await act(async () => {
        fireEvent.click(getToolbarButton("Delete"));
      });

      // The dialog has its own Delete button — find it inside the dialog surface
      const dialog = screen.getByText("Delete plan?").closest("[role='dialog']") as HTMLElement;
      const confirmBtn = within(dialog).getAllByText("Delete")
        .map((el) => el.closest("button"))
        .filter(Boolean)
        .pop()!;
      await act(async () => {
        fireEvent.click(confirmBtn);
      });

      expect(mockDeletePlan).toHaveBeenCalledWith("room-1");
      await waitFor(() => {
        expect(screen.getByText("No plan yet")).toBeInTheDocument();
      });
    });

    it("shows error when delete fails", async () => {
      mockDeletePlan.mockRejectedValue(new Error("Delete denied"));
      await renderLoaded("room-1", "# Content");

      await act(async () => {
        fireEvent.click(getToolbarButton("Delete"));
      });

      const dialog = screen.getByText("Delete plan?").closest("[role='dialog']") as HTMLElement;
      const confirmBtn = within(dialog).getAllByText("Delete")
        .map((el) => el.closest("button"))
        .filter(Boolean)
        .pop()!;
      await act(async () => {
        fireEvent.click(confirmBtn);
      });

      await waitFor(() => {
        expect(screen.getByText("Delete denied")).toBeInTheDocument();
      });
    });

    it("Delete button is not shown when content is empty", async () => {
      await renderLoaded("room-1", "");
      const deleteElements = screen.queryAllByText("Delete").filter(
        (el) => el.closest("button") !== null,
      );
      expect(deleteElements).toHaveLength(0);
    });
  });

  // ── Error state ──

  describe("error state", () => {
    it("shows error message when getPlan rejects", async () => {
      mockGetPlan.mockRejectedValue(new Error("Network failure"));
      renderPanel("room-1");

      await waitFor(() => {
        expect(screen.getByText("Network failure")).toBeInTheDocument();
      });
    });

    it("shows stringified error for non-Error throws", async () => {
      mockGetPlan.mockRejectedValue("raw string error");
      renderPanel("room-1");

      await waitFor(() => {
        expect(screen.getByText("raw string error")).toBeInTheDocument();
      });
    });

    it("dismiss button clears the error banner", async () => {
      mockGetPlan.mockRejectedValue(new Error("Server error"));
      renderPanel("room-1");

      await waitFor(() => {
        expect(screen.getByText("Server error")).toBeInTheDocument();
      });

      const dismissBtn = screen.getByRole("button", { name: /dismiss/i });
      expect(dismissBtn).toBeInTheDocument();

      await act(async () => {
        fireEvent.click(dismissBtn);
      });

      expect(screen.queryByText("Server error")).not.toBeInTheDocument();
    });
  });
});
