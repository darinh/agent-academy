// @vitest-environment jsdom
/**
 * Interactive RTL tests for TemplateCard.
 *
 * Uses @testing-library/react + jsdom.
 * Covers: collapsed/expanded toggle, new-template form, editing existing
 * template, form validation (name + content required), save/cancel/delete
 * interactions, error handling, and delete confirmation dialog.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, configure, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// Fluent UI Dialog portals can take >1s to mount under parallel test load.
configure({ asyncUtilTimeout: 5000 });

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../api", () => ({
  createInstructionTemplate: vi.fn(),
  updateInstructionTemplate: vi.fn(),
  deleteInstructionTemplate: vi.fn(),
}));

import TemplateCard from "../TemplateCard";
import type { InstructionTemplate } from "../api";
import {
  createInstructionTemplate,
  updateInstructionTemplate,
  deleteInstructionTemplate,
} from "../api";

const mockCreate = vi.mocked(createInstructionTemplate);
const mockUpdate = vi.mocked(updateInstructionTemplate);
const mockDelete = vi.mocked(deleteInstructionTemplate);

// ── Factories ──────────────────────────────────────────────────────────

function makeTemplate(overrides: Partial<InstructionTemplate> = {}): InstructionTemplate {
  return {
    id: "tmpl-1",
    name: "Engineering",
    description: "Standard engineering template",
    content: "Follow engineering best practices.",
    createdAt: "2026-04-01T00:00:00Z",
    updatedAt: "2026-04-01T00:00:00Z",
    ...overrides,
  };
}

// ── Helpers ─────────────────────────────────────────────────────────────

interface RenderProps {
  template?: InstructionTemplate;
  isNew?: boolean;
  expanded?: boolean;
  onToggle?: () => void;
  onSaved?: () => void;
  onCancelNew?: () => void;
}

function renderCard(props: RenderProps = {}) {
  // Use "in" check so callers can explicitly pass template: undefined
  const template = "template" in props ? props.template : makeTemplate();
  const isNew = props.isNew ?? false;
  const expanded = props.expanded ?? false;
  const onToggle = props.onToggle ?? vi.fn();
  const onSaved = props.onSaved ?? vi.fn();
  const onCancelNew = props.onCancelNew ?? vi.fn();

  return {
    ...render(
      createElement(
        FluentProvider,
        { theme: webDarkTheme },
        createElement(TemplateCard, {
          template,
          isNew,
          expanded,
          onToggle,
          onSaved,
          onCancelNew,
        }),
      ),
    ),
    onToggle,
    onSaved,
    onCancelNew,
  };
}

// ── Tests ───────────────────────────────────────────────────────────────

describe("TemplateCard (interactive)", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  afterEach(() => {
    cleanup();
    document.body.innerHTML = "";
  });

  // ── Collapsed state ──

  describe("collapsed state", () => {
    it("shows the template name", () => {
      renderCard({ expanded: false });
      expect(screen.getByText("Engineering")).toBeInTheDocument();
    });

    it("shows the template description when present", () => {
      renderCard({ expanded: false });
      expect(screen.getByText("Standard engineering template")).toBeInTheDocument();
    });

    it("hides description when null", () => {
      renderCard({
        template: makeTemplate({ description: null }),
        expanded: false,
      });
      expect(screen.queryByText("Standard engineering template")).not.toBeInTheDocument();
    });

    it("does not show the form fields when collapsed", () => {
      renderCard({ expanded: false });
      expect(screen.queryByText("Content")).not.toBeInTheDocument();
      expect(screen.queryByRole("button", { name: /save/i })).not.toBeInTheDocument();
    });

    it("calls onToggle when the header is clicked", async () => {
      const user = userEvent.setup();
      const { onToggle } = renderCard({ expanded: false });
      await user.click(screen.getByText("Engineering"));
      expect(onToggle).toHaveBeenCalledOnce();
    });
  });

  // ── Expanded state (existing template) ──

  describe("expanded state", () => {
    it("shows form fields populated from the template", () => {
      renderCard({ expanded: true });
      expect(screen.getByDisplayValue("Engineering")).toBeInTheDocument();
      expect(
        screen.getByDisplayValue("Follow engineering best practices."),
      ).toBeInTheDocument();
    });

    it("shows Save and Delete buttons", () => {
      renderCard({ expanded: true });
      expect(screen.getByRole("button", { name: /save/i })).toBeInTheDocument();
      expect(screen.getByRole("button", { name: /delete/i })).toBeInTheDocument();
    });

    it("calls onToggle when the header is clicked while expanded", async () => {
      const user = userEvent.setup();
      const { onToggle } = renderCard({ expanded: true });
      await user.click(screen.getByText("Engineering"));
      expect(onToggle).toHaveBeenCalledOnce();
    });
  });

  // ── Form validation ──

  describe("form validation", () => {
    it("disables Save when name is empty", async () => {
      const user = userEvent.setup();
      renderCard({ expanded: true });
      const nameInput = screen.getByDisplayValue("Engineering");
      await user.clear(nameInput);
      expect(screen.getByRole("button", { name: /save/i })).toBeDisabled();
    });

    it("disables Save when content is empty", async () => {
      const user = userEvent.setup();
      renderCard({ expanded: true });
      const contentInput = screen.getByDisplayValue(
        "Follow engineering best practices.",
      );
      await user.clear(contentInput);
      expect(screen.getByRole("button", { name: /save/i })).toBeDisabled();
    });

    it("enables Save when name and content are filled", () => {
      renderCard({ expanded: true });
      expect(screen.getByRole("button", { name: /save/i })).toBeEnabled();
    });
  });

  // ── Save (existing template) ──

  describe("save existing template", () => {
    it("calls updateInstructionTemplate on save and then onSaved", async () => {
      const user = userEvent.setup();
      mockUpdate.mockResolvedValue(makeTemplate());
      const { onSaved } = renderCard({ expanded: true });

      await user.click(screen.getByRole("button", { name: /save/i }));

      await waitFor(() => {
        expect(mockUpdate).toHaveBeenCalledWith("tmpl-1", {
          name: "Engineering",
          description: "Standard engineering template",
          content: "Follow engineering best practices.",
        });
      });
      expect(onSaved).toHaveBeenCalledOnce();
    });

    it("shows error message when save fails", async () => {
      const user = userEvent.setup();
      mockUpdate.mockRejectedValue(new Error("Network failure"));
      renderCard({ expanded: true });

      await user.click(screen.getByRole("button", { name: /save/i }));

      await waitFor(() => {
        expect(screen.getByText("Network failure")).toBeInTheDocument();
      });
    });

    it("shows generic error for non-Error throws", async () => {
      const user = userEvent.setup();
      mockUpdate.mockRejectedValue("string error");
      renderCard({ expanded: true });

      await user.click(screen.getByRole("button", { name: /save/i }));

      await waitFor(() => {
        expect(screen.getByText("Failed to save template")).toBeInTheDocument();
      });
    });

    it("saves with updated values after editing", async () => {
      const user = userEvent.setup();
      mockUpdate.mockResolvedValue(makeTemplate({ name: "Updated" }));
      const { onSaved } = renderCard({ expanded: true });

      const nameInput = screen.getByDisplayValue("Engineering");
      await user.clear(nameInput);
      await user.type(nameInput, "Updated");

      await user.click(screen.getByRole("button", { name: /save/i }));

      await waitFor(() => {
        expect(mockUpdate).toHaveBeenCalledWith("tmpl-1", {
          name: "Updated",
          description: "Standard engineering template",
          content: "Follow engineering best practices.",
        });
      });
      expect(onSaved).toHaveBeenCalledOnce();
    });
  });

  // ── Delete (existing template) ──

  describe("delete template", () => {
    it("shows a confirmation dialog when Delete is clicked", async () => {
      renderCard({ expanded: true });

      fireEvent.click(screen.getByRole("button", { name: /delete/i }));

      const dialog = await screen.findByRole("dialog");
      expect(within(dialog).getByText(/delete "Engineering"\?/i)).toBeInTheDocument();
    });

    it("does not delete when dialog Cancel is clicked", async () => {
      const user = userEvent.setup();
      renderCard({ expanded: true });

      fireEvent.click(screen.getByRole("button", { name: /delete/i }));

      const dialog = await screen.findByRole("dialog");
      await user.click(within(dialog).getByRole("button", { name: /cancel/i }));

      expect(mockDelete).not.toHaveBeenCalled();
    });

    it("calls deleteInstructionTemplate when dialog Delete is confirmed", async () => {
      const user = userEvent.setup();
      mockDelete.mockResolvedValue({ status: "deleted", id: "tmpl-1" });
      const { onSaved } = renderCard({ expanded: true });

      fireEvent.click(screen.getByRole("button", { name: /delete/i }));

      const dialog = await screen.findByRole("dialog");
      await user.click(within(dialog).getByRole("button", { name: /delete/i }));

      await waitFor(() => {
        expect(mockDelete).toHaveBeenCalledWith("tmpl-1");
      });
      expect(onSaved).toHaveBeenCalledOnce();
    });

    it("shows error when delete fails", async () => {
      const user = userEvent.setup();
      mockDelete.mockRejectedValue(new Error("Permission denied"));
      renderCard({ expanded: true });

      fireEvent.click(screen.getByRole("button", { name: /delete/i }));

      const dialog = await screen.findByRole("dialog");
      await user.click(within(dialog).getByRole("button", { name: /delete/i }));

      await waitFor(() => {
        expect(screen.getByText("Permission denied")).toBeInTheDocument();
      });
    });

    it("shows generic error for non-Error delete failures", async () => {
      const user = userEvent.setup();
      mockDelete.mockRejectedValue(42);
      renderCard({ expanded: true });

      fireEvent.click(screen.getByRole("button", { name: /delete/i }));

      const dialog = await screen.findByRole("dialog");
      await user.click(within(dialog).getByRole("button", { name: /delete/i }));

      await waitFor(() => {
        expect(screen.getByText("Failed to delete template")).toBeInTheDocument();
      });
    });
  });

  // ── New template mode ──

  describe("new template (isNew)", () => {
    it("shows 'New Template' header and empty form fields", () => {
      renderCard({ isNew: true, expanded: true, template: undefined });
      expect(screen.getByText("New Template")).toBeInTheDocument();
      expect(
        screen.getByPlaceholderText("Template name (must be unique)"),
      ).toHaveValue("");
      expect(
        screen.getByPlaceholderText("Short description (optional)"),
      ).toHaveValue("");
      expect(
        screen.getByPlaceholderText(
          /instruction template content/i,
        ),
      ).toHaveValue("");
    });

    it("shows Create and Cancel buttons", () => {
      renderCard({ isNew: true, expanded: true, template: undefined });
      expect(screen.getByRole("button", { name: /create/i })).toBeInTheDocument();
      expect(screen.getByRole("button", { name: /cancel/i })).toBeInTheDocument();
    });

    it("disables Create when name and content are empty", () => {
      renderCard({ isNew: true, expanded: true, template: undefined });
      expect(screen.getByRole("button", { name: /create/i })).toBeDisabled();
    });

    it("disables Create when only name is filled", async () => {
      const user = userEvent.setup();
      renderCard({ isNew: true, expanded: true, template: undefined });
      await user.type(
        screen.getByPlaceholderText("Template name (must be unique)"),
        "My Template",
      );
      expect(screen.getByRole("button", { name: /create/i })).toBeDisabled();
    });

    it("enables Create when name and content are filled", async () => {
      const user = userEvent.setup();
      renderCard({ isNew: true, expanded: true, template: undefined });
      await user.type(
        screen.getByPlaceholderText("Template name (must be unique)"),
        "My Template",
      );
      await user.type(
        screen.getByPlaceholderText(/instruction template content/i),
        "Some content",
      );
      expect(screen.getByRole("button", { name: /create/i })).toBeEnabled();
    });

    it("calls createInstructionTemplate on create and then onSaved", async () => {
      const user = userEvent.setup();
      mockCreate.mockResolvedValue(
        makeTemplate({ id: "tmpl-new", name: "My Template" }),
      );
      const { onSaved } = renderCard({
        isNew: true,
        expanded: true,
        template: undefined,
      });

      await user.type(
        screen.getByPlaceholderText("Template name (must be unique)"),
        "My Template",
      );
      await user.type(
        screen.getByPlaceholderText("Short description (optional)"),
        "A desc",
      );
      await user.type(
        screen.getByPlaceholderText(/instruction template content/i),
        "Template body",
      );

      await user.click(screen.getByRole("button", { name: /create/i }));

      await waitFor(() => {
        expect(mockCreate).toHaveBeenCalledWith({
          name: "My Template",
          description: "A desc",
          content: "Template body",
        });
      });
      expect(onSaved).toHaveBeenCalledOnce();
    });

    it("sends null description when left empty", async () => {
      const user = userEvent.setup();
      mockCreate.mockResolvedValue(makeTemplate());
      const { onSaved } = renderCard({
        isNew: true,
        expanded: true,
        template: undefined,
      });

      await user.type(
        screen.getByPlaceholderText("Template name (must be unique)"),
        "Bare",
      );
      await user.type(
        screen.getByPlaceholderText(/instruction template content/i),
        "Content here",
      );

      await user.click(screen.getByRole("button", { name: /create/i }));

      await waitFor(() => {
        expect(mockCreate).toHaveBeenCalledWith({
          name: "Bare",
          description: null,
          content: "Content here",
        });
      });
      expect(onSaved).toHaveBeenCalledOnce();
    });

    it("calls onCancelNew when Cancel is clicked", async () => {
      const user = userEvent.setup();
      const { onCancelNew } = renderCard({
        isNew: true,
        expanded: true,
        template: undefined,
      });
      await user.click(screen.getByRole("button", { name: /cancel/i }));
      expect(onCancelNew).toHaveBeenCalledOnce();
    });

    it("shows error when create fails", async () => {
      const user = userEvent.setup();
      mockCreate.mockRejectedValue(new Error("Duplicate name"));
      renderCard({ isNew: true, expanded: true, template: undefined });

      await user.type(
        screen.getByPlaceholderText("Template name (must be unique)"),
        "Dup",
      );
      await user.type(
        screen.getByPlaceholderText(/instruction template content/i),
        "Some content",
      );

      await user.click(screen.getByRole("button", { name: /create/i }));

      await waitFor(() => {
        expect(screen.getByText("Duplicate name")).toBeInTheDocument();
      });
    });
  });

  // ── Edge cases ──

  describe("edge cases", () => {
    it("returns null when not isNew and template is undefined", () => {
      renderCard({
        template: undefined,
        isNew: false,
        expanded: false,
      });
      // Component returns null — no card content should be present
      expect(screen.queryByRole("button")).not.toBeInTheDocument();
      expect(screen.queryByText("Name")).not.toBeInTheDocument();
    });

    it("trims whitespace from name and description when saving", async () => {
      const user = userEvent.setup();
      mockUpdate.mockResolvedValue(makeTemplate());
      renderCard({
        template: makeTemplate({ name: "  Padded  ", description: "  desc  " }),
        expanded: true,
      });

      await user.click(screen.getByRole("button", { name: /save/i }));

      await waitFor(() => {
        expect(mockUpdate).toHaveBeenCalledWith("tmpl-1", {
          name: "Padded",
          description: "desc",
          content: "Follow engineering best practices.",
        });
      });
    });
  });
});
